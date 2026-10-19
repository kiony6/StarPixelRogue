#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M27 伤害飘字对象池无头实测

做什么：
    拉起 4 个无头进程，把「伤害飘字改成对象池」这件事的两面各钉一遍：
      POOL     120 次高频伤害：累计新建必须有上限（而不是 120）、复用率必须过半、
               峰值并发必须真的上去、结束后必须全部归还
      REUSE    同一个实例被**第二次**使用：必须复用同一实例、文案必须更新、
               生命周期必须被重置、必须仍然可见、字体必须还在、最后仍要归还
      BURST    单帧 40 次伤害（AOE 清场）：一张都不能丢，随后全部归还
      LEFTOVER 真实重开序列后，上一局的飘字对象一个都不许活下来

为什么必须测：
    敌人 / 弹丸 / 回旋镖 / 追踪弹 / 地雷 / 宝石 / 回血球**全都**走 PoolManager，
    唯独伤害飘字每次伤害都 new GameObject + AddComponent<Text>
    + 两次 Resources.GetBuiltinResource（字体回退链）。
    它恰好是全项目**每秒触发次数最多**的对象（每个敌人每次被打一张），
    重战况下每秒可达数百次分配，与项目「对象池抗 GC」的原则直接相悖。

    但"改成池"这一步本身才是危险的地方：**复用实例时字段初始化器不会重跑**。
    FloatingText._life 的初值 0.6f 只在 AddComponent 时赋过一次；
    漏了重置的话，第二次被复用的实例 _life 仍是 0，Update 里立刻归还——
    玩家看到的是"伤害数字只出现前几次，之后全消失"，而崩溃日志一行都没有。
    这正是 M25「字段默认值不是未初始化，它会被立刻使用」的同一类陷阱。

判据的区分力（本脚本最要紧的地方）：
    1) 累计新建数由探针**逐帧采样 instanceID 去重**得到，产品代码里根本没有
       "新建计数"这种东西——加了就是为测试而写的假账本（M21 DECLINE 假通过同类教训）。
    2) 「新建得少」必须配「峰值并发确实很大」的对偶判据：
       只测前者的话，"压根没显示几张"也能让新建数很小（M25「必须发生 / 必须结束」同源）。
    3) REUSE 专门盯**第二次**使用：单发命中完全正常，复用才暴露（M23/M26 叠乘同类）。
    4) LEFTOVER 用 Unity 的"已销毁对象 == null"语义比对**对象引用**，
       不比对 instanceID（Unity 可能回收 ID，比对引用才可靠）。

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
    ("POOL", "120 次高频伤害：累计新建必须有上限、复用率过半、结束后全部归还"),
    ("REUSE", "复用实例必须重置寿命与透明度、文案必须更新、字体必须还在"),
    ("BURST", "单帧 40 次伤害必须一张不丢，随后全部归还"),
    ("LEFTOVER", "真实重开序列后，上一局的飘字对象一个都不许活下来"),
)

BACKUP_SUFFIX = ".m27bak"

TIMEOUT = {"POOL": 120, "REUSE": 120, "BURST": 120, "LEFTOVER": 150}


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
    args = [exe, "-batchmode", "-nographics", "-poolProbe", "-poolScene", scene]
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
    """把 '40' / '0.500' / '62.5%' 之类取成 float；取不出来返回 None。"""
    try:
        return float(str(v).strip().rstrip("sS%"))
    except (TypeError, ValueError):
        return None


def _yes(v):
    return v == "是"


def _zero(v):
    """必须为 0 的计数（残留/泄漏类判据）。"""
    return v.isdigit() and int(v) == 0


def _cap_create(v):
    """120 次伤害累计新建必须 ≤40：未池化时每次伤害都新建一个，实测会是 120。"""
    return v.isdigit() and int(v) <= 40


def _burst_cap(v):
    """单帧爆发 40 张，累计新建不该明显超过峰值。"""
    return v.isdigit() and int(v) <= 48


def _peak_up(v):
    """峰值并发必须 ≥10：否则"新建得少"可能只是压根没显示几张。"""
    return v.isdigit() and int(v) >= 10


def _reuse_rate(v):
    """复用率必须 ≥50%。"""
    n = _num(v)
    return n is not None and n >= 50.0


def _alpha_up(v):
    """复用实例 0.30s 后仍必须可见（alpha > 0.2）。"""
    n = _num(v)
    return n is not None and n > 0.2


def _left_before(v):
    """重开前必须确实造出了飘字（5 张 + 模板 = 6）。"""
    return v.isdigit() and int(v) >= 6


def _rebuilt(v):
    """重开后新世界必须有自己的池模板（≥1）。"""
    return v.isdigit() and int(v) >= 1


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "POOL": [
            # 「新建得少」与「并发确实很大」必须同时成立，缺一就成了假绿
            ("峰值并发", _peak_up),
            ("累计新建", _cap_create),
            ("复用率", _reuse_rate),
            ("结束后活跃", _zero),
            ("并发够大", _yes),
            ("新建有上限", _yes),
            ("复用过半", _yes),
            ("全部归还", _yes),
            ("断点通过", "4/4"),
        ],
        "REUSE": [
            # 复用同一实例是前提：不是复用就谈不上"状态重置"
            ("复用同一实例", _yes),
            ("复用文案", "89"),
            ("0.3s后活跃", "1"),
            ("0.3s后透明度", _alpha_up),
            ("文案已更新", _yes),
            ("寿命已重置", _yes),
            ("仍然可见", _yes),
            ("字体仍在", _yes),
            ("会归还", _yes),
            ("断点通过", "6/6"),
        ],
        "BURST": [
            ("峰值并发", "40"),
            ("累计新建", _burst_cap),
            ("爆发后活跃", _zero),
            ("一张不丢", _yes),
            ("新建不超峰值", _yes),
            ("全部归还", _yes),
            ("断点通过", "3/3"),
        ],
        "LEFTOVER": [
            # 先证明"确实造出了"，否则"存活=0"可能只是压根没造出来（M24/M26 同类前置断言）
            ("重开前存量", _left_before),
            ("上一局存活", _zero),
            # Track(组件) 的后果：组件销毁了，GameObject 与子物体全留下，一局一局堆
            ("空壳管理器", _zero),
            ("飘字画布数", "1"),
            ("重开后存量", _rebuilt),
            ("已造出飘字", _yes),
            ("上一局清零", _yes),
            ("无空壳残留", _yes),
            ("画布只剩一块", _yes),
            ("新世界已建", _yes),
            ("断点通过", "5/5"),
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
                 "≤40" if expect is _cap_create else
                 "≤48" if expect is _burst_cap else
                 "≥10" if expect is _peak_up else
                 "≥50%" if expect is _reuse_rate else
                 ">0.2" if expect is _alpha_up else
                 "≥6" if expect is _left_before else
                 "≥1" if expect is _rebuilt else expect)
        rows.append((key, shown, got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M27 伤害飘字对象池无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 POOL")
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
    print("M27 伤害飘字对象池无头实测 · %d 个场景" % len(scenes))
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
            # 但失败时能直接指出是"压根没复用"还是"复用了但状态没重置"
            for extra in ("伤害次数", "累计新建", "峰值并发", "复用率", "结束后活跃",
                          "首次实例号", "复用实例号", "首次文案", "复用文案",
                          "0.3s后活跃", "0.3s后透明度",
                          "单帧次数", "爆发后活跃",
                          "重开前存量", "重开后存量", "上一局存活", "存活构成",
                          "空壳管理器", "飘字画布数"):
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
    print("结论：%s" % ("通过 —— 飘字已池化：新建数有上限且复用过半 / 复用实例状态被正确重置 / "
                        "单帧爆发一张不丢 / 重开后上一局对象全部清零"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
