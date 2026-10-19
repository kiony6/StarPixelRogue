#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M35 区分力验证：往工程里注入 4 组互不相同的缺陷，看判据是不是真的会红。

静态检查最危险的失败形态是"永远不报错"——第 14 项若写成摆设，工程照样一片绿，
而三份法律正文会继续在英文界面下整篇冒中文。所以这里反向验证：
该红的必须红、不该红的必须不红，且**四组注入的失败面互不相交**——
否则说明其中某条判据是另一条的影子，删掉也没人知道。

  R1 删掉英文版 privacy.txt      → 静态红(缺英文版) + EN 红(回退 1 次、含中文)
                                    UI 也红（那份窗弹出中文）
  R2 英文版版本号改成 2026.09    → 静态红(版本不一致) + 运行时全绿
                                    （正文仍是英文，只有"版本号同步"这一条抓得住——
                                     独立判据的证据：它不依赖"有没有中文"）
  R3 缓存不按语言分区            → 静态绿 + EN 红 + UI 红
                                    （文件都在，但切语言后拿到的还是中文缓存——
                                     静态看文件、运行时看结果，两层不重合）
  R4 法务窗不挂刷新器            → 静态绿 + 运行时只有 REFRESH 红
                                    （重开窗就能拿到英文，只有"不重开"才看得出来）

四组签名（静态红?, EN 红?, UI 红?, REFRESH 红?）互不相同：
  R1 = (是, 是, 是, 否)
  R2 = (是, 否, 否, 否)
  R3 = (否, 是, 是, 否)
  R4 = (否, 否, 否, 是)

用法：  python m35_inject.py            # 四组全跑
        python m35_inject.py R2         # 只跑指定组
跑完自动还原源码与资源（.m35inject.bak）。
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

LEGAL = os.path.join(PROJ, "Assets/_Project/Resources", "Legal")
EN_PRIVACY = os.path.join(LEGAL, "en", "privacy.txt")
EN_TERMS = os.path.join(LEGAL, "en", "terms.txt")
LEGAL_TEXTS = os.path.join(PROJ, "Assets/_Project/Scripts/Compliance/LegalTexts.cs")
LEGAL_VIEWER = os.path.join(PROJ, "Assets/_Project/Scripts/Compliance/LegalViewer.cs")

BACKUP = ".m35inject.bak"


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
         "-logFile", os.path.join(ROOT, "unity_build_m35_inject.log")],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0


def run_probe(scene):
    r = subprocess.run([PY, os.path.join(ROOT, "legal_probe.py"), "--only", scene],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT)
    return r.returncode == 0


def static():
    """跑静态第 14 项，返回报错条数。"""
    r = subprocess.run([PY, os.path.join(ROOT, "audit_project.py")],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    n = 0
    hit = []
    for line in out.splitlines():
        if "法务文本" in line:
            n += 1
            if len(hit) < 3:
                hit.append(line.strip())
    return n, hit


# ── 四组注入 ────────────────────────────────────────────────

def r1():
    """删掉英文版 privacy.txt：静态抓"缺英文版"，运行时抓"回退中文"。"""
    backup(EN_PRIVACY)
    os.remove(EN_PRIVACY)
    # Unity 会给被删资源留下 .meta，收尾时一并处理
    return [EN_PRIVACY]


def r2():
    """英文版版本号漂移：正文仍是英文，只有"版本号一致"这一条抓得住。"""
    backup(EN_TERMS)
    edit(EN_TERMS, "[Version] 2026.10", "[Version] 2026.09")
    return [EN_TERMS]


def r3():
    """缓存不按语言分区：切到英文后拿到的还是中文缓存。文件都在，静态看不出来。"""
    backup(LEGAL_TEXTS)
    edit(LEGAL_TEXTS,
         '            string cacheKey = lang + "/" + key;',
         '            string cacheKey = key;   // R3 注入：不按语言分区')
    return [LEGAL_TEXTS]


def r4():
    """法务窗不挂刷新器：重开窗能拿到英文，只有"不重开就地切语言"才看得出来。"""
    backup(LEGAL_VIEWER)
    edit(LEGAL_VIEWER,
         '            var refresher = root.AddComponent<LegalTextRefresher>();\n'
         '            refresher.TitleText = titleText;\n'
         '            refresher.BodyText = bodyText;\n'
         '            refresher.CloseText = closeText;\n'
         '            refresher.TitleKey = titleKey;\n'
         '            refresher.LegalKey = legalKey;\n',
         '            // R4 注入：不挂刷新器，窗打开后就是死的\n')
    return [LEGAL_VIEWER]


CASES = {
    "R1": (r1, [EN_PRIVACY]),
    "R2": (r2, [EN_TERMS]),
    "R3": (r3, [LEGAL_TEXTS]),
    "R4": (r4, [LEGAL_VIEWER]),
}


def run_case(name, only=None):
    fn, paths = CASES[name]
    print("=" * 74)
    print("注入 %s" % name)
    print("=" * 74)

    for p in paths:
        backup(p)
    touched = fn()

    try:
        n, hit = static()
        print("  静态第 14 项：报错 %d 条" % n)
        for h in hit:
            print("      %s" % h)

        if not build():
            print("  构建失败——注入把代码改坏了，判据无从谈起")
            return None

        ok_en = run_probe("EN")
        ok_ui = run_probe("UI")
        ok_rf = run_probe("REFRESH")
        print("  运行时 EN       ：%s" % ("通过" if ok_en else "不通过"))
        print("  运行时 UI       ：%s" % ("通过" if ok_ui else "不通过"))
        print("  运行时 REFRESH  ：%s" % ("通过" if ok_rf else "不通过"))
        return (name, n, ok_en, ok_ui, ok_rf)
    finally:
        for p in set(paths + touched):
            restore(p)
        for side in (p + ".meta" for p in set(paths + touched)):
            if os.path.exists(side + BACKUP):
                shutil.copy2(side + BACKUP, side)
                os.remove(side + BACKUP)


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    names = [only] if only else ["R1", "R2", "R3", "R4"]

    results = []
    for n in names:
        r = run_case(n, only)
        if r:
            results.append(r)
        print()

    # 每组结束时只还原了源码，最后一次构建仍带着该组的注入——
    # 不重建的话，后面跑任何探针用的都是"带缺陷"的产物（REFRESH 会莫名其妙变红）。
    print("还原源码后重新构建（清掉最后一组注入的产物）…")
    build()

    if only:
        return 0

    print("=" * 74)
    print("互不相交检查")
    print("=" * 74)
    sig = {}
    for name, n, ok_en, ok_ui, ok_rf in results:
        sig[name] = (n > 0, not ok_en, not ok_ui, not ok_rf)
    for name in ("R1", "R2", "R3", "R4"):
        if name in sig:
            s = sig[name]
            print("  %s 静态红=%-3s EN红=%-3s UI红=%-3s REFRESH红=%s"
                  % (name, s[0], s[1], s[2], s[3]))
    ok = len(set(sig.values())) == len(sig)
    print("\n结论：%s" % ("四组失败面互不相同" if ok
                         else "有两组失败面重合——其中一条判据是另一条的影子"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
