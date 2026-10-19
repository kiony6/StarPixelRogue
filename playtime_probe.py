#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M19 防沉迷无头实测（PlaytimeGuard）

做什么：
    拉起两个无头进程，分别以「青少年模式」和「成人模式」跑一遍时长守护状态机，
    断言限时会真的生效，而且**绕不过去**。

为什么必须专门测它：
    防沉迷是合规功能，同时是一个纯时间驱动的状态机——真实等 1 小时去验证不现实，
    所以它此前完全没有实测覆盖。而"没测过"的合规功能是最危险的：
    只要有一个分支写错，限时就是摆设，且没有任何人会发现。

怎么做到几秒钟跑完一天：
    PlaytimeGuard 的计时判定已收敛到唯一入口 Tick(dt)，线上由 Update 喂真实 deltaTime，
    探针以固定步长（60 秒）喂同一个入口——于是"几秒跑完一天"验证的是**产品代码本身**，
    而不是照抄一份判定逻辑的副本（那只能证明副本对，证明不了产品对）。

两个场景：
    MINOR（青少年模式）：54 分钟世界照常 → 55 分钟弹预警 → **故意不点关闭**继续推进
                        → 到点强制退出 → 回菜单再点「开始游戏」→ 跨天重置
    ADULT（成人模式）  ：45 分钟弹健康提示 → **同样不点关闭**继续推进 → 关掉后再推进一个间隔
                        → 时长落盘/读档往返

判据的区分力（本脚本最要紧的地方）：
    每条断言都指向"修复前必然失败"的可观测量：
      1) 不关弹窗累计秒数 ≥ 3600 —— 旧实现在弹窗挂起时停止计时，永远到不了上限，
         玩家只要把预警晾着就能无限续玩；
      2) 二次开局被拦 == 是 —— 旧实现每天只强制退出一次，回菜单再点开始照样能玩；
      3) 挂起期间增量 ≈ 600 —— 成人模式的同一个冻结缺陷；
      4) 强制退出次数 == 1 且世界已拆 —— 只断言"没崩溃"的话，
         一个压根不执行限时的实现也能全绿。
    反过来，54 分钟时不能有任何干预、跨天后必须能重新开局：
    双向都成立，"限时生效"才不是靠"干脆永远不让玩"蒙出来的。

退出码：
    0 = 全部场景通过；1 = 有场景未通过；2 = 前置条件不满足
"""

import argparse
import os
import re
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")

SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")

# 无头环境必然出现的渲染噪声，不算运行时异常
NOISE = ("shader", "gpu", "memorysetup", "unitymemory", "curl error", "unitlets",
         "screen position out of view frustum")

# 青少年模式每日上限（秒）——与 PlaytimeGuard.MinorDailyLimitSeconds 一致。
# 刻意在本脚本里独立写死而不是读进程自报：期望值必须由被测之外的一方给出。
MINOR_LIMIT = 60 * 60

SCENARIOS = (
    ("MINOR", "青少年模式（限时 / 强制退出 / 二次开局 / 跨天）"),
    ("ADULT", "成人模式（健康提示 / 计时不被弹窗冻结 / 时长持久化）"),
)


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


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


def as_float(d, key, default=-1.0):
    try:
        return float(str(d.get(key, default)).strip())
    except (TypeError, ValueError):
        return default


def log_too_big(path, limit_mb):
    try:
        return os.path.getsize(path) > limit_mb * 1024 * 1024
    except OSError:
        return False


def has_cjk(s):
    """是否含中日韩字符或全角标点——英文界面出现它就说明有中文漏出来了。"""
    for ch in s or "":
        if "\u4e00" <= ch <= "\u9fff":
            return True
        if "\u3000" <= ch <= "\u303f":
            return True
        if "\uff00" <= ch <= "\uffef":
            return True
    return False


def run_one(exe, scene, limit_mb, timeout=90):
    log = os.path.join(ROOT, "playtime_%s.log" % scene)
    if os.path.exists(log):
        os.remove(log)

    try:
        proc = subprocess.Popen(
            [exe, "-batchmode", "-nographics", "-playtimeProbe",
             "-playtimeScene", scene, "-logFile", log],
            cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as e:
        print("        [错误] 无法启动进程: %s" % e)
        return None

    killed = False
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            break
        if log_too_big(log, limit_mb):
            print("        [熔断] 日志超过 %dMB，强制结束" % limit_mb)
            proc.kill()
            killed = True
            break
        time.sleep(0.5)
    else:
        print("        [警告] 超时未退出，强制结束")
        proc.kill()
        killed = True

    return {
        "scene": scene,
        "code": proc.returncode,
        "killed": killed,
        "data": parse_smoke(log),
        "log": log,
        "exc": count_exceptions(log),
    }


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

    # —— 通用：异常必须为零 ——
    if as_int(d, "运行期异常") != 0:
        problems.append("运行期异常 %s 条：%s"
                        % (d.get("运行期异常"), d.get("首条错误", "?")))
    if r["exc"] > 0 and as_int(d, "运行期异常") <= 0:
        problems.append("日志中检出 %d 条异常，但进程自报 0——异常发生在探针订阅之前" % r["exc"])

    if scene == "MINOR":
        if d.get("开局正常") != "是":
            problems.append("开局未成功（%s）——对局世界没建起来，后续断言无意义"
                            % d.get("开局正常"))
        if d.get("54分钟世界存活") != "是":
            problems.append("54 分钟时世界已不在（%s）——未到上限就干预了，限时过于激进"
                            % d.get("54分钟世界存活"))
        if as_int(d, "54分钟预警次数") != 0:
            problems.append("54 分钟就弹了 %s 次预警（应为 0）——预警提前量不对"
                            % d.get("54分钟预警次数"))
        if as_int(d, "55分钟预警次数") != 1:
            problems.append("55 分钟预警次数为 %s（应为 1）——到点前 5 分钟的预警没弹或弹了多次"
                            % d.get("55分钟预警次数"))
        if d.get("55分钟弹窗挂起") != "是":
            problems.append("55 分钟时弹窗未挂起（%s）——无法验证「不点关闭也照常计时」"
                            % d.get("55分钟弹窗挂起"))

        # 核心判据 1：不点关闭也必须照常累计到上限
        sec = as_float(d, "不关弹窗累计秒数")
        if sec < MINOR_LIMIT:
            problems.append("不关弹窗时累计仅 %ss（上限 %ss）——弹窗挂起期间时长被冻结，"
                            "玩家把预警晾着就能无限续玩" % (d.get("不关弹窗累计秒数"), MINOR_LIMIT))

        # 核心判据 2：到点必须强制退出且世界真的拆了
        if as_int(d, "强制退出次数") != 1:
            problems.append("强制退出次数为 %s（应为 1）——到点未踢人或被反复踢"
                            % d.get("强制退出次数"))
        if d.get("强退后对局已停") != "是":
            problems.append("强制退出后对局标记仍为运行中（%s）——世界没真的停下来"
                            % d.get("强退后对局已停"))
        if d.get("强退后世界已拆") != "是":
            problems.append("强制退出后世界未拆除（%s）——只是弹了个提示，对局还在继续"
                            % d.get("强退后世界已拆"))

        # 核心判据 3：二次开局必须被拦下
        if d.get("二次开局被拦") != "是":
            problems.append("二次开局未被拦下（拦截计数 %s）——回主菜单再点「开始游戏」"
                            "就能继续玩，限时被完整绕过" % d.get("开局拦截计数"))
        if as_int(d, "开局拦截计数") < 1:
            problems.append("开局拦截计数为 %s——入口拦截根本没走到" % d.get("开局拦截计数"))

        # 反向判据：跨天必须解禁，不能"干脆永远不让玩"
        if as_float(d, "跨天后累计秒数") >= 5:
            problems.append("跨天后累计仍为 %ss（应归零）——跨天重置未生效"
                            % d.get("跨天后累计秒数"))
        if d.get("跨天后可重开") != "是":
            problems.append("跨天后无法重新开局（%s）——限时没有随新的一天解除"
                            % d.get("跨天后可重开"))

        # 强制退出提示的本地化（合规文案，英文玩家看不懂等于没提示）
        zh, en = d.get("中文强退提示", ""), d.get("英文强退提示", "")
        if not zh or zh.startswith("playtime."):
            problems.append("中文强退提示是「%s」——空或本地化键未翻译" % zh)
        if not en or en.startswith("playtime."):
            problems.append("英文强退提示是「%s」——空或本地化键未翻译" % en)
        if zh and en and zh == en:
            problems.append("中英强退提示完全相同（%s）——本地化未生效" % zh)
        if has_cjk(en):
            problems.append("英文强退提示残留中文「%s」" % en)

    else:  # ADULT
        if as_int(d, "45分钟提示次数") != 1:
            problems.append("45 分钟健康提示次数为 %s（应为 1）" % d.get("45分钟提示次数"))
        if d.get("提示弹窗挂起") != "是":
            problems.append("健康提示弹窗未挂起（%s）——无法验证「提示不冻结计时」"
                            % d.get("提示弹窗挂起"))

        # 核心判据：提示挂起期间时长必须继续涨
        delta = as_float(d, "挂起期间增量")
        if delta < 590:
            problems.append("提示挂起期间时长增量为 %ss（应约 600s）——弹窗冻结了计时，"
                            "提示间隔会失真" % d.get("挂起期间增量"))

        if as_int(d, "第二次提示次数") < 2:
            problems.append("第二个间隔后提示次数为 %s（应 ≥2）——健康提示不 repeat"
                            % d.get("第二次提示次数"))

        # 时长持久化：读档不能把当天已累计的时长读没
        before, after = as_float(d, "读档前秒数"), as_float(d, "读档后秒数")
        if before <= 0:
            problems.append("读档前秒数为 %s——成人场景未正常累计时长" % d.get("读档前秒数"))
        if abs(after - before) >= 2:
            problems.append("落盘/读档往返后秒数由 %s 变为 %s（差 %s）——时长被读没或被误判跨天"
                            % (d.get("读档前秒数"), d.get("读档后秒数"), round(after - before, 1)))

        if d.get("未走青少年限时", "是") != "是":
            problems.append("成人模式触发了青少年强制退出分支（%s）——限时误伤了成年玩家"
                            % d.get("未走青少年限时"))

    return problems


def main():
    ap = argparse.ArgumentParser(description="M19 防沉迷无头实测（青少年限时 / 成人健康提示）")
    ap.add_argument("--exe", default=DEFAULT_EXE, help="构建产物路径")
    ap.add_argument("--only", default="", help="只跑指定场景：MINOR 或 ADULT")
    ap.add_argument("--log-limit-mb", type=int, default=200, help="单场景日志体积上限（MB）")
    args = ap.parse_args()

    exe = args.exe
    if not os.path.exists(exe):
        print("[错误] 找不到构建产物: %s" % rel(exe))
        print("       请先构建，例如：")
        print("       Unity.exe -batchmode -nographics -quit -projectPath StarPixelRogue \\")
        print("         -executeMethod StarPixel.EditorTools.BuildScript.HeadlessBuild")
        return 2

    wanted = [s.strip().upper() for s in args.only.split(",") if s.strip()]
    scene_list = [s for s in SCENARIOS if not wanted or s[0] in wanted]
    if not scene_list:
        print("[错误] --only 未匹配到任何场景（可选：MINOR, ADULT）")
        return 2

    print("=" * 74)
    print("M19 防沉迷无头实测 · %d 个场景（虚拟时长由 60s 步长推进，无需真实等待）" % len(scene_list))
    print("  产物   %s" % rel(exe))
    print("=" * 74)

    all_problems = {}
    for scene, name in scene_list:
        print("\n  [%s] %s" % (scene, name))
        r = run_one(exe, scene, args.log_limit_mb)
        if r is None:
            all_problems[scene] = ["进程无法启动"]
            continue

        for k in sorted(r["data"].keys()):
            if k in ("场景",):
                continue
            print("      %-18s %s" % (k, r["data"][k]))

        ps = check(r)
        if ps:
            all_problems[scene] = ps
            print("      → 未通过")
        else:
            print("      → 通过")

    print("\n" + "=" * 74)
    if all_problems:
        print("结论：未通过")
        for scene, ps in all_problems.items():
            print("  [%s]" % scene)
            for p in ps:
                print("    - " + p)
        return 1

    print("结论：通过 —— 青少年模式到点预警并强制退出（晾着弹窗也绕不过），"
          "二次开局被入口拦截，跨天自动解禁；成人模式健康提示按间隔弹出且不再冻结计时")
    return 0


if __name__ == "__main__":
    sys.exit(main())
