#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M35 法务文本双语无头实测

做什么：
    拉起 5 个无头进程，把「法律正文必须跟着语言走」钉死：
      BASE    扫描器自检：自造中文数得出来、改英文后数不出来、中文标点也认得
      ZH      中文态基线：三份文档都取得到、且都含中文
      EN      英文态零中文零回退：Resources.Load 真的取到了 Legal/en/ 下那份
      UI      三份法务窗逐个打开上屏（L10N 场景从未点开过它们），切英文后零中文
      REFRESH 已打开的窗**不关掉重开**，切语言时必须就地刷新

为什么必须测：
    三份法律正文（用户协议 / 隐私政策 / 儿童个人信息保护规则）住在
    Resources/Legal/*.txt，是**唯一一类不在 .cs 里的玩家可见文案**。于是前面每一层
    断言都绕过了它：
      · 静态第 12 项扫 .cs 里的中文字面量——txt 不是 .cs；
      · 静态第 13 项扫 Text 字段赋值——正文是 Resources.Load 出来的整段字符串；
      · 运行时 L10N 场景只扫 ComplianceGate.PanelRoot 下的按钮标签——
        三个法律文档**从来没有被点开过**。
    而 LegalTexts.Get() 原本只认 Legal/<key>.txt 一条路径、那三份是纯中文的：
    英文界面下点开「Terms of Service」，弹出来的是整篇中文。不同语言的用户拿到的
    法务文本不对等——这是合规风险，不是体验瑕疵。

    运行时这一层的独有价值是「资源真的取到了」：静态检查能确认
    Resources/Legal/en/terms.txt 这个文件存在，但路径拼错、平台差异、
    资源没被打进包这些事，只有真的 Load 一次才知道。

判据的区分力（本脚本最要紧的地方）：
    1) BASE 是"手法自检"：若扫描器压根数不出中文，EN 的 0 会变成假绿，
       必须先在 BASE 红掉。这与 M32 翻 isLocal 前先证明翻转有效是同一条纪律。
       M35 多加一条：中文标点（书名号《》）也必须算中文——只查汉字会放过
       "英文版里残留书名号"这种看着像英文、其实没翻译的情况。
    2) ZH 与 EN 必须成对：只写"英文态零中文"的话，文档压根没取到也能全绿，
       必须靠 ZH 的"三份都取到、且都含中文"抓住。
    3) EN 的"总字符数"下限：防止取到空壳（空串自然零中文）。
    4) EN 的"回退次数"必须为 0：英文版缺失时运行时会回退中文（给玩家看占位串
       更糟），这个回退必须被计数并由本脚本判红——不能静默。
    5) UI 的"英文态条数不得比中文态少"：切语言若把窗弄没了，"0 条中文"是假绿。
    6) REFRESH 不重开窗：这是 M35 修的第二处（窗原本拿的是成品字符串），
       重开一次就能掩盖缺陷，所以判据特意要求"就地切换"。

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
    ("BASE", "扫描器自检：中文数得出来、改英文数不出来、中文标点也算数"),
    ("ZH", "中文态基线：三份文档都取得到、且都含中文"),
    ("EN", "英文态零中文零回退：Resources.Load 真的取到了英文那份"),
    ("UI", "三份法务窗逐个打开上屏，切英文后窗内零中文"),
    ("REFRESH", "已打开的窗不关掉重开，切语言时就地刷新"),
)

BACKUP_SUFFIX = ".m35bak"

TIMEOUT = {"BASE": 90, "ZH": 90, "EN": 90, "UI": 120, "REFRESH": 120}


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
    args = [exe, "-batchmode", "-nographics", "-legalProbe", "-legalScene", scene]
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


def _three(v):
    return v == "3"


def _at_least_1(v):
    try:
        return int(v) >= 1
    except (TypeError, ValueError):
        return False


def _at_least(n):
    def f(v):
        try:
            return int(v) >= n
        except (TypeError, ValueError):
            return False
    return f


def _no(v):
    return v in ("无", "")


def _ge_zh_count(res, key):
    """REFRESH：切回中文后的中文条数不得比中文态少（少了说明窗被切语言弄没了）。"""
    try:
        return int(res.get(key, "0")) >= int(res.get("中文态含中文", "0"))
    except (TypeError, ValueError):
        return False


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 扫描器自检：证明"数得出中文"，否则 EN 的 0 是假绿
        "BASE": [
            ("扫描器自检", lambda v: "中文态=1" in v and "英文态=0" in v and "中文标点=认得" in v),
            ("失败项", _zero),
        ],
        # 中文态基线：三份都得取到、且都得含中文
        "ZH": [
            ("文档份数", _three),
            ("中文态含中文份数", _three),
            ("失败项", _zero),
        ],
        # 英文态：核心判据
        "EN": [
            ("文档份数", _three),
            ("英文态含中文份数", _zero),
            ("首条中文", _no),
            ("英文版回退次数", _zero),
            ("英文态总字符", _at_least(300)),
            ("失败项", _zero),
        ],
        # 法务窗上屏：先证明窗真的建出来了、中文态数得到中文
        "UI": [
            ("中文态窗内文本", _at_least_1),
            ("中文态含中文", _at_least_1),
            ("英文态窗内文本", _at_least(1)),
            ("英文态含中文", _zero),
            ("首条中文", _no),
            ("失败项", _zero),
        ],
        # 就地刷新：不重开窗，切语言就得变
        "REFRESH": [
            ("中文态含中文", _at_least_1),
            ("就地切英文后含中文", _zero),
            ("切回中文后含中文", lambda v: _ge_zh_count(res, "切回中文后含中文")),
            ("失败项", _zero),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got)
        rows.append((key, _label(expect, res), got, good))
        ok = ok and good

    # UI：英文态条数不得比中文态少——切语言把窗弄没了的话，"0 条中文"是假绿
    if scene == "UI":
        try:
            if int(res.get("英文态窗内文本", "0")) < int(res.get("中文态窗内文本", "0")):
                rows.append(("窗内文本条数不下降", "英文态≥中文态",
                             res.get("英文态窗内文本", "?") + " < " + res.get("中文态窗内文本", "?"), False))
                ok = False
        except (TypeError, ValueError):
            ok = False

    if res.get("结论") != "通过":
        ok = False
    return ok, rows


def _label(f, res=None):
    """把判据函数还原成人能读的期望描述。"""
    if f is _zero:
        return "0"
    if f is _three:
        return "3"
    if f is _at_least_1:
        return "≥1"
    if f is _no:
        return "无"
    return "达标"


def main():
    ap = argparse.ArgumentParser(description="M35 法务文本双语无头实测")
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
    print("M35 法务文本双语无头实测 · %d 个场景" % len(scenes))
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
                print("      %s %-20s 期望 %-8s 实际 %s" % (" " if good else "x", k, e, g))

            # 实测数值行全部复读：失败时能直接看出是扫描失灵还是真有漏网
            for extra in ("扫描器自检", "文档份数", "中文态含中文份数", "英文态含中文份数",
                          "首条中文", "英文版回退次数", "中文态总字符", "英文态总字符",
                          "中文态窗内文本", "中文态含中文", "英文态窗内文本", "英文态含中文",
                          "就地切英文后含中文", "切回中文后含中文"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-20s %s" % (extra, res[extra]))

            if res.get("运行期异常", "0") != "0":
                print("      x %-20s 实际 %s" % ("运行期异常", res["运行期异常"]))
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
    print("结论：%s" % ("通过 —— 三份法律正文中英双语同步 / 英文态零中文零回退 / "
                        "法务窗上屏并随语言就地刷新"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
