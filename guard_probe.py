#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M32 联机本地守卫对称性无头实测

做什么：
    拉起 5 个无头进程，把「只有本地玩家该做的事」钉死：
      BASE    地基自检：isLocal 字段可读写、单机初始为 true、翻转后已守卫的路径真的不广播
      REMOTE  远程玩家不得广播：解锁 / 升级 / 重复解锁三条路都必须零广播
      LOCAL   本地必须广播：同样三条路在本地都必须恰好一次（防修过头把本地也堵死）
      STATE   远程玩家不得污染本地全局状态：ComboManager 单例的加成不许被远程玩家改写
      RESTORE 恢复与无副作用：翻转后要恢复、恢复后本地广播照常、全程零异常

为什么必须测：
    PlayerController 上有一批「只有本地玩家该做」的动作——往 HUD 广播、往全局单体写本地加成。
    它们此前从未被断言过，因为**只在联机下才可能出错**：单机时 isLocal 恒为 true，
    走哪条路都一样，于是「远程玩家串台」这类缺陷天然测不出。M31 收尾时就留了一处：
    AddSkill（解锁武器）的 RaiseWeaponsChanged 没有 isLocal 守卫，而同类 LevelUpWeapon 有。

怎么在单机无头环境测到「只有联机才成立」的分支：
    isLocal 是 public 字段（默认 false，单机由 GameBuilder 显式置 true），
    探针直接把它翻成 false，就等价于「这是一个远程玩家对象」，无需真开联机。
    这一手法必须由 BASE 场景先证明有效，否则后面全绿是假的。

判据的区分力（本脚本最要紧的地方）：
    1) REMOTE 与 LOCAL 必须成对：只写「远程不许广播」的话，把 RaiseWeaponsChanged 整行
       删掉也能全绿（本地也收不到，HUD 彻底不刷新），必须靠 LOCAL 的正向判据抓住。
    2) BASE 第 3 条是「手法自检」：若翻转没生效，REMOTE 会一片假绿，必须先在 BASE 红掉。
    3) STATE 先做本地对照再加远程断言：不先证明「加武器确实会改加成」，
       「远程加武器后加成没变」就可能只是因为这条路径本来就不改加成（假绿）。
    4) RESTORE 判「标志已恢复」：翻转留下副作用会让同一进程里后续判据全部失真。

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
    ("BASE", "地基自检：isLocal 字段可读写、单机初始为 true、翻转手法真的生效"),
    ("REMOTE", "远程玩家：解锁 / 升级 / 重复解锁三条路都必须零广播"),
    ("LOCAL", "本地玩家：三条路都必须恰好广播一次（防修过头把本地堵死）"),
    ("STATE", "全局状态：远程玩家不得改写 ComboManager 单例的本地加成"),
    ("RESTORE", "恢复性：标志要还原、还原后本地广播照常、全程零异常"),
)

BACKUP_SUFFIX = ".m32bak"

TIMEOUT = {"BASE": 90, "REMOTE": 90, "LOCAL": 90, "STATE": 90, "RESTORE": 90}


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
    args = [exe, "-batchmode", "-nographics", "-guardProbe", "-guardScene", scene]
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


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 地基：证明翻转手法本身成立，否则后面全是假绿
        "BASE": [
            ("字段可读写", _yes),
            ("单机初始为true", _yes),
            ("翻转手法生效", _yes),
            ("翻转后广播次数", _zero),
            ("断点通过", _eq("3/3")),
        ],
        # 远程：三条改武器状态的路都必须零广播
        "REMOTE": [
            ("远程解锁广播", _zero),
            ("远程升级广播", _zero),
            ("远程重复解锁广播", _zero),
            ("解锁零广播", _yes),
            ("升级零广播", _yes),
            ("重复解锁零广播", _yes),
            ("断点通过", _eq("3/3")),
        ],
        # 本地：必须与 REMOTE 成对，否则「整行删掉广播」也能全绿
        "LOCAL": [
            ("本地解锁广播", _one),
            ("本地升级广播", _one),
            ("累计广播", _eq("2")),
            ("解锁恰好一次", _yes),
            ("升级恰好一次", _yes),
            ("累计恰好两次", _yes),
            ("断点通过", _eq("3/3")),
        ],
        # 全局状态：先证明本地对照数得出变化，再断言远程不改写
        "STATE": [
            ("本地对照数得出变化", _yes),
            ("远程未改伤害加成", _yes),
            ("远程未改穿透加成", _yes),
            ("断点通过", _eq("3/3")),
        ],
        # 恢复性：翻转不得留下副作用
        "RESTORE": [
            ("标志已恢复", _yes),
            ("本地广播照常", _yes),
            ("全程零异常", _yes),
            ("恢复后广播次数", _one),
            ("异常条数", _zero),
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
    ap = argparse.ArgumentParser(description="M32 联机本地守卫对称性无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 REMOTE")
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
    print("M32 联机本地守卫对称性无头实测 · %d 个场景" % len(scenes))
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
                print("      %s %-16s 期望 %-6s 实际 %s" % (" " if good else "x", k, e, g))

            # 实测数值行全部复读：失败时能直接看出是广播不对还是状态被污染
            for extra in ("isLocal字段", "翻转后广播次数",
                          "远程解锁广播", "远程升级广播", "远程重复解锁广播",
                          "本地解锁广播", "本地升级广播", "累计广播",
                          "加成基线", "本地加武器后", "远程加武器后", "穿透 本地/远程",
                          "恢复后广播次数", "异常条数"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-16s %s" % (extra, res[extra]))
            if res.get("首个失败", "无") not in ("", "无"):
                print("        %-16s %s" % ("首个失败", res["首个失败"]))

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
    print("结论：%s" % ("通过 —— 远程玩家零广播且不污染本地状态 / 本地广播恰好一次 / "
                        "翻转后标志与广播均正常恢复"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
