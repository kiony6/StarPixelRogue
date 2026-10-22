#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M56 · 区分力注入（传输层装配与目标分派）

做什么：
    每组只改一处产品代码 → 重新构建无头产物 → 跑 transport_probe.py 的对应场景
    → 看**哪几条判据变红**。期望列事先写死在这里，事后再挑就变成了解释而不是检验。

为什么这轮要注入：
    换传输层的失败模式不是崩溃，是**静默失效**——不报错、不打日志、点了加入没反应。
    这种失败与"网络本来就不通"在现象上完全一样，所以判据必须被证明有牙，
    否则整层逻辑可能全是假绿，等 EOS 凭据到了才发现一条都拦不住。

    最要紧的是 Q2：
      M56 之前的代码正是"只 AddComponent、从不给 TransportManager.Transport 赋值"，
      靠 FishNet 自己的 TryAddDefaultTransport 抓"场景里任意一个"兜住。
      只有一个传输层时这看起来完全正常——**判据要是抓不到它，这一整层就白做了**。
    Q3 是 Q2 的对偶：装不上时不许偷偷回落成 Tugboat。
      只测 Q2 的话，一个"失败就塞个 Tugboat"的实现照样全绿，
      而那会让"我在跑 EOS"变成"其实在跑局域网直连"。

用法：
    python m56_inject.py                # 全部九组
    python m56_inject.py --only Q2,Q6   # 只跑指定组
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
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PY = sys.executable
BAK = ".m56bak"

TRANS = "Net/NetTransport.cs"        # 说明书（纯数据）
SETUP = "Net/NetTransportSetup.cs"   # 装配与设参（整个文件在 FISHNET_ENABLED 里）
LOBBY = "Net/NetLobby.cs"

JUDGES = {
    "SPEC": {"未知种类无说明书", "Tugboat 不能跨公网", "EOS 能跨公网", "Tugboat 不用登录",
             "EOS 要登录", "Tugboat 有端口", "EOS 无端口", "EOS 不走客户端地址",
             "类型名互不相同", "Tugboat 类型可找到", "断点通过"},
    "INSTALL": {"装配后管理器认它", "装配类型正确", "重复装配不产生第二个",
                "缺 SDK 时装配失败", "失败时不许回落", "反查得到 Tugboat",
                "空管理器反查未知", "仅限局域网提示上屏", "能跨公网时提示为空", "断点通过"},
    "TARGET": {"Tugboat 目标设得上", "目标能读回", "种类不符时设不上",
               "设不上时不留副作用", "端口设得上", "端口不适用时不改",
               "非法端口被挡", "断点通过"},
}

# transport_probe.py 的判据行形如：
#   "      x 装配后管理器认它       期望 是        实际 是"
# 第一个非空白字符是 x（不达标）或空格（达标）
#
# 判据名允许**含空格**（"反查得到 Tugboat"、"EOS 能跨公网"），所以这里不能用 \S+。
# 用 \S+ 的后果不是报错，而是整行悄悄匹配不上：名字在第一个空格处被截断，
# 后面就撞不上「期望」，于是**变红的判据凭空消失**。
# 这正是第一轮跑出来的样子——源码里明明红了三条，脚本只看见两条。
JUDGE_RE = re.compile(r"^\s{4,}(?P<mark>x| )\s+(?P<key>.+?)\s+期望\s+(?P<exp>\S+)\s+实际\s+(?P<got>.*)$")

CASES = [
    dict(
        id="Q1", scene="SPEC,INSTALL,TARGET", name="什么都不改（对照组）", edits=[], expect=set(),
        note="确认三个场景本身不产生假红——否则后面几组的红都可能是这一组自带的。\n"
             "     对照组把三个场景全跑一遍，是因为后面每组只跑一个场景，\n"
             "     另外两个场景的基线得有个地方立住。",
    ),
    dict(
        id="Q2", scene="INSTALL", name="只装组件，不告诉管理器用哪一个",
        edits=[(SETUP,
                "            tm.Transport = t;\n            return true;",
                "            // 注入：这就是 M56 之前的代码——组件装上去了，但没人告诉\n"
                "            // TransportManager 该用哪一个。它会在初始化时自己抓\"场景里任意一个\"，\n"
                "            // 抓到谁取决于组件顺序。只有一个传输层时看起来完全正常。\n"
                "            return true;")],
        expect={"装配后管理器认它", "装配类型正确", "反查得到 Tugboat", "断点通过"},
        note="三红一连串：没赋值 -> Transport 为空 -> 认不出类型 -> 反查只能返回 Unknown。\n"
             "     这是**同一条判据的两端**（装上了 / 认的是它），不是三件独立的事。\n"
             "     「重复装配不产生第二个」仍绿：幂等逻辑在 EnsureOn 里，不依赖赋值。",
    ),
    dict(
        id="Q3", scene="INSTALL", name="缺 SDK 时悄悄回落成 Tugboat",
        edits=[(SETUP,
                "            var type = FindType(spec.TypeName);\n"
                "            if (type == null) { reason = \"type not found: \" + spec.TypeName + \" (SDK not installed)\"; return null; }",
                "            var type = FindType(spec.TypeName);\n"
                "            // 注入：找不到就回落成 Tugboat。于是\"我在跑 EOS\"变成\"其实在跑局域网直连\"，\n"
                "            // 而现象只有一个：跨公网连不上，且没有任何报错。\n"
                "            if (type == null) type = FindType(NetTransport.Describe(TransportKind.Tugboat).TypeName);\n"
                "            if (type == null) { reason = \"type not found: \" + spec.TypeName; return null; }")],
        expect={"缺 SDK 时装配失败", "失败时不许回落", "断点通过"},
        note="两红成对：装配\"成功\"了（其实是 Tugboat），管理器也真的认了它。\n"
             "     这正是最难从外部现象反推的那一类缺陷——所有内部状态都自洽，只有玩家连不上。\n"
             "     与 Q2 的失败面完全不相交。\n"
             "     「空管理器反查未知」保持绿：它用的是**从没装过东西**的第三个管理器，\n"
             "     不会因为这里回落了就被连带拉红。",
    ),
    dict(
        id="Q4", scene="SPEC", name="把 EOS 说成不能跨公网",
        edits=[(TRANS,
                "                        CanCrossInternet = true,       // EOS Connect 自带打洞 + 中继兜底",
                "                        // 注入：能力位说反了。UI 会因此对玩家提示\"仅限同一网络\"，\n"
                "                        // 而这条提示与真相相反——玩家会以为必须换网络才能一起玩。\n"
                "                        CanCrossInternet = false,")],
        expect={"EOS 能跨公网", "断点通过"},
        note="只红这一条：Tugboat 的能力位不受影响。\n"
             "     能力位是界面上那句话的唯一来源，说反了玩家就照着错的信息行事。",
    ),
    dict(
        id="Q5", scene="SPEC", name="把 EOS 改成走 SetClientAddress",
        edits=[(TRANS,
                "                        UsesClientAddress = false,     // SetClientAddress 被覆写成空实现",
                "                        // 注入：假装 EOS 也认 SetClientAddress。而 FishyEOS 的源码里\n"
                "                        // 那一行是 `_ = address;`——参数直接被扔掉，不报错、不连上。\n"
                "                        UsesClientAddress = true,")],
        expect={"EOS 不走客户端地址", "断点通过"},
        note="这是整轮调研的核心事实，专门为它立了一条判据：\n"
             "     FishyEOS 把 SetClientAddress 覆写成空实现，所以\"设目标\"必须走它自己的成员。\n"
             "     这张表一旦被谁\"简化\"成一句 SetClientAddress，换 EOS 就是全静默失败。",
    ),
    dict(
        id="Q6", scene="TARGET", name="设目标一律走 SetClientAddress",
        edits=[(SETUP,
                "            if (spec.UsesClientAddress)\n"
                "            {\n"
                "                // 类型对不上就没资格往这个实例上设地址（探针会故意喂这种组合）\n"
                "                if (!Matches(t, spec)) { reason = \"transport type mismatch: expected \" + spec.TypeName; return false; }\n"
                "                t.SetClientAddress(target ?? \"\");\n"
                "                return true;\n"
                "            }",
                "            // 注入：反正都是 Transport，一律走 SetClientAddress。\n"
                "            // 这正是换 EOS 时会踩的坑：FishyEOS 把这个参数直接扔掉，\n"
                "            // 于是不报错、不连上，和\"网络本来就不通\"完全无法区分。\n"
                "            t.SetClientAddress(target ?? \"\");\n"
                "            return true;")],
        expect={"种类不符时设不上", "设不上时不留副作用", "断点通过"},
        note="两红成对：既\"设上了\"（撒谎），又真的把地址改掉了（留下副作用）。\n"
             "     只测前一条的话，一个\"先改地址再返回 false\"的实现看不出来。",
    ),
    dict(
        id="Q7", scene="TARGET", name="端口不检查种类，一律设上去",
        edits=[(SETUP,
                "            var spec = NetTransport.Describe(kind);\n"
                "            if (spec == null) { reason = \"unknown transport kind: \" + (int)kind; return false; }\n"
                "            if (!spec.UsesPort) { reason = \"transport has no port concept: \" + spec.TypeName; return false; }\n"
                "            if (!Matches(t, spec)) { reason = \"transport type mismatch: expected \" + spec.TypeName; return false; }",
                "            var spec = NetTransport.Describe(kind);\n"
                "            if (spec == null) { reason = \"unknown transport kind: \" + (int)kind; return false; }\n"
                "            // 注入：不管这个种类有没有端口概念，一律设上去。\n"
                "            // 于是平台中继也能把 UDP 直连的端口顺手改掉——\"不用端口\"这件事就没有防线了。")],
        expect={"端口不适用时不改", "断点通过"},
        note="只红这一条：「端口设得上」仍绿（Tugboat 本来该设）。\n"
             "     注：探针把「非法端口被挡」排在这条**之前**跑，否则端口一旦被改掉，\n"
             "     那条也会跟着红——那是连锁，不是又抓到一个独立缺陷。",
    ),
    dict(
        id="Q8", scene="INSTALL", name="反查一律说成 Tugboat",
        edits=[(SETUP,
                "            if (tm == null || tm.Transport == null) return TransportKind.Unknown;",
                "            // 注入：认不出也说是 Tugboat。于是\"根本没装上\"这个状态消失了，\n"
                "            // 装配失败会被误当成装配成功，而对外一律显示为\"联机正常\"。\n"
                "            if (tm == null || tm.Transport == null) return TransportKind.Tugboat;")],
        expect={"空管理器反查未知", "断点通过"},
        note="只红这一条：「反查得到 Tugboat」仍绿（装上的那个本来就该是 Tugboat）。\n"
             "     \"认不出\"必须能被说出来，否则所有诊断都会指向一个不存在的传输层。",
    ),
    dict(
        id="Q9", scene="INSTALL", name="仅限局域网的提示恒显示",
        edits=[(LOBBY,
                "            _scopeText.text = NetTransport.CanCrossInternet ? \"\" : Core.Localization.T(\"net.lan_only\");",
                "            // 注入：提示恒显示。恒显示的提示等于没提示——\n"
                "            // 换了能跨公网的传输层之后，玩家仍被告知\"只能同一局域网\"。\n"
                "            _scopeText.text = Core.Localization.T(\"net.lan_only\");")],
        expect={"能跨公网时提示为空", "断点通过"},
        note="只红这一条：「仅限局域网提示上屏」仍绿。\n"
             "     两面对偶才说明这句话是真的在跟着能力位走，而不是硬写上去的。",
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
    """把所有 .m56bak 还原回去（顺带删掉 Unity 为备份文件生成的 .meta）。"""
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
        for relpath, _old, new in case["edits"]:
            path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
            if new.split("\n")[-1].strip() and new.split("\n")[-1].strip() not in read(path):
                raise RuntimeError("注入没落上（替换写歪了）: %s" % relpath)


def build():
    log = os.path.join(ROOT, "unity_build_m56_inject.log")
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
    p = subprocess.run([PY, os.path.join(ROOT, "transport_probe.py"), "--only", scene],
                       cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    txt = p.stdout.decode("utf-8", "replace")
    red = set()
    for line in txt.splitlines():
        m = JUDGE_RE.search(line)
        if m and m.group("mark") == "x":
            red.add(m.group("key"))
    return red, txt


def main():
    ap = argparse.ArgumentParser(description="M56 传输层区分力注入")
    ap.add_argument("--only", default=None, help="只跑指定组，如 Q2,Q6")
    args = ap.parse_args()

    cases = CASES
    if args.only:
        want = set(args.only.split(","))
        cases = [c for c in CASES if c["id"] in want]
        if not cases:
            print("未知组: %s" % args.only)
            return 2

    print("=" * 74)
    print("M56 传输层区分力注入 · %d 组" % len(cases))
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
        print("\n  源码已还原（.m56bak 全部收回）")

        # 还原源码**不等于**还原产物。最后留在 Build/Smoke 里的是"最后一组注入版"，
        # 紧接着跑回归探针就会拿它当干净基线——而且失败面正好是那一组改的地方，
        # 看起来像"产品代码有 bug"，实际只是产物没重建。这一步把那种可能堵死。
        try:
            build()
            print("  产物已按原源码重建")
        except RuntimeError as e:
            print("  !!! 还原后重建失败：%s" % str(e)[:300])
            inconsistent.append("restore-build")

    print("=" * 74)
    if inconsistent:
        print("不一致的组：%s" % ", ".join(inconsistent))
        return 1
    print("%d 组全部一致：每组只红自己那几条，红面互不相交" % len(cases))
    return 0


if __name__ == "__main__":
    sys.exit(main())
