#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M29 静态事件总线订阅对称性无头实测

做什么：
    拉起 3 个无头进程，把「EventBus 订阅忘了退订」钉死：
      BASE    反射自检：必须真读到 EventBus 的委托链，否则后面全是假绿
      RESTART 重开三局后，订阅总数不许增长、链上不许出现已销毁对象
      LEVELUP 行为后果：重开三局后只升一级，升级面板必须恰好弹一次

为什么必须测：
    EventBus 是静态的，订阅者却全是有生命周期的 MonoBehaviour。
    这与 M24/M26/M27 是同一类「跨局残留」，但形态完全不同：
    前几轮漏的是 GameObject（场景里数得出来），这一轮漏的是**委托引用**——
    场景里什么都多不出来，只能靠反射才看得到，而且已销毁对象上的方法
    大多只写写字段、不抛异常，日志一片干净。
    玩家侧的后果是真实的：重开三局后升一级，会连弹三次升级面板。

判据的区分力（本脚本最要紧的地方）：
    1) BASE 先证明「读得到」再看数字。反射取不到 backing field 时链长一律是 0，
       「没有泄漏」和「根本没读着」长得一模一样——这条是全套判据的地基。
    2) RESTART 同时看**总数**与**死订阅数**：总数不增长只说明没有重复订阅，
       抓不到「订阅了但对象已销毁」；死订阅数补的是这一半。
    3) 死订阅分两种形态分别识别：方法组委托（Target 就是订阅者本身）与
       匿名 lambda（Target 是闭包，捕获对象藏在闭包字段里）。
       只认前者会对 lambda 完全失明——LevelUpManager 正是 lambda 形态。
    4) LEVELUP 把「至少弹一次」与「最多弹一次」拆成两条：
       前者防「压根没弹出来」的假绿，后者才真正在抓重复订阅。

用户数据保护：跑前备份 save.json / settings.json，跑后原样恢复；探针产物全部清除。

退出码：0 = 全部通过；1 = 有场景未通过；2 = 前置条件不满足
"""

import argparse
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
    ("BASE", "反射自检：必须真读到 EventBus 的委托链"),
    ("RESTART", "重开三局：订阅总数不许增长、链上不许有已销毁对象"),
    ("LEVELUP", "行为后果：重开三局后升一级，面板必须恰好弹一次"),
)

BACKUP_SUFFIX = ".m29bak"

TIMEOUT = {"BASE": 90, "RESTART": 150, "LEVELUP": 150}


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


def clear_artifacts(data_dir):
    for n in ("save.json", "settings.json"):
        p = os.path.join(data_dir, n)
        if os.path.exists(p):
            os.remove(p)


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


def run_scenario(exe, scene):
    args = [exe, "-batchmode", "-nographics", "-subProbe", "-subScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _yes(v):
    return v == "是"


def _zero(v):
    """必须为 0 的计数（泄漏类判据）。"""
    return v.isdigit() and int(v) == 0


def _at_least(n):
    def f(v):
        return v.isdigit() and int(v) >= n
    return f


def _full(v):
    return v == "18/18"


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 地基：证明反射真的读到了委托链，且建世界后链上确实有订阅
        "BASE": [
            ("事件字段", _full),
            ("订阅总数", _at_least(1)),
            ("断点通过", "2/2"),
        ],
        "RESTART": [
            # 前置：首局确实有订阅，否则「不增长」只是两边都是 0
            ("订阅总数基线", _at_least(1)),
            ("净增长", _zero),
            ("死订阅数", _zero),
            ("确有订阅", _yes),
            ("总数不增长", _yes),
            ("升级不重复", _yes),
            ("链上无死对象", _yes),
            ("断点通过", "4/4"),
        ],
        # 前两条是"判据本身没失灵"的护栏：
        # 弹出面板数 ≥1 证明升级链路真的走到了建面板，
        # 世界就绪证明三局之后管理器与玩家都还在。
        "LEVELUP": [
            ("弹出面板数", _at_least(1)),
            ("孤儿面板数", _zero),
            ("世界就绪", _yes),
            ("至少弹一次", _yes),
            ("最多弹一次", _yes),
            ("无孤儿面板", _yes),
            ("事件未抛异常", _yes),
            ("断点通过", "5/5"),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        if callable(expect):
            good = expect(got)
            if expect is _yes:
                shown = "是"
            elif expect is _zero:
                shown = "0"
            elif expect is _full:
                shown = "18/18"
            else:
                shown = "≥1"
        else:
            good = (got == expect)
            shown = expect
        rows.append((key, shown, got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M29 事件总线订阅对称性无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 RESTART")
    ap.add_argument("--keep", action="store_true", help="保留探针产物（排查用）")
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
    print("M29 事件总线订阅对称性无头实测 · %d 个场景" % len(scenes))
    print("  产物   %s" % rel(exe))
    print("  数据   %s" % data_dir)
    print("=" * 74)

    backed = backup(data_dir, ["save.json", "settings.json"])
    if backed:
        print("  已备份 %d 个用户文件（跑后恢复）\n" % len(backed))

    all_ok = True
    try:
        for scene, desc in scenes:
            clear_artifacts(data_dir)
            print("  [%s] %s" % (scene, desc))
            log = run_scenario(exe, scene)
            res = parse_smoke(log)
            if not res:
                print("      → 不通过（未捕获 [SMOKE] 输出）")
                print("      " + "\n      ".join(log.splitlines()[-8:]))
                all_ok = False
                continue

            exc = count_exceptions(log)
            if exc:
                res["运行期异常"] = str(exc)

            ok, rows = check(scene, res)
            for (k, e, g, good) in rows:
                print("      %s %-16s 期望 %-8s 实际 %s" % (" " if good else "x", k, e, g))

            # 实测数值行全部复读：失败时能直接指出是哪一个事件在攒订阅
            for extra in ("各事件链长", "订阅总数基线", "重开后总数", "净增长",
                          "OnLevelUp基线", "OnLevelUp之后", "死订阅构成",
                          "弹出面板数", "孤儿面板数", "升级事件异常"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-14s %s" % (extra, res[extra]))
            if res.get("失败项", "无") != "无":
                print("        %-14s %s" % ("失败项", res["失败项"]))

            if res.get("运行期异常", "0") != "0":
                print("      x %-16s 实际 %s" % ("运行期异常", res["运行期异常"]))
                ok = False
            print("      → %s\n" % ("通过" if ok else "不通过"))
            all_ok = all_ok and ok
    finally:
        if args.keep:
            print("  已保留探针产物（--keep）")
        else:
            clear_artifacts(data_dir)
        restore(data_dir, backed)
        print("  用户文件已恢复原样")

    print("=" * 74)
    print("结论：%s" % ("通过 —— 重开三局后：订阅总数不增长 / 链上没有已销毁对象 / "
                        "升一级只弹一次面板"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
