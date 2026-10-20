#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M42 联机长局判据的区分力验证。

做什么：
    往被测代码里注入 4 组**互相独立**的缺陷，每组重建 + 跑一遍联机长局探针，
    核对"实际报出来的失败面"是否恰好等于预期。

为什么必须做这一步：
    判据全绿有两种可能——"没问题"和"判据根本不具备发现问题的能力"。
    两者从结果上看一模一样。M41 在这里栽过最狠的一跤：注入脚本的解析器
    照抄了 Python 端渲染的表格格式，而它跑的其实是 exe 本体（只输出
    `[SMOKE] 名字  是/否`），结果四组全部假绿，差点得出"判据没有区分力"
    的反向结论。**验证工具的盲区比产品盲区更危险**——它把"没抓到"伪装成"没问题"。

    本轮为此做了两件事：
      1) 不自己解析日志，直接以驱动脚本 net_longrun_probe.py 的**最终裁决**
         （"结论：未通过" + [问题] 列表）为准——判据的权威解释只有一份。
      2) 每组跑完立刻 `assert_clean()`：确认源码已还原、无残留备份、无注入标记。
         注入物一旦漏在工作区里，后面所有回归都是假的。

四组失败面（互不相交，各自对应一种真实的坏写法）：
    R1 联机宝石无上限      → 房主/客户端「宝石有界」+ 房主「末段贴顶后不再抬升」
                            （M41 单机侧修掉、联机侧原样存在的那个缺陷）
    R2 联机回血球无上限    → 「回血球有界」
                            （两类共用"只进不出"的写法，只修一半是最容易发生的漏修）
    R3 世界没跑起来        → 「波次确实推进」+ 驱动脚本的"波次只到 N"
                            （不给加速：60s/1x 下宝石堆不到上限，所有"有界"判据会全绿——
                             若没有这一条守着，它和"真的没问题"长得一模一样。
                             这也是为什么「上限真的生效」在这里必须判"不适用"：
                             没贴到顶的世界里回收从未发生，计数为 0 说的是"世界太小"，
                             不是"上限失效"——这一格是本轮靠 R1 的第一次跑撞出来才改对的）
    R4 客户端自行销毁联机对象 → 仅「双端宝石计数不一致」
                            （静态检查第 20 项要抓的写法；注意它让客户端**偏少**，
                             单向的交叉核对会完全漏掉这一面）

用法：
    python m42_inject.py                 # 四组全跑
    python m42_inject.py --only R4        # 只跑一组
    python m42_inject.py --seconds 60     # 缩短每组的观察时长

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
BUILD_LOG = os.path.join(ROOT, "unity_build_m42_inject.log")

SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
NETWORLD = os.path.join(SCRIPTS, "Net", "NetWorld.cs")
NETPICKUP = os.path.join(SCRIPTS, "Net", "NetPickup.cs")

# 必须是**注释**前缀：只写 `[M42INJ]` 会被 C# 当成特性声明，编译报
# CS7014 "Attributes are not valid in this context"（本轮第一次跑就撞上）。
MARK = "// M42INJ"

CASES = [
    {
        "id": "R1",
        "name": "联机宝石无上限（删掉宝石侧的回收调用）",
        # 「上限真的生效」必然一起红：回收调用被摘掉，计数器自然也是 0——
        # 这不是判据冗余，而是"上限真的没生效"的同一件事在两个口径上各自报了一次。
        "expect": {"房主:宝石有界", "房主:末段贴顶后不再抬升", "房主:上限真的生效",
                   "客户端:宝石有界"},
        "edits": [
            (NETWORLD, [("            TrimGems();\n",
                         "            %s TrimGems();\n" % MARK)]),
        ],
    },
    {
        "id": "R2",
        "name": "联机回血球无上限（删掉回血球侧的回收调用）",
        "expect": {"房主:回血球有界", "客户端:回血球有界"},
        "edits": [
            (NETWORLD, [("            TrimHealth();\n",
                         "            %s TrimHealth();\n" % MARK)]),
        ],
    },
    {
        "id": "R3",
        "name": "世界没跑起来（不给加速，波次推不动）",
        # 这一组**不改代码**，只把驱动参数调成 1x：它要验的是"增长类判据在世界没跑起来时
        # 会假绿"这个判断本身。60s/1x 下波次只到个位数、宝石堆不到上限，
        # 「宝石有界」「回血球有界」会全绿——若没有「波次确实推进」这一条守着，
        # 这一局会和"真的没问题"长得一模一样。
        # 也正因如此，「上限真的生效」在这里是"不适用"而不是"否"：
        # 没贴到顶的世界里回收从未发生，计数为 0 报的是"世界太小"而非"上限失效"。
        "speed": 1.0,
        "expect": {"房主:波次确实推进", "世界未推进"},
        "edits": [],
    },
    {
        "id": "R4",
        "name": "客户端自行 Destroy 联机宝石（静查第 20 项要抓的写法）",
        "expect": {"双端宝石计数不一致"},
        "edits": [
            (NETPICKUP, [
                ("        private const float MagnetSpeed = 12f;\n",
                 "        private const float MagnetSpeed = 12f;\n"
                 "        private float _injAge;   // %s\n" % MARK),
                ("            if (!IsServerInitialized) return;   "
                 "// 纯客户端只跟随位置同步，不做本地判定\n",
                 "            if (!IsServerInitialized)\n"
                 "            {\n"
                 "                // %s 客户端自行销毁联机对象：房主不知情，双端计数分叉\n"
                 "                _injAge += Time.deltaTime;\n"
                 "                if (_injAge > 2f && _kind.Value == NetPickupKind.Xp)\n"
                 "                    Object.Destroy(gameObject);\n"
                 "                return;\n"
                 "            }\n" % MARK),
            ]),
        ],
    },
]

# 驱动脚本 [问题] 文案 -> 归一化失败键。
# 键必须稳定：注入脚本靠它比对"实际失败面"与"预期失败面"。
PROBLEM_KEYS = (
    ("双端宝石计数不一致", "双端宝石计数不一致"),
    ("双端回血球计数不一致", "双端回血球计数不一致"),
    ("客户端侧宝石峰值恒为 0", "客户端未收到同步"),
    ("双端波次不一致", "双端波次不一致"),
    ("加速可能没生效", "世界未推进"),
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
        write(p + ".m42bak", read(p))


def restore(paths):
    for p in paths:
        bak = p + ".m42bak"
        if os.path.exists(bak):
            write(p, read(bak))
            os.remove(bak)


def assert_clean(case=None):
    """
    确认工作区里没有注入残留。

    按**文件**判而不是按"组"判：M41 里 R1/R2 同时改 NetWorld.cs，按组判重会误报
    "残留注入"。同理，备份文件也只认**本组目标文件**的那一份——
    别人的 .m42bak 才是真残留。
    """
    allowed = set()
    if case:
        allowed = {os.path.basename(p) for p, _ in case["edits"]}

    for dirpath, _dn, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(".m42bak"):
                continue
            if n[: -len(".m42bak")] not in allowed:
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


def run_probe(seconds, speed):
    cmd = [sys.executable, os.path.join(ROOT, "net_longrun_probe.py"),
           "--seconds", str(seconds), "--speed", str(speed)]
    r = subprocess.run(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = r.stdout.decode("utf-8", errors="replace")
    return r.returncode, out


def fails_of(out):
    """从驱动脚本的裁决里抽失败键。判据的权威解释只有这一份，不自己解析日志。"""
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
    ap = argparse.ArgumentParser(description="M42 联机长局判据区分力验证")
    ap.add_argument("--seconds", type=int, default=60, help="每组观察时长（真实秒）")
    ap.add_argument("--speed", type=float, default=6.0, help="加速倍率（房主侧）")
    ap.add_argument("--only", help="只跑指定组，如 R4")
    ap.add_argument("--skip-build", action="store_true", help="跳过重建（调试用）")
    args = ap.parse_args()

    cases = [c for c in CASES if not args.only or c["id"] == args.only]
    if not cases:
        print("[错误] --only %s 没有匹配的组" % args.only)
        return 2

    assert_clean()
    print("=" * 68)
    print("M42 联机长局判据 · 区分力验证（%d 组）" % len(cases))
    print("  每组：注入 → 重建 → 跑 %ds/%sx → 核对失败面 → 还原" % (args.seconds, args.speed))
    print("=" * 68)

    bad = 0
    for case in cases:
        print("\n[%s] %s" % (case["id"], case["name"]))
        # 备份必须在注入**之前**取：本轮第一次跑把它放在了 apply_edits 之后，
        # 于是备份里存的是注入后的内容，"还原"等于把缺陷又写了一遍，
        # 工作区从此再没干净过（assert_clean 立刻把它抓出来了）。
        touched = [p for p, _ in case["edits"]]
        backup(touched)
        apply_edits(case)
        speed = case.get("speed", args.speed)
        try:
            # 无条件重建：纯参数组（R3）虽然没改代码，但上一组注入的产物还留在
            # Build 目录里——还原的只是源码，exe 不会自己变回去。
            # 少了这一步，R3 跑的其实是 R2 的注入版，失败面会张冠李戴。
            if not args.skip_build:
                print("  重建…", end="", flush=True)
                if not build():
                    print(" 失败（编译错误见 %s）" % rel(BUILD_LOG))
                    bad += 1
                    continue
                print(" 完成")
            rc, out = run_probe(args.seconds, speed)
            keys = fails_of(out)
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

    # 四组互相比较：任一组的失败面若包含另一组，说明其中一条判据是冗余的
    print("\n" + "=" * 68)
    print("失败面交叉核对")
    print("=" * 68)
    for i, a in enumerate(CASES):
        for b in CASES[i + 1:]:
            inter = a["expect"] & b["expect"]
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
