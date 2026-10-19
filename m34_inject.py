#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M34 区分力验证：往工程里注入 4 组互不相同的缺陷，看判据是不是真的会红。

静态检查最危险的失败形态是"永远不报错"——第 13 项若写成摆设，工程照样一片绿，
而 HUD 的模式标识与断线遮罩会继续停在旧语言。所以这里反向验证：
该红的必须红、不该红的必须不红，且**四组注入的失败面互不相交**——
否则说明其中某条判据是另一条的影子，删掉也没人知道。

  R1 回退 modeText 的语言重设     → 静态红(modeText) + NETUI 红（两层都抓得到）
  R2 遮罩重建顺序写反             → 静态绿 + NETUI 红（只有运行时看得出：静态看"有没有重设"，
                                    看不出"重设之后又被销毁了"）
  R3 大厅新增一处一次性本地化文案 → 静态红 + NETUI 绿（静态的独有价值：
                                    大厅不在 NETUI 的扫描范围里）
  R4 豁免注解清空理由             → 静态红（证明注解是"封闭词汇"，空理由不能绕过）

用法：  python m34_inject.py            # 四组全跑
        python m34_inject.py R2         # 只跑指定组
跑完自动还原源码（.m34inject.bak）。
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

HUD = os.path.join(PROJ, "Assets/_Project/Scripts/UI/HUDController.cs")
RECONNECT = os.path.join(PROJ, "Assets/_Project/Scripts/Net/NetReconnect.cs")
LOBBY = os.path.join(PROJ, "Assets/_Project/Scripts/Net/NetLobby.cs")

BACKUP = ".m34inject.bak"


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
         "-logFile", os.path.join(ROOT, "unity_build_m34_inject.log")],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0


def run_probe(scene):
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


def static():
    """返回 (第13项报错条数, 报错文本里出现的字段名集合, ERROR 合计)"""
    r = subprocess.run([PY, os.path.join(ROOT, "audit_project.py")],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT)
    out = r.stdout or ""
    hits = re.findall(r"切语言后不会刷新: (\S+) 第 (\d+) 行.*字段 (\w+)", out)
    m = re.search(r"合计：ERROR (\d+)", out)
    return len(hits), {h[2] for h in hits}, (int(m.group(1)) if m else -1)


# ── 四组注入 ────────────────────────────────────────────────

def r1():
    """回退 modeText 的语言重设：静态按字段配对抓得到，运行时也看得到中文。"""
    backup(HUD)
    edit(HUD,
         '            if (modeText) modeText.text = Net.NetSession.ModeLabel();\n',
         '')


def r2():
    """遮罩重建顺序写反：先建后销毁，新遮罩立刻没了。静态看"有没有重设"，看不出这个。"""
    backup(RECONNECT)
    edit(RECONNECT,
         '            Destroy(_overlay);\n'
         '            _status = null;\n'
         '            BuildOverlay();',
         '            BuildOverlay();\n'
         '            Destroy(_overlay);\n'
         '            _status = null;')


def _inject_lobby_tip(reason):
    """大厅新增一处一次性本地化文案：它不在 NETUI 的扫描范围里，只有静态抓得到。

    注意注入点必须是一个**不会被语言回调调到**的新方法——大厅的 BuildUI 已被修复成
    由 Rebuild 调用（Rebuild 订阅了 LanguageChanged），写进 BuildUI 里反而抓不到。
    """
    backup(LOBBY)
    edit(LOBBY,
         '        private Text _statusText;\n',
         '        private Text _statusText;\n'
         '        private Text _tipText;\n')
    edit(LOBBY,
         '        private void SetStatus(string s)\n',
         '        private void ShowTip()\n'
         '        {\n'
         '            %s\n'
         '            _tipText = CreateText(_root.transform, Core.Localization.T("net.pick_mode"),'
         ' new Vector2(0, -190), 18);\n'
         '        }\n\n'
         '        private void SetStatus(string s)\n' % reason)


def r3():
    """新增一处一次性文案 + **带理由**豁免：静态应当放行（证明豁免不是摆设）。"""
    _inject_lobby_tip('// @nolangrefresh: 演示用的一次性提示，每次 Show 都会重建')


def r4():
    """同一处 + **空理由**豁免：静态仍须报错（注解是封闭词汇，空理由不能绕过）。"""
    _inject_lobby_tip('// @nolangrefresh:')


CASES = {
    "R1": (r1, [(HUD,)]),
    "R2": (r2, [(RECONNECT,)]),
    "R3": (r3, [(LOBBY,)]),
    "R4": (r4, [(LOBBY,)]),
}


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    names = [only] if only else ["R1", "R2", "R3", "R4"]
    results = []

    for name in names:
        inject, targets = CASES[name]
        print("=" * 70)
        print("%s 注入：%s" % (name, inject.__doc__.strip().splitlines()[0]))
        print("=" * 70)
        inject()
        try:
            if not build():
                print("  构建失败——注入本身不成立\n")
                continue
            n, fields, total = static()
            ok_net, vals = run_probe("NETUI")
            print("  静态第13项：%d 处（字段 %s）· ERROR 合计 %d" % (n, sorted(fields) or "无", total))
            print("  NETUI 场景：%s" % ("通过" if ok_net else "不通过（红了）"))
            for k in ("联机UI已上屏", "联机UI文本总数", "中文态含中文",
                      "英文态文本总数", "英文态含中文", "首条中文", "切回后含中文"):
                if k in vals:
                    print("      %-14s %s" % (k, vals[k]))
            results.append((name, n, fields, ok_net, vals))
        finally:
            for t in targets[0]:
                restore(t)
        print()

    if len(results) >= 4:
        print("=" * 70)
        print("互不相交检查（期望的失败面）")
        print("=" * 70)
        want = {"R1": (True, True),    # 两层都抓得到：modeText 既在源码里、也上屏了
                "R2": (False, True),   # 只有运行时看得出：静态看不出"重设完又被销毁"
                "R3": (False, False),  # 带理由豁免 → 放行（豁免不是摆设）
                "R4": (True, False)}   # 空理由豁免 → 仍须报（注解是封闭词汇）
        sig, bad = {}, []
        for name, n, fields, ok_net, _v in results:
            sig[name] = (n > 0, not ok_net)
            print("  %s 静态红=%-5s 运行时红=%-5s 期望%s 实际%s"
                  % (name, sig[name][0], sig[name][1], want[name], sig[name]))
            if sig[name] != want[name]:
                bad.append(name)
        disjoint = len(set(sig.values())) == len(sig)
        print("\n结论：%s" % (
            "四组失败面互不相同" if disjoint else "有两组失败面重合——其中一条判据是另一条的影子"))
        if bad:
            print("未达期望：%s" % ", ".join(bad))


if __name__ == "__main__":
    main()
