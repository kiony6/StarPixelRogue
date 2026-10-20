#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M41 长局稳定性无头实测

做什么：
    拉起 2 个无头进程，把"玩久了会烂"这一类缺陷钉死：
      GROWTH    跑满 30 分钟游戏时间（-smokeSpeed 6 加速，真实 5 分钟），分 6 段采样：
                波次确实推进 / 宝石有界 / 回血球有界 / 后半程不加速堆积 /
                托管堆收敛 / 长程数值不越界 / 全程零异常
      RESTART   长局之后拆世界重开一局：根对象回落 / 波次重置 / 玩家可控 / 全程零异常

为什么必须测：
    此前全部实测只跑 45～60 秒。短局能证明"开局没坏"，证明不了"一直玩下去不会烂"。
    典型的长局专属缺陷是**某个列表只进不出**：45 秒里掉 30 个、捡走 28 个，看不出异常；
    跑到第 30 分钟堆到 887 个，而且每帧还要全表遍历算距离——越玩越卡。
    实测（修复前）：宝石 469 → 862（峰 887）、回血球峰 148、波次 47、关卡零异常。
    短局对这一切完全沉默。

为什么需要 -smokeSpeed：
    要看波次推到 30+ 之后的累积，真等 30 分钟既跑不动回归，也没法做四组区分力注入。
    它放大的是**模拟步长**（每帧 deltaTime），不是真实负载——所以本探针测的是"累积"，
    真实帧耗时另由常规冒烟覆盖，二者不可互相替代。
    判据里"波次确实推进"正是这条加速的自检：若 BaseScale 被写回 1，世界几乎不动，
    所有增长类判据会一片假绿。

判据的区分力（本脚本最要紧的地方）：
    1) "后半程不加速堆积"比"最终有界"更狠：只加上限的话堆到上限就不再涨，
       最终值判据照样绿——但前半程早就一路涨上去了，玩家在涨的那段时间里已经卡了。
    2) 宝石与回血球**分开**断言：两类共用"只进不出"的写法，只修一半（最容易发生的漏修）
       会被立刻抓住。
    3) 这里**没有**"根对象/对象总数不增长"这条判据，它是错的：场上实体数随波次增长
       是设计使然（每波 8+4×波 只怪），波次 47 时场上就是该有几千个对象。
       真正的泄漏判定放在 RESTART——拆掉世界后还回不来，那才是泄漏。
    4) RESTART 反向断言重开路径：只测"长局没炸"的话，把重开路径改坏也照样全绿。

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
    ("GROWTH", "长局 30 分钟游戏时间（6x 加速）：掉落物有界且不加速堆积 / 堆收敛 / 数值不越界 / 零异常"),
    ("RESTART", "长局之后拆世界重开一局：根对象回落 / 波次重置 / 玩家可控 / 零异常"),
    ("PANEL", "M46 真实重开路径：结算面板弹出 → 冻结 → 点「重新开始」→ 时间恢复 / 面板消失 / HUD 唯一"),
)

BACKUP_SUFFIX = ".m41bak"

# 每个场景的时长（秒）。GROWTH 真跑 300s（= 游戏内 30 分钟），RESTART 120s 足够堆起来。
# PANEL 前半程堆世界、后半程给新世界跑，120s 够。
SECONDS = {"GROWTH": 300, "RESTART": 120, "PANEL": 120}
SPEED = 6.0


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


def run_scenario(exe, scene, speed):
    args = [exe, "-batchmode", "-nographics", "-longrunProbe", "-longrunScene", scene,
            "-smokeSeconds", str(SECONDS.get(scene, 300)), "-smokeSpeed", str(speed)]
    limit = SECONDS.get(scene, 300) + 180
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log = out + "\n[SMOKE] 结论              不通过（超时）"
    return log


def _yes(v):
    return v == "是"


def _yes_or_na(v):
    """三态判据：是 / 不适用 都算达标。

    "不适用"只出现在"世界没推进到稳态"时——那时前后半段都在爬升，
    谈"有没有加速堆积"没有意义。当"通过"会掩盖问题，当"失败"会误报。
    """
    return v in ("是", "不适用")


def _eq(s):
    return lambda v: v == s


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "GROWTH": [
            ("波次确实推进", _yes),
            ("宝石有界", _yes),
            ("回血球有界", _yes),
            ("后半程不加速堆积", _yes_or_na),
            ("托管堆收敛", _yes),
            ("长程数值不越界", _yes),
            ("全程零异常", _yes),
            ("断点通过", _eq("7/7")),
        ],
        # 这里没有"掉落物清零"：TeardownWorld 的 PoolManager.DestroyAll() 会销毁所有池化实例，
        # 无论怎么改这条路重开后都必然为 0——留着就是一条永远绿的假判据。
        "RESTART": [
            ("重开后根对象回落", _yes),
            ("重开后波次重置", _yes),
            ("重开后玩家可控", _yes),
            ("全程零异常", _yes),
            ("断点通过", _eq("4/4")),
        ],
        # M46：走按钮，不走 TeardownWorld()+Build() 两行。
        # 这里没有"重开后掉落物清零"——同 RESTART 的理由（PoolManager.DestroyAll 必然清）。
        "PANEL": [
            ("结算面板已弹出", _yes),
            ("结算时世界冻结", _yes),
            ("重开后时间恢复", _yes),
            ("重开后结算画布消失", _yes),
            ("重开后 HUD 唯一", _yes),
            ("重开后波次重置", _yes),
            ("重开后世界继续推进", _yes),
            ("重开后玩家可控", _yes),
            ("全程零异常", _yes),
            ("拆世界后结算画布消失", _yes),
            ("拆世界后时间解冻", _yes),
            ("断点通过", _eq("11/11")),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        if callable(expect):
            good = expect(got)
            shown = ("是/不适用" if expect is _yes_or_na
                     else ("是" if expect is _yes else "达标"))
        else:
            good = (got == expect)
            shown = expect
        rows.append((key, shown, got, good))
        ok = ok and good

    # M46：PANEL 场景故意以「世界已拆」收尾（强制退出路径），SmokeTest 随后按空世界
    # 报出「波次 0 / 结论 未通过」。那是本场景的预期状态而不是回归，故 PANEL 只认
    # 探针自己的结论键（LongRunProbe 里同样刻意避开了同名键）。
    if scene == "PANEL":
        if res.get("PANEL结论") != "通过":
            ok = False
        if res.get("PANEL失败项", "无") != "无":
            ok = False
        return ok, rows

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M41 长局稳定性无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 GROWTH")
    ap.add_argument("--speed", type=float, default=SPEED, help="加速倍率（默认 6）")
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
    print("M41 长局稳定性无头实测 · %d 个场景（加速 %gx）" % (len(scenes), args.speed))
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
            log = run_scenario(exe, scene, args.speed)
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
                print("      %s %-18s 期望 %-10s 实际 %s" % (" " if good else "x", k, e, g))

            # 实测数值全部复读：失败时能直接看出是堆积还是泄漏
            for extra in ("最终波次", "最终等级", "最终击杀", "场上敌人", "宝石峰值",
                          "回血球峰值", "根对象", "对象总数", "托管堆MB", "平均帧率",
                          "长局末根对象", "重开后根对象", "重开后宝石", "重开后波次", "首个异常",
                          "timeScale", "结算画布", "HUD", "波次轨迹", "按钮点击", "强制退出"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-18s %s" % (extra, res[extra]))

            if res.get("运行期异常", "0") != "0":
                print("      x %-18s 实际 %s" % ("运行期异常", res["运行期异常"]))
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

    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
