#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M43 联机重开局 / 玩家遍历判据的区分力验证。

做什么：
    往被测代码里注入 4 组**互相独立**的缺陷，每组重建 + 跑一遍联机探针，
    核对"实际报出来的失败面"是否恰好等于预期。

为什么这一轮必须做：
    M43 修的是"拆世界时不反生成联机实体"——一个**只在重开时才出现**的缺陷。
    它不会在 45 秒冒烟里露头，也不会在 30 分钟长局里露头（那局根本没重开过），
    唯一的观测点就是"重开一局之后"。同理，"每帧只建一次玩家快照"是个纯成本改动，
    改前改后所有玩法判据都一样绿——只有专门的计数器判据才看得见它。

    所以这两类判据都必须被注入验证：**看不见的改动等于没改，看不见的判据等于没判。**

四组失败面（互不相交，各自对应一种真实的坏写法）：
    S1 宝石不反生成（RESTART）  → 房主/客户端「重开后掉落物清零」
                                 （M42 §9 留的盲区：ResetPickups 清了账本、没清实体）
    S2 世界状态不反生成（RESTART）→ 房主/客户端「重开后世界状态唯一」
                                 （旧的没走 Despawn，新的抢不到 Instance，波次在两份之间跳）
    S3 敌人不反生成（RESTART）   → 房主/客户端「重开后敌人清空」
                                 （NetEnemy 不在 EnemyManager.ActiveEnemies 里，
                                  拆世界时被完整漏掉——正是本轮的真实成因）
    S4 玩家快照不去重（GROWTH）  → 房主「玩家遍历不随掉落物增长」
                                 （退回"每个掉落物各自遍历注册表"，成本随数量线性涨而行为不变）

用法：
    python m43_inject.py                 # 四组全跑
    python m43_inject.py --only S1       # 只跑一组
    python m43_inject.py --seconds 60    # 缩短每组观察时长（RESTART 至少 40）

退出码：
    0 = 四组失败面均与预期一致；1 = 有不一致 / 跑批失败；2 = 前置条件不满足
"""

import argparse
import io
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
BUILD_LOG = os.path.join(ROOT, "unity_build_m43_inject.log")

SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
NETWORLD = os.path.join(SCRIPTS, "Net", "NetWorld.cs")

# 必须是注释前缀：只写 `[M43INJ]` 会被 C# 当成特性声明，编译报 CS7014。
MARK = "// M43INJ"

CASES = [
    {
        "id": "S1",
        "name": "拆世界不反生成宝石（账本清了、实体还在）",
        "scene": "RESTART",
        "expect": {"房主:拆完立刻无残留掉落物", "房主:重开后掉落物清零",
                   "客户端:重开后掉落物清零"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetPickup>();\n",
                         "            %s 宝石不反生成：跳过扫场，实体留在场上\n" % MARK)]),
        ],
    },
    {
        "id": "S2",
        "name": "拆世界不反生成世界状态（新旧两份抢 Instance）",
        "scene": "RESTART",
        "expect": {"房主:拆完立刻无残留世界状态", "房主:重开后世界状态唯一",
                   "客户端:重开后世界状态唯一", "双端世界状态数异常"},
        "edits": [
            (NETWORLD, [("            n += SweepDespawn<NetWorldState>();\n",
                         "            %s 世界状态不反生成\n" % MARK)]),
        ],
    },
    {
        "id": "S3",
        "name": "拆世界不反生成敌人（NetEnemy 不在 ActiveEnemies 里）",
        "scene": "RESTART",
        "expect": {"房主:拆完立刻无残留敌人"},
        "edits": [
            (NETWORLD, [
                ("            n += SweepDespawn<NetEnemy>();\n",
                 "            %s 敌人不反生成\n" % MARK),
                ("            NetEnemyRegistry.DespawnAll();   // 清掉注册表里已失效 / 未激活的引用\n",
                 "            %s NetEnemyRegistry.DespawnAll();\n" % MARK),
            ]),
        ],
    },
    {
        "id": "S4",
        "name": "玩家快照每帧不去重（退回逐掉落物遍历）",
        "scene": "GROWTH",
        "expect": {"房主:玩家遍历不随掉落物增长"},
        "edits": [
            (NETWORLD, [("            if (_snapshotFrame == frame) return;\n",
                         "            %s 去掉每帧去重：每条掉落物各建一次快照\n" % MARK)]),
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
        write(p + ".m43bak", read(p))


def restore(paths):
    for p in paths:
        bak = p + ".m43bak"
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
            if not n.endswith(".m43bak"):
                continue
            if n[: -len(".m43bak")] not in allowed:
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
    ap = argparse.ArgumentParser(description="M43 联机重开局判据区分力验证")
    ap.add_argument("--seconds", type=int, default=0,
                    help="每组观察时长（真实秒）；0 = 按场景取默认（RESTART 90 / GROWTH 60）")
    ap.add_argument("--speed", type=float, default=6.0, help="加速倍率（房主侧）")
    ap.add_argument("--only", help="只跑指定组，如 S1")
    ap.add_argument("--skip-build", action="store_true", help="跳过重建（调试用）")
    args = ap.parse_args()

    cases = [c for c in CASES if not args.only or c["id"] == args.only]
    if not cases:
        print("[错误] --only %s 没有匹配的组" % args.only)
        return 2

    assert_clean()
    print("=" * 68)
    print("M43 联机重开局判据 · 区分力验证（%d 组）" % len(cases))
    print("  每组：注入 → 重建 → 跑探针 → 核对失败面 → 还原")
    print("=" * 68)

    bad = 0
    actual = {}
    for case in cases:
        scene = case["scene"]
        seconds = args.seconds or (90 if scene == "RESTART" else 60)
        print("\n[%s] %s" % (case["id"], case["name"]))
        print("  场景 %s / %ds / %sx" % (scene, seconds, args.speed))

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
            rc, out = run_probe(seconds, args.speed, scene)
            keys = fails_of(out)
            actual[case["id"]] = keys
            ok = (rc == 1) and (keys == case["expect"])
            print("  退出码 %d（期望 1）" % rc)
            print("  实际失败面 %s" % (sorted(keys) or "（空）"))
            print("  预期失败面 %s" % sorted(case["expect"]))
            if not ok:
                bad += 1
                print("  >>> 不一致", end="")
                if rc != 1:
                    print("（驱动脚本没有判失败——这一组没被抓到）")
                else:
                    miss = case["expect"] - keys
                    extra = keys - case["expect"]
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
        print("  四组失败面两两不相交")

    print("\n" + "=" * 68)
    print("结论：" + ("通过 —— 四组失败面均与预期一致且互不相交" if not bad
                     else "未通过（%d 项不符）" % bad))
    print("=" * 68)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
