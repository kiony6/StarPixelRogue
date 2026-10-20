#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M46「真实重开路径」判据的区分力注入验证

做什么：
    把 M46 新加的修复逐个改回缺陷态，确认 PANEL 场景那 11 条判据真的会红——
    而且**只红该红的那些**。四组缺陷 + 一组对照：

      Q1 拆世界不收结算面板   → {拆世界后结算画布消失}
                                （M46 本体修复：青少年模式强制退出那条路）
      Q2 点重开 + 拆世界都不收 → {重开后时间恢复, 重开后结算画布消失, 重开后世界继续推进,
                                  拆世界后结算画布消失}
                                （必须**两处都摘**：只摘 OnRestart 那一处会被 M46 的
                                  拆世界兜底顶掉，实测全绿——这不是判据没牙，
                                  是缺陷被本轮的修复掩盖了。
                                  第 4 组那一条也跟着红：面板没收时 _showing 恒为 true，
                                  第 2 阶段的 RaiseRunEnded 被守卫挡掉，
                                  "拆世界前"数到的就是上一局那块残留画布。
                                  与 Q1 在第 10 条上相交——同一根因覆盖两个观察窗口，如实记）
      Q3 结算时不冻结世界     → {结算时世界冻结}
                                （只有它红：证明「时间恢复」不是靠"根本没冻过"混过去的）
      Q4 HUD 画布建两份       → {重开后 HUD 唯一}
      Q5 什么都不改（对照）    → 期望**全绿**：证明 PANEL 场景本身不产生假红

为什么这一轮必须做：
    这 11 条判据里，有 8 条是"看一眼日志就能觉得它对"的类型——面板出现、
    时间恢复、画布消失……它们全绿的样子和"判据根本没在判"的样子一模一样。
    M38 第 16 项曾出现过"8 个夹具全过、注入却一片绿"，M41 又出现过第一版
    解析格式照抄 py 表格、四组全部假绿。判据不咬人的话，它只是装饰。

    尤其 Q3：如果"结算时世界冻结"这条本身是假的（比如探针根本没等到面板），
    "重开后时间恢复"就会永远绿——因为从来没被冻过。所以必须单独注入一次，
    确认"不冻结"这件事能被抓到，而不是被"恢复"那条的假绿盖住。

怎么读失败面：
    直接读 exe 自己打的 `[SMOKE] PANEL失败项  <判据名>` 行。**不去解析判据表格**
    （`否（画布数 2）` 这类值不以"否"结尾，按表格解析会漏），也不去读
    SmokeTest 收尾那份同名 `失败项`（那是它按空世界报的，与本场景无关）。

用法：
    python m46_inject.py                # 五组全跑
    python m46_inject.py --only Q1      # 只跑一组
    python m46_inject.py --seconds 90   # 缩短每组观察时长

退出码：
    0 = 各组失败面均与预期一致；1 = 有不一致 / 跑批失败；2 = 前置条件不满足
"""

import argparse
import io
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")
UNITY = "E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PROJECT = os.path.join(ROOT, "StarPixelRogue")
BAK = ".m46bak"

GAME_BUILDER = "GameBuilder.cs"
GAME_OVER = "UI/GameOverPanel.cs"

# edits 的每一项是 (相对 Scripts 的路径, 原文, 替换文)——Q2 需要同时改两个文件，
# 所以按"文件"组织而不是按组。
CASES = [
    dict(
        id="Q1", name="拆世界不收结算面板（M46 本体修复）",
        edits=[(GAME_BUILDER,
                "            if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.CloseAndResume();\n",
                "            // 注入：拆世界不收结算面板（M46 修复前的状态）\n")],
        expect={"拆世界后结算画布消失"},
        note="青少年模式强制退出那条路：没人点按钮，面板就永远盖在菜单上"),
    dict(
        id="Q2", name="点重开不收面板 + 拆世界也不兜底",
        edits=[(GAME_OVER,
                "            Core.Telemetry.Log(\"run_restart\");\n            CloseAndResume();\n",
                "            Core.Telemetry.Log(\"run_restart\");\n"
                "            // 注入：点重开不收面板、不恢复时间\n"),
               (GAME_BUILDER,
                "            if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.CloseAndResume();\n",
                "            // 注入：拆世界也不兜底（配合上一处，否则会被顶掉）\n")],
        # 首跑按"timeScale 会卡在 0"写了期望，实测**第 3/7 条没红**：
        # LevelUpManager.OnDestroy（M4.5 加的兜底）先于 HitstopManager 被销毁，
        # 它的 SetPaused(false) 把时间顶回 BaseScale——拆世界这一动作本身就顺带解冻。
        # 于是"没人主动解冻"这个注入下，第 3/7 条必然绿；只有连那份兜底一起摘
        # （Q6 组）才红。第 11 条反而红了：面板没收时 `_showing` 恒为 true，
        # 第 2 阶段的 RaiseRunEnded 被守卫挡掉，压根没冻结过，
        # 于是「拆世界后时间解冻」的前提（拆之前必须冻着）不成立。
        expect={"重开后结算画布消失", "拆世界后结算画布消失", "拆世界后时间解冻"},
        note="面板赖在屏幕上；时间被 LevelUpManager 的兜底解冻顶住了（见 Q6）"),
    dict(
        id="Q3", name="结算时不冻结世界",
        edits=[(GAME_OVER,
                "                if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(true);\n"
                "                else Time.timeScale = 0f;\n",
                "                // 注入：结算时不冻结世界（两行一起摘，否则 else 悬空编译不过）\n")],
        # 第 11 条跟着红：第 2 阶段也没冻结过，它的前提（拆之前必须冻着）不成立。
        # 这组正因为此才必须做——它证明「重开后时间恢复」不是靠"根本没冻过"混绿的。
        expect={"结算时世界冻结", "拆世界后时间解冻"},
        note="只有这两条红：证明「时间恢复」不是靠「根本没冻过」混绿的"),
    dict(
        id="Q4", name="HUD 画布建两份",
        edits=[(GAME_BUILDER,
                "            var hud = new GameObject(\"HUD\").AddComponent<HUDController>();\n"
                "            Track(hud);\n"
                "            CreateRuntimeHUD(hud);\n"
                "\n"
                "            player.GetComponent<PlayerController>().SetProjectileTemplate(projectileTemplate);\n",
                "            var hud = new GameObject(\"HUD\").AddComponent<HUDController>();\n"
                "            Track(hud);\n"
                "            CreateRuntimeHUD(hud);\n"
                "            CreateRuntimeHUD(hud);   // 注入：HUD 画布建两份\n"
                "\n"
                "            player.GetComponent<PlayerController>().SetProjectileTemplate(projectileTemplate);\n")],
        expect={"重开后 HUD 唯一"},
        note="双份 HUD 的症状是双份订阅（击杀数翻倍那类），别的判据一条都看不见"),
    # Q6 是 Q2 的"钻到底"版：Q2 摘掉两处收面板之后，第 3/7 条居然还是绿的——
    # 被 LevelUpManager.OnDestroy 的兜底解冻顶住了。这一组连那份兜底一起摘，
    # 用来回答"第 3/7 条到底是没牙，还是被别处兜住了"。答案是后者：摘到底它们就红。
    dict(
        id="Q6", name="Q2 之外再把 LevelUpManager 的解冻兜底也摘掉",
        edits=[(GAME_OVER,
                "            Core.Telemetry.Log(\"run_restart\");\n            CloseAndResume();\n",
                "            Core.Telemetry.Log(\"run_restart\");\n"
                "            // 注入：点重开不收面板、不恢复时间\n"),
               (GAME_BUILDER,
                "            if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.CloseAndResume();\n",
                "            // 注入：拆世界也不兜底（配合上一处，否则会被顶掉）\n"),
               ("UI/LevelUpManager.cs",
                # 这两行在本文件里出现两次（OnDestroy 的兜底 / 选完卡恢复的出口），
                # 所以连上面三行注释一起锚——M4.5 那句只出现在 OnDestroy 里。
                "            // M4.5：断线拆世界时防止时间缩放卡在 0（选卡面板打开中被销毁）\n"
                "            // M41：兜底分支此前写死 1f，与 HitstopManager 里的\"正常速度\"是两份事实。\n"
                "            // 加速档下会在选卡面板被销毁的瞬间把倍率拽回 1，长局压测因此只能跑真实时长。\n"
                "            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);\n"
                "            else Time.timeScale = Core.HitstopManager.BaseScale;\n",
                "            // 注入：M4.5 的兜底解冻也摘掉（它是 Q2 那三条判据绿着的真正原因）\n")],
        # 首跑预期里写了第 3/7 条，实测**还是绿的**——兜底不止一处：
        # 拆完紧接着 Build()，新世界那个 HitstopManager 的 Update 每帧 ApplyNow()
        # 会把 timeScale 写回 BaseScale。要看见"卡在 0"，得连它一起摘（Q7 组）。
        expect={"重开后结算画布消失", "拆世界后结算画布消失", "拆世界后时间解冻"},
        note="摘掉 LevelUpManager 兜底后第 3/7 条仍绿：新 HitstopManager 每帧还在写（见 Q7）"),
    # Q7 = Q6 + 摘掉 HitstopManager.Update 里那句每帧 ApplyNow()。
    # 到这一层，单机重开路径上"timeScale 卡在 0"才真的造得出来。
    dict(
        id="Q7", name="Q6 之外再把新 HitstopManager 的每帧写入也摘掉",
        edits=[(GAME_OVER,
                "            Core.Telemetry.Log(\"run_restart\");\n            CloseAndResume();\n",
                "            Core.Telemetry.Log(\"run_restart\");\n"
                "            // 注入：点重开不收面板、不恢复时间\n"),
               (GAME_BUILDER,
                "            if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.CloseAndResume();\n",
                "            // 注入：拆世界也不兜底（配合上一处，否则会被顶掉）\n"),
               ("UI/LevelUpManager.cs",
                "            // M4.5：断线拆世界时防止时间缩放卡在 0（选卡面板打开中被销毁）\n"
                "            // M41：兜底分支此前写死 1f，与 HitstopManager 里的\"正常速度\"是两份事实。\n"
                "            // 加速档下会在选卡面板被销毁的瞬间把倍率拽回 1，长局压测因此只能跑真实时长。\n"
                "            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);\n"
                "            else Time.timeScale = Core.HitstopManager.BaseScale;\n",
                "            // 注入：M4.5 的兜底解冻也摘掉\n"),
               ("Core/HitstopManager.cs",
                "                if (_hitstopTimer < 0f) _hitstopTimer = 0f;\n"
                "            }\n"
                "            ApplyNow();\n",
                "                if (_hitstopTimer < 0f) _hitstopTimer = 0f;\n"
                "            }\n"
                "            // 注入：每帧写入也摘掉（新世界建好后没人把时间写回来了）\n")],
        expect={"重开后时间恢复", "重开后世界继续推进", "重开后结算画布消失",
                "拆世界后结算画布消失", "拆世界后时间解冻"},
        note="挖到第三层：确认第 3/7 条有牙，前面两层的绿是两处兜底顶的"),
    dict(id="Q5", name="什么都不改（对照组）", edits=[], expect=set(),
         note="确认 PANEL 场景本身不产生假红"),
]

# exe 自己会打 `[SMOKE] PANEL失败项  <判据名>`，一行一条。
FAIL_RE = "PANEL失败项"
CONC_KEY = "PANEL结论"


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

    按**文件**判而不是按组：Q1 与 Q4 落在同一个 GameBuilder.cs，
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


def build():
    log = os.path.join(ROOT, "unity_build_m46_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                    "-logFile", log.replace("\\", "/")],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    # Unity 日志混着 GBK 字节（中文路径/驱动信息），只能容错读
    txt = io.open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run(seconds):
    args = [EXE, "-batchmode", "-nographics", "-longrunProbe", "-longrunScene", "PANEL",
            "-smokeSeconds", str(seconds), "-smokeSpeed", "6"]
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=seconds + 240)
        return (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return out + "\n[SMOKE] PANEL结论              不通过（超时）"


def parse(log):
    """取 PANEL 专属的失败项与结论。

    Line 的格式是 `[SMOKE] KEY              VALUE`（14 个空格），按 2+ 空格切。
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
        if k == FAIL_RE:
            fails.append(v)
        elif k == CONC_KEY:
            conc = v
    return set(fails), conc


def run_case(case, seconds):
    print("=" * 74)
    print("%s %s —— %s" % (case["id"], case["name"], case["note"]))
    print("=" * 74)

    inject(case)
    try:
        assert_clean(case)
        build()
        log = run(seconds)
        fails, conc = parse(log)
        ok = (fails == case["expect"]) and (conc in ("通过", "不通过"))
        print("  期望失败面 %s" % (sorted(case["expect"]) or "（全绿）"))
        print("  实际失败面 %s" % (sorted(fails) or "（全绿）"))
        print("  PANEL结论  %s" % (conc if conc else "（缺失：探针没跑到收尾，结果不可信）"))
        print("  → %s\n" % ("一致" if ok else "不一致"))
        return ok
    finally:
        restore_all()          # 每组必还原，否则下一组的"干净"是假的
        assert_clean(None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--seconds", type=int, default=120)
    args = ap.parse_args()

    ids = [s for s in args.only.split(",") if s]
    cases = [c for c in CASES if not ids or c["id"] in ids]
    if not cases:
        print("未知组: %s" % args.only)
        return 2

    if not os.path.exists(EXE):
        print("先跑一次 longrun_probe.py 产出 exe：%s" % EXE)
        return 2

    assert_clean(None)
    all_ok = True
    try:
        for c in cases:
            all_ok = run_case(c, args.seconds) and all_ok
    finally:
        restore_all()
        build()                # 恢复产物：别把注入版留给后续回归
        print("=" * 74)
        print("合计：%s" % ("各组失败面均与预期一致" if all_ok else "有组不一致"))
        print("=" * 74)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
