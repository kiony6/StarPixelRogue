#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M20 持久化层无头实测（SaveManager + Telemetry）

做什么：
    拉起两个无头进程，分别验证两个**纯本机、却始终零实测覆盖**的持久化模块：
      SAVE     战绩提交（强局刷新纪录/弱局不刷但累计要加）、图鉴发现去重、
               落盘→读档往返不丢字段、损坏存档优雅回退为空档；
      TELEMETRY 埋点事件真的进了队列并落盘、落盘失败时批次回到队列（重试不丢）、
               关闭「匿名数据统计」开关即丢弃未落盘事件、
               **JSONL 文件每一行都是合法 JSON**（含特殊字符的字符串精确往返）。

为什么必须测：这两类缺陷都是**静默作废**型的——
    埋点文件是非法 JSON 时，游戏照常跑、日志零报错，采集了等于白采集；
    存档读档丢字段时，只有玩家某天发现纪录没了才知道。

判据的区分力（本脚本最要紧的地方）：
    1) JSONL 合法性**不由被测进程自判**——被测进程只负责制造含特殊字符的事件，
       本脚本用 Python 的 json.loads 逐行独立解析，并断言特殊字符串**精确往返**
       （转义修坏任何一个字符都过不了）。期望值绝不来自被测对象自己。
       修复前，仅反斜杠一个字符就足以让标记行变成非法 JSON。
    2) 落盘失败由被测进程用"目录与现有文件同名"这一真实冲突制造（必抛 IOException），
       断言 FlushFailCount ≥1 且批次回到队列——旧实现在这里队列被清空、事件无声消失；
       随后重试成功，且 retry 事件真的出现在文件里（不是进程自说自话）。
    3) 关开关断言队列归零，且被丢弃的事件**不得出现在文件里**——
       旧实现残留队列，开关前的事件仍会被写进文件。

用户数据保护（本脚本会写真实的持久化目录）：
    跑前把 save.json / settings.json 改名备份、把当日 telemetry 文件挪开；
    跑完校验后**全部恢复原样**；探针产物（测试用 telemetry 文件）直接删除。

退出码：0 = 全部通过；1 = 有场景未通过；2 = 前置条件不满足
"""

import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")

SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")

# 无头环境必然出现的渲染噪声，不算运行时异常
NOISE = ("shader", "gpu", "memorysetup", "unitymemory", "curl error", "unitlets",
         "screen position out of view frustum")

SCENARIOS = (
    ("SAVE", "本地存档（战绩 / 图鉴去重 / 落盘读档往返 / 损坏回退）"),
    ("TELEMETRY", "埋点（JSONL 合法性 / 落盘失败重试 / 关开关清队列）"),
)

# 与 Dev/PersistenceProbe.TrickyString 完全一致：
# 含双引号、反斜杠、换行、制表、中文——转义修复前仅反斜杠一项就能让该行变非法 JSON。
TRICKY = "路径C:\\Games\\星界\"引号\"\n换行\t制表"

BACKUP_SUFFIX = ".m20bak"


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def find_data_dir(explicit=None):
    """定位 persistentDataPath（company/product 目录）。

    依据『目录里有 save.json（最强的本游戏信号：目录里还有 Logs/telemetry-*.jsonl）』
    从 LocalLow 逐层找，避免把公司名/产品名硬编码进脚本（改名即失效）。
    """
    if explicit:
        return explicit if os.path.isdir(explicit) else None
    low = os.path.join(os.environ.get("USERPROFILE", os.path.expanduser("~")),
                       "AppData", "LocalLow")
    if not os.path.isdir(low):
        return None
    strong = []
    weak = []
    for dirpath, dirnames, filenames in os.walk(low):
        if dirpath[len(low):].count(os.sep) >= 3:
            dirnames[:] = []
            continue
        if "save.json" not in filenames:
            continue
        # 本游戏既有 save.json 又有 Logs/telemetry-*.jsonl——最强信号
        if glob.glob(os.path.join(dirpath, "Logs", "telemetry-*.jsonl")):
            strong.append(dirpath)
        else:
            weak.append(dirpath)
    for c in sorted(strong, key=lambda p: -os.path.getmtime(os.path.join(p, "save.json"))):
        return c
    return weak[0] if weak else None


def parse_smoke(path):
    data = {}
    if not os.path.exists(path):
        return data
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = SMOKE_RE.search(line)
            if m:
                data[m.group(1).strip()] = m.group(2).strip()
    return data


def count_exceptions(path):
    n = 0
    if not os.path.exists(path):
        return n
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            low = line.lower()
            if "exception" in low and not any(k in low for k in NOISE):
                n += 1
    return n


def as_int(d, key, default=-1):
    try:
        return int(str(d.get(key, default)).strip())
    except (TypeError, ValueError):
        return default


def backup_user_files(data_dir):
    """跑前备份（改名挪开）：save.json / settings.json / 当日 telemetry 文件。"""
    moved = {}
    names = ["save.json", "settings.json"]
    names += [os.path.basename(p) for p in
              glob.glob(os.path.join(data_dir, "Logs", "telemetry-*.jsonl"))]
    for name in names:
        if name.startswith("telemetry-"):
            src = os.path.join(data_dir, "Logs", name)
        else:
            src = os.path.join(data_dir, name)
        if os.path.exists(src):
            dst = src + BACKUP_SUFFIX
            if os.path.exists(dst):
                os.remove(dst)
            os.rename(src, dst)
            moved[src] = dst
    return moved


def restore_user_files(moved):
    """跑后恢复：探针写出的新文件删除，备份改回原名（幂等，可重复调用）。

    实测踩过的坑：`--only TELEMETRY` 时 save.json 已被备份挪走，
    探针用来制造写失败的那个"同名目录"于是真的被创建出来——
    恢复时 `os.rename` 撞上已存在的目录会抛 FileExistsError。
    故这里先删同名物（文件或目录都删），再改名。
    """
    for src in list(moved.keys()):
        dst = moved.pop(src)
        if not os.path.exists(dst):
            continue                      # 备份已不在（上次已恢复），无需处理
        if os.path.isdir(src) and not os.path.islink(src):
            shutil.rmtree(src, ignore_errors=True)
        elif os.path.exists(src):
            try:
                os.remove(src)
            except OSError:
                pass
        if os.path.exists(dst):
            os.rename(dst, src)


def run_one(exe, scene, limit_mb=200, timeout=90):
    log = os.path.join(ROOT, "persistence_%s.log" % scene)
    if os.path.exists(log):
        os.remove(log)
    try:
        proc = subprocess.Popen(
            [exe, "-batchmode", "-nographics", "-persistenceProbe",
             "-persistenceScene", scene, "-logFile", log],
            cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as e:
        print("        [错误] 无法启动进程: %s" % e)
        return None

    killed = False
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            break
        time.sleep(0.5)
        try:
            if os.path.getsize(log) > limit_mb * 1024 * 1024:
                proc.kill()
                killed = True
                break
        except OSError:
            pass
    else:
        proc.kill()
        killed = True

    return {"scene": scene, "code": proc.returncode, "killed": killed,
            "data": parse_smoke(log), "log": log, "exc": count_exceptions(log)}


# ==================== JSONL 独立校验（判据的核心） ====================

def validate_telemetry_file(data_dir):
    """用 Python 的 json.loads 逐行独立解析当日 telemetry 文件。

    返回 (problems, stats)。绝不依赖被测进程对"什么是合法 JSON"的自判。
    """
    problems = []
    files = sorted(glob.glob(os.path.join(data_dir, "Logs", "telemetry-*.jsonl")))
    if not files:
        return ["未找到 telemetry-*.jsonl——探针没写出任何文件"], {}

    path = files[-1]                       # 当日（探针刚写的）文件
    total = 0
    marker_ok = False
    retry_events = 0
    discarded_leaked = 0
    first_bad_line = ""

    with open(path, encoding="utf-8", errors="strict") as f:
        for lineno, line in enumerate(f, 1):
            line = line.rstrip("\r\n")
            if not line.strip():
                continue
            total += 1
            try:
                obj = json.loads(line)
            except ValueError as e:
                if not first_bad_line:
                    first_bad_line = "第 %d 行非法 JSON（%s）: %s…" % (
                        lineno, e, line[:80])
                continue
            if obj.get("e") == "probe_marker":
                # 特殊字符串必须精确往返——转义修坏任何一个字符都过不了
                if obj.get("tricky") == TRICKY:
                    marker_ok = True
                else:
                    problems.append("标记事件的特殊字符串未精确往返：\n           期望 %r\n           实际 %r"
                                    % (TRICKY, obj.get("tricky")))
            if obj.get("e") in ("probe_retry_a", "probe_retry_b"):
                retry_events += 1
            if obj.get("e") == "probe_should_discard":
                discarded_leaked += 1

    if first_bad_line:
        problems.append(first_bad_line + "——埋点文件含非法 JSON，整个文件无法解析，采集等于白采集")
    if total < 5:
        problems.append("telemetry 文件只有 %d 行事件（应 ≥5：标记/普通/中文/两条重试事件）" % total)
    if not marker_ok and not first_bad_line:
        problems.append("未找到标记事件 probe_marker（或其特殊字符串往返失败）")
    if retry_events < 2:
        problems.append("重试事件只出现 %d 条（应 2 条）——落盘失败后重试没真的写进文件" % retry_events)
    if discarded_leaked > 0:
        problems.append("被丢弃的事件 probe_should_discard 出现了 %d 次——关开关后队列残留仍被落盘"
                        % discarded_leaked)

    stats = {"file": os.path.basename(path), "lines": total,
             "bad": 1 if first_bad_line else 0,
             "marker_ok": "是" if marker_ok else "否",
             "retry_events": retry_events,
             "discard_leak": discarded_leaked}
    return problems, stats


def check(r):
    """对单个场景做判定。判据在本脚本独立重写，不读被测进程自报的「结论」。"""
    problems = []
    d = r["data"]
    scene = r["scene"]

    if not d:
        return ["未产出任何 [SMOKE] 指标（日志 %s），无法判定" % rel(r["log"])]
    if r["killed"]:
        problems.append("进程被强制结束（未自行收尾），详见 %s" % rel(r["log"]))
    if r["code"] != 0:
        problems.append("进程退出码 %s（0=通过）" % r["code"])
    if as_int(d, "运行期异常") != 0:
        problems.append("运行期异常 %s 条：%s" % (d.get("运行期异常"), d.get("首条错误", "?")))
    if r["exc"] > 0 and as_int(d, "运行期异常") <= 0:
        problems.append("日志中检出 %d 条异常，但进程自报 0——异常发生在探针订阅之前" % r["exc"])

    if scene == "SAVE":
        expects = (
            ("强局刷新纪录", "是"), ("弱局不刷纪录", "是"), ("弱局后纪录保持", "是"),
            ("上局成绩已更新", "是"), ("累计统计正确", "是"), ("角色使用计数", "是"),
            ("图鉴去重", "是"), ("落盘读档往返", "是"), ("损坏存档回退", "是"),
        )
        for key, want in expects:
            if d.get(key) != want:
                problems.append("「%s」为 %s（应为 %s）——存档逻辑在该分支出错"
                                % (key, d.get(key), want))
    else:
        expects = (
            ("事件进入队列", "是"), ("正常落盘", "是"), ("失败批次回队列", "是"),
            ("重试后落盘成功", "是"), ("关开关即清队列", "是"),
        )
        for key, want in expects:
            if d.get(key) != want:
                problems.append("「%s」为 %s（应为 %s）" % (key, d.get(key), want))
        fails = as_int(d, "落盘失败次数")
        if fails < 1:
            problems.append("落盘失败次数为 %s（应 ≥1）——未制造出真实的写失败" % d.get("落盘失败次数"))

    return problems


def main():
    ap = argparse.ArgumentParser(description="M20 持久化层无头实测（存档 + 埋点）")
    ap.add_argument("--exe", default=DEFAULT_EXE, help="构建产物路径")
    ap.add_argument("--only", default="", help="只跑指定场景：SAVE 或 TELEMETRY")
    ap.add_argument("--data-dir", default="", help="显式指定 persistentDataPath（默认自动发现）")
    args = ap.parse_args()

    exe = args.exe
    if not os.path.exists(exe):
        print("[错误] 找不到构建产物: %s" % rel(exe))
        print("       请先构建，例如：")
        print("       Unity.exe -batchmode -nographics -quit -projectPath StarPixelRogue \\")
        print("         -executeMethod StarPixel.EditorTools.BuildScript.HeadlessBuild")
        return 2

    data_dir = find_data_dir(args.data_dir or None)
    if data_dir is None:
        print("[错误] 未找到游戏的 persistentDataPath（LocalLow 下无 save.json）")
        return 2

    wanted = [s.strip().upper() for s in args.only.split(",") if s.strip()]
    scene_list = [s for s in SCENARIOS if not wanted or s[0] in wanted]
    if not scene_list:
        print("[错误] --only 未匹配到任何场景（可选：SAVE, TELEMETRY）")
        return 2

    print("=" * 74)
    print("M20 持久化层无头实测 · %d 个场景" % len(scene_list))
    print("  产物   %s" % rel(exe))
    print("  数据   %s" % data_dir)
    print("=" * 74)

    # 跑前备份用户文件（探针会写真实的 save.json / settings.json / telemetry）
    moved = backup_user_files(data_dir)
    print("  已备份 %d 个用户文件（跑后恢复）" % len(moved))

    all_problems = {}
    telemetry_stats = {}
    try:
        for scene, name in scene_list:
            print("\n  [%s] %s" % (scene, name))
            r = run_one(exe, scene)
            if r is None:
                all_problems[scene] = ["进程无法启动"]
                continue

            for k in sorted(r["data"].keys()):
                if k == "场景":
                    continue
                print("      %-18s %s" % (k, r["data"][k]))

            ps = check(r)

            # TELEMETRY 场景的**核心判据**在进程外：用 json.loads 独立校验文件
            if scene == "TELEMETRY":
                fprobs, telemetry_stats = validate_telemetry_file(data_dir)
                ps += fprobs
                for k, v in telemetry_stats.items():
                    print("      %-18s %s" % ("文件·" + k, v))

            if ps:
                all_problems[scene] = ps
                print("      → 未通过")
            else:
                print("      → 通过")
    finally:
        # 无论成败都恢复用户文件；恢复前不再 drop 新文件（第一次恢复已删），
        # 这里保守起见先删探针产物再还原备份。
        restore_user_files(moved)
        print("\n  用户文件已恢复原样")

    print("\n" + "=" * 74)
    if all_problems:
        print("结论：未通过")
        for scene, ps in all_problems.items():
            print("  [%s]" % scene)
            for p in ps:
                print("    - " + p)
        return 1

    print("结论：通过 —— 存档的纪录/累计/图鉴去重/往返/损坏回退全部正确；"
          "埋点文件每一行都是合法 JSON（特殊字符串精确往返），"
          "落盘失败批次回到队列重试成功，关闭遥测开关即丢弃未落盘事件")
    return 0


if __name__ == "__main__":
    sys.exit(main())
