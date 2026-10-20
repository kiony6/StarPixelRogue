#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M47「强制退出真实路径」判据的区分力注入验证

做什么：
    把 M46 / M47 两轮的修复逐个改回缺陷态，确认 EXIT 场景那 17 条判据真的会红——
    而且**只红该红的那些**。五组缺陷 + 一组对照：

      Q1 拆世界不收结算面板（M46 本体）
                              → {强制退出后结算面板被收}
      Q2 强制退出回到 `if (MainMenu.Instance != null)` 的老写法
        + 探针不再 Ensure 主菜单（模拟"没建过菜单的启动路径"）
                              → {强制退出后主菜单可见且有提示}
                                （必须**两处一起**：探针自己 Ensure 的话，
                                  菜单就在那儿，老写法照样弹提示——全绿。
                                  这正是 M47 那处修复最难被看见的原因：
                                  M19 的 PlaytimeProbe 恰好替它把菜单建好了）
      Q3 建世界不收跨局残留的提示遮罩（M47 本体）
                              → {重开后旧提示遮罩已收}
      Q4 强制退出不拆世界       → {强制退出后结算面板被收, 强制退出后 HUD 清零,
                                  强制退出后时间解冻, 强制退出后世界已拆, 二次开局被拦下}
                                （首跑漏算了「时间解冻」：摘掉 TeardownWorld
                                  等于把**唯一的解冻者**也摘了——面板冻着世界，
                                  而 TeardownWorld 里的 CloseAndResume 是这条路径上
                                  唯一会 SetPaused(false) 的地方。
                                  它一红，反而证伪了 M46 §5.3 那句
                                  「时间解冻没有独立区分力」：那条结论只在
                                  "只摘收面板、保留拆世界"的注入下成立）
      Q6 到点前不弹预警         → {到点前弹出一次预警}
                                （只有它红：证明「遮罩已收」不是靠"根本没弹过"混绿的）
      Q5 什么都不改（对照）      → 期望**全绿**：证明 EXIT 场景本身不产生假红

为什么这一轮必须做：
    EXIT 的 17 条里有一大半是"看一眼日志就觉得它对"的类型——面板出现、主菜单可见、
    提示非空、二次开局被拦……它们全绿的样子和"判据根本没在判"长得一模一样。
    M38 第 16 项出现过"8 个夹具全过、注入却一片绿"，M41 出现过第一版解析格式
    照抄 py 表格、四组全部假绿。判据不咬人的话它只是装饰。

    尤其 Q6：如果"到点前弹出一次预警"是假的（压根没弹过），
    「重开后旧提示遮罩已收」就会永远绿——因为从来没有遮罩。所以必须单独注入一次，
    确认"不弹预警"能被抓到，而不是被"遮罩已收"的假绿盖住。

怎么读失败面：
    直接读 exe 自己打的 `[SMOKE] EXIT失败项  <判据名>` 行。**不去解析判据表格**
    （`否（画布数 2）` 这类值不以"否"结尾，按表格解析会漏），也不去读
    SmokeTest 收尾那份同名 `失败项`（那是它按空世界报的，与本场景无关）。

用法：
    python m47_inject.py                # 六组全跑
    python m47_inject.py --only Q1      # 只跑一组
    python m47_inject.py --seconds 150  # 缩短每组观察时长

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
BAK = ".m47bak"

GAME_BUILDER = "GameBuilder.cs"
PLAYTIME = "Core/PlaytimeGuard.cs"
LONG_RUN = "Dev/LongRunProbe.cs"

# edits 的每一项是 (相对 Scripts 的路径, 原文, 替换文)：
# Q2 需要同时改产品代码与探针（缺一个就看不见那个静默失败），所以按"文件"组织。
CASES = [
    dict(
        id="Q1", name="拆世界不收结算面板（M46 本体修复）",
        edits=[(GAME_BUILDER,
                "            if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.CloseAndResume();\n",
                "            // 注入：拆世界不收结算面板（M46 修复前的状态）\n")],
        expect={"强制退出后结算面板被收"},
        note="M46 的修复理由第一次在真实路径上被验证：没人点按钮，面板就赖在主菜单上"),
    dict(
        id="Q2", name="强制退出回到「菜单存在才提示」+ 探针不再 Ensure 菜单",
        edits=[(PLAYTIME,
                "            UI.MainMenu.Ensure();\n"
                "            UI.MainMenu.Instance.ShowWithNotice(Localization.T(\"playtime.force_exit\"));\n",
                "            // 注入：回到 M47 之前的老写法（菜单不存在就什么都不说）\n"
                "            if (UI.MainMenu.Instance != null)\n"
                "                UI.MainMenu.Instance.ShowWithNotice(Localization.T(\"playtime.force_exit\"));\n"),
               (LONG_RUN,
                "            UI.MainMenu.Ensure();\n",
                "            // 注入：不建主菜单（模拟绕开合规门流程的开局入口）\n")],
        expect={"强制退出后主菜单可见且有提示"},
        note="静默踢人：两处都得改才看得见——探针自己 Ensure 的话老写法照样弹提示"),
    dict(
        id="Q3", name="建世界不收跨局残留的提示遮罩（M47 本体修复）",
        edits=[(GAME_BUILDER,
                "            Core.PlaytimeGuard.Instance?.DismissHint();\n",
                "            // 注入：建世界不收跨局残留的提示遮罩（M47 修复前的状态）\n")],
        expect={"重开后旧提示遮罩已收"},
        note="预警挂起时死亡 → 点重开，那层遮罩会一路盖进新一局"),
    dict(
        id="Q4", name="强制退出不拆世界",
        edits=[(PLAYTIME,
                "            GameBuilder.TeardownWorld();\n",
                "            // 注入：强制退出只提示、不拆世界\n")],
        # 首跑预期里漏了「强制退出后时间解冻」，实测它跟着红了——
        # 摘掉 TeardownWorld 的同时把这条路径上唯一会 SetPaused(false) 的地方也摘了
        # （LevelUpManager 那份兜底只在它被销毁时触发，这里谁都没被销毁）。
        expect={"强制退出后结算面板被收", "强制退出后 HUD 清零", "强制退出后时间解冻",
                "强制退出后世界已拆", "二次开局被拦下"},
        note="只弹提示不拆世界：面板还在、世界还在跑、时间还冻着，玩家被拦在外面却回不去"),
    dict(
        id="Q6", name="到点前不弹预警",
        edits=[(PLAYTIME,
                "                    _today.warned = true;\n"
                "                    ShowHint(Localization.T(\"settings.minor_mode\"), Localization.T(\"playtime.warn\"), true);\n",
                "                    _today.warned = true;\n"
                "                    // 注入：不弹预警（于是从来就没有遮罩这回事）\n")],
        expect={"到点前弹出一次预警"},
        note="只有这一条红：证明「遮罩已收」不是靠「根本没弹过」混绿的"),
    dict(id="Q5", name="什么都不改（对照组）", edits=[], expect=set(),
         note="确认 EXIT 场景本身不产生假红"),
]

# exe 自己会打 `[SMOKE] EXIT失败项  <判据名>`，一行一条。
FAIL_RE = "EXIT失败项"
CONC_KEY = "EXIT结论"


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

    按**文件**判而不是按组：Q1 与 Q3 落在同一个 GameBuilder.cs，
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
            # Unity 会给"备份文件"也生成一个 .meta，还原后它成了孤儿。
            # 留着会让下一次 git status 多出一个未跟踪文件（M47 实测撞到）。
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def build():
    log = os.path.join(ROOT, "unity_build_m47_inject.log")
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
    args = [EXE, "-batchmode", "-nographics", "-longrunProbe", "-longrunScene", "EXIT",
            "-smokeSeconds", str(seconds), "-smokeSpeed", "6"]
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=seconds + 240)
        return (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return out + "\n[SMOKE] EXIT结论              不通过（超时）"


def parse(log):
    """取 EXIT 专属的失败项与结论。

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
        print("  EXIT结论   %s" % (conc if conc else "（缺失：探针没跑到收尾，结果不可信）"))
        print("  → %s\n" % ("一致" if ok else "不一致"))
        return ok
    finally:
        restore_all()          # 每组必还原，否则下一组的"干净"是假的
        assert_clean(None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--seconds", type=int, default=180)
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

    print("M47 强制退出真实路径 · 区分力注入 %d 组（每组 %ds）" % (len(cases), args.seconds))
    all_ok = True
    try:
        for c in cases:
            try:
                all_ok = run_case(c, args.seconds) and all_ok
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
