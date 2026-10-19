#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M28 新建对象归属无头实测

做什么：
    拉起 3 个无头进程，把「重开一局后，上一局的东西一个都不许留下来」钉死：
      CHAIN 链闪电的电弧段/闪光（与 M26 警示圈同形）：暂停冻结后重开，一个都不许留下
      BOSS  上一局还活着的 Boss：不许被带进新世界（它会继续追新玩家、继续开火）
      ROOT  通用兜底：重开一局后，场景根对象集合不得比首次建世界多出任何一个

为什么必须测：
    「new GameObject 出来的东西没人负责回收」连续三轮踩到，每次都靠人肉复查才找到：
      M24 —— 池化出来的宝石/敌人挂在场景根，TeardownWorld 只销毁管理器，整批漏到新世界；
      M26 —— Boss 前摇警示圈 / Nova 爆发环同样建在场景根，漏到新世界的开局画面里；
      M27 —— Track(组件) 只摘组件、留下 GameObject 本体与子物体，每次重开遗留一整套空壳。
    本轮把它做成 audit_project.py 的第 7 项静态检查（audit_project.py --selftest 可自检），
    而这里补的是另一半证明：静态检查说"有归属"不够，必须证明"没归属时真的会漏过去"。

判据的区分力（本脚本最要紧的地方）：
    1) 每个「不许残留」都配「确实造出来了」的前置断言：
       否则"残留 0"可能只是压根没生成（M24/M26/M27 同一个前置断言套路）。
    2) CHAIN 必须**先把时间冻住**再造电弧：真实场景就是选卡/结算面板的 timeScale=0
       把自毁计时冻住、重开后才播完。不冻结的话电弧 0.16s 就自己消失了，
       判据会"假绿"——这是本场景最容易写错的地方。
    3) ROOT 比对的是**重名对象的个数差**（multiset 差），不是总数：
       新世界本来就会重建一批同名对象，只看总数会被"建了又漏了"互相抵消。
    4) 存活构成一律列出对象名："残留 3 个"必须能指出是哪 3 个，否则无法定位。

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
    ("CHAIN", "链闪电电弧段/闪光：暂停冻结后重开，一个都不许留下"),
    ("BOSS", "上一局还活着的 Boss 不许被带进新世界"),
    ("ROOT", "通用兜底：重开后场景根不得比首次建世界多出任何对象"),
)

BACKUP_SUFFIX = ".m28bak"

TIMEOUT = {"CHAIN": 120, "BOSS": 120, "ROOT": 150}


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
    args = [exe, "-batchmode", "-nographics", "-orphanProbe", "-orphanScene", scene]
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
    """必须为 0 的计数（残留/泄漏类判据）。"""
    return v.isdigit() and int(v) == 0


def _at_least(n):
    def f(v):
        return v.isdigit() and int(v) >= n
    return f


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "CHAIN": [
            # 先证明"确实造出了"，否则"残留 0"可能只是压根没生成
            ("重开前个数", _at_least(5)),
            ("上一局存活", _zero),
            ("新世界存量", _zero),
            ("已造出电弧", _yes),
            ("上一局清零", _yes),
            ("新世界干净", _yes),
            ("断点通过", "3/3"),
        ],
        "BOSS": [
            ("重开前个数", _at_least(1)),
            ("上一局存活", _zero),
            ("新世界存量", _zero),
            ("已造出Boss", _yes),
            ("上一局清零", _yes),
            ("断点通过", "2/2"),
        ],
        "ROOT": [
            ("首次根对象", _at_least(1)),
            ("多出的对象", _zero),
            ("已建成世界", _yes),
            ("无新增残留", _yes),
            ("断点通过", "2/2"),
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
            elif key == "重开前个数":
                shown = "≥5"      # 3 段 + 3 闪
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
    ap = argparse.ArgumentParser(description="M28 新建对象归属无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 CHAIN")
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
    print("M28 新建对象归属无头实测 · %d 个场景" % len(scenes))
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

            # 实测数值行全部复读：它们不是独立判据，
            # 但失败时能直接指出漏下来的到底是哪一个对象
            for extra in ("重开前个数", "上一局存活", "存活构成", "新世界存量",
                          "首次根对象", "重开后根对象", "多出的对象", "多出构成"):
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
    print("结论：%s" % ("通过 —— 重开一局后：链闪电全部清零 / 上一局的 Boss 未被带入新世界 / "
                        "场景根没有多出任何对象"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
