#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M50 联机到点强制退出 · 区分力注入

做什么：
    每组只改一处产品代码 → 重新构建联机产物 → 跑一遍 net_longrun_probe.py --scene EXIT
    → 看**哪几条判据变红**。期望列是事先写好的，事后再挑就变成了解释而不是检验。

为什么 M50 的注入必须双开：
    EXIT 的核心判据是跨端的——"他走了吗"只有房主能回答。
    单端注入单端跑，永远测不出"客户端本地干净了、房主还以为他在场"这种形态。

用法：
    python m50_inject.py                # 全部四组
    python m50_inject.py --only Q2      # 只跑一组
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
EXE = os.path.join(PROJECT, "Build", "SmokeNet", "StarPixelRogue.exe")
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PY = sys.executable
BAK = ".m50bak"

GUARD = "Core/PlaytimeGuard.cs"
BACKEND = "Net/FishNetBackend.cs"

# 判据白名单（与 NetLongRunProbe.ReportExit / net_longrun_probe.py 的 EXIT 两面一致）
HOST_JUDGES = ("开局对端在场", "对端到点后已离开", "房主世界继续推进",
               "房主自身不受影响", "全程零异常")
CLIENT_JUDGES = ("青少年模式且对局计时在走", "到点前弹出一次预警", "到点恰好强制退出一次",
                 "强制退出后已离开会话", "强制退出后世界已拆",
                 "强制退出后本端玩家已清", "强制退出后主菜单可见且有提示", "全程零异常")
ALL_JUDGES = set(HOST_JUDGES) | set(CLIENT_JUDGES)

CASES = [
    dict(
        id="Q1", name="什么都不改（对照组）", edits=[], expect=set(),
        note="确认 EXIT 场景本身不产生假红；也确认双开没被上一轮残留污染",
    ),
    dict(
        id="Q2", name="到点只拆世界、不断连（退回 M47 行为）",
        edits=[(GUARD,
                "            if (networked) LeaveSession();\n",
                "            // 注入：不断连（这正是 M50 之前的形态）\n")],
        # 为什么期望里**没有**「全程零异常」，尽管首跑它红过、而且红得很有说服力：
        # 那 23,089 条 NRE 全部来自 `PlayerController.FireChain` —— 链闪电那条分支。
        # 换一局没拿到链闪电，`Update` 里根本不会走到那里，异常就是 0。
        # 重跑一次果然只剩三条红。**非确定性的后果不能进期望**：
        # 写进去就是给自己埋一个看运气的红（M44 的哨兵弹丸就是为此才存在——
        # 需要某个后果稳定出现时，得先把它变成确定的，而不是祈祷它出现）。
        # 这三条是确定性的：连接状态、场上玩家对象数、房主侧扫场人数。
        expect={"强制退出后已离开会话", "强制退出后本端玩家已清", "对端到点后已离开"},
        # 「全程零异常」在 Q2 下**时红时不红**：那 23,089 条 NRE 全部来自
        # `PlayerController.FireChain`，这一局没拿到链闪电就只有 0 条。
        # 所以它是"可选项"而不是期望——允许出现，不允许缺席别的东西。
        # 这不是给判据松绑：它只是承认"这一条在旧实现下取决于随机武器"。
        optional={"全程零异常"},
        note="本轮要修的那个缺陷本身。客户端本地大部分是绿的（世界拆了、菜单弹了），"
             "红的三条：还连着 / 自己那个角色还在场上 / 房主还看得见他。"
             "另有一条非确定性的放大器：拿到链闪电时，那个留在场上的角色每帧开火撞空引用",
    ),
    dict(
        id="Q3", name="断了，但不把会话模式归零",
        edits=[(BACKEND,
                "                        _stopHandled = true;\n"
                "                        NetSession.ResetToOffline();\n",
                "                        _stopHandled = true;\n"
                "                        // 注入：不归零（IsNetworked 会一直为真）\n")],
        expect={"强制退出后已离开会话"},
        note="M50 修的第 ① 个缺陷：旧 Stop() 先退订回调，这条分支永远走不到。"
             "只归零失败、断连正常时，房主那侧仍应绿——证明两侧判据各管一件事",
    ),
    dict(
        id="Q4", name="只断连、不拆世界",
        edits=[(GUARD,
                "            GameBuilder.TeardownWorld();\n",
                "            // 注入：不拆世界（只挂断连接）\n")],
        expect={"强制退出后世界已拆"},
        note="反方向：修好了'走'却忘了'收拾'。证明「世界已拆」不是跟着断连顺带绿的",
    ),
]

HOST_LOG = os.path.join(ROOT, "netlongrun_host.log")
CLIENT_LOG = os.path.join(ROOT, "netlongrun_client.log")
JUDGE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(是|否)\s*$")


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    return io.open(p, encoding="utf-8").read()


def write(p, t):
    io.open(p, "w", encoding="utf-8").write(t)


def case_files(case):
    return [os.path.normpath(os.path.join(SCRIPTS, f)) for f, _o, _n in case["edits"]]


def assert_clean(case=None):
    """注入前后都不许有备份 / 注入标记残留（多组可能落在同一文件上，故按文件判）。"""
    allowed = set(case_files(case)) if case is not None else set()
    for dirpath, _dirs, files in os.walk(SCRIPTS):
        for n in files:
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


def inject(case):
    """每处替换必须恰好命中 1 次（命中 0 处 = 源码已变，注入失效）。"""
    for f, old, new in case["edits"]:
        p = os.path.join(SCRIPTS, f)
        if not os.path.exists(p + BAK):
            shutil.copy2(p, p + BAK)
        t = read(p)
        n = t.count(old)
        if n != 1:
            raise RuntimeError("%s: %s 期望命中 1 处，实际 %d 处 —— 源码已变，注入失效"
                               % (case["id"], f, n))
        write(p, t.replace(old, new))


def restore_all():
    for dirpath, _dirs, files in os.walk(SCRIPTS):
        for n in files:
            if not n.endswith(BAK):
                continue
            src = os.path.join(dirpath, n)
            shutil.move(src, src[:-len(BAK)])
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def build():
    log = os.path.join(ROOT, "unity_build_m50_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessNetBuild",
                    "-logFile", log.replace("\\", "/")],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    txt = io.open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run():
    """跑一遍双开 EXIT。返回 (退出码, 输出)。"""
    try:
        p = subprocess.run([PY, os.path.join(ROOT, "net_longrun_probe.py"),
                            "--scene", "EXIT", "--seconds", "75"],
                           cwd=ROOT, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=420)
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return 99, out + "\n（超时）"


def red_judges():
    """从两端日志里收集值为「否」的判据名。

    只看**判据**（是/否 两态），不看信息行——信息行里也有"否"（如"首个异常 无"之外的字样），
    混进来会让失败面变脏。白名单再加一道。
    """
    red = set()
    for path in (HOST_LOG, CLIENT_LOG):
        if not os.path.exists(path):
            continue
        for line in io.open(path, encoding="utf-8", errors="replace"):
            m = JUDGE_RE.search(line)
            if not m:
                continue
            k, v = m.group(1).strip(), m.group(2)
            if k in ALL_JUDGES and v == "否":
                red.add(k)
    return red


def run_case(case):
    print("=" * 74)
    print("%s %s —— %s" % (case["id"], case["name"], case["note"]))
    print("=" * 74)

    inject(case)
    try:
        assert_clean(case)
        build()
        code, out = run()
        red = red_judges()
        verdict_lines = [l.strip() for l in out.splitlines() if l.startswith("结论：")]
        print("  探针退出码 %d / %s" % (code, verdict_lines[-1] if verdict_lines else "（无结论行）"))
        print("  期望失败面 %s" % (sorted(case["expect"]) or "（全绿）"))
        print("  实际失败面 %s" % (sorted(red) or "（全绿）"))
        optional = case.get("optional", set())
        if optional:
            print("  可选失败面 %s（出现与否取决于随机武器，不作判据）" % sorted(optional))
        ok = (red == case["expect"] or
              (case["expect"] <= red and (red - case["expect"]) <= optional))
        print("  → %s\n" % ("一致" if ok else "不一致"))
        return ok
    finally:
        restore_all()          # 每组必还原，否则下一组的"干净"是假的
        assert_clean(None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    args = ap.parse_args()

    ids = [s for s in args.only.split(",") if s]
    cases = [c for c in CASES if not ids or c["id"] in ids]
    if not cases:
        print("未知组: %s" % args.only)
        return 2
    if not os.path.exists(EXE):
        print("产物不存在: %s" % rel(EXE))
        return 2

    try:
        assert_clean(None)
    except RuntimeError as e:
        print("前置检查失败: %s" % e)
        return 2

    print("M50 联机到点强制退出 · 区分力注入 %d 组" % len(cases))
    all_ok = True
    try:
        for c in cases:
            try:
                all_ok = run_case(c) and all_ok
            except RuntimeError as e:
                print("  [中断] %s\n" % e)
                all_ok = False
    finally:
        # 收尾再还原一次 + 重建：交付物必须是**没有注入**的那份
        restore_all()
        assert_clean(None)
        build()
        print("已还原源码并重建产物（交付物无注入残留）")

    print("=" * 74)
    print("总计：%s" % ("全部与预期一致" if all_ok else "存在不一致，见上"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
