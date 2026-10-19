#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M33 区分力验证：注入缺陷，确认「对应判据变红、且互不串扰」。

一条判据只有在「该红的时候红、不该红的时候不红」时才算有效。
本脚本往真实代码里注入已知缺陷，重建产物后跑探针，逐条核对：

  R1 把主菜单一个按钮文案改回硬编码中文
     期望：EN 红（英文界面冒中文）+ 静态第 12 项红；ZH / KEY / BASE 保持绿
     意义：证明「英文态零中文」这条判据真的会红，且两层都抓得到

  R2 把动态键拼错（"combo." → "combo_x."）
     期望：KEY 红（界面出现裸键）；EN 绿（这条与语言无关）
     意义：证明运行时层有静态层覆盖不到的独立价值——键是运行时拼的，
           静态扫源码看不见拼完是什么

  R3 修过头：建世界时不真的 Build（UI 压根没建出来）
     期望：ZH 红（中文态数不到中文 → "数得出"的对照判据不可省）；EN 也红
     意义：只写「英文态零中文」的话，UI 没了也能全绿

  R4 静态层：把 UpgradeCatalog 的块豁免理由清空
     期望：静态第 12 项恢复报错 10 处
     意义：证明豁免机制不会「写了标记就一律放行」

每组跑完立即还原并复验基线，绝不把注入留在源码里。

退出码：0 = 四组全部符合预期；1 = 有不符合的
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

MAINMENU = os.path.join(PROJ, "Assets/_Project/Scripts/UI/MainMenu.cs")
HUD = os.path.join(PROJ, "Assets/_Project/Scripts/UI/HUDController.cs")
CATALOG = os.path.join(PROJ, "Assets/_Project/Scripts/Combat/UpgradeCatalog.cs")
PROBE = os.path.join(PROJ, "Assets/_Project/Scripts/Dev/LocProbe.cs")

BACKUP = ".m33inject.bak"


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


def edit(path, old, new, expect=1):
    t = open(path, encoding="utf-8").read()
    n = t.count(old)
    if n != expect:
        raise SystemExit("注入锚点命中 %d 次（期望 %d）: %s" % (n, expect, old[:70]))
    open(path, "w", encoding="utf-8").write(t.replace(old, new))


def build():
    r = subprocess.run(
        [UNITY, "-batchmode", "-nographics", "-quit",
         "-projectPath", PROJ,
         "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
         "-logFile", os.path.join(ROOT, "unity_build_m33_inject.log")],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0


def run_probe(scene):
    """跑单个场景，返回 (通过?, 关键数值 dict)"""
    r = subprocess.run([PY, os.path.join(ROOT, "loc_probe.py"), "--only", scene],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    vals = {}
    for line in out.splitlines():
        m = re.search(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$", line)
        if m:
            vals[m.group(1).strip()] = m.group(2).strip()
    return r.returncode == 0, vals


def audit_has_cjk_error():
    r = subprocess.run([PY, os.path.join(ROOT, "audit_project.py")],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT)
    out = (r.stdout or "")
    n = len(re.findall(r"UI 文案硬编码中文", out))
    m = re.search(r"合计：ERROR (\d+)", out)
    return n, (int(m.group(1)) if m else -1)


def main():
    print("=" * 74)
    print("M33 区分力验证 · 注入缺陷逐条反向验证判据")
    print("=" * 74)

    results = []

    # ── R1：主菜单按钮文案改回硬编码中文 ──────────────────────
    print("\n[R1] 主菜单「开始游戏」按钮改回硬编码中文")
    print("     期望：EN 红 · ZH 绿 · KEY 绿 · 静态第 12 项红")
    backup(MAINMENU)
    try:
        edit(MAINMENU,
             'Core.UIFactory.CreateButton(_root.transform, "StartBtn", Core.Localization.T("menu.start"),',
             'Core.UIFactory.CreateButton(_root.transform, "StartBtn", "开始游戏",')
        n, err_total = audit_has_cjk_error()
        print("     静态第 12 项：报错 %d 处（合计 ERROR %d）" % (n, err_total))
        if not build():
            raise SystemExit("构建失败")
        ok_en, v_en = run_probe("EN")
        ok_zh, v_zh = run_probe("ZH")
        ok_key, v_key = run_probe("KEY")
        print("     EN  通过=%s  文本 %s 条 / 含中文 %s 条"
              % (ok_en, v_en.get("UI 文本总数", "?"), v_en.get("含中文条数", "?")))
        print("     ZH  通过=%s  含中文 %s 条" % (ok_zh, v_zh.get("含中文条数", "?")))
        print("     KEY 通过=%s  裸键 %s 条" % (ok_key, v_key.get("裸键条数", "?")))
        good = (not ok_en) and ok_zh and ok_key and n >= 1
        results.append(("R1 硬编码中文", good, "EN 应红=%s / ZH 应绿=%s / KEY 应绿=%s / 静态应红=%s"
                        % (not ok_en, ok_zh, ok_key, n >= 1)))
    finally:
        restore(MAINMENU)

    # ── R2：动态键拼错 ────────────────────────────────────────
    print("\n[R2] 动态键拼错：T(\"combo.\" + key) → T(\"combo_x.\" + key)")
    print("     期望：KEY 红 · EN 绿（与语言无关，静态也看不见）")
    backup(HUD)
    try:
        edit(HUD, 'Core.Localization.T("combo." + key)',
                  'Core.Localization.T("combo_x." + key)')
        n, _ = audit_has_cjk_error()
        if not build():
            raise SystemExit("构建失败")
        ok_key, v_key = run_probe("KEY")
        ok_en, v_en = run_probe("EN")
        print("     KEY 通过=%s  裸键 %s 条（首条: %s）"
              % (ok_key, v_key.get("裸键条数", "?"), v_key.get("首条裸键", "无")))
        print("     EN  通过=%s  含中文 %s 条" % (ok_en, v_en.get("含中文条数", "?")))
        print("     静态第 12 项报错 %d 处（应为 0：静态看不见拼接结果）" % n)
        good = (not ok_key) and ok_en and n == 0
        results.append(("R2 动态键拼错", good, "KEY 应红=%s / EN 应绿=%s / 静态应为0=%s"
                        % (not ok_key, ok_en, n == 0)))
    finally:
        restore(HUD)

    # ── R3：修过头，UI 压根不建 ───────────────────────────────
    print("\n[R3] 修过头：建世界时不真的 Build（UI 没建出来）")
    print("     期望：ZH 红 · EN 红（证明「数得出」的对照判据不可省）")
    backup(PROBE)
    try:
        edit(PROBE, "            GameBuilder.Build();\n            yield return null;\n            yield return null;",
                    "            // 注入：不建世界\n            yield return null;\n            yield return null;")
        if not build():
            raise SystemExit("构建失败")
        ok_zh, v_zh = run_probe("ZH")
        ok_en, v_en = run_probe("EN")
        print("     ZH 通过=%s  文本 %s 条 / 含中文 %s 条"
              % (ok_zh, v_zh.get("UI 文本总数", "?"), v_zh.get("含中文条数", "?")))
        print("     EN 通过=%s  文本 %s 条" % (ok_en, v_en.get("UI 文本总数", "?")))
        good = (not ok_zh) and (not ok_en)
        results.append(("R3 不建 UI", good, "ZH 应红=%s / EN 应红=%s" % (not ok_zh, not ok_en)))
    finally:
        restore(PROBE)

    # ── R4：静态层豁免空理由 ──────────────────────────────────
    print("\n[R4] 静态层：把 UpgradeCatalog 的块豁免理由清空")
    print("     期望：静态第 12 项恢复报错 10 处（写了标记≠一律放行）")
    backup(CATALOG)
    try:
        edit(CATALOG,
             "// @zhonly-begin: 开发期可读标识，界面显示走 locKey（SkillDef.DisplayName 已保证）",
             "// @zhonly-begin:")
        n, err_total = audit_has_cjk_error()
        print("     静态第 12 项：报错 %d 处（合计 ERROR %d）" % (n, err_total))
        good = n == 10
        results.append(("R4 豁免空理由", good, "应报 10 处，实际 %d 处" % n))
    finally:
        restore(CATALOG)

    # ── 还原后复跑基线 ────────────────────────────────────────
    print("\n[基线] 还原全部注入后重建，跑全部 5 个场景")
    if not build():
        raise SystemExit("构建失败")
    baseline_ok = True
    for scene in ("BASE", "ZH", "EN", "KEY", "RESTORE"):
        ok, v = run_probe(scene)
        baseline_ok = baseline_ok and ok
        extra = ""
        if scene in ("ZH", "EN"):
            extra = " 文本 %s / 中文 %s" % (v.get("UI 文本总数", "?"), v.get("含中文条数", "?"))
        elif scene == "KEY":
            extra = " 文本 %s / 裸键 %s" % (v.get("UI 文本总数", "?"), v.get("裸键条数", "?"))
        print("     %-8s %s%s" % (scene, "通过" if ok else "不通过", extra))

    print("\n" + "=" * 74)
    all_good = True
    for name, good, detail in results:
        print("  %s %-16s %s" % ("√" if good else "x", name, detail))
        all_good = all_good and good
    print("  %s %-16s 全部 5 场景通过=%s" % ("√" if baseline_ok else "x", "还原后基线", baseline_ok))
    print("=" * 74)
    print("结论：%s" % ("通过 —— 四组注入均符合预期且互不串扰" if (all_good and baseline_ok)
                        else "不通过"))
    return 0 if (all_good and baseline_ok) else 1


if __name__ == "__main__":
    sys.exit(main())
