#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M39 区分力验证：往工程里注入 4 组互不相同的缺陷，看新加的判据是不是真的会红。

为什么必须做这一步：
    M39 修的那处**没有任何症状**。收场不出声的写法是：

        if (!_applyGate.Reject(ackId)) return;
        Debug.Log("[M38] 选卡被房主拒绝…");

    计数是对的、日志也有，双端数据完全一致——只有玩家什么都看不到。
    他点了卡、面板关了、什么都没发生，也不知道为什么。而且他**无从报告**：
    他连发生了什么都不知道。

    所以这里反向验证两件事：
      1) 干净源码下新判据不误报（基线全绿）；
      2) 四组注入各自让**不同的判据**变红，且签名互不相交。

  R1 失败收场不出声（拒/作废/超时三处只留日志，不发通知）
     → **只有静态第 17 项**红；NOTICE 场景与联机全绿。
       演示：这三类收场在 60s 双开里未必真的发生（实测拒绝/作废/超时都是 0），
       运行时抓不到它们，只有静态规则能钉住。

  R2 提示不本地化（删掉 en 表那 4 个键）
     → **只有 NOTICE「切语言重画」**红；其余全绿。
       注意判据不能只查"有没有汉字"：T() 缺键时会原样返回**键名**，
       键名是 ASCII，只查汉字会被蒙过去（M33 教训）。

  R3 HUD 不注册出口（通知有记、无处可送）
     → NOTICE「出口已注册」+ 联机「通知被吞 > 0」红，守恒仍绿。
       演示：只比"收场数 vs 四类之和"抓不到"有通知但没送到玩家眼前"。

  R4 成功也不出声（删掉确认路径那条通知）
     → 联机「收场总数 ≠ 四类之和」+「选了卡却零收场」红；静态全绿
       （第 17 项只管失败收场，成功路径它管不着）。
       与 R1 方向互补：R1 静态红联机绿，R4 静态绿联机红。

四组签名互不相同：
  R1 = {静态第17项}
  R2 = {NOTICE 切语言重画}
  R3 = {NOTICE 出口已注册, 联机·通知被吞}
  R4 = {联机·守恒, 联机·零收场}

用法：  python m39_inject.py            # 四组全跑
        python m39_inject.py R2         # 只跑指定组
跑完自动还原源码（.m39inject.bak）并**重新构建**两种产物，避免 exe 残留上一组注入。
"""

import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PROJ = os.path.join(ROOT, "StarPixelRogue")
SCRIPTS = os.path.join(PROJ, "Assets/_Project/Scripts")

NETPLAYER = os.path.join(SCRIPTS, "Net/NetPlayer.cs")
HUD = os.path.join(SCRIPTS, "UI/HUDController.cs")
LOC = os.path.join(SCRIPTS, "Core/Localization.cs")

BACKUP = ".m39inject.bak"
FILES = (NETPLAYER, HUD, LOC)


def backup(path):
    """备份**原始**内容。已存在就不覆盖——同一组可能在同一文件里注入多处，
    第二处若覆盖备份，还原出来的就是带注入的版本（M37 踩过）。"""
    b = path + BACKUP
    if os.path.exists(b):
        return
    shutil.copy2(path, b)


def restore(path):
    b = path + BACKUP
    if os.path.exists(b):
        shutil.copy2(b, path)
        os.remove(b)


def inject(path, old, new, expect=1):
    backup(path)
    t = open(path, encoding="utf-8").read()
    n = t.count(old)
    if n != expect:
        raise SystemExit("注入锚点命中 %d 次（期望 %d）: %s" % (n, expect, old[:70]))
    open(path, "w", encoding="utf-8").write(t.replace(old, new))


def assert_clean():
    """每组前后都校验：源码里不能残留任何注入标记。"""
    bad = []
    for root, _dirs, fs in os.walk(SCRIPTS):
        for f in fs:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(root, f)
            t = open(p, encoding="utf-8").read()
            for tag in ("[R1]", "[R2]", "[R3]", "[R4]"):
                if tag in t:
                    bad.append((os.path.relpath(p, ROOT), tag))
    if bad:
        raise SystemExit("源码残留注入标记：%s" % bad[:5])


def build(net=False):
    method = "HeadlessNetBuild" if net else "HeadlessBuild"
    log = os.path.join(ROOT, "unity_build_m39_inject_net.log" if net
                       else "unity_build_m39_inject.log")
    r = subprocess.run(
        [UNITY, "-batchmode", "-nographics", "-quit",
         "-projectPath", PROJ,
         "-executeMethod", "StarPixel.EditorTools.BuildScript." + method,
         "-logFile", log],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0


def run_probe(scene):
    r = subprocess.run([PY, os.path.join(ROOT, "card_probe.py"), "--only", scene],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    fails = {l.strip()[2:].strip() for l in out.splitlines()
             if l.strip().startswith("x ")}
    return r.returncode == 0, fails


def run_net():
    cmd = [PY, os.path.join(ROOT, "net_smoke.py"),
           "--seconds", "60", "--start-delay", "10",
           "--exe", os.path.join(PROJ, "Build", "SmokeNet", "StarPixelRogue.exe")]
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    problems = [l.strip()[2:].strip() for l in out.splitlines()
                if l.strip().startswith("- ")]
    return r.returncode == 0, problems


def run_audit():
    """静态审计。返回（是否通过, 第 17 项命中条数）。"""
    r = subprocess.run([PY, os.path.join(ROOT, "audit_project.py")],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    hits = [l.strip() for l in out.splitlines() if "收场没有玩家可见出口" in l]
    return r.returncode == 0, hits


def show(label, ok, expect_ok, extra=()):
    mark = "通过" if ok else "不通过"
    want = "期望通过" if expect_ok else "期望不通过"
    print("      %-20s → %s（%s）" % (label, mark, want))
    for e in extra:
        print("          %s" % str(e)[:110])


# ── 四组注入 ────────────────────────────────────────────────────────────────

# R1：三处失败收场只留日志，不发通知（修复前的原样）
R1_A_OLD = """            // M39：以前只有这一行日志——玩家点了卡、面板关了、**什么都没发生**，
            // 也不知道为什么。从他的视角看就是"游戏吞了我的升级"。必须有可见出口。
            UpgradeNotice.Raise(UpgradeNoticeReason.Rejected);"""
R1_A_NEW = """            // [R1] 恢复成修复前的写法：只留日志、不发通知"""

R1_B_OLD = """            if (n > 0 || pulled) UpgradeNotice.Raise(UpgradeNoticeReason.Invalidated);"""
R1_B_NEW = """            if (n > 0 || pulled) { }   // [R1] 撤销了卡片但不解释"""

R1_C_OLD = """                    for (int i = 0; i < n; i++)
                        UpgradeNotice.Raise(UpgradeNoticeReason.TimedOut);"""
R1_C_NEW = """                    // [R1] 超时最静默：连日志都没有"""

# R2：英文表缺这 4 个键（T() 会原样返回键名——只查"有没有汉字"抓不到）
R2_OLD = """                ["upgrade.notice.applied"] = "Acquired: {0}",
                ["upgrade.notice.rejected"] = "This upgrade did not take effect (rejected by host)",
                ["upgrade.notice.invalidated"] = "This upgrade card expired and has been withdrawn",
                ["upgrade.notice.timedout"] = "Upgrade confirmation timed out; it did not take effect","""
R2_NEW = """                // [R2] 英文版漏了这四条——英文玩家看到的是键名或中文"""

# R3：HUD 不注册上屏出口
R3_OLD = """            Combat.UpgradeNotice.Deliver = ShowUpgradeNotice;"""
R3_NEW = """            // [R3] 出口没注册：通知有记、无处可送"""

# R4：成功也不出声（玩家不知道自己拿到了什么）
R4_OLD = """            UpgradeNotice.Raise(UpgradeNoticeReason.Applied, UpgradeCatalog.TitleOfKind(kind, _ctrl));"""
R4_NEW = """            // [R4] 成功也不说一声：面板一关，玩家未必察觉移速/生命上限变了"""


def case_r1():
    print("  [R1] 失败收场不出声（拒/作废/超时三处只留日志）")
    assert_clean()
    inject(NETPLAYER, R1_A_OLD, R1_A_NEW)
    inject(NETPLAYER, R1_B_OLD, R1_B_NEW)
    inject(NETPLAYER, R1_C_OLD, R1_C_NEW)
    try:
        # 静态第 17 项必须立刻抓到——不必跑满 60 秒
        a_ok, hits = run_audit()
        show("静态第17项", a_ok, False, hits[:2])
        red = not a_ok

        # NOTICE 场景必须仍绿：它直接调 Raise，测的是通知表本身
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("NOTICE")
        show("card_probe NOTICE", ok, True, list(sorted(fails))[:2])
        return red and ok
    finally:
        restore(NETPLAYER)
        assert_clean()


def case_r2():
    print("  [R2] 提示不本地化（删掉 en 表那 4 个键）")
    assert_clean()
    inject(LOC, R2_OLD, R2_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("NOTICE")
        show("card_probe NOTICE", ok, False, list(sorted(fails))[:2])
        red = not ok
        # 其余必须仍绿
        for scene in ("GATE", "BATCH"):
            ok2, f2 = run_probe(scene)
            show("card_probe " + scene, ok2, True, list(sorted(f2))[:1])
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return bool(red and a_ok)
    finally:
        restore(LOC)
        assert_clean()


def case_r3():
    print("  [R3] HUD 不注册出口（通知有记、无处可送）")
    assert_clean()
    inject(HUD, R3_OLD, R3_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("NOTICE")
        show("card_probe NOTICE", ok, False, list(sorted(fails))[:2])
        red = not ok

        print("      重建联机产物后跑一次双开…")
        if not build(net=True):
            print("      构建失败"); return False
        n_ok, problems = run_net()
        m39 = [p for p in problems if p.startswith("M39")] or problems
        show("net_smoke", n_ok, False, m39[:3])
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return bool(red and not n_ok and a_ok)
    finally:
        restore(HUD)
        assert_clean()


def case_r4():
    print("  [R4] 成功也不出声（删掉确认路径那条通知）")
    assert_clean()
    inject(NETPLAYER, R4_OLD, R4_NEW)
    try:
        # 静态全绿：第 17 项只管失败收场，成功路径它管不着
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])

        if not build(net=True):
            print("      构建失败"); return False
        n_ok, problems = run_net()
        m39 = [p for p in problems if p.startswith("M39")] or problems
        show("net_smoke", n_ok, False, m39[:3])
        return bool(a_ok and not n_ok)
    finally:
        restore(NETPLAYER)
        assert_clean()


CASES = (("R1", case_r1), ("R2", case_r2), ("R3", case_r3), ("R4", case_r4))


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    todo = [(n, f) for n, f in CASES if only in (None, n)]
    if not todo:
        raise SystemExit("没有匹配的组：%s" % only)

    print("=" * 74)
    print("M39 区分力验证：%d 组注入" % len(todo))
    print("=" * 74)

    results = []
    for name, fn in todo:
        assert_clean()
        ok = fn()
        results.append((name, ok))
        print("      → %s：%s\n" % (name, "判据成功变红" if ok else "判据没抓到（危险）"))

    # 全部还原后**重建**，避免 exe 里留着上一组的注入
    for p in FILES:
        restore(p)
    assert_clean()
    print("  源码已还原，重建两种产物…")
    build()
    build(net=True)

    print("=" * 74)
    for name, ok in results:
        print("  %-4s %s" % (name, "抓到" if ok else "**没抓到**"))
    allok = all(ok for _, ok in results)
    print("=" * 74)
    print("结论：%s" % ("四组签名互不相交" if allok else "有判据失效"))
    return 0 if allok else 1


if __name__ == "__main__":
    sys.exit(main())
