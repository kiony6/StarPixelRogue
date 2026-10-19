#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M33/M34 本地化覆盖完整性无头实测

做什么：
    拉起 6 个无头进程，把「英文界面不许冒中文」钉死：
      BASE    扫描器自检：自造的中文文本能被数出来、改成英文后数不出来
      ZH      中文态基线：UI 真的建出来了、且中文态确实有中文
      EN      英文态零中文：切到 en-US 后所有 UI 文本不得含一个中文字符
      KEY     动态键不回退："combo." + id、"codex.tab_" + tab 拼出来的键不得变成裸键
      RESTORE 切回中文后中文必须回来（证明 EN 的 0 不是因为 UI 没了）
      NETUI   联机专属 UI（模式标识 / 断线遮罩）切英文后不得残留中文（M34 新增）

为什么必须测：
    本地化表本身是自洽的（zh/en 各 204 键、集合完全一致），切到英文后绝大多数界面
    也确实变英文了——于是"看起来没问题"。漏网的只有绕过 Localization 直接写字面量的
    地方，以及 M34 抓出的另一类：**文案进了本地化表、但切语言时没人去重设它**。

    M34 订正了 M33 写错的一条结论：M33 认为 Net 层整段在 #if FISHNET_ENABLED 里、
    "没装联机插件时不参与编译"，所以运行时扫不到。事实是 Fish-Net 一直以内置包形式
    在 Packages/ 下、ProjectSettings 的 Standalone 也一直带着 FISHNET_ENABLED，
    那些代码**是编译的**。真正扫不到的原因是：模式标识在单机下 SetActive(false)、
    断线遮罩只在掉线时才出现——是"没让它上屏"，不是"没编译"。
    NETUI 场景就是把这两处手动请上屏，然后照样按"切英文不许有中文"判。

    运行中这一层真正的独有价值是 KEY 与 NETUI：
      · KEY  —— 键是运行时拼出来的，静态扫源码看不见拼完是什么；
      · NETUI —— 模式标识的文案是 CreateText(… ModeLabel()) 写进去的，
                 静态扫源码看不见"这个返回值是本地化文案"，只有看成品才知道。

判据的区分力（本脚本最要紧的地方）：
    1) BASE 是"手法自检"：若扫描器压根数不出中文，EN 场景的 0 会变成假绿，
       必须先在 BASE 红掉。这与 M32 翻 isLocal 前先证明翻转有效是同一条纪律。
    2) ZH 与 EN 必须成对：只写"英文态零中文"的话，UI 压根没建出来也能全绿，
       必须靠 ZH 的"中文态必须数得到中文"抓住。
    3) RESTORE 判"切回来中文要恢复"：同样是为了排除"UI 没了"这种假绿。
    4) KEY 必须先真的触发组合技广播 + 打开图鉴，否则"没有裸键"只是因为那条路没走过。
    5) NETUI 必须先证明"联机 UI 真的上屏了"（≥2 条文本）且中文态数得到中文；
       这两条不做，"英文态 0 条中文"既可能是修好了，也可能是压根没扫到。

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
    ("BASE", "扫描器自检：自造中文数得出来、改英文后数不出来"),
    ("ZH", "中文态基线：UI 建出来了、且确实扫得到中文"),
    ("EN", "英文态零中文：切到 en-US 后不得有任何一条 UI 文本含中文"),
    ("KEY", "动态键不回退：combo.* / codex.tab_* 拼出来的键不得变成裸键"),
    ("RESTORE", "切回中文后中文必须回来（排除「UI 没了」的假绿）"),
    ("NETUI", "联机专属 UI（模式标识 / 断线遮罩）切英文后不得残留中文"),
)

BACKUP_SUFFIX = ".m33bak"

TIMEOUT = {"BASE": 90, "ZH": 120, "EN": 120, "KEY": 120, "RESTORE": 150, "NETUI": 180}


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
    args = [exe, "-batchmode", "-nographics", "-locProbe", "-locScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _zero(v):
    return v == "0"


def _yes(v):
    return v == "是"


def _at_least_1(v):
    try:
        return int(v) >= 1
    except (TypeError, ValueError):
        return False


def _no(v):
    return v in ("无", "")


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 扫描器自检：证明"数得出中文"，否则 EN 的 0 是假绿
        "BASE": [
            ("扫描器自检", lambda v: "中文态=1" in v and "英文态=0" in v),
            ("失败项", _zero),
        ],
        # 中文态：UI 必须建出来，且确实扫得到中文
        "ZH": [
            ("UI 文本总数", _at_least_1),
            ("含中文条数", _at_least_1),
            ("失败项", _zero),
        ],
        # 英文态：核心判据
        "EN": [
            ("UI 文本总数", _at_least_1),
            ("含中文条数", _zero),
            ("首条中文", _no),
            ("失败项", _zero),
        ],
        # 动态键：必须先真的走通那两条路，"没有裸键"才有意义
        "KEY": [
            ("组合技已触发", _yes),
            ("图鉴已打开", _yes),
            ("UI 文本总数", _at_least_1),
            ("裸键条数", _zero),
            ("首条裸键", _no),
            ("失败项", _zero),
        ],
        # 恢复：证明 EN 的 0 是因为本地化生效，不是因为 UI 没了
        "RESTORE": [
            ("英文态文本/中文", lambda v: v.strip().endswith("/ 0")),
            ("切回后文本/中文", lambda v: _at_least_1(v.split("/")[-1].strip())),
            ("失败项", _zero),
        ],
        # 联机 UI：先证明它真的上屏了、中文态数得到中文，英文态的 0 才有意义
        "NETUI": [
            ("联机UI已上屏", _yes),
            ("联机UI文本总数", lambda v: int(v) >= 2 if v.isdigit() else False),
            ("中文态含中文", _at_least_1),
            ("英文态文本总数", lambda v: int(v) >= 2 if v.isdigit() else False),
            ("英文态含中文", _zero),
            ("首条中文", _no),
            ("切回后含中文", _at_least_1),
            ("失败项", _zero),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got)
        rows.append((key, _label(expect), got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    return ok, rows


def _label(f):
    """把判据函数还原成人能读的期望描述。"""
    if f is _zero:
        return "0"
    if f is _yes:
        return "是"
    if f is _no:
        return "无"
    if f is _at_least_1:
        return "≥1"
    return "达标"


def main():
    ap = argparse.ArgumentParser(description="M33 本地化覆盖完整性无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 EN")
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
    print("M33/M34 本地化覆盖完整性无头实测 · %d 个场景" % len(scenes))
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

            # 实测数值行全部复读：失败时能直接看出是扫描失灵还是真有漏网
            for extra in ("扫描器自检", "UI 文本总数", "含中文条数", "首条中文",
                          "组合技已触发", "图鉴已打开", "裸键条数", "首条裸键",
                          "英文态文本/中文", "切回后文本/中文", "异常条数",
                          "联机UI已上屏", "联机UI文本总数", "中文态含中文",
                          "英文态文本总数", "英文态含中文", "切回后含中文"):
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
    print("结论：%s" % ("通过 —— 英文界面零中文 / 动态键不回退成裸键 / "
                        "联机 UI 切语言同步刷新 / 切回中文后中文正常恢复"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
