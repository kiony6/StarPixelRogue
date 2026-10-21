#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M26 可见层第二批无头实测（字体 / 伤害飘字 / 受击冲击 / 前摇警示圈）

做什么：
    拉起 4 个无头进程，把手感层里**还没被断言过**的第二批"看得见"的量逐条走一遍：
      FONT      运行时字体：UIFactory.UIFont 与伤害飘字的 Text.font 都必须非空，
                且必须是**同一实例**（三处各自实现，回退链不得分叉）
      DMG       伤害飘字：必须生成、文案必须等于取整后的伤害、必须上浮且淡出、
                连打 8 次后必须**全部自行回收**（抓"创建了不销毁"的泄漏）
      PUNCH     受击冲击：倍率必须放大到 ≈1.12×、必须回落、连续受击**不得叠乘**
      TELEGRAPH Boss 前摇警示圈：必须生成、必须有精灵、透明度必须脉动、
                真实时长≈请求时长、到点必须自毁

为什么必须测（承接 M25）：
    M25 抓出的"相机基准被自己拉塌"证明了一件事：**无头实测能证明"跑通"，证明不了"看得见"**。
    那一轮只覆盖了镜头与顿帧，手感层还剩一整批"产出只在画面上"的东西从未被判过分：
      · 运行时字体：三处各自实现（UIFactory / LevelUpManager / DamageNumberManager）。
        一旦内置字体取不到，uGUI 的 Text 就是**空白**——不报错、不崩溃，玩家什么都看不到。
      · 伤害飘字：从没断言过"数字对不对""会不会上浮淡出""会不会自己消失"。
        "创建了但永远不销毁"的实现在既有实测里同样全绿（内存慢慢涨而已）。
      · 受击冲击：敌人被打时的缩放冲击从未被断言；若写成叠乘，尺寸会越打越大。
      · Boss 前摇警示圈：最关键的一处——它是**玩家唯一的躲避依据**，
        从没断言过"真的画出来了""透明度在脉动""到点真的消失"。

判据的区分力（本脚本最要紧的地方）：
    1) 一律采信**组件上的实测值**（Text.font / Text.text / localScale / SpriteRenderer.sprite），
       并由本脚本在**进程外**复算一遍，不采信被测对象自己的账本。
    2) 飘字与警示圈都同时断言"必须出现"和"必须消失"两侧：
       只测单边的话，"创建了不销毁"和"压根没创建"各能骗过一半。
    3) 冲击倍率用**相对比值**（受击后 / 受击前），不依赖私有字段 _baseScale，
       顺带覆盖了精英怪呼吸脉冲带来的基线差异。
    4) 「连续受击不叠乘」单独成条：若实现写成 scale *= 1.12，单发命中照样能过，
       只有第二发才暴露——这条判据专门用来抓"乘了又乘"（M23 叠乘同类）。
    5) 真实时长用**墙钟**度量；探针内所有等待走 unscaledDeltaTime，
       否则会被被测对象自己的缩放时间一起拖住，量不出真实时长。

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
    ("FONT", "运行时字体必须真的取得到，且两处不得分叉"),
    ("DMG", "飘字必须生成、文案正确、上浮淡出、并自行回收"),
    ("PUNCH", "受击冲击必须放大、回落，连续受击不得叠乘"),
    ("TELEGRAPH", "Boss 前摇警示圈必须画出来、会脉动、按时自毁"),
    ("LEFTOVER", "重开局后不得残留上一局的特效对象（警示圈 / 爆发环）"),
    ("FX", "M53 池化特效：连放 24 轮纹理零增量 / 不越上限 / 全归还 / 收支守恒 / 拆世界清零"),
    ("SHAPE", "M53 六个程序化形状：两次取用同一实例 / 六个互不相同 / 重复预热零增量"),
    ("SKIN", "M53 选卡卡片：三档 kind 各上各的稀有度色 / 标签已本地化 / 卡片真点得到"),
)

BACKUP_SUFFIX = ".m26bak"

TIMEOUT = {"FONT": 120, "DMG": 120, "PUNCH": 120, "TELEGRAPH": 90, "LEFTOVER": 120,
           "FX": 150, "SHAPE": 90, "SKIN": 120}


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
    args = [exe, "-batchmode", "-nographics", "-visualProbe", "-visualScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _num(v):
    """把 '1.120' / '0.500s' 之类取成 float；取不出来返回 None。"""
    try:
        return float(str(v).strip().rstrip("sS"))
    except (TypeError, ValueError):
        return None


def _yes(v):
    return v == "是"


def _zero(v):
    """必须为 0 的计数（残留/泄漏类判据）。"""
    return v.isdigit() and int(v) == 0


def _punch_up(v):
    """受击冲击倍率必须落在 1.08~1.20（≈1.12×）。"""
    n = _num(v)
    return n is not None and 1.08 <= n <= 1.20


def _punch_back(v):
    """回落后必须回到 1.0（±0.02）。"""
    n = _num(v)
    return n is not None and abs(n - 1.0) <= 0.02


def _blink(v):
    """警示圈透明度振幅必须 > 0.05——静止不动的圈起不到警示作用。"""
    n = _num(v)
    return n is not None and n > 0.05


def _telegraph_dur(v):
    """警示圈真实时长必须≈0.50s。"""
    n = _num(v)
    return n is not None and 0.35 <= n <= 0.80


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "FONT": [
            ("工厂字体非空", _yes),
            ("飘字字体非空", _yes),
            ("两处同一实例", _yes),
            ("断点通过", "3/3"),
        ],
        "DMG": [
            ("飘字数量", "1"),
            ("飘字文案", "37"),
            # 上浮与淡出分开报：这样能区分"压根没动"和"动了但没收回去"
            ("会上浮", _yes),
            ("会淡出", _yes),
            ("打完后残留", _zero),
            ("断点通过", "5/5"),
        ],
        "PUNCH": [
            ("受击倍率", _punch_up),
            ("回落后倍率", _punch_back),
            # 专门抓叠乘：单发命中过的实现，第二发才露出马脚
            ("连击后倍率", _punch_up),
            ("受击有冲击", _yes),
            ("会回落", _yes),
            ("不叠乘", _yes),
            ("断点通过", "3/3"),
        ],
        "TELEGRAPH": [
            ("有精灵", _yes),
            ("闪烁振幅", _blink),
            ("真实时长", _telegraph_dur),
            ("到点残留", _zero),
            ("已生成", _yes),
            ("会脉动", _yes),
            ("时长正确", _yes),
            ("会自毁", _yes),
            ("断点通过", "5/5"),
        ],
        "LEFTOVER": [
            # 先证明"确实造出了 3 个"，否则"残留=0"可能只是压根没造出来（M24 LEFTOVER 同类前置断言）
            ("重开前存量", "3"),
            ("重开后残留", _zero),
            ("已造出残留", _yes),
            ("重开后清零", _yes),
            ("断点通过", "2/2"),
        ],
        # M53 池化特效：最要紧的两条是"确实放出来了"与"纹理零增量"必须同时成立——
        # 只测后者的话，"一颗都没生成"的实现同样拿 0 增量
        "FX": [
            ("纹理增量", _zero),
            ("演完账上", _zero),
            ("演完场上", _zero),
            ("确实放出来了", _yes),
            ("纹理零增量", _yes),
            ("未越过上限", _yes),
            ("全部归还", _yes),
            ("收支守恒", _yes),
            ("拆世界清零", _yes),
            ("断点通过", "6/6"),
        ],
        "SHAPE": [
            ("重预热纹理增量", _zero),
            ("六个都有图", _yes),
            ("两次同一实例", _yes),
            ("六个互不相同", _yes),
            ("重复预热零增量", _yes),
            ("断点通过", "4/4"),
        ],
        "SKIN": [
            ("分档正确", _yes),
            ("三色互不相同", _yes),
            ("标签已本地化", _yes),
            ("卡片可点", _yes),
            ("断点通过", "4/4"),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got) if callable(expect) else (got == expect)
        shown = ("是" if expect is _yes else
                 "0" if expect is _zero else
                 "≈1.12" if expect is _punch_up else
                 "≈1.00" if expect is _punch_back else
                 ">0.05" if expect is _blink else
                 "≈0.50s" if expect is _telegraph_dur else expect)
        rows.append((key, shown, got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M26 可见层第二批无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 FONT")
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
    print("M26 可见层第二批无头实测 · %d 个场景" % len(scenes))
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
            # 但失败时能直接指出是"压根没生成"还是"生成了没收回去"
            for extra in ("字体", "飘字数量", "飘字文案", "上浮量", "淡出量",
                          "打完后残留", "受击倍率", "回落后倍率", "连击后倍率",
                          "闪烁振幅", "真实时长", "到点残留",
                          "重开前存量", "重开后残留", "残留构成"):
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
    print("结论：%s" % ("通过 —— 运行时字体真的取得到且两处一致 / 飘字生成·文案·上浮·淡出·回收全部正确 / "
                        "受击冲击放大并回落且不叠乘 / 前摇警示圈画得出来、会脉动、按时自毁"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
