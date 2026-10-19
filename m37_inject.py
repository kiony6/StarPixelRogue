#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M37 区分力验证：往工程里注入 4 组互不相同的缺陷，看新加的判据是不是真的会红。

为什么要做这一步：
    M37 的两处缺陷**藏了很久**，因为负责盯它的那条判据本身是坏的：
    SmokeTest 只在 `Time.timeScale <= 0` 时才去统计选卡，而联机推卡是刻意不暂停的
    （免得冻结网络心跳）——于是客户端的选卡数恒为 0（实测 60s：房主 5 次 / 客户端 0 次），
    看起来像"客户端没升级"，其实是探针瞎了。修好统计后同一个场景立刻露出真身：
    客户端收到 7 批、也选掉了 7 次。

    所以这里反向验证两件事：
      1) 干净源码下新判据不误报（基线）；
      2) 四组注入各自让**不同的判据**变红，且互不相交。

  R1 客户端恢复静默丢弃（面板占用时推来的卡直接扔掉）
     → 探针 QUEUE「丢弃=2、收支不平」红、DRAIN 红、ORDER 红；联机「丢弃>0 / 收支不平」红。
       这就是 M37 修掉的真缺陷，也是"统计修好后才看得见"的那一处。

  R2 房主候选表改回「单份被覆盖」（取回时忽略批次 id，永远给最新一批）
     → 只有 BATCH 场景红，其余五个场景与联机**全绿**。
       这条专门演示：不抽成纯逻辑的话，房主侧错配在联机双开里根本看不出来
       （计数全对、玩家选 A 生效 B），只有把两批标题并排比才抓得到。

  R3 队列改成后进先出（弹出最后一批而不是第一批）
     → DRAIN / ORDER 的顺序判据红，但 QUEUE 的「收支 / 丢弃」仍全绿。
       证明"只数收支"抓不到乱序——QUEUE 那条"上屏的必须是第一组"也是同理。

  R4 外部供选整个不通（PresentExternal 收到就返回，永不弹卡）
     → BASE「单组上屏」红、QUEUE「收支不平」红，但**丢弃恒为 0**、RESTORE 的本地判据仍绿。
       与 R1 的签名不同（R1 丢弃 >0），两条合起来才说明判据分得清"丢"和"不弹"。

四组签名（变红的判据集合）互不相同：
  R1 = {QUEUE丢弃, QUEUE收支, DRAIN, ORDER, 联机丢弃}
  R2 = {BATCH}
  R3 = {DRAIN顺序, ORDER顺序}                    （QUEUE 全绿）
  R4 = {BASE单组上屏, QUEUE收支}                  （丢弃恒 0、RESTORE 本地绿）

用法：  python m37_inject.py            # 四组全跑
        python m37_inject.py R2         # 只跑指定组
跑完自动还原源码（.m37inject.bak）并**重新构建**，避免产物残留上一组的注入。
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

LEVEL_UP = os.path.join(SCRIPTS, "UI/LevelUpManager.cs")
BATCHES = os.path.join(SCRIPTS, "Combat/UpgradeOfferBatches.cs")

BACKUP = ".m37inject.bak"
SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def backup(path):
    b = path + BACKUP
    if os.path.exists(b):
        os.remove(b)
    shutil.copy2(path, b)


def restore(path):
    b = path + BACKUP
    if os.path.exists(b):
        shutil.copy2(b, path)
        os.remove(b)


def inject(path, old, new, expect=1):
    # 备份必须发生在**注入之前**，不能等到还原时才想起——
    # 初版把 backup 漏了，restore() 找不到 .bak 就成了空操作：第一组的注入一直留在源码里，
    # 后面每一组都带着它跑，于是 R2 那组"只有 BATCH 该红"的签名被污染成一片红，
    # 看起来像判据没抓到，其实是源码根本没还原（本轮真实踩到）。
    backup(path)
    t = open(path, encoding="utf-8").read()
    n = t.count(old)
    if n != expect:
        raise SystemExit("注入锚点命中 %d 次（期望 %d）: %s" % (n, expect, old[:70]))
    open(path, "w", encoding="utf-8").write(t.replace(old, new))


def build(net=False):
    method = "HeadlessNetBuild" if net else "HeadlessBuild"
    log = os.path.join(ROOT, "unity_build_m37_inject_net.log" if net
                       else "unity_build_m37_inject.log")
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
    """跑一次联机双开，返回（是否通过, 失败项列表, 房主数据, 客户端数据）。"""
    cmd = [PY, os.path.join(ROOT, "net_smoke.py"),
           "--seconds", "60", "--start-delay", "10",
           "--exe", os.path.join(PROJ, "Build", "SmokeNet", "StarPixelRogue.exe")]
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    problems = [l.strip()[2:] for l in out.splitlines() if l.strip().startswith("- ")]
    return r.returncode == 0, problems


def parse_smoke(path):
    d = {}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = SMOKE_RE.search(line.rstrip("\n"))
                if m:
                    d[m.group(1).strip()] = m.group(2).strip()
    except OSError:
        pass
    return d


# ── 四组注入 ────────────────────────────────────────────────────────────────

R1_OLD = """                _pendingExternal.Add(new PendingExternal { offers = offers, onChosen = onChosen });
                ExternalQueued++;"""
R1_NEW = """                ExternalDropped++;   // [R1] 恢复成修复前的做法：面板占用时直接丢掉"""

R2_OLD = """            List<UpgradeOffer> list;
            if (!_batches.TryGetValue(batchId, out list) || list == null) return false;"""
R2_NEW = """            // [R2] 恢复成修复前的做法：只有一份候选表，取回时永远给最新的一批
            if (_order.Count > 0) batchId = _order[_order.Count - 1];
            List<UpgradeOffer> list;
            if (!_batches.TryGetValue(batchId, out list) || list == null) return false;"""

R3_OLD = """                var next = _pendingExternal[0];
                _pendingExternal.RemoveAt(0);"""
R3_NEW = """                var next = _pendingExternal[_pendingExternal.Count - 1];   // [R3] 后进先出
                _pendingExternal.RemoveAt(_pendingExternal.Count - 1);"""

R4_OLD = """            if (offers == null || offers.Count == 0) return;
            ExternalReceived++;"""
R4_NEW = """            if (offers == null || offers.Count == 0) return;
            ExternalReceived++;
            return;   // [R4] 外部供选整个不通：收到就走，永不弹卡"""


def case_r1():
    print("  [R1] 客户端恢复静默丢弃（面板占用时推来的卡直接扔掉）")
    inject(LEVEL_UP, R1_OLD, R1_NEW)
    ok1 = True
    try:
        if not build():
            print("      构建失败"); return False
        for scene in ("QUEUE", "DRAIN", "ORDER"):
            ok, fails = run_probe(scene)
            print("      card_probe %-7s → %s" % (scene, "通过" if ok else "不通过"))
            for f in sorted(fails):
                print("          %s" % f[:110])
            ok1 = ok1 and not ok           # 期望：都不通过
        print("      重建联机产物后跑一次双开…")
        if not build(net=True):
            print("      构建失败"); return False
        ok, problems = run_net()
        print("      net_smoke         → %s" % ("通过" if ok else "不通过"))
        for p in problems:
            print("          %s" % p[:110])
        ok1 = ok1 and not ok
    finally:
        restore(LEVEL_UP)
    return ok1


def case_r2():
    print("  [R2] 房主候选表改回「单份被覆盖」（取回时忽略批次 id）")
    inject(BATCHES, R2_OLD, R2_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        # 期望：只有 BATCH 红，其余五个场景全绿（证明房主侧错配在别的层面看不见）
        ok_b, fails = run_probe("BATCH")
        print("      card_probe BATCH   → %s（期望不通过）" % ("通过" if ok_b else "不通过"))
        for f in sorted(fails):
            print("          %s" % f[:110])
        others = True
        for scene in ("QUEUE", "DRAIN", "ORDER", "RESTORE"):
            ok, _ = run_probe(scene)
            print("      card_probe %-7s → %s（期望通过）" % (scene, "通过" if ok else "不通过"))
            others = others and ok
        return (not ok_b) and others
    finally:
        restore(BATCHES)


def case_r3():
    print("  [R3] 队列改成后进先出（弹出最后一批而不是第一批）")
    inject(LEVEL_UP, R3_OLD, R3_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok_q, _ = run_probe("QUEUE")
        print("      card_probe QUEUE   → %s（期望通过：收支与丢弃都对）"
              % ("通过" if ok_q else "不通过"))
        red = True
        for scene in ("DRAIN", "ORDER"):
            ok, fails = run_probe(scene)
            print("      card_probe %-7s → %s（期望不通过）" % (scene, "通过" if ok else "不通过"))
            for f in sorted(fails):
                print("          %s" % f[:110])
            red = red and not ok
        return ok_q and red
    finally:
        restore(LEVEL_UP)


def case_r4():
    print("  [R4] 外部供选整个不通（收到就返回，永不弹卡）")
    inject(LEVEL_UP, R4_OLD, R4_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        red = True
        for scene in ("BASE", "QUEUE"):
            ok, fails = run_probe(scene)
            print("      card_probe %-7s → %s（期望不通过）" % (scene, "通过" if ok else "不通过"))
            for f in sorted(fails):
                print("          %s" % f[:110])
            red = red and not ok
        # RESTORE 的本地判据必须仍绿：否则分不清「外部不通」和「本地也被堵死」
        ok_r, fails = run_probe("RESTORE")
        print("      card_probe RESTORE → %s（期望通过：本地路径没被堵死）"
              % ("通过" if ok_r else "不通过"))
        for f in sorted(fails):
            print("          %s" % f[:110])
        return red and ok_r
    finally:
        restore(LEVEL_UP)


CASES = (("R1", case_r1), ("R2", case_r2), ("R3", case_r3), ("R4", case_r4))


def assert_clean():
    """还原是否真的生效。留着上一组的注入会让后面每一组的签名都被污染。"""
    for p in (LEVEL_UP, BATCHES):
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
    print("M37 区分力验证 · %d 组注入" % len(todo))
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
