#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M31 升级选卡生效链路无头实测

做什么：
    拉起 5 个无头进程，把「roguelite 的成长核心——选卡之后到底有没有真的变强」钉死：
      BASE    地基自检：读得到玩家与武器、初始恰好一把火系 Bolt、选卡池 3 张不重复
      STAT    属性强化精确生效：一次是单次倍率，两次是平方；生命上限同步回满
      WEAPON  武器等级强化：升一级 +1，连升 10 次必须停在 MaxLevel，满级再升不再涨
      UNLOCK  解锁新武器：武器数 +1、该武器的解锁卡从池里消失、重复解锁改为升级
      COMBO   组合技广播：凑齐 tag 必须广播一次，且恰好一次

为什么必须测：
    roguelite 的全部成长都压在「选卡」这一个动作上，而这条链路此前从未被断言过——
    M29 只证明了面板弹得出来且只弹一次，选完之后有没有生效没有任何判据。
    失效方式有两类，且都不报错：① 选了没生效（数值没动）；
    ② 生效了但同一条效果被两条路径实现、其中一条漏了副作用——
    数值其实是对的，玩家却收不到提示也听不到音效，这种最难发现。

判据的区分力（本脚本最要紧的地方）：
    1) STAT 用「一次」与「两次（平方）」两条，而不是只测「变了」：
       只测「变了」的话，漏应用与「应用了三次」都表现为 != 基线，分不开。
    2) WEAPON 用「连升 10 次必须停在 MaxLevel」钉封顶：
       越界的后果要玩到后期才炸，只测「升一级 +1」前期一切正常、抓不到。
    3) COMBO 订阅的是**事件**而非字段：bonus 由 Recompute 写入（数值层面是对的），
       而提示/音效走 OnComboActivated 广播——只看数值会把「广播漏了」完全漏掉。
    4) UNLOCK 除了「加进来」还判「该武器的解锁卡从池里消失」：
       否则玩家会抽到一张选了什么都不会发生的卡。
    5) COMBO 另判「恰好一次」：重复广播会让玩家以为激活了两个组合。

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
    ("BASE", "地基自检：读得到玩家、初始一把 Bolt、选卡池 3 张不重复"),
    ("STAT", "属性强化：一次是单次倍率、两次是平方、生命上限同步回满"),
    ("WEAPON", "武器等级：升一级 +1、连升 10 次停在 MaxLevel、满级不再涨"),
    ("UNLOCK", "解锁武器：进武器池、解锁卡消失、重复解锁转为升级"),
    ("COMBO", "组合技：凑齐 fire+storm 必须广播恰好一次"),
)

BACKUP_SUFFIX = ".m31bak"

TIMEOUT = {"BASE": 90, "STAT": 90, "WEAPON": 90, "UNLOCK": 90, "COMBO": 90}


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
    args = [exe, "-batchmode", "-nographics", "-upgradeProbe", "-upgradeScene", scene]
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


def _eq(s):
    return lambda v: v == s


def _one(v):
    return v == "1"


def _zero(v):
    return v == "0"


def _at_least(n):
    def f(v):
        return v.isdigit() and int(v) >= n
    return f


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 地基：证明读得到玩家、初始武器正确、选卡池不重复
        "BASE": [
            ("初始武器数", _one),
            ("选卡张数", _eq("3")),
            ("重复卡数", _zero),
            ("读得到玩家", _yes),
            ("初始一把Bolt", _yes),
            ("选卡不重复", _yes),
            ("断点通过", _eq("3/3")),
        ],
        "STAT": [
            ("一次精确", _yes),
            ("两次是平方", _yes),
            ("上限同步回满", _yes),
            ("断点通过", _eq("3/3")),
        ],
        "WEAPON": [
            ("升级生效", _yes),
            ("封顶不越界", _yes),
            ("满级不再涨", _yes),
            ("断点通过", _eq("3/3")),
        ],
        "UNLOCK": [
            ("新武器进池", _yes),
            ("解锁卡消失", _yes),
            ("重复解锁转升级", _yes),
            ("解锁后武器数", _eq("2")),
            ("残留解锁卡", _zero),
            ("断点通过", _eq("3/3")),
        ],
        "COMBO": [
            ("基线干净", _yes),
            ("凑齐会广播", _yes),
            ("恰好一次", _yes),
            ("基线广播数", _zero),
            ("凑齐后广播数", _one),
            ("断点通过", _eq("3/3")),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        if callable(expect):
            good = expect(got)
            shown = _label(expect)
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


def _label(f):
    """把判据函数还原成人能读的期望描述。"""
    if f is _yes:
        return "是"
    if f is _one:
        return "1"
    if f is _zero:
        return "0"
    return "达标"


def main():
    ap = argparse.ArgumentParser(description="M31 升级选卡生效链路无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 COMBO")
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
    print("M31 升级选卡生效链路无头实测 · %d 个场景" % len(scenes))
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
                print("      %s %-14s 期望 %-6s 实际 %s" % (" " if good else "x", k, e, g))

            # 实测数值行全部复读：失败时能直接看出是数值不对还是广播不对
            for extra in ("初始武器数", "攻击倍率基线", "选卡张数", "重复卡数",
                          "倍率基线", "选一次后", "选两次后", "生命上限", "当前血",
                          "等级基线", "升一级后", "连升10次后", "满级再升", "等级上限",
                          "解锁前武器数", "解锁后武器数", "残留解锁卡", "重复解锁后等级",
                          "基线广播数", "凑齐后广播数", "广播内容"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-14s %s" % (extra, res[extra]))
            if res.get("失败项", "无") != "无":
                print("        %-14s %s" % ("失败项", res["失败项"]))

            if res.get("运行期异常", "0") != "0":
                print("      x %-14s 实际 %s" % ("运行期异常", res["运行期异常"]))
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
    print("结论：%s" % ("通过 —— 选卡精确生效 / 武器升级封顶不越界 / 解锁进池且解锁卡消失 / "
                        "组合技激活广播恰好一次"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
