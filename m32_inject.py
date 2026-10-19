#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M32 区分力验证：逐个注入缺陷，确认「对应判据变红、其余判据不串扰」。

每组注入后都要重新编译 + 构建 + 跑探针，跑完立刻还原源码，
因此脚本用 try/finally 保证任何情况下都还原。
"""

import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
UNITY = "E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PROJ = os.path.join(ROOT, "StarPixelRogue")
PC = os.path.join(PROJ, "Assets", "_Project", "Scripts", "Player", "PlayerController.cs")
PY = "C:/Users/VOS-User/.workbuddy/binaries/python/versions/3.13.12/python.exe"

I = " " * 12


def blk(*ls):
    return "\n".join(ls)


# AddSkill 修复后的形状（当前状态）：Recompute/CheckCombos/Raise 全在 if (isLocal) 块内
ADD_BLOCK = blk(
    I + "if (isLocal)",
    I + "{",
    I + "    ComboManager.Instance?.Recompute(GetTags());",
    I + "    CheckCombos();",
    I + "    EventBus.RaiseWeaponsChanged();",
    I + "}",
    I + "Core.SaveManager.Instance?.DiscoverWeapon(def.id);   // M6.4 图鉴",
)

LV_HEAD = I + "// M32：远程玩家升级武器不得改写本地加成，也不得往本地 HUD 广播"
LV_BLOCK = blk(
    LV_HEAD,
    I + "if (isLocal)",
    I + "{",
    I + "    ComboManager.Instance?.Recompute(GetTags());",
    I + "    CheckCombos();",
    I + "    EventBus.RaiseWeaponsChanged();",
    I + "}",
)

CASES = [
    # R1：把 AddSkill 的广播移出守卫（回到 M31 遗留的原始缺陷形态）
    #     预期 REMOTE 红；LOCAL / STATE 必须仍绿
    ("R1_广播移出守卫",
     ADD_BLOCK,
     blk(
         I + "if (isLocal)",
         I + "{",
         I + "    ComboManager.Instance?.Recompute(GetTags());",
         I + "    CheckCombos();",
         I + "}",
         I + "EventBus.RaiseWeaponsChanged();",
         I + "Core.SaveManager.Instance?.DiscoverWeapon(def.id);   // M6.4 图鉴",
     ),
     ["REMOTE", "LOCAL", "STATE"]),

    # R2：把 LevelUpWeapon 的广播移出守卫（另一条同类路径）
    #     预期 REMOTE 红——验证判据覆盖两条路径，不是只盯着 AddSkill
    ("R2_同类另一条路失守",
     LV_BLOCK,
     blk(
         LV_HEAD,
         I + "if (isLocal)",
         I + "{",
         I + "    ComboManager.Instance?.Recompute(GetTags());",
         I + "    CheckCombos();",
         I + "}",
         I + "EventBus.RaiseWeaponsChanged();",
     ),
     ["REMOTE", "LOCAL"]),

    # R3：修过头——把 AddSkill 的广播整行删掉
    #     预期 LOCAL 红、REMOTE 绿——只写"远程不许广播"会把这种改法判成通过
    ("R3_修过头删掉广播",
     ADD_BLOCK,
     blk(
         I + "if (isLocal)",
         I + "{",
         I + "    ComboManager.Instance?.Recompute(GetTags());",
         I + "    CheckCombos();",
         I + "}",
         I + "Core.SaveManager.Instance?.DiscoverWeapon(def.id);   // M6.4 图鉴",
     ),
     ["LOCAL", "REMOTE"]),

    # R4：只把 Recompute 移出守卫，广播仍守着
    #     预期 STATE 红、REMOTE 绿——验证"广播泄漏"与"全局状态污染"是两条独立判据
    ("R4_只漏全局状态污染",
     ADD_BLOCK,
     blk(
         I + "ComboManager.Instance?.Recompute(GetTags());",
         I + "CheckCombos();",
         I + "if (isLocal)",
         I + "{",
         I + "    EventBus.RaiseWeaponsChanged();",
         I + "}",
         I + "Core.SaveManager.Instance?.DiscoverWeapon(def.id);   // M6.4 图鉴",
     ),
     ["STATE", "REMOTE"]),
]


def read():
    with open(PC, "r", encoding="utf-8", newline="") as f:
        return f.read()


def write(t):
    with open(PC, "w", encoding="utf-8", newline="") as f:
        f.write(t)


def run(cmd, timeout=300):
    p = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def compile_and_build(tag):
    log = os.path.join(ROOT, "unity_compile_%s.log" % tag)
    rc, _ = run([UNITY, "-batchmode", "-nographics", "-quit",
                 "-projectPath", PROJ, "-logFile", log])
    if rc != 0:
        return False, "编译退出码 %d" % rc
    logb = os.path.join(ROOT, "unity_build_%s.log" % tag)
    rc2, _ = run([UNITY, "-batchmode", "-nographics", "-quit",
                  "-projectPath", PROJ,
                  "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                  "-logFile", logb])
    if rc2 != 0:
        return False, "构建退出码 %d" % rc2
    return True, ""


def probe(scene):
    """返回 True=通过（绿），False=不通过（红）。"""
    rc, out = run([PY, os.path.join(ROOT, "guard_probe.py"), "--only", scene], timeout=300)
    return rc == 0, out


def main():
    original = read()
    all_ok = True
    try:
        for name, old, new, scenes in CASES:
            t = read()
            n = t.count(old)
            if n != 1:
                print("  [%s] 锚点匹配 %d 次（应为 1），跳过" % (name, n))
                all_ok = False
                continue
            write(t.replace(old, new))

            ok, why = compile_and_build(name.split("_")[0].lower())
            if not ok:
                print("  [%s] %s —— 无法验证" % (name, why))
                write(original)
                all_ok = False
                continue

            marks = []
            for sc in scenes:
                green, _out = probe(sc)
                marks.append("%s=%s" % (sc, "绿" if green else "红"))
            print("  [%s] %s" % (name, "  ".join(marks)))

            write(original)   # 立刻还原
    finally:
        write(original)
        print("  源码已还原")

    # 还原后重新编译构建，保证后续跑的是修复版
    ok, why = compile_and_build("final")
    print("  还原后重新构建：%s" % ("成功" if ok else why))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
