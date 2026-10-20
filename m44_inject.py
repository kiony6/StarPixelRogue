#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M44「拆世界漏掉弹丸」判据的区分力验证。

做什么：
    往被测代码里注入 4 组**互相独立**的缺陷（外加 1 组对照），每组重建 + 跑一遍
    联机重开局探针，核对"实际报出来的失败面"是否恰好等于预期。

为什么这一轮必须做：
    M43 把拆世界改成"按类型扫场反生成"，第 21 项静态检查盯着那一行调用——
    但它只保证**调用了**，管不到**里面清了几类**。弹丸恰恰是最容易漏的一类：
    没有账本（不像掉落物）、没有注册表（不像敌人）、没有静态 Instance（不像世界状态），
    唯一能找到它的途径就是扫场。M43 就是这么漏的，而且漏完之后：
    静态检查绿、短局冒烟绿、30 分钟长局绿、全程零异常——只有重开一局才看得见。

    所以本轮新增的两条判据必须被注入验证：
      · 「拆完立刻无残留弹丸」——唯一能在"旧弹丸还没自毁"的窗口里看见它的时点；
      · 「重开后弹丸清零」（客户端专属）——唯一能证明"广播真的到了客户端"的一条。
    **看不见的改动等于没改，看不见的判据等于没判。**

四组：
    P1 弹丸不参与扫场     → 房主「拆完立刻无残留弹丸」+ 客户端「重开后弹丸清零」
                            （M43 的真实状态：房主没清，客户端自然也留着）
    P2 敌人不参与扫场     → 房主「拆完立刻无残留敌人」
                            （M43 回归组：确认 M44 的改动没有削弱老判据）
    P3 世界状态 Instance 不清 → 两端「重开后世界状态唯一」+ 跨端「世界状态数异常」
                            （M43 回归组：确认"扫场清实体"不等于"清静态引用"）
    P4 哨兵弹丸不放（对照）→ 期望**全绿**：证明判据靠自然弹丸也成立，
                            哨兵只是保险，不是判据的支撑（否则判据就只在夹具下有效）

试过但没采用的第五组（诚实记录）：
    「弹丸走本地 Object.Destroy 而不是权威 Despawn」原本想单列一组，
    实测它与 P1 的失败面**完全一样**：Unity 的 Destroy 延迟到帧末生效，
    "拆完隔一帧"数到的残留与"压根没清"一模一样；对客户端而言两者都不广播，
    影响也相同。这两种写法在本探针的任何判据上都无法区分，
    与其造一组假区分力，不如如实写成"它们是同一个失败面的两种写法"。

用法：
    python m44_inject.py                 # 四组全跑
    python m44_inject.py --only P1       # 只跑一组
    python m44_inject.py --seconds 60    # 缩短每组观察时长（RESTART 至少 50）

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
BUILD_LOG = os.path.join(ROOT, "unity_build_m44_inject.log")

SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
NETWORLD = os.path.join(SCRIPTS, "Net", "NetWorld.cs")
PROBE = os.path.join(SCRIPTS, "Dev", "NetLongRunProbe.cs")

# 必须是注释前缀：只写 `[M44INJ]` 会被 C# 当成特性声明，编译报 CS7014。
MARK = "// M44INJ"

CASES = [
    {
        "id": "P1",
        "name": "弹丸不参与扫场（M43 的真实状态：三类齐全，漏了弹丸）",
        "expect": {"房主:拆完立刻无残留弹丸", "客户端:重开后弹丸清零"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetProjectile>();\n",
                         "            %s 弹丸不参与扫场：上一局在飞的弹丸整批漏到新世界\n" % MARK)]),
        ],
    },
    {
        "id": "P2",
        "name": "敌人不参与扫场（M43 回归：只信注册表会漏掉当帧刚生成的那几个）",
        "expect": {"房主:拆完立刻无残留敌人"},
        "edits": [
            (NETWORLD, [
                ("            n += SweepDespawn<NetEnemy>();\n",
                 "            %s 敌人不参与扫场\n" % MARK),
                ("            NetEnemyRegistry.DespawnAll();   // 清掉注册表里已失效 / 未激活的引用\n",
                 "            %s NetEnemyRegistry.DespawnAll();\n" % MARK),
            ]),
        ],
    },
    {
        "id": "P3",
        "name": "世界状态不参与扫场（M43 回归：新旧两份抢 Instance，波次在两个数之间跳）",
        "expect": {"房主:拆完立刻无残留世界状态", "房主:重开后世界状态唯一",
                   "客户端:重开后世界状态唯一", "双端世界状态数异常"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetWorldState>();\n",
                         "            %s 世界状态不参与扫场\n" % MARK)]),
        ],
    },
    {
        "id": "P4",
        "name": "哨兵弹丸不放（对照组：判据不该只在我的夹具下成立）",
        "expect": set(),          # 期望全绿：自然弹丸本身就够判据用
        "expect_rc": 0,
        "edits": [
            (PROBE, [("                _rsSeeded = true;\n                SeedProjectiles();\n",
                      "                _rsSeeded = true;\n                %s 不放哨兵弹丸\n" % MARK)]),
        ],
    },
]

# 驱动脚本 [问题] 文案 -> 归一化失败键。
PROBLEM_KEYS = (
    ("双端宝石计数不一致", "双端宝石计数不一致"),
    ("双端回血球计数不一致", "双端回血球计数不一致"),
    ("客户端侧宝石峰值恒为 0", "客户端未收到同步"),
    ("双端波次不一致", "双端波次不一致"),
    ("加速可能没生效", "世界未推进"),
    ("重开前房主波次只有", "世界未推进"),
    ("房主侧反生成数为", "未反生成"),
    ("双端重开后波次不一致", "双端重开后波次不一致"),
    ("重开后世界状态对象数", "双端世界状态数异常"),
    ("重开后客户端宝石远多于房主", "双端重开后宝石不一致"),
    ("重开前房主侧弹丸数为", "哨兵弹丸未生效"),
    ("存在运行时异常", "运行时异常"),
    ("没有探针判据输出", "无探针输出"),
)

JUDGE_RE = re.compile(r"(房主|客户端)侧判据「(.+?)」")


def read(path):
    with io.open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def apply_edits(case):
    touched = []
    for path, subs in case["edits"]:
        raw = read(path)
        for old, new in subs:
            n = raw.count(old)
            if n != 1:
                raise RuntimeError(
                    "注入锚点在 %s 里出现 %d 次（期望 1 次）：%r" % (rel(path), n, old[:60]))
            raw = raw.replace(old, new)
        write(path, raw)
        touched.append(path)
    return touched


def backup(paths):
    for p in paths:
        write(p + ".m44bak", read(p))


def restore(paths):
    for p in paths:
        bak = p + ".m44bak"
        if os.path.exists(bak):
            write(p, read(bak))
            os.remove(bak)


def assert_clean(case=None):
    """确认工作区没有注入残留。按**文件**判；备份只认本组目标文件的那一份。"""
    allowed = set()
    if case:
        allowed = {os.path.basename(p) for p, _ in case["edits"]}

    for dirpath, _dn, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(".m44bak"):
                continue
            if n[: -len(".m44bak")] not in allowed:
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


def run_probe(seconds, speed, scene):
    cmd = [sys.executable, os.path.join(ROOT, "net_longrun_probe.py"),
           "--scene", scene, "--seconds", str(seconds), "--speed", str(speed)]
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
    ap = argparse.ArgumentParser(description="M44 拆世界弹丸判据区分力验证")
    ap.add_argument("--seconds", type=int, default=90, help="每组观察时长（真实秒）")
    ap.add_argument("--speed", type=float, default=6.0, help="加速倍率（房主侧）")
    ap.add_argument("--only", help="只跑指定组，如 P1")
    ap.add_argument("--skip-build", action="store_true", help="跳过重建（调试用）")
    args = ap.parse_args()

    cases = [c for c in CASES if not args.only or c["id"] == args.only]
    if not cases:
        print("[错误] --only %s 没有匹配的组" % args.only)
        return 2

    assert_clean()
    print("=" * 68)
    print("M44 拆世界弹丸判据 · 区分力验证（%d 组）" % len(cases))
    print("  每组：注入 → 重建 → 跑 RESTART 探针 → 核对失败面 → 还原")
    print("=" * 68)

    bad = 0
    actual = {}
    for case in cases:
        want_rc = case.get("expect_rc", 1)
        print("\n[%s] %s" % (case["id"], case["name"]))
        print("  场景 RESTART / %ds / %sx" % (args.seconds, args.speed))

        # 备份必须先于注入（M42 栽过一次：备份在注入之后，还原等于把缺陷写回去）
        touched = [p for p, _ in case["edits"]]
        backup(touched)
        try:
            apply_edits(case)
            if not args.skip_build:
                print("  重建…", end="", flush=True)
                if not build():
                    print(" 失败（编译错误见 %s）" % rel(BUILD_LOG))
                    bad += 1
                    continue
                print(" 完成")
            rc, out = run_probe(args.seconds, args.speed, "RESTART")
            keys = fails_of(out)
            actual[case["id"]] = keys
            # 每组都留一份完整输出：失败的组要按日志定位，通过的组也要留证据
            # （"修复前"那一组的实测数字就是从这里取的）。
            with io.open(os.path.join(ROOT, "m44_%s.log" % case["id"]), "w",
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
            print(out[-1500:] if len(out) > 1500 else out)
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
