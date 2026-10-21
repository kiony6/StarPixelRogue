#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M49「玩家在选卡面板上被踢」判据的区分力注入验证

做什么：
    M49 补的是到点的**另一种姿势**：M47/M48 的 EXIT 场景里玩家是被踢在结算面板上的，
    而真实玩家也可能正停在选卡面板上（timeScale 已被 SetPaused(true) 压到 0）时被踢。
    这条路径要收的是 LevelUpCanvas、要解冻的是选卡暂停，与 EXIT 是另一条链，
    且此前从未被任何测试走到过（M48 §7 第 1 条）。

    六组注入把新判据校准一遍，重点是**两个方向都要有的那一对**：

      Q2 选卡面板挂到场景根（不随管理器拆世界）
                              → {强制退出后选卡面板被收}
                                （M5 那句"随管理器一起拆世界"的反面。面板一旦 new 在场景根上，
                                  拆世界那套清理就碰不到它 —— 与 M46 的结算面板同一个形态）
      Q3 只摘 LevelUpManager.OnDestroy 的解冻兜底
                              → 期望**全绿**
                                （单点破坏不红：TeardownWorld 里收结算面板那行会顺带解冻。
                                  记下它是为了说明这条判据的**冗余度**，
                                  免得 Q4 那一红被误读成"只有一处会坏"）
      Q4 把两处解冻来源一起拔掉（Q3 + TeardownWorld 的 CloseAndResume）
                              → {强制退出后时间解冻}
                                （证明这条判据有牙：两处同时失效，时间才真的卡在 0）
      Q5 命中测试恒判「可点到」
                              → {预警遮罩挡住选卡卡片}
                                （**本轮最要紧的一组**。M48 的 Q5 期望全绿 —— 因为那时只有
                                  "应当点得到"的判据，"恒可点"是最宽容的坏法，测不出来。
                                  M49 补了反向判据之后，同一种坏法必须被抓到）
      Q6 命中测试恒判「被挡住」
                              → {卡片可被点到, 关掉提示后卡片恢复可点}
                                （另一端的校准：三条命中判据真的由命中测试的输出驱动，
                                  不是"刚好绿着"，否则与 onClick.Invoke() 是一类东西）
      Q1 什么都不改（对照）    → 期望**全绿**

    为什么必须有 Q5 那一侧：
        判据的方向决定它能看见什么。"卡片应可点"这条，在命中测试恒判可点时是**平凡成立**的
        ——它永远绿，无论世界变成了什么样。只有存在一条"卡片应被挡住"的反向判据，
        同一处坏法才会暴露。M48 §7 第 4 条留的就是这件事。

怎么读失败面：
    直接读 exe 自己打的 `[SMOKE] CARD失败项  <判据名>`，一行一条。
    不去解析判据表格（`否（卡片 2）` 这类值不以"否"结尾，按表格解析会漏），
    也不读 SmokeTest 收尾那份同名 `失败项`（那是它按空世界报的，与本场景无关）。

用法：
    python m49_inject.py                # 六组全跑
    python m49_inject.py --only Q5      # 只跑一组

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
BAK = ".m49bak"

LEVEL_UP = "UI/LevelUpManager.cs"
GAME_BUILDER = "GameBuilder.cs"
UI_HIT = "Dev/UIHitTest.cs"

# —— 单点注入片段（Q4 = Q3 + 摘 TeardownWorld 收面板那行）——

# 摘掉 LevelUpManager.OnDestroy 里的解冻兜底
KILL_LEVELUP_THAW_OLD = (
    "            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);\n"
    "            else Time.timeScale = Core.HitstopManager.BaseScale;\n"
)
KILL_LEVELUP_THAW_NEW = (
    "            // 注入：摘掉这里的解冻兜底（单点破坏不红 —— TeardownWorld 收面板那行会顶住）\n"
)

# 摘掉 TeardownWorld 里收结算面板（顺带解冻）的那一行
KILL_TEARDOWN_RESUME_OLD = (
    "            if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.CloseAndResume();\n"
)
KILL_TEARDOWN_RESUME_NEW = (
    "            // 注入：不收结算面板（与摘掉 LevelUpManager 兜底一起，两处解冻来源全拔）\n"
)

# edits 的每一项是 (相对 Scripts 的路径, 原文, 替换文)
CASES = [
    dict(
        id="Q2", name="选卡面板挂到场景根（不随管理器拆世界）",
        scenes=["CARD"],
        edits=[(LEVEL_UP,
                "            canvasGO.transform.SetParent(transform, false);  // M5：随管理器一起拆世界\n",
                "            canvasGO.transform.SetParent(null, false);   // 注入：挂到场景根，不随管理器拆\n")],
        expect={"强制退出后选卡面板被收"},
        note="M5 那句「随管理器一起拆世界」的反面 —— 面板 new 在场景根上，拆世界就碰不到它"),
    dict(
        id="Q3", name="只摘 LevelUpManager.OnDestroy 的解冻兜底",
        scenes=["CARD"],
        edits=[(LEVEL_UP, KILL_LEVELUP_THAW_OLD, KILL_LEVELUP_THAW_NEW)],
        expect=set(),
        note="单点破坏不红：TeardownWorld 收面板那行会顺带解冻，两处互为冗余"),
    dict(
        id="Q4", name="两处解冻来源一起拔掉",
        scenes=["CARD"],
        edits=[(LEVEL_UP, KILL_LEVELUP_THAW_OLD, KILL_LEVELUP_THAW_NEW),
               (GAME_BUILDER, KILL_TEARDOWN_RESUME_OLD, KILL_TEARDOWN_RESUME_NEW)],
        expect={"强制退出后时间解冻"},
        note="证明这条判据有牙：两处同时失效，时间才真的卡在 0（主菜单动画/下一局都动不了）"),
    dict(
        id="Q5", name="命中测试恒判「可点到」",
        scenes=["CARD"],
        edits=[(UI_HIT,
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n',
                '            // 注入：命中测试恒判"可点到"（M48 Q5 里这种坏法**测不出来**，\n'
                "            //  因为那时只有「应当点得到」的判据。本轮补了反向判据，它必须被抓到）\n"
                '            if (btn != null)\n'
                '            {\n'
                "                r.Found = true; r.Reachable = true; r.Blocker = btn.name;\n"
                '                r.TopCanvas = "LevelUpCanvas"; r.Detail = "恒可点（注入）";\n'
                "                return r;\n"
                "            }\n"
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n')],
        expect={"预警遮罩挡住选卡卡片"},
        note="本轮最要紧的一组：M48 Q5 期望全绿（那一侧是盲的），本组必须红"),
    dict(
        id="Q6", name="命中测试恒判「被挡住」",
        scenes=["CARD"],
        edits=[(UI_HIT,
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n',
                '            // 注入：命中测试恒判"被挡住"（另一端校准 —— 三条命中判据\n'
                "            //  真的由命中测试的输出驱动，而不是「刚好绿着」）\n"
                '            if (btn != null)\n'
                '            {\n'
                "                r.Found = true; r.Reachable = false;\n"
                '                r.Blocker = "(恒被挡·注入)"; r.TopCanvas = "PlaytimeHint";\n'
                '                r.Detail = "恒被挡（注入）";\n'
                "                return r;\n"
                "            }\n"
                '            if (btn == null) { r.Detail = "按钮不存在"; return r; }\n')],
        expect={"卡片可被点到", "关掉提示后卡片恢复可点"},
        note="检测器校准：三条命中判据必须一起被驱动，否则与 onClick.Invoke() 没区别"),
    dict(id="Q1", name="什么都不改（对照组）", scenes=["CARD"], edits=[], expect=set(),
         note="确认 CARD 场景本身不产生假红"),
]

FAIL_KEYS = {"CARD": "CARD失败项"}
CONC_KEYS = {"CARD": "CARD结论"}
SECONDS = {"CARD": 180}


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
    log = os.path.join(ROOT, "unity_build_m49_inject.log")
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

    print("M49 选卡面板上被踢 · 区分力注入 %d 组" % len(cases))
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
