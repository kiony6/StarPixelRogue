#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M30 运行时 GPU 资源无头实测

做什么：
    拉起 4 个无头进程，把「运行时 new 出来的 UnityEngine.Object 没人回收」钉死：
      BASE    计数器自检：数得到纹理、数得出增量、工厂连调返回同一实例
      BURST   真实高频路径（爆发环 / 警示圈 / 链闪电）连放 24 轮，纹理不许增长
      RESTART 重开三局（每局真打），纹理与 Sprite 总数都不许增长
      CACHE   反射读工厂静态缓存：条目不许有已销毁的、同一个 key 反复取不许涨

为什么必须测：
    前几轮（M24/M26/M27/M28/M29）抓的跨局残留，漏的都是 GameObject 与委托引用——
    它们在场景里、在委托链上，数得出来。这一轮漏的是**第三类**：运行时 new 出来的
    Texture2D / Sprite。它们不挂在场景里、不进任何登记表，Object.Destroy 也从来
    不会有人对它们调用（根本没人持有引用）。GC 管不到 Unity 的原生侧内存：
    托管包装被回收，显存里那张纹理还在。玩家玩得越久内存越高，
    而场景里干干净净、日志一片安静。

判据的区分力（本脚本最要紧的地方）：
    1) BASE 先证明「数得到」，再证明「数得出增量」。两者缺一不可：
       只会报恒定值的计数器，与真的没有泄漏，长得一模一样。
    2) BASE 的引用相等判据直击缓存本身：「总数没涨」可能被别处的释放抵消（假绿），
       ReferenceEquals 不会被抵消。
    3) BURST 先预热一次再取基线：第一次调用本来就该建缓存，
       不预热会把「建缓存那一次」误判成泄漏，反过来又会掩盖真正的泄漏。
    4) RESTART 每局必须真打一会儿：特效是自毁型的，不跑时间它压根不会生成，
       「没泄漏」和「没生成」长得一模一样。
    5) 增量判据统一用 <= 0 而不是 == 0：允许基线附近的小幅抖动，
       但绝不允许增长。

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
    ("BASE", "计数器自检：数得到纹理、数得出增量、工厂返回同一实例"),
    ("BURST", "高频特效连放 24 轮：纹理总数不许增长"),
    ("RESTART", "重开三局（每局真打）：纹理与 Sprite 都不许增长"),
    ("CACHE", "静态缓存：条目不许有已销毁的、同 key 反复取不许涨"),
)

BACKUP_SUFFIX = ".m30bak"

# RESTART 每局要真打约 2.4s，三局约 8s，加上构建拆世界余量
TIMEOUT = {"BASE": 90, "BURST": 180, "RESTART": 150, "CACHE": 90}


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
    args = [exe, "-batchmode", "-nographics", "-gpuProbe", "-gpuScene", scene]
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


def _at_least(n):
    def f(v):
        return v.isdigit() and int(v) >= n
    return f


def _no_grow(v):
    """增量必须 <= 0：允许基线抖动，绝不允许增长。"""
    if not v.isdigit() and not (v.startswith("-") and v[1:].isdigit()):
        return False
    return int(v) <= 0


def _eq(s):
    return lambda v: v == s


def _rounds_all_made(v):
    """RESTART 的护栏：四局（预热局 + 三局）每一局都必须真的生成了特效。

    末局个数只是陪跑信息，不参与判定——它随电弧拆几节、对局里同时在放几颗粒子而变。
    """
    return v.startswith("4/4 局")


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 地基：证明计数器既数得到东西，又看得出增量；再直击缓存本身
        "BASE": [
            ("纹理基线", _at_least(1)),
            ("手造20张后增量", _at_least(20)),
            ("工厂同实例次数", _eq("20/20")),
            ("数得到纹理", _yes),
            ("数得出增量", _yes),
            ("工厂有缓存", _yes),
            ("断点通过", "3/3"),
        ],
        "BURST": [
            # 前置：确实放满了 24 轮，否则「没增长」只是压根没生成
            ("特效轮数", _eq("24")),
            ("纹理净增长", _no_grow),
            ("Sprite净增长", _no_grow),
            ("确实生成", _yes),
            ("纹理不增长", _yes),
            ("断点通过", "2/2"),
        ],
        "RESTART": [
            ("纹理基线", _at_least(1)),
            ("重开轮数", _eq("3")),
            # M53：原先写死整串"4/4 局（末局 6 个）"。末局那 6 个是
            # 「环 1 + 圈 1 + 电弧 2 段 + 闪光 2」，而电弧段数随"每段拆几节"变——
            # M53 把折线改成带抖动的多节之后它就成了 9。这个数是**观感的副产品**，
            # 不是本条护栏要看的东西：护栏只问"每一局是否真的生成了特效"，
            # 免得"没泄漏"和"压根没生成"长得一样。改判开头，细节留给复读行去看。
            ("确实生成特效", _rounds_all_made),
            ("纹理净增长", _no_grow),
            ("Sprite净增长", _no_grow),
            ("计数有效", _yes),
            ("纹理不增长", _yes),
            ("Sprite不增长", _yes),
            ("断点通过", "4/4"),
        ],
        "CACHE": [
            ("缓存条目数", _at_least(1)),
            ("死条目数", _eq("0")),
            ("重复取用增长", _no_grow),
            ("读得到缓存", _yes),
            ("条目都活着", _yes),
            ("条目数不涨", _yes),
            ("同key返回同实例", _yes),
            ("断点通过", "4/4"),
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
            elif expect is _no_grow:
                shown = "≤0"
            elif expect.__name__ == "<lambda>":
                shown = "相等"
            else:
                shown = "≥%s" % getattr(expect, "_n", 1)
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
    ap = argparse.ArgumentParser(description="M30 运行时 GPU 资源无头实测")
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
    print("M30 运行时 GPU 资源无头实测 · %d 个场景" % len(scenes))
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

            # 实测数值行全部复读：失败时能直接看出是哪一类资源在涨
            for extra in ("纹理基线", "纹理之后", "Sprite基线", "Sprite之后",
                          "手造20张后增量", "工厂同实例次数", "逐局纹理序列",
                          "确实生成特效", "缓存条目数", "死条目构成",
                          "重开轮数", "特效轮数"):
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
    print("结论：%s" % ("通过 —— 高频特效连放不新建纹理 / 重开三局纹理与 Sprite 均不增长 / "
                        "静态缓存条目全部存活且按 key 复用"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
