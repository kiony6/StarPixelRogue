#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M21 合规门无头实测（ComplianceGate + 同意链路）

做什么：
    拉起 6 个无头进程，逐一验证合规门（ComplianceGate）这条**从未被测过**的链路：
      FIRST          首启：门必须弹出 → 点「同意并继续」→ 同意态落盘 → 主菜单解锁
      AGREED         已同意当前版本：门不该重复弹
      BUMP           已同意**旧**版本（法律文本已升级）：门必须重新弹出
      DECLINE        点「不同意并退出」：必须走到退出，且同意态不得写入
      CONSENT_CLOUD  同意前云存档零请求；同意后必须补一次拉取
      L10N           英文界面下合规门文案无裸键、无中文残留

为什么必须测：这是最后一个零实测的合规模块，而合规功能失效是**静默**的——
    门没弹出来，游戏一切正常，只是"用户其实从没同意过协议"。
    其中 BUMP（协议更新后重新征得同意）最关键：文本换了而用户没重新确认，
    等于拿旧同意去覆盖新条款，正是监管最关心的点。

判据的区分力（本脚本最要紧的地方）：
    1) 同意态是否落盘，由探针调真实 SettingsManager.Load() 读回比对，不查内存字段——
       只查内存的话，一个根本不保存的实现也能全绿。
    2) "门弹了没有"(Shown) 与 "点了没有"(AgreeCount) 分开断言，
       才能区分「门压根没弹」和「门弹了但按钮点不动」。
    3) CONSENT_CLOUD 用**拉取次数**而非笼统的"联网了没"判定：
       只断言请求数 >0 的话，一次都不拉、直接上传覆盖云端的实现照样能过——
       那正是本轮要抓的缺陷（本地旧档静默覆盖云端新档）。
    4) DECLINE 用可替换的退出钩子断言"确实走到退出"，而不是让进程真退出——
       真退出了剩下的断言结果就再也打印不出来了。

用户数据保护：跑前备份 settings.json / save.json，跑后原样恢复。

退出码：0 = 全部通过；1 = 有场景未通过；2 = 前置条件不满足
"""

import argparse
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")

SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")

# 无头环境必然出现的渲染噪声，不算运行时异常
NOISE = ("shader", "gpu", "memorysetup", "unitymemory", "curl error", "unitlets",
         "screen position out of view frustum")

SCENARIOS = (
    ("FIRST", "首启：弹门 / 同意落盘 / 主菜单解锁 / 三份法律文本齐全"),
    ("AGREED", "已同意当前版本：不重复弹门"),
    ("BUMP", "协议版本升级：必须重新征得同意"),
    ("DECLINE", "点不同意：走到退出且同意态不写入"),
    ("CONSENT_CLOUD", "同意前零请求 / 同意后补一次拉取（防本地档覆盖云端）"),
    ("L10N", "英文界面：无裸键、无中文残留"),
)

BACKUP_SUFFIX = ".m21bak"

# CONSENT_CLOUD 需要一个"确定存在但连不上"的后端，才能在不联网的前提下观察请求与拉取
CLOUD_URL = "https://127.0.0.1:9/save"


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def find_data_dir(explicit=None):
    """定位 persistentDataPath（company/product 目录）。

    依据『目录里有 save.json 或 settings.json』从 LocalLow 逐层找，
    避免把公司名/产品名硬编码进脚本（改名即失效）。
    """
    if explicit:
        return explicit if os.path.isdir(explicit) else None
    low = os.path.join(os.environ.get("USERPROFILE", os.path.expanduser("~")),
                       "AppData", "LocalLow")
    if not os.path.isdir(low):
        return None
    best = None
    for company in sorted(os.listdir(low)):
        cpath = os.path.join(low, company)
        if not os.path.isdir(cpath):
            continue
        for product in sorted(os.listdir(cpath)):
            ppath = os.path.join(cpath, product)
            if not os.path.isdir(ppath):
                continue
            if (os.path.exists(os.path.join(ppath, "save.json"))
                    or os.path.exists(os.path.join(ppath, "settings.json"))):
                return ppath
            if best is None:
                best = ppath
    return best


def backup(data_dir, names):
    made = []
    for n in names:
        p = os.path.join(data_dir, n)
        if os.path.exists(p):
            b = p + BACKUP_SUFFIX
            if os.path.exists(b):
                os.remove(b)
            os.rename(p, b)
            made.append(n)
    return made


def restore(data_dir, names):
    for n in names:
        p = os.path.join(data_dir, n)
        b = p + BACKUP_SUFFIX
        if os.path.exists(p):
            os.remove(p)          # 探针写过的测试档，直接丢弃
        if os.path.exists(b):
            os.rename(b, p)


def parse_smoke(log):
    out = {}
    for line in log.splitlines():
        m = SMOKE_RE.search(line)
        if m:
            out[m.group(1).strip()] = m.group(2).strip()
    return out


def count_exceptions(log):
    n = 0
    for line in log.splitlines():
        low = line.lower()
        if "exception" not in low:
            continue
        if any(k in low for k in NOISE):
            continue
        n += 1
    return n


def run_scenario(exe, scene, lang, data_dir, seconds):
    args = [exe, "-batchmode", "-nographics", "-complianceProbe",
            "-complianceScene", scene, "-complianceLang", lang]
    if scene == "CONSENT_CLOUD":
        # 已配置但不可达：既让"同意后确实会发请求"可被观测，又不真的联网
        args += ["-cloudSaveUrl", CLOUD_URL]
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=seconds + 60)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or b"").decode("utf-8", "replace") if isinstance(e.stdout, bytes) else (e.stdout or "")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _at_least(n):
    """期望写成谓词：用于「次数 ≥ n」这类不宜写成等值比较的判据。"""
    return lambda v: v.isdigit() and int(v) >= n


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    # 期望可以是字面值，也可以是谓词（后者用于"次数 ≥1"这类不宜等值比较的判据）
    spec = {
        "FIRST": [
            ("门已弹出", "是"), ("点同意", "是"), ("首启弹门", "是"),
            ("法律文本齐全", "是"), ("同意态落盘", "是"),
            ("同意前菜单可见", "否"), ("同意后菜单可见", "是"),
        ],
        "AGREED": [
            ("门已弹出", "否"), ("已同意不重复弹门", "是"), ("同意后菜单可见", "是"),
        ],
        "BUMP": [
            ("门已弹出", "是"), ("协议升级重新弹门", "是"), ("点同意", "是"),
            ("同意态落盘", "是"), ("同意后菜单可见", "是"),
        ],
        "DECLINE": [
            ("门已弹出", "是"), ("点不同意", "是"), ("走到退出", "是"),
            ("同意态未写入", "是"), ("主菜单未解锁", "是"),
        ],
        # 关键：用**拉取次数**判定，而非笼统的"发过请求"——
        # 只断言请求数 >0 的话，一次都不拉、直接上传覆盖云端的实现照样能过。
        "CONSENT_CLOUD": [
            ("门已弹出", "是"), ("同意前请求数", "0"), ("同意后拉取次数", _at_least(1)),
        ],
        "L10N": [
            ("门已弹出", "是"), ("裸键", "无"), ("中文残留", "无"),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got) if callable(expect) else (got == expect)
        rows.append((key, "判据" if callable(expect) else expect, got, good))
        ok = ok and good

    # 进程自述的结论也纳入（探针内部还有若干未逐项上报的判据）
    if res.get("结论") != "通过":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M21 合规门无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 FIRST")
    ap.add_argument("--seconds", type=int, default=25)
    args = ap.parse_args()

    exe = args.exe
    if not os.path.exists(exe):
        print("产物不存在: %s" % rel(exe))
        return 2

    data_dir = find_data_dir(args.data_dir)
    if not data_dir:
        print("找不到 persistentDataPath，用 --data-dir 指定")
        return 2

    scenes = SCENARIOS
    if args.only:
        scenes = [s for s in SCENARIOS if s[0] == args.only]
        if not scenes:
            print("未知场景: %s" % args.only)
            return 2

    print("=" * 74)
    print("M21 合规门无头实测 · %d 个场景" % len(scenes))
    print("  产物   %s" % rel(exe))
    print("  数据   %s" % data_dir)
    print("=" * 74)

    backed = backup(data_dir, ["settings.json", "save.json"])
    if backed:
        print("  已备份 %d 个用户文件（跑后恢复）\n" % len(backed))

    all_ok = True
    try:
        for scene, desc in scenes:
            # 每个场景前清掉上一场景落盘的文件。
            # 不做这一步，后一个场景的 SettingsManager.Load() 会读到前一个场景留下的值：
            # 曾出现 DECLINE 的「同意态未写入」因为 BUMP 写了更高版本号而**碰巧成立**——
            # 那是假通过，判据必须建立在已知初始态上。
            for n in ("settings.json", "save.json"):
                p = os.path.join(data_dir, n)
                if os.path.exists(p):
                    os.remove(p)

            lang = "en-US" if scene == "L10N" else "zh-CN"
            print("  [%s] %s" % (scene, desc))
            log = run_scenario(exe, scene, lang, data_dir, args.seconds)
            res = parse_smoke(log)
            if not res:
                print("      → 不通过（未捕获 [SMOKE] 输出）")
                print("      " + "\n      ".join(log.splitlines()[-5:]))
                all_ok = False
                continue

            # 运行期异常由脚本独立计数，不采信进程自述
            exc = count_exceptions(log)
            if exc:
                res["运行期异常"] = str(exc)

            ok, rows = check(scene, res)
            for (k, e, g, good) in rows:
                print("      %s %-16s 期望 %-8s 实际 %s" % (" " if good else "x", k, e, g))
            if res.get("运行期异常", "0") != "0":
                print("      x %-16s 实际 %s" % ("运行期异常", res["运行期异常"]))
                ok = False
            print("      → %s\n" % ("通过" if ok else "不通过"))
            all_ok = all_ok and ok
    finally:
        restore(data_dir, backed)
        print("  用户文件已恢复原样")

    print("=" * 74)
    print("结论：%s" % ("通过 —— 合规门首启弹出 / 已同意放行 / 协议升级重新征得同意 / "
                        "不同意即退出 / 同意态落盘 / 同意后补拉取 全部正确"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
