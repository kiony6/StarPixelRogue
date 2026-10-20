#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M40 区分力验证：往工程里注入 4 组互不相同的缺陷，看新加的判据是不是真的会红。

为什么必须做这一步：
    M40 修的那处缺陷在**同语言**环境下完全不可见。
    房主中文 + 客户端中文时，卡片是中文，一切正常；房主把 title[]/desc[] 一起发过去，
    客户端原样上屏，计数对、日志对、面板也对。只有两端语言不同才露馅：
    英文玩家眼前是一整块中文。

    所以这里反向验证两件事：
      1) 干净源码下新判据不误报（基线全绿）；
      2) 四组注入各自让**不同的判据**变红，且签名互不相交。

  R1 房主把文案一起发过去（修复前的原样）
     → **静态第 18 项**（两条）+ 联机「M40 英文客户端看到中文卡片」红；
       LOCALE 场景全绿——文案生成函数本身没坏，坏的是"谁的语言"。
       演示：这类缺陷在单机/同语言双开里一次都抓不到，只有静态规则 +
       异语言双开才能钉住。

  R2 武器解锁卡没有描述
     → **只有 LOCALE「中文态卡片完整」**红；英文态与其余场景全绿。
       演示：只核对标题会漏掉"卡片只有半张"。

  R3 武器升级卡不按本机等级算
     → **只有 LOCALE「等级按本机现算」**红；中文态/英文态/面板全绿。
       演示：标题是英文、描述也非空，唯独等级对不上本机——
       只查"有没有汉字"完全看不出来。

  R4 客户端拿到的卡带了可执行的 apply
     → **只有 LOCALE「非法kind安全」**红；其余全绿。
       演示：M38 的确认门靠"客户端不许本地应用"兜底，
       一旦卡片自带 apply，先斩后奏就有了回来的路。

四组签名互不相同：
  R1 = {静态第18项(a), 静态第18项(b), 联机·M40 文案语言}
  R2 = {LOCALE 中文态卡片完整}
  R3 = {LOCALE 等级按本机现算}
  R4 = {LOCALE 非法kind安全}

用法：  python m40_inject.py            # 四组全跑
        python m40_inject.py R3         # 只跑指定组
跑完自动还原源码（.m40inject.bak）并**重新构建**两种产物，避免 exe 残留上一组注入。
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
CATALOG = os.path.join(SCRIPTS, "Combat/UpgradeCatalog.cs")

BACKUP = ".m40inject.bak"
FILES = (NETPLAYER, CATALOG)


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
    log = os.path.join(ROOT, "unity_build_m40_inject_net.log" if net
                       else "unity_build_m40_inject.log")
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
    # 只取"判据名"这一列：行格式是 `x 判据名  期望 …  实际 …`（列间 2+ 空格）
    fails = {re.split(r'\s{2,}', l.strip()[2:].strip())[0] for l in out.splitlines()
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
    """静态审计。返回（是否通过, 第 18 项命中条数）。"""
    r = subprocess.run([PY, os.path.join(ROOT, "audit_project.py")],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    hits = [l.strip() for l in out.splitlines()
            if "RPC 跨端传界面文案" in l or "RPC 跨端文案直接上屏" in l]
    return r.returncode == 0, hits


def show(label, ok, expect_ok, extra=()):
    mark = "通过" if ok else "不通过"
    want = "期望通过" if expect_ok else "期望不通过"
    print("      %-20s → %s（%s）" % (label, mark, want))
    for e in extra:
        print("          %s" % str(e)[:110])


# ── 四组注入 ────────────────────────────────────────────────────────────────

# R1：房主把 title/desc 一起发过去（修复前的原样）
R1_A_OLD = """            var kinds = new int[offers.Count];
            for (int i = 0; i < offers.Count; i++) kinds[i] = offers[i].kind;
            ShowCardsTargetRpc(Owner, batchId, kinds);"""
R1_A_NEW = """            // [R1] 恢复修复前的写法：把房主生成的文案一起发过去
            var titles = new string[offers.Count];
            var descs = new string[offers.Count];
            var kinds = new int[offers.Count];
            for (int i = 0; i < offers.Count; i++)
            {
                titles[i] = offers[i].title;
                descs[i] = offers[i].desc;
                kinds[i] = offers[i].kind;
            }
            ShowCardsTargetRpc(Owner, batchId, titles, descs, kinds);"""

R1_B_OLD = """        private void ShowCardsTargetRpc(NetworkConnection conn, int batchId, int[] kinds)
        {
            // M40：房主的 title/desc 一个字节都不过网——按本机语言现算。
            var offers = UpgradeCatalog.OffersFromKinds(kinds, _ctrl);
            if (offers.Count == 0) return;"""
R1_B_NEW = """        private void ShowCardsTargetRpc(NetworkConnection conn, int batchId,
                                        string[] titles, string[] descs, int[] kinds)
        {
            // [R1] 原样使用房主发来的文案（客户端自己的语言被晾在一边）
            var offers = new List<UpgradeOffer>();
            for (int i = 0; i < titles.Length; i++)
                offers.Add(new UpgradeOffer
                {
                    title = titles[i],
                    desc = i < descs.Length ? descs[i] : "",
                    kind = i < kinds.Length ? kinds[i] : 0,
                    apply = null
                });
            if (offers.Count == 0) return;"""

# R2：武器解锁卡没有描述（卡片只剩半张）
R2_OLD = """            if (kind >= 100)
            {
                var def = CreateById(kind - 100);
                return def != null ? WeaponDesc(def) : "";
            }"""
R2_NEW = """            if (kind >= 100)
            {
                // [R2] 解锁卡不写描述：标题对、描述空
                return "";
            }"""

# R3：武器升级卡的等级不按本机算（固定成"1→2"）
R3_OLD = """                        int next = Mathf.Min(w.level + 1, SkillDef.MaxLevel);"""
R3_NEW = """                        int next = 2;   // [R3] 不按本机等级算"""

# R4：客户端拿到的卡自带可执行的 apply（先斩后奏的回头路）
R4_OLD = """                    kind = kind,
                    apply = null"""
R4_NEW = """                    kind = kind,
                    apply = () => { }   // [R4] 客户端私下也能应用的入口"""


def case_r1():
    print("  [R1] 房主把文案一起发过去（修复前的原样）")
    assert_clean()
    inject(NETPLAYER, R1_A_OLD, R1_A_NEW)
    inject(NETPLAYER, R1_B_OLD, R1_B_NEW)
    try:
        # 静态第 18 项必须立刻抓到（两条：签名参数 + 数组元素直接上屏）
        a_ok, hits = run_audit()
        show("静态第18项", a_ok, False, hits[:2])
        red = not a_ok and len(hits) >= 1

        # LOCALE 仍应绿：文案生成函数本身没坏，坏的是"谁的语言"
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("LOCALE")
        show("card_probe LOCALE", ok, True, list(sorted(fails))[:2])

        # 异语言双开必须红
        print("      重建联机产物后跑一次双开（房主中文 / 客户端英文）…")
        if not build(net=True):
            print("      构建失败"); return False
        n_ok, problems = run_net()
        m40 = [p for p in problems if p.startswith("M40")] or problems
        show("net_smoke", n_ok, False, m40[:2])
        return bool(red and ok and not n_ok)
    finally:
        restore(NETPLAYER)
        assert_clean()


def case_r2():
    print("  [R2] 武器解锁卡没有描述（卡片只剩半张）")
    assert_clean()
    inject(CATALOG, R2_OLD, R2_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("LOCALE")
        show("card_probe LOCALE", ok, False, list(sorted(fails))[:3])
        # 必须只红"中文态卡片完整"这一条
        only = (fails - {"断点通过"}) == {"中文态卡片完整"}   # 断点计数必然跟着红，不算签名
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return bool(not ok and only and a_ok)
    finally:
        restore(CATALOG)
        assert_clean()


def case_r3():
    print("  [R3] 武器升级卡不按本机等级算")
    assert_clean()
    inject(CATALOG, R3_OLD, R3_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("LOCALE")
        show("card_probe LOCALE", ok, False, list(sorted(fails))[:3])
        only = (fails - {"断点通过"}) == {"等级按本机现算"}
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return bool(not ok and only and a_ok)
    finally:
        restore(CATALOG)
        assert_clean()


def case_r4():
    print("  [R4] 客户端拿到的卡自带可执行的 apply")
    assert_clean()
    inject(CATALOG, R4_OLD, R4_NEW)
    try:
        if not build():
            print("      构建失败"); return False
        ok, fails = run_probe("LOCALE")
        show("card_probe LOCALE", ok, False, list(sorted(fails))[:3])
        only = (fails - {"断点通过"}) == {"非法kind安全"}
        a_ok, hits = run_audit()
        show("静态全项", a_ok, True, hits[:1])
        return bool(not ok and only and a_ok)
    finally:
        restore(CATALOG)
        assert_clean()


CASES = (("R1", case_r1), ("R2", case_r2), ("R3", case_r3), ("R4", case_r4))


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    todo = [(n, f) for n, f in CASES if only in (None, n)]
    if not todo:
        raise SystemExit("没有匹配的组：%s" % only)

    print("=" * 74)
    print("M40 区分力验证：%d 组注入" % len(todo))
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
