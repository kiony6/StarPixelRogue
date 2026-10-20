#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M45「4 人局 + 波次 200+ 的拆建」判据的区分力验证。

做什么：
    往被测代码里注入 4 组**互相独立**的缺陷（外加 1 组对照），每组重建 + 在
    **4 人局**下跑一遍联机重开局探针，核对"实际报出来的失败面"是否恰好等于预期。

为什么这一轮必须做：
    M43 起这三轮（拆世界扫场 → 补弹丸）全部只验证过 **2 人局、波次 ~100**。
    M44 报告 §9 第一条把它列为首位遗留，本轮才做，理由与 M44 补弹丸时一样：
    **当时没有能看见它的判据**——重开局那 11 条判据只数敌人 / 掉落物 / 世界状态 /
    弹丸，没有一条数"人"。4 人局里掉一个人时另外三个照打，世界照样推进、波次照样涨，
    日志里连一条异常都没有，而每一条既有判据都是绿的。

    所以本轮先补判据再动配置：
      · 「拆完玩家未被误清」（房主专属）—— 拆完、尚未重建的那一帧点数，
        这一刻玩家若被误清，随后 Build() 会立刻把人重新建出来，1.6 秒后再数就看不出了；
      · 「重开后玩家齐全」（每端都判）—— 上一条只守"拆的动作"，这一条守整段；
      · 跨端「每端重开后玩家数 == 人数」—— 探针那条只比"重开前后"，
        比不出"本来就该是 4 个"（4 人局里有人一开始就没连上时，两端都少一个，"齐全"照样绿）。

    判据之外还有一条静态：第 23 项——`DespawnAllWorldObjects()` 里**不得**出现
    `SweepDespawn<NetPlayer>()`。第 22 项管"少清了谁"，它管反方向"多清了谁"。

四组：
    Q1 扫场把玩家也清了   → 房主{拆完玩家未被误清, 重开后玩家齐全} + 每端{重开后玩家齐全}
                            + 跨端{重开后玩家数 != 4}
                            （M45 的核心回归组：证明新判据真的有牙）
    Q2 弹丸不参与扫场     → 房主{拆完立刻无残留弹丸} + 客户端{重开后弹丸清零}
                            （M44 回归：4 人局下老判据还灵不灵）
    Q3 敌人不参与扫场     → 房主{拆完立刻无残留敌人}
                            （M43 回归：同上）
    Q4 什么都不改（对照）  → 期望**全绿**：证明 4 人局本身不产生假红，
                            否则上面三组的"红"可能只是"人多就红"

用法：
    python m45_inject.py                 # 四组全跑
    python m45_inject.py --only Q1       # 只跑一组
    python m45_inject.py --seconds 60    # 缩短每组观察时长（RESTART 至少 50）
    python m45_inject.py --clients 1     # 退回 2 人局跑（对照用）

退出码：
    0 = 各组失败面均与预期一致且互不相交；1 = 有不一致 / 跑批失败；2 = 前置条件不满足
"""

import argparse
import io
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
BUILD_LOG = os.path.join(ROOT, "unity_build_m45_inject.log")

SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
NETWORLD = os.path.join(SCRIPTS, "Net", "NetWorld.cs")

# 必须是注释前缀：只写 `[M45INJ]` 会被 C# 当成特性声明，编译报 CS7014。
MARK = "// M45INJ"

CASES = [
    {
        "id": "Q1",
        "name": "扫场把玩家也一起清了（M45 要抓的写法：看起来合理，只会让重开后少一个人）",
        # 「重开后玩家可控」必然跟着红：NetPlayerRegistry.Local 变成 null 了。
        # 它与「重开后玩家齐全」在这个注入下失败面重合，因为"扫场清玩家"是
        # 全端一致的动作——两条真正的分工在"只有某一端少人"时才分得开，
        # 那需要注入单端行为，本探针造不出来。如实记在报告 §5。
        "expect": {"房主:拆完玩家未被误清", "房主:重开后玩家齐全", "房主:重开后玩家可控",
                   "客户端1:重开后玩家齐全", "客户端1:重开后玩家可控",
                   "客户端2:重开后玩家齐全", "客户端2:重开后玩家可控",
                   "客户端3:重开后玩家齐全", "客户端3:重开后玩家可控",
                   "玩家数不足"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetProjectile>();\n",
                         "            n += SweepDespawn<NetProjectile>();\n"
                         "            %s 顺手把玩家也扫了——世界都拆了，玩家不也在世界里吗\n"
                         "            n += SweepDespawn<NetPlayer>();\n" % MARK)]),
        ],
    },
    {
        "id": "Q2",
        "name": "弹丸不参与扫场（M44 回归：4 人局下这条还灵不灵）",
        "expect": {"房主:拆完立刻无残留弹丸", "客户端1:重开后弹丸清零",
                   "客户端2:重开后弹丸清零", "客户端3:重开后弹丸清零"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetProjectile>();\n",
                         "            %s 弹丸不参与扫场\n" % MARK)]),
        ],
    },
    {
        "id": "Q3",
        "name": "敌人不参与扫场（M43 回归：只信注册表会漏掉当帧刚生成的那几个）",
        "expect": {"房主:拆完立刻无残留敌人"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetEnemy>();\n",
                         "            %s 敌人不参与扫场\n" % MARK)]),
        ],
    },
    {
        "id": "Q4",
        "name": "什么都不改（对照组：确认 4 人局本身不产生假红）",
        "expect": set(),
        "expect_rc": 0,
        "edits": [],
    },
]

# 驱动脚本的裁决文案 → 稳定的失败键。判据名那条走 JUDGE_RE，
# 这里只收"跨端核对"这类没有统一命名的问题。
PROBLEM_KEYS = (
    ("宝石计数与房主不一致", "双端宝石计数不一致"),
    ("回血球计数与房主不一致", "双端回血球计数不一致"),
    ("宝石峰值恒为 0", "客户端未收到同步"),
    ("波次与房主不一致", "双端波次不一致"),
    ("加速可能没生效", "世界未推进"),
    ("重开前房主波次只有", "世界未推进"),
    ("房主侧反生成数为", "未反生成"),
    ("重开后波次与房主不一致", "双端重开后波次不一致"),
    ("重开后世界状态对象数", "世界状态数异常"),
    ("重开后宝石远多于房主", "双端重开后宝石不一致"),
    ("重开前房主侧弹丸数为", "哨兵弹丸未生效"),
    # 人数键的文案是「房主侧重开后玩家 = 0（4 人局期望 4）」，用「人局期望」这个
    # 只在人数裁决里出现的串来匹配，免得跟 GROWTH 场景的「场上玩家」混淆。
    ("人局期望", "玩家数不足"),
    ("存在运行时异常", "运行时异常"),
    ("没有探针判据输出", "无探针输出"),
)

# 4 人局下端名带序号，键必须带序号——否则三个客户端的失败面会被合并成一个，
# "只红了其中一个客户端"这种情形就看不出来（那正是多人局最该抓的面）。
JUDGE_RE = re.compile(r"(房主|客户端\d+)侧判据「(.+?)」")


def read(path):
    with io.open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def backup(paths):
    for p in paths:
        bak = p + ".m45bak"
        if os.path.exists(bak):
            raise RuntimeError("备份已存在，上一次可能没还原: %s" % rel(bak))
        write(bak, read(p))


def restore(paths):
    for p in paths:
        bak = p + ".m45bak"
        if os.path.exists(bak):
            write(p, read(bak))
            os.remove(bak)


def apply_edits(case):
    for path, subs in case["edits"]:
        t = read(path)
        for old, new in subs:
            if t.count(old) < 1:
                raise RuntimeError("锚点未命中: %s :: %s" % (rel(path), old[:60]))
            t = t.replace(old, new, 1)
        write(path, t)


def assert_clean(case=None):
    """跑批前后都必须干净：残留的备份/注入会让下一组跑在被污染的代码上。"""
    allowed = {os.path.abspath(p) for p, _ in (case["edits"] if case else [])}
    for dirpath, _dn, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(".m45bak"):
                continue
            if n[: -len(".m45bak")] not in {os.path.basename(p) for p in allowed}:
                if os.path.join(dirpath, n[: -len(".m45bak")]) not in allowed:
                    raise RuntimeError("残留备份: %s" % rel(os.path.join(dirpath, n)))

    seen = set()
    for dirpath, _dn, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(".cs"):
                continue
            p = os.path.join(dirpath, n)
            if p in seen:
                continue
            seen.add(p)
            if MARK in read(p):
                raise RuntimeError("残留注入: %s" % rel(p))


def build():
    cmd = [UNITY, "-batchmode", "-nographics", "-quit",
           "-projectPath", os.path.join(ROOT, "StarPixelRogue"),
           "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessNetBuild",
           "-logFile", BUILD_LOG]
    r = subprocess.run(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    text = io.open(BUILD_LOG, encoding="utf-8", errors="replace").read()
    n = text.count("error CS")
    if n:
        for line in text.split("\n"):
            if "error CS" in line:
                print("    " + line.strip()[:150])
    return n == 0 and r.returncode == 0


def run_probe(seconds, speed, scene, clients):
    cmd = [sys.executable, os.path.join(ROOT, "net_longrun_probe.py"),
           "--scene", scene, "--seconds", str(seconds), "--speed", str(speed),
           "--clients", str(clients)]
    r = subprocess.run(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = r.stdout.decode("utf-8", errors="replace")
    return r.returncode, out


def fails_of(out):
    """从驱动脚本的裁决里抽失败键。判据的权威解释只有一份，不自己解析日志。"""
    keys = set()
    for line in out.split("\n"):
        if "[问题]" not in line:
            continue
        p = line.split("[问题]", 1)[1].strip()
        m = JUDGE_RE.search(p)
        if m:
            keys.add(m.group(1) + ":" + m.group(2))
            continue
        for needle, key in PROBLEM_KEYS:
            if needle in p:
                keys.add(key)
                break
        else:
            keys.add("其它:" + p[:40])
    return keys


def main():
    ap = argparse.ArgumentParser(description="M45 多人局拆世界判据区分力验证")
    ap.add_argument("--seconds", type=int, default=90, help="每组观察时长（真实秒）")
    ap.add_argument("--speed", type=float, default=6.0, help="加速倍率（房主侧）")
    ap.add_argument("--clients", type=int, default=3,
                    help="客户端数量（3 = 4 人局，本脚本的被测配置）")
    ap.add_argument("--only", help="只跑指定组，如 Q1")
    ap.add_argument("--skip-build", action="store_true", help="跳过重建（调试用）")
    args = ap.parse_args()

    cases = [c for c in CASES if not args.only or c["id"] == args.only]
    if not cases:
        print("[错误] --only %s 没有匹配的组" % args.only)
        return 2

    assert_clean()
    print("=" * 68)
    print("M45 多人局拆世界判据 · 区分力验证（%d 组）" % len(cases))
    print("  配置 %d 人局 / RESTART / %ds / %sx" % (args.clients + 1, args.seconds, args.speed))
    print("  每组：注入 → 重建 → 跑探针 → 核对失败面 → 还原")
    print("=" * 68)

    bad = 0
    actual = {}
    for case in cases:
        want_rc = case.get("expect_rc", 1)
        print("\n[%s] %s" % (case["id"], case["name"]))
        print("  场景 RESTART / %ds / %sx / %d 人局"
              % (args.seconds, args.speed, args.clients + 1))

        # 备份必须先于注入（M42 栽过一次：备份在注入之后，还原等于把缺陷写回去）
        touched = [p for p, _ in case["edits"]]
        backup(touched)
        try:
            apply_edits(case)
            # Q4 是对照组，没有注入——但同样要重建，否则跑的是上一组留下的产物
            # （M42 栽过一次：纯参数组跳过重建，结果每组都在跑上一组的 exe）。
            if not args.skip_build:
                print("  重建…", end="", flush=True)
                if not build():
                    print(" 失败（编译错误见 %s）" % rel(BUILD_LOG))
                    bad += 1
                    continue
                print(" 完成")
            rc, out = run_probe(args.seconds, args.speed, "RESTART", args.clients)
            keys = fails_of(out)
            actual[case["id"]] = keys
            # 每组都留一份完整输出：失败的组要按日志定位，通过的组也要留证据。
            with io.open(os.path.join(ROOT, "m45_%s.log" % case["id"]), "w",
                         encoding="utf-8", newline="\n") as f:
                f.write(out)
            ok = (rc == want_rc) and (keys == set(case["expect"]))
            print("  退出码 %d（期望 %d）" % (rc, want_rc))
            print("  实际失败面 %s" % (sorted(keys) or "（空）"))
            print("  预期失败面 %s" % (sorted(case["expect"]) or "（空）"))
            if ok:
                continue
            bad += 1
            print("  >>> 不一致", end="")
            if rc != want_rc:
                print("（驱动脚本的裁决与预期相反——这一组没被抓到 / 不该被抓到）")
            else:
                miss = set(case["expect"]) - keys
                extra = keys - set(case["expect"])
                if miss:
                    print(" 未报出: %s" % sorted(miss))
                if extra:
                    print(" 多报出: %s" % sorted(extra))
                if not miss and not extra:
                    print()
            print(out[-2000:] if len(out) > 2000 else out)
        finally:
            restore(touched)
            assert_clean(case)

    print("\n" + "=" * 68)
    print("失败面交叉核对")
    print("=" * 68)
    named = [c for c in CASES if c["id"] in actual]
    for i, a in enumerate(named):
        for b in named[i + 1:]:
            inter = actual[a["id"]] & actual[b["id"]]
            if inter:
                print("  [重叠] %s ∩ %s = %s" % (a["id"], b["id"], sorted(inter)))
                bad += 1
    if bad == 0:
        print("  各组失败面两两不相交")

    print("\n" + "=" * 68)
    print("结论：" + ("通过 —— 各组失败面均与预期一致且互不相交" if not bad
                     else "未通过（%d 项不符）" % bad))
    print("=" * 68)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
