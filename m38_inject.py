#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M38 区分力验证：往工程里注入 4 组互不相同的缺陷，看新加的判据是不是真的会红。

为什么必须做这一步：
    M38 修的那处**没有任何症状**。先斩后奏的写法是：
        RequestChooseUpgradeServerRpc(batchId, index);       // 可能被拒
        UpgradeCatalog.ApplyKind(kinds[index], _ctrl);       // 无条件先加上
    房主拒绝了，客户端已经加成。不报错、不崩、看字段也对，
    表现出来只是"客户端好像跑得快一点"。M37 把房主改成"取不到就拒绝"之后，
    这个写法反而更危险——安全网每拦一次，就制造一次分叉。

    所以这里反向验证两件事：
      1) 干净源码下新判据不误报（基线全绿）；
      2) 四组注入各自让**不同的判据**变红，且签名互不相交。

  R1 恢复先斩后奏（面板回调里无条件先把强化加给本地）
     → 联机「本地强化次数 > 本地已应用」红（多出来的就是自己给自己发的那几次）
       + 静态第 16 项红；card_probe 全绿（纯逻辑那层没动）。

  R2 确认可重放（gate.Confirm 不把它取走，同一笔能被确认多次）
     → 只有 GATE「重复确认被拒」红；联机与静态全绿。
       演示"只比双端总数"抓不到重放：总数照样对得上。

  R3 淘汰不出声（去掉 OnEvicted 触发，只留 EvictedCount）
     → 只有 BATCH「淘汰有出口」红；其余全绿。
       这正是 M37 遗留的"淘汰计数没有出口"——计数是对的，但对端永远不知道。

  R4 修过头（确认之后也不应用，玩家选了卡却什么都没得到）
     → 联机「房主已应用 > 本地已应用 + 在途」红，方向与 R1 **相反**；
       card_probe 与静态全绿（守卫还在，只是应用被吞了）。
       与 R1 配对，证明这条判据分得清"多给"和"不给"。

四组签名互不相同：
  R1 = {联机·双端多应用, 静态第16项}
  R2 = {GATE 重复确认}
  R3 = {BATCH 淘汰出口}
  R4 = {联机·双端少应用}

用法：  python m38_inject.py            # 四组全跑
        python m38_inject.py R2         # 只跑指定组
跑完自动还原源码（.m38inject.bak）并**重新构建**两种产物，避免 exe 残留上一组注入。
"""

import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PROJ = os.path.join(ROOT, "StarPixelRogue")
SCRIPTS = os.path.join(PROJ, "Assets/_Project/Scripts")

NETPLAYER = os.path.join(SCRIPTS, "Net/NetPlayer.cs")
GATE = os.path.join(SCRIPTS, "Combat/UpgradeApplyGate.cs")
BATCHES = os.path.join(SCRIPTS, "Combat/UpgradeOfferBatches.cs")

BACKUP = ".m38inject.bak"
FILES = (NETPLAYER, GATE, BATCHES)


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def backup(path):
    """备份**原始**内容。已存在就不覆盖——同一组可能在同一文件里注入多处，
    第二处若覆盖备份，还原出来的就是带注入的版本（M37 踩过类似的坑）。"""
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


def build(net=False):
    method = "HeadlessNetBuild" if net else "HeadlessBuild"
    log = os.path.join(ROOT, "unity_build_m38_inject_net.log" if net
                       else "unity_build_m38_inject.log")
    r = subprocess.run(
        [UNITY, "-batchmode", "-nographics", "-quit",
         "-projectPath", PROJ,
         "-executeMethod", "StarPixel.EditorTools.BuildScript." + method,
         "-logFile", log],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0


def run_probe(scene):
    """跑一个 CardProbe 场景，返回（是否通过, 失败项集合）。"""
    r = subprocess.run([PY, os.path.join(ROOT, "card_probe.py"), "--only", scene],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    if r.returncode != 0:
        fails = {l.strip() for l in out.splitlines() if l.strip().startswith("x ")}
        return False, fails
    return True, set()


def run_net():
    """跑一次联机双开，返回（是否通过, 失败项列表）。"""
    cmd = [PY, os.path.join(ROOT, "net_smoke.py"),
           "--seconds", "60", "--start-delay", "10",
           "--exe", os.path.join(PROJ, "Build", "SmokeNet", "StarPixelRogue.exe")]
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    problems = [l.strip()[2:] for l in out.splitlines() if l.strip().startswith("- ")]
    return r.returncode == 0, problems


def run_audit():
    """静态审计，返回（是否通过, 报错条数）。命中第 16 项返回 False。"""
    r = subprocess.run([PY, os.path.join(ROOT, "audit_project.py")],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    hits = [l.strip() for l in out.splitlines() if "先斩后奏" in l]
    return r.returncode == 0, hits


def show(label, ok, expect_ok, extra=()):
    mark = "通过" if ok else "不通过"
    want = "期望通过" if expect_ok else "期望不通过"
    print("      %-22s → %s（%s）" % (label, mark, want))
    for e in extra:
        print("          %s" % str(e)[:110])


# ── 四组注入 ────────────────────────────────────────────────────────────────

R1_OLD = """                int ack = _applyGate.Submit(batchId, index, UnityEngine.Time.unscaledTime);
                if (ack > 0) RequestChooseUpgradeServerRpc(ack, batchId, index);"""
R1_NEW = """                int ack = _applyGate.Submit(batchId, index, UnityEngine.Time.unscaledTime);
                if (ack > 0) RequestChooseUpgradeServerRpc(ack, batchId, index);
                // [R1] 恢复成修复前的先斩后奏：不等回执，先把强化加给本地。
                // 确认路径**照旧保留**——这样红的就只有"多出来的那几次"，
                // 与 R4（确认了也不应用，方向相反）失败面严格互补。
                if (index >= 0 && index < kinds.Length)
                    UpgradeCatalog.ApplyKind(kinds[index], _ctrl);"""

R2_OLD = """            _pending.Remove(ackId);
            _order.Remove(ackId);
            ConfirmedCount++;
            return true;"""
R2_NEW = """            // [R2] 确认可重放：不把它取走，同一笔能被确认多次
            ConfirmedCount++;
            return true;"""

R3_OLD = """                    EvictedCount++;
                    var h = OnEvicted;                 // M38：淘汰必须能被对端感知（详见事件注释）
                    if (h != null) h.Invoke(oldest);"""
R3_NEW = """                    EvictedCount++;   // [R3] 恢复成修复前：只计数，不通知对端"""

R4_OLD = """            if (!_applyGate.Confirm(ackId, batchId, index)) return;
            if (_ctrl == null) return;
            UpgradeCatalog.ApplyKind(kind, _ctrl);"""
R4_NEW = """            if (!_applyGate.Confirm(ackId, batchId, index)) return;
            if (_ctrl == null) return;
            return;   // [R4] 修过头：确认了也不应用，玩家选了卡却什么都没得到
            UpgradeCatalog.ApplyKind(kind, _ctrl);"""


def case_r1():
    print("  [R1] 恢复先斩后奏（面板回调里无条件先把强化加给本地）")
    inject(NETPLAYER, R1_OLD, R1_NEW)
    red = False
    try:
        # 静态第 16 项必须立刻抓到（不必跑满 60 秒）
        a_ok, hits = run_audit()
        show("静态第16项", a_ok, False, hits[:2])
        red = red or (not a_ok)

        # card_probe 是全绿的那半边：纯逻辑层没被改
        if not build():
            print("      构建失败"); return False
        for scene in ("BATCH", "GATE", "INVAL"):
            ok, fails = run_probe(scene)
            show("card_probe " + scene, ok, True, list(sorted(fails))[:1])

        print("      重建联机产物后跑一次双开…")
        if not build(net=True):
            print("      构建失败"); return False
        ok, problems = run_net()
        show("net_smoke", ok, False, problems[:3])
        red = red or (not ok)
    finally:
        restore(NETPLAYER)
    return red


def case_r2():
    print("  [R2] 确认可重放（同一笔选择能被确认多次）")
    inject(GATE, R2_OLD, R2_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("GATE")
        show("card_probe GATE", ok, False, list(sorted(fails))[:2])
        red = not ok
        # 其余场景与静态必须仍绿：证明只有"重复确认"这一条抓到了它
        for scene in ("BATCH", "QUEUE", "INVAL"):
            ok2, f2 = run_probe(scene)
            show("card_probe " + scene, ok2, True, list(sorted(f2))[:1])
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return red and a_ok
    finally:
        restore(GATE)


def case_r3():
    print("  [R3] 淘汰不出声（只留 EvictedCount，不通知对端撤卡）")
    inject(BATCHES, R3_OLD, R3_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("BATCH")
        show("card_probe BATCH", ok, False, list(sorted(fails))[:2])
        red = not ok
        for scene in ("GATE", "QUEUE", "RESTORE"):
            ok2, f2 = run_probe(scene)
            show("card_probe " + scene, ok2, True, list(sorted(f2))[:1])
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return red and a_ok
    finally:
        restore(BATCHES)


def case_r4():
    print("  [R4] 修过头（确认之后也不应用）")
    inject(NETPLAYER, R4_OLD, R4_NEW)
    try:
        a_ok, hits = run_audit()
        show("静态第16项", a_ok, True, hits[:1])
        if not build(net=True):
            print("      构建失败"); return False
        ok, problems = run_net()
        show("net_smoke", ok, False, problems[:3])
        return (not ok) and a_ok
    finally:
        restore(NETPLAYER)


CASES = (("R1", case_r1), ("R2", case_r2), ("R3", case_r3), ("R4", case_r4))


def assert_clean():
    """还原是否真的生效。留着上一组的注入会让后面每一组的签名都被污染。"""
    for p in FILES:
        t = open(p, encoding="utf-8").read()
        for tag in ("[R1]", "[R2]", "[R3]", "[R4]"):
            if tag in t:
                print("  !! %s 仍残留注入标记 %s —— 还原失败，后续结果不可信" % (rel(p), tag))
                return False
    return True


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    todo = [c for c in CASES if only is None or c[0] == only]
    if not todo:
        print("未知组: %s" % only)
        return 2

    print("=" * 74)
    print("M38 区分力验证 · %d 组注入" % len(todo))
    print("=" * 74)

    results = []
    for name, fn in todo:
        if not assert_clean():
            results.append((name, False))
            break
        ok = fn()
        clean = assert_clean()
        results.append((name, ok and clean))
        print("      → %s：%s\n" % (name, "判据成功变红" if (ok and clean) else "判据没抓到（危险）"))

    # 还原后必须重建：M35 踩过——注入脚本每组还原后不重建，exe 里残留上一组注入
    print("  还原源码并重建两种产物…")
    build()
    build(net=True)

    print("=" * 74)
    bad = [n for n, ok in results if not ok]
    print("结论：%s" % ("全部注入都被判据抓住" if not bad
                        else "以下注入未被抓住：%s" % ", ".join(bad)))
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
