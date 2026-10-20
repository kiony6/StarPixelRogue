#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M48「玩家到底点不点得到」判据的区分力注入验证

做什么：
    M48 补的是此前所有探针共同绕过的那一层：点按钮一直是 `onClick.Invoke()`，
    那是**直接调用回调**，EventSystem 的 raycast 压根没走。于是"一层全屏遮罩盖在按钮上"
    这类缺陷在探针眼里不存在——探针照样把按钮"点"响，判据照样绿。
    新增的 `UIHitTest` 补上这一层，EXIT 场景多了两条判据：

      16) 预警遮罩确实挡住了重开按钮   （第一次死亡那一刻，遮罩压在按钮上 → 应"点不到"）
      17) 重开后重开按钮可被点到       （遮罩已被收掉那一局 → 应"点得到"）

    五组注入把这三条**以及命中测试本身**都校准一遍：

      Q2 摘掉建世界时收起跨局遮罩的那一行（M47 本体修复）
                              → {重开后旧提示遮罩已收, 重开后屏幕中心无残留遮罩}
                                （M47 只验了"遮罩对象还在"；这条验的是"因此新一局的操作界面
                                  被它盖住了"——缺陷真正伤害玩家的那一半）
      Q3 把遮罩画布的 sortOrder 顶到最上层
                              → {结算面板叠在时长遮罩之上}
                                （证明第 16 条验的是**叠放顺序**：正常情况下面板后建、
                                  盖在遮罩上，把遮罩顶上去之后它就得红。
                                  没有这一组，第 16 条只是"面板在"的另一种说法）
      Q4 命中测试恒判"被挡住"    → {结算面板叠在时长遮罩之上, 重开后屏幕中心无残留遮罩,
                                  重开后重开按钮可被点到} + PANEL {重开按钮可被点到}
                                （检测器校准：命中测试这个新工具坏掉时，这四条必须一起红。
                                  否则它们只是"刚好绿着"，与 onClick.Invoke() 是一类东西）
      Q5 命中测试恒判"可点到"    → 期望**全绿**
                                （另一端的校准：恒"可点到"是最宽容的坏法，
                                  三条"应该点得到"的判据都绿是**平凡成立**的——
                                  把它记下来，是为了不让 Q4 那一红被误读成"判据有牙"）
      Q1 什么都不改（对照）      → 期望**全绿**

    两条判据的期望方向因此是相反的，别看混：
      16)「结算面板叠在时长遮罩之上」= 期望**点得到**（面板后建、在上）
      17)「重开后屏幕中心无残留遮罩」= 期望**屏幕上没有遮罩**（新一局没有更晚建的画布）

    顺带记一笔本轮的意外：M47 §7 第 2 条写着"真实玩家在遮罩下面点不到「重新开始」"，
    实测**不成立**——面板是死亡时才建的，比 55 分钟那层遮罩晚，叠在它上面。
    第 16 条就是这个证伪的固化。

怎么读失败面：
    直接读 exe 自己打的 `[SMOKE] EXIT失败项  <判据名>` / `[SMOKE] PANEL失败项  <判据名>`，
    一行一条。不去解析判据表格（`否（画布数 2）` 这类值不以"否"结尾，按表格解析会漏），
    也不读 SmokeTest 收尾那份同名 `失败项`（那是它按空世界报的，与本场景无关）。

用法：
    python m48_inject.py                # 五组全跑
    python m48_inject.py --only Q2      # 只跑一组
    python m48_inject.py --seconds 150  # 缩短每组观察时长

退出码：
    0 = 各组失败面均与预期一致；1 = 有不一致 / 跑批失败；2 = 前置条件不满足
"""

import argparse
import io
import os
import shutil
import subprocess

ROOT = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")
UNITY = "E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PROJECT = os.path.join(ROOT, "StarPixelRogue")
BAK = ".m48bak"

GAME_BUILDER = "GameBuilder.cs"
PLAYTIME = "Core/PlaytimeGuard.cs"
UI_HIT = "Dev/UIHitTest.cs"

# edits 的每一项是 (相对 Scripts 的路径, 原文, 替换文)
CASES = [
    dict(
        id="Q2", name="建世界不收跨局残留的提示遮罩（M47 本体修复）",
        scenes=["EXIT"],
        edits=[(GAME_BUILDER,
                "            Core.PlaytimeGuard.Instance?.DismissHint();\n",
                "            // 注入：建世界不收跨局残留的提示遮罩（M47 修复前的状态）\n")],
        expect={"重开后旧提示遮罩已收", "重开后屏幕中心无残留遮罩"},
        note="M47 验的是「遮罩对象还在」，本条验的是「新一局的操作界面被它盖住了」——后果那一半"),
    dict(
        id="Q3", name="把时长遮罩顶到最上层（sortingOrder = 20）",
        scenes=["EXIT"],
        edits=[(PLAYTIME,
                '            var (mask, root) = UIFactory.CreateModal("PlaytimeHint", new Color(0f, 0f, 0f, 0.75f));\n'
                "            _hintRoot = mask;\n",
                '            var (mask, root) = UIFactory.CreateModal("PlaytimeHint", new Color(0f, 0f, 0f, 0.75f));\n'
                "            _hintRoot = mask;\n"
                "            mask.GetComponent<Canvas>().sortingOrder = 20;   // 注入：遮罩顶到最上层\n")],
        expect={"结算面板叠在时长遮罩之上"},
        note="证明第 16 条验的是叠放顺序本身：把遮罩顶上去，面板就得让位"),
    dict(
        id="Q4", name="命中测试恒判「被挡住」",
        scenes=["EXIT", "PANEL"],
        edits=[(UI_HIT,
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n',
                '            // 注入：命中测试恒判"被挡住"（校准用——它坏成这样时下面四条必须一起红）\n'
                '            if (btn != null)\n'
                '            {\n'
                "                r.Found = true; r.Reachable = false;\n"
                '                r.Blocker = "(恒被挡·注入)"; r.TopCanvas = "PlaytimeHint";\n'
                '                r.Detail = "恒被挡（注入）";\n'
                "                return r;\n"
                "            }\n"
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n'),
                # 第 17 条走的是 ProbePoint（测屏幕中心，不是测按钮），
                # 只改 Probe 的话它不会跟着红 —— 两边都得改，这次校准才算数。
                (UI_HIT,
                 '            var r = new Hit { Screen = Screen.width + "x" + Screen.height, Blocker = "-",\n'
                 '                              At = Mathf.RoundToInt(screenPos.x) + "," + Mathf.RoundToInt(screenPos.y),\n'
                 '                              Found = true, TopCanvas = "-" };\n',
                 '            var r = new Hit { Screen = Screen.width + "x" + Screen.height, Blocker = "-",\n'
                 '                              At = Mathf.RoundToInt(screenPos.x) + "," + Mathf.RoundToInt(screenPos.y),\n'
                 '                              Found = true, TopCanvas = "-" };\n'
                 '            // 注入：屏幕中心恒判"盖着遮罩"\n'
                 '            r.TopCanvas = "PlaytimeHint"; r.Blocker = "(恒被挡·注入)";\n'
                 '            r.Reachable = true; r.Detail = "恒被挡（注入）";\n'
                 "            return r;\n")],
        expect={"结算面板叠在时长遮罩之上", "重开后屏幕中心无残留遮罩",
                "重开后重开按钮可被点到", "重开按钮可被点到"},
        note="检测器校准：新工具坏掉时四条一起红，否则它们只是「刚好绿着」"),
    dict(
        id="Q5", name="命中测试恒判「可点到」",
        scenes=["EXIT"],
        edits=[(UI_HIT,
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n',
                '            // 注入：命中测试恒判"可点到"（最宽容的坏法——三条"应点得到"的判据\n'
                "            //  会平凡成立。记下这一点，Q4 那一红才不会被误读成判据有牙）\n"
                '            if (btn != null)\n'
                '            {\n'
                "                r.Found = true; r.Reachable = true; r.Blocker = btn.name;\n"
                '                r.TopCanvas = "GameOverCanvas"; r.Detail = "恒可点（注入）";\n'
                "                return r;\n"
                "            }\n"
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n')],
        expect=set(),
        note="全绿是平凡成立的：它说明「恒可点」这种坏法测不出来，判据的宽容边界在这"),
    dict(id="Q1", name="什么都不改（对照组）", scenes=["EXIT"], edits=[], expect=set(),
         note="确认 EXIT 场景本身不产生假红"),
]

# exe 自己会打 `[SMOKE] EXIT失败项  <判据名>`，一行一条。
FAIL_KEYS = {"EXIT": "EXIT失败项", "PANEL": "PANEL失败项"}
CONC_KEYS = {"EXIT": "EXIT结论", "PANEL": "PANEL结论"}
SECONDS = {"EXIT": 180, "PANEL": 120}


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    return io.open(p, encoding="utf-8").read()


def write(p, t):
    io.open(p, "w", encoding="utf-8").write(t)


def case_files(case):
    return [os.path.normpath(os.path.join(SCRIPTS, f)) for f, _o, _n in case["edits"]]


def assert_clean(case=None):
    """注入前后的干净性：不许有别的组的备份 / 注入标记残留。

    按**文件**判而不是按组：多组可能落在同一个文件上，
    按组比对会把本组合规的注入误判成上一组的残留。
    """
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
            if "// 注入：" not in read(p):
                continue
            if p not in allowed:
                raise RuntimeError("残留注入: %s" % rel(p))


def inject(case):
    """替换前先备份；每处替换都必须恰好命中 1 次（命中 0 处 = 源码已变，注入失效）。"""
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
            # Unity 会给"备份文件"也生成一个 .meta，还原后它成了孤儿（M47 实测撞到）
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def build():
    log = os.path.join(ROOT, "unity_build_m48_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                    "-logFile", log.replace("\\", "/")],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    txt = io.open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run(scene, seconds):
    args = [EXE, "-batchmode", "-nographics", "-longrunProbe", "-longrunScene", scene,
            "-smokeSeconds", str(seconds), "-smokeSpeed", "6"]
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=seconds + 240)
        return (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return out + "\n[SMOKE] %s结论              不通过（超时）" % scene


def parse(log, scene):
    """取该场景专属的失败项与结论。

    Line 的格式是 `[SMOKE] KEY              VALUE`（多个空格），按 2+ 空格切。
    失败项可能有多行，用 list 收；结论只有一行。
    """
    fails, conc = [], None
    for line in log.splitlines():
        if "[SMOKE]" not in line:
            continue
        body = line.split("[SMOKE]", 1)[1].strip()
        parts = [s for s in body.split("  ") if s.strip()]
        if len(parts) < 2:
            continue
        k, v = parts[0].strip(), "  ".join(parts[1:]).strip()
        if k == FAIL_KEYS[scene]:
            fails.append(v)
        elif k == CONC_KEYS[scene]:
            conc = v
    return set(fails), conc


def run_case(case):
    print("=" * 74)
    print("%s %s —— %s" % (case["id"], case["name"], case["note"]))
    print("=" * 74)

    inject(case)
    try:
        assert_clean(case)
        build()
        fails, concs = set(), {}
        for scene in case["scenes"]:
            log = run(scene, SECONDS[scene])
            f, c = parse(log, scene)
            fails |= f
            concs[scene] = c
            print("  [%s] 失败面 %s / 结论 %s"
                  % (scene, sorted(f) or "（全绿）", c or "（缺失：没跑到收尾，结果不可信）"))
        ok = (fails == case["expect"]) and all(c in ("通过", "不通过") for c in concs.values())
        print("  期望失败面 %s" % (sorted(case["expect"]) or "（全绿）"))
        print("  实际失败面 %s" % (sorted(fails) or "（全绿）"))
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

    # 前置：源码必须是干净的（上一轮的注入/备份没留下痕迹）
    try:
        assert_clean(None)
    except RuntimeError as e:
        print("前置检查失败: %s" % e)
        return 2

    print("M48 玩家点不点得到 · 区分力注入 %d 组" % len(cases))
    all_ok = True
    try:
        for c in cases:
            try:
                all_ok = run_case(c) and all_ok
            except RuntimeError as e:
                print("  !! %s\n" % e)
                all_ok = False
    finally:
        restore_all()
        print("源码已还原（还原后需重新构建才能跑正常实测）")

    print("=" * 74)
    print("总计：%s" % ("各组失败面均与预期一致" if all_ok else "存在不一致，见上"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
