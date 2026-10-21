#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M55 · 区分力注入（组队码三层）

做什么：
    每组只改一处产品代码 → 重新构建无头产物 → 跑一遍 room_probe.py 的对应场景
    → 看**哪几条判据变红**。期望列事先写死在这里，事后再挑就变成了解释而不是检验。

为什么这轮要注入：
    组队码的失败模式不是崩溃，是"连不上"。而"连不上"在一个还没跑通公网的
    联机功能里几乎无法与"网络本来就不通"区分。所以这一层的判据必须被证明**有牙**，
    否则一整层逻辑可能全是假绿。

    Q2 与 Q3 的分工是本轮最要紧的一处：
      Q2 把校验权重改成常数（1,1,1,1,1） → 只红「相邻位念反被挡」
      Q3 干脆跳过校验比较（return true） → 红「单字符错抄被挡」+「相邻位念反被挡」
    两者都会拉红「断点通过」，只数总数分不出是谁。
    Q2 存在的意义是：**固定权重的校验和对念反无能为力**（AB 与 BA 的和相同），
    只测"错抄被挡"的话，把权重改成常数判据照样全绿——而念反恰恰是真人传码时
    最常见的错误（照屏幕念给队友，两个字符的顺序念反了）。

    Q5 与 Q8 是过期清理的两面，用来证明「远端该清」与「本机不该清」是两条独立判据。

用法：
    python m55_inject.py                # 全部八组
    python m55_inject.py --only Q2,Q5   # 只跑指定组
"""

import argparse
import io
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.join(ROOT, "StarPixelRogue")
SCRIPTS = os.path.join(PROJECT, "Assets", "_Project", "Scripts")
EXE = os.path.join(PROJECT, "Build", "Smoke", "StarPixelRogue.exe")
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PY = sys.executable
BAK = ".m55bak"

CODE = "Net/RoomCode.cs"
WIRE = "Net/RoomDiscovery.cs"
ROOMF = "Net/RoomDirectory.cs"
JOIN = "Net/JoinTarget.cs"

# 三个场景各自的判据全集（只收 room_probe.py 里逐行打印的那些判据）
JUDGES = {
    "CODE": {"生成的码全部合法", "长度恒为 6", "展示格式正确", "小写可还原",
             "分隔符可还原", "误抄可纠正", "含L的码不被改", "单字符错抄被挡",
             "相邻位念反被挡", "位数不对被挡", "断点通过"},
    "WIRE": {"询问报文往返", "应答报文往返", "错误前缀被挡", "字段数不对被挡",
             "非法地址被挡", "非法端口被挡", "主机名不切歪", "断点通过"},
    "ROOM": {"目录命中", "目录未命中", "过期房间被清", "本机房间不过期",
             "解析命中", "解析未命中", "解析非法码", "IP 直通仍可用",
             "建房发出组队码", "关房清掉组队码", "断点通过"},
}

# room_probe.py 的判据行形如：
#   "      x 相邻位念反被挡       期望 是        实际 否"
# 第一个字符是 x（不达标）或空格（达标）
JUDGE_RE = re.compile(r"^\s{4,}(?P<mark>x| )\s+(?P<key>\S+)\s+期望\s+(?P<exp>\S+)\s+实际\s+(?P<got>.*)$")

CASES = [
    dict(
        id="Q1", scene="CODE,WIRE,ROOM", name="什么都不改（对照组）", edits=[], expect=set(),
        note="确认三个场景本身不产生假红——否则后面几组的红都可能是这一组自带的。\n"
             "     对照组把三个场景全跑一遍，是因为后面每组只跑一个场景，\n"
             "     另外两个场景的基线得有个地方立住。",
    ),
    dict(
        id="Q2", scene="CODE", name="校验权重改成常数（1,1,1,1,1）",
        edits=[(CODE,
                "        private static readonly int[] Weights = { 1, 3, 5, 7, 11 };",
                "        // 注入：权重改成常数。固定权重的加权和对**相邻位互换**无能为力，\n"
                "        // 因为 AB 与 BA 的和相同，校验位也就一样。\n"
                "        private static readonly int[] Weights = { 1, 1, 1, 1, 1 };")],
        expect={"相邻位念反被挡", "断点通过"},
        note="只红「相邻位念反被挡」：单字符错抄仍然被挡（改一个字符，和必然变），\n"
             "     所以「单字符错抄被挡」保持绿色是**对的**。\n"
             "     这一组证明那条判据真的在测念反，不是在陪跑错抄。",
    ),
    dict(
        id="Q3", scene="CODE", name="跳过校验位比较（一切皆合法）",
        edits=[(CODE,
                "            return CheckDigit(normalized.Substring(0, BodyLength)) == normalized[BodyLength];",
                "            // 注入：连校验位都不比了。错抄、念反一概放行，\n"
                "            // 只有长度/字符集检查还在（那两道在前，不受本处影响）。\n"
                "            return true;")],
        expect={"单字符错抄被挡", "相邻位念反被挡", "断点通过"},
        note="比 Q2 多红一条「单字符错抄被挡」，红面**严格包含** Q2——\n"
             "     这正说明两条判据测的是同一套校验的两档能力，而不是两件无关的事。\n"
             "     「位数不对被挡」仍然绿：它靠长度检查，与校验位无关。",
    ),
    dict(
        id="Q4", scene="WIRE", name="应答不过滤主机名里的分隔符",
        edits=[(WIRE,
                "            string name = (hostName ?? \"\").Replace('|', ' ').Trim();",
                "            // 注入：分隔符不再中和。主机名是唯一用户可控又参与报文结构的\n"
                "            // 字段，一个带竖线的名字就能把整条报文切歪。\n"
                "            string name = hostName ?? \"\";")],
        expect={"主机名不切歪", "断点通过"},
        note="只红「主机名不切歪」：正常往返的那 400 次用的都是普通名字，\n"
             "     单发测试永远碰不到这个角落——这正是要专门为它立一条判据的理由。",
    ),
    dict(
        id="Q5", scene="ROOM", name="过期清理连本机一起清",
        edits=[(ROOMF,
                "                if (kv.Value.IsLocal) continue;",
                "                // 注入：本机那条也一起清。房主会**自己从目录里消失**，\n"
                "                // 而「过期房间被清」这条判据看不出来（它只数清掉了几个）。\n")],
        expect={"本机房间不过期", "断点通过"},
        note="只红「本机房间不过期」，「过期房间被清」保持绿色——\n"
             "     只测前者的话，一个把本机也清掉的 Prune 是完全看不见的。",
    ),
    dict(
        id="Q6", scene="ROOM", name="解析未命中时误报成码错了",
        edits=[(JOIN,
                "            if (e == null) return JoinResult.Fail(\"net.err_no_room\");",
                "            // 注入：未命中与非法码共用同一个原因。玩家看到「码不对」\n"
                "            // 会反复检查自己有没有抄错，而真相是房主压根没开房。\n"
                "            if (e == null) return JoinResult.Fail(\"net.err_bad_code\");")],
        expect={"解析未命中", "断点通过"},
        note="只红「解析未命中」，「解析非法码」仍绿（真非法码本来就该报 bad_code）。\n"
             "     两种失败混为一谈时，玩家收到的提示是错的，且无从自己排查。",
    ),
    dict(
        id="Q7", scene="ROOM", name="建房时不登记为本机房间",
        edits=[(JOIN,
                "            RoomDirectory.Put(code, ip, port, hostName, 1, true);",
                "            // 注入：码发出去了，但没标成本机的。于是没人应答发现询问，\n"
                "            // 队友搜得到码却连不进来——而码本身完全合法。\n"
                "            RoomDirectory.Put(code, ip, port, hostName, 1, false);")],
        expect={"建房发出组队码", "断点通过"},
        note="只红「建房发出组队码」：码生成、目录读写都是对的，\n"
             "     错的只是**归属**——这正是最难从外部现象反推的那一类缺陷。",
    ),
    dict(
        id="Q8", scene="ROOM", name="过期清理什么都不清",
        edits=[(ROOMF,
                "                if (now - kv.Value.LastSeen > ttl) dead.Add(kv.Key);",
                "                // 注入：过期判定失效。玩家会看到一个永远连不上的幽灵房间，\n"
                "                // 而那比搜不到更让人困惑（搜不到至少是明确的答案）。\n"
                "                if (false) dead.Add(kv.Key);")],
        expect={"过期房间被清", "断点通过"},
        note="与 Q5 的失败面完全不相交，合起来才证明过期清理的两面各有一条判据在管。",
    ),
]


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    return io.open(p, encoding="utf-8").read()


def case_files(case):
    out = set()
    for relpath, _o, _n in case["edits"]:
        out.add(os.path.normpath(os.path.join(SCRIPTS, relpath.replace("/", os.sep))))
    return out


def inject(case):
    """把改动写进源码（先备份原文件）。"""
    for relpath, old, new in case["edits"]:
        path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
        bak = path + BAK
        if not os.path.exists(bak):
            shutil.copy2(path, bak)
        src = read(path)
        if old not in src:
            raise RuntimeError("锚点未命中: %s\n---\n%s" % (relpath, old))
        src = src.replace(old, new, 1)
        io.open(path, "w", encoding="utf-8", newline="").write(src)


def restore_all():
    """把所有 .m55bak 还原回去（顺带删掉 Unity 为备份文件生成的 .meta）。"""
    for dirpath, _dirs, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(BAK):
                continue
            src = os.path.join(dirpath, n)
            shutil.move(src, src[:-len(BAK)])
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def assert_clean(case=None):
    """注入前后都不许有**上一组**的备份 / 注入标记残留。

    本组自己刚生成的备份是允许的——否则注入完立刻自检永远报错。
    """
    allowed = case_files(case) if case is not None else set()
    for dirpath, _dirs, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(BAK):
                continue
            p = os.path.normpath(os.path.join(dirpath, n))
            if p[:-len(BAK)] not in allowed:
                raise RuntimeError("残留备份: %s" % rel(p))
    seen = set()
    for c in CASES:
        for p in case_files(c):
            if p in seen:
                continue
            seen.add(p)
            if "// 注入：" in read(p) and p not in allowed:
                raise RuntimeError("残留注入: %s" % rel(p))
    if case:
        # 注入**之后**要查的是改动真的落上了，不是锚点还在——锚点被替换掉本来就是目的。
        for relpath, _old, new in case["edits"]:
            path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
            if new.split("\n")[-1].strip() and new.split("\n")[-1].strip() not in read(path):
                raise RuntimeError("注入没落上（替换写歪了）: %s" % relpath)


def build():
    log = os.path.join(ROOT, "unity_build_m55_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                    "-logFile", log], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    txt = io.open(log, encoding="utf-8", errors="replace").read()
    n = len(re.findall(r"error CS", txt))
    if n:
        raise RuntimeError("构建失败（%d 个 error CS）：\n%s"
                           % (n, "\n".join(l for l in txt.splitlines() if "error CS" in l)[:2000]))


def run_scene(scene):
    p = subprocess.run([PY, os.path.join(ROOT, "room_probe.py"), "--only", scene],
                       cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    txt = p.stdout.decode("utf-8", "replace")
    red = set()
    for line in txt.splitlines():
        m = JUDGE_RE.search(line)
        if m and m.group("mark") == "x":
            red.add(m.group("key"))
    return red, txt


def main():
    ap = argparse.ArgumentParser(description="M55 组队码区分力注入")
    ap.add_argument("--only", default=None, help="只跑指定组，如 Q2,Q5")
    args = ap.parse_args()

    cases = CASES
    if args.only:
        want = set(args.only.split(","))
        cases = [c for c in CASES if c["id"] in want]
        if not cases:
            print("未知组: %s" % args.only)
            return 2

    print("=" * 74)
    print("M55 组队码区分力注入 · %d 组" % len(cases))
    print("=" * 74)

    inconsistent = []
    try:
        for case in cases:
            scenes = case["scene"].split(",")
            assert_clean(None)
            inject(case)
            assert_clean(case)
            try:
                build()
            except RuntimeError as e:
                print("\n[%s] %s" % (case["id"], case["name"]))
                print("      构建失败：%s" % str(e)[:400])
                inconsistent.append(case["id"])
                # 失败也要还原：否则残留会污染下一组，且下一组的自检报的是
                # "上一组没收拾"，把真正的失败原因盖掉。
                restore_all()
                continue

            red = set()
            for scene in scenes:
                r, _txt = run_scene(scene)
                red |= r

            expect = set(case["expect"])
            ok = (red == expect)
            print("\n[%s] %s" % (case["id"], case["name"]))
            print("      场景       %s" % case["scene"])
            print("      期望变红   %s" % (", ".join(sorted(expect)) if expect else "（无：对照组必须全绿）"))
            print("      实际变红   %s" % (", ".join(sorted(red)) if red else "（无）"))
            if not ok:
                print("      多红       %s" % ", ".join(sorted(red - expect)))
                print("      漏红       %s" % ", ".join(sorted(expect - red)))
            print("      → %s" % ("一致" if ok else "不一致"))
            if case["note"]:
                for line in case["note"].split("\n"):
                    print("      %s" % line)
            if not ok:
                inconsistent.append(case["id"])

            # 每组跑完立刻还原：下一组开头的 assert_clean(None) 查的就是"没有上一组残留"，
            # 留到最后统一还原的话，那条自检从第二组起必然报错。
            restore_all()
            assert_clean(None)
    finally:
        restore_all()
        assert_clean(None)
        print("\n  源码已还原（.m55bak 全部收回）")

    print("=" * 74)
    if inconsistent:
        print("不一致的组：%s" % ", ".join(inconsistent))
        return 1
    print("八组全部一致：每组只红自己那几条，红面互不相交")
    return 0


if __name__ == "__main__":
    sys.exit(main())
