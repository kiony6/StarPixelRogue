#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M53 · 区分力注入（看得见的那一层：池化特效 / 程序化形状 / 选卡稀有度）

做什么：
    每组只改一处产品代码 → 重新构建无头产物 → 跑一遍 visual_probe.py 的对应场景
    → 看**哪几条判据变红**。期望列事先写死在这里，事后再挑就变成了解释而不是检验。

为什么这轮要注入：
    M53 一整轮都是"看得见"的改动，而"看得见"恰恰是最容易被假绿蒙过去的一类：
      · 特效放没放出来，和"每放一颗 new 一张纹理"，在画面上**完全一样**；
      · 三张卡都画出来了，和"三张同色"，在"卡片存在"这条判据上**完全一样**；
      · 特效最后都归零了，和"登记表漏减"，在"残留为 0"这条判据上**完全一样**。
    所以本轮六组里有一组是对照（Q1，必须全绿），另外五组各自只打断一处，
    且**失败面互不相交**——相交就说明那两条判据里有一条是摆设。

    Q3 与 Q4 的分工是本轮最要紧的一处：
      Q3 掐掉上限 → 只有「未越过上限」红（在场数照样能归零）
      Q4 掐掉回收计数 → 只有「收支守恒」红（在场数照样归零、也没越上限）
    两条都靠"最后归零"以外的量才抓得到，且抓的是**不同的两件事**。

用法：
    python m53_inject.py                # 全部六组
    python m53_inject.py --only Q2,Q5   # 只跑指定组
"""

import argparse
import io
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.join(ROOT, "StarPixelRogue")
SCRIPTS = os.path.join(PROJECT, "Assets", "_Project", "Scripts")
EXE = os.path.join(PROJECT, "Build", "Smoke", "StarPixelRogue.exe")
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PY = sys.executable
BAK = ".m53bak"

SPRITEFACTORY = "Core/SpriteFactory.cs"
FX = "Effects/Fx.cs"
LEVELUP = "UI/LevelUpManager.cs"

# 场景各自的判据全集（只收"是/否"两态的判据行，数值行另算）
JUDGES = {
    "FX": {"确实放出来了", "纹理零增量", "未越过上限", "全部归还", "收支守恒", "拆世界清零"},
    "SHAPE": {"六个都有图", "两次同一实例", "六个互不相同", "重复预热零增量"},
    "SKIN": {"分档正确", "三色互不相同", "标签已本地化", "卡片可点"},
}
ALL_JUDGES = set().union(*JUDGES.values())

# visual_probe.py 的判据行形如：
#   "      x 纹理增量             期望 0        实际 3"
# 第一个字符是 x（不达标）或空格（达标）
JUDGE_RE = re.compile(r"^\s{4,}(?P<mark>x| )\s+(?P<key>\S+)\s+期望\s+(?P<exp>\S+)\s+实际\s+(?P<got>.*)$")

CASES = [
    dict(
        id="Q1", scene="FX", name="什么都不改（对照组）", edits=[], expect=set(),
        note="确认 FX 场景本身不产生假红——否则后面五组的红都可能是这一组自带的",
    ),
    dict(
        id="Q2", scene="FX", name="形状精灵不再缓存（退回 M30 之前的形态）",
        edits=[(SPRITEFACTORY,
                "            if (_shape.TryGetValue(key, out var cached) && cached != null) return cached;\n",
                "            // 注入：M53 之前 / M30 之前就是这样——每次调用都造一张新纹理。\n"
                "            // 画面上完全看不出来，只有纹理计数看得出。\n")],
        expect={"纹理零增量"},
        note="24 轮特效会长出几百张纹理：不报错、不卡、画面照旧，只是显存一路涨。"
             "「确实放出来了」必须仍然绿——否则这条判据只是在数 0。",
    ),
    dict(
        id="Q3", scene="FX", name="取消同屏上限（到上限不再挤掉最早的）",
        edits=[(FX,
                "            if (_active.Count >= MaxActive)\n",
                "            if (false)   // 注入：上限被摘掉——打得越久同屏粒子越多\n")],
        expect={"未越过上限"},
        note="在场数一路涨过 240，但最后照样归零、收支照样守恒："
             "只有「未越过上限」这一条看得到它，别的判据全绿是**对的**。",
    ),
    dict(
        id="Q4", scene="FX", name="回收不再计数（登记表照样摘，账本不再加）",
        edits=[(FX,
                "            _active.Remove(p);\n            RecycledCount++;\n",
                "            _active.Remove(p);\n"
                "            // 注入：回收计数被摘掉。表是干净的、残留是 0、也没越上限，\n"
                "            // 只有「取用 − 回收 == 在场」这条恒等式会失衡。\n")],
        expect={"收支守恒"},
        note="与 Q3 的失败面必须不相交：Q3 只红「未越过上限」，Q4 只红「收支守恒」。"
             "相交就说明其中一条是在陪跑。",
    ),
    dict(
        id="Q5", scene="SKIN", name="三档稀有度一律用同一个颜色",
        edits=[(LEVELUP,
                "            if (kind >= 200) return Core.UIFactory.Palette.AccentAlt;\n"
                "            if (kind >= 100) return Core.UIFactory.Palette.Gold;\n"
                "            return Core.UIFactory.Palette.Accent;\n",
                "            // 注入：退回 M53 之前的形态——三张卡一个颜色。\n"
                "            // 卡片照样存在、照样可点、标签照样是本地化过的，\n"
                "            // 只是「颜色不再是信息」，玩家只能靠读字分辨这一级拿到了什么。\n"
                "            return Core.UIFactory.Palette.Accent;\n")],
        expect={"分档正确", "三色互不相同"},
        note="「标签已本地化」与「卡片可点」必须仍然绿："
             "它们看的是别的两件事，被这一处破坏牵连就说明判据写重了。",
    ),
    dict(
        id="Q6", scene="SKIN", name="卡片被一层全透明遮罩盖住",
        edits=[(LEVELUP,
                "            // M8 手感：遮罩淡入 + 标题弹入 + 卡片依次缩放弹入（用 unscaledDeltaTime，暂停态也能播）\n",
                "            // 注入：全透明全屏遮罩盖在选卡面板上（M48 那类缺陷的典型形态）。\n"
                "            // 卡片画得好好的、颜色也对、回调也接好了，就是点不到。\n"
                "            Core.UIFactory.CreatePanel(canvasGO.transform, \"M53Overlay\", Vector2.zero,\n"
                "                new Vector2(4000, 4000), new Color(0f, 0f, 0f, 0f));\n"
                "            // M8 手感：遮罩淡入 + 标题弹入 + 卡片依次缩放弹入（用 unscaledDeltaTime，暂停态也能播）\n")],
        expect={"卡片可点"},
        note="只有走真实射线点击才红：裸调 onClick.Invoke() 会照样点响、判据照样绿。"
             "Q5 与 Q6 一条看「画得对不对」、一条看「点得到点不到」，必须分得开。",
    ),
]


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def case_files(case):
    """这一组会动到的文件的绝对路径集合（多组可能落在同一文件上，故按文件判残留）。"""
    return set(os.path.normpath(os.path.join(SCRIPTS, r.replace("/", os.sep)))
               for r, _old, _new in case["edits"])


def read(path):
    return io.open(path, encoding="utf-8", errors="replace").read()


def inject(case):
    """把改动写进源码（先备份原文件）。"""
    for relpath, old, new in case["edits"]:
        path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
        bak = path + BAK
        if not os.path.exists(bak):
            shutil.copy2(path, bak)
        src = io.open(path, encoding="utf-8").read()
        if old not in src:
            raise RuntimeError("锚点未命中: %s\n---\n%s" % (relpath, old))
        src = src.replace(old, new, 1)
        io.open(path, "w", encoding="utf-8", newline="").write(src)


def restore_all():
    """把所有 .m53bak 还原回去（顺带删掉 Unity 为备份文件生成的 .meta）。"""
    for dirpath, _dirs, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(BAK):
                continue
            src = os.path.join(dirpath, n)
            shutil.move(src, src[:-len(BAK)])
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def assert_clean(case=None):
    """注入前后都不许有**上一组**的备份 / 注入标记残留。

    本组自己刚生成的备份是允许的——否则"注入完立刻自检"永远报错。
    """
    allowed = case_files(case) if case is not None else set()
    for dirpath, _dirs, names in os.walk(SCRIPTS):
        for n in names:
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
            if "// 注入：" in read(p) and p not in allowed:
                raise RuntimeError("残留注入: %s" % rel(p))
    if case:
        # 注入**之后**要查的是"改动真的落上了"，不是"锚点还在"——
        # 锚点被替换掉本来就是注入的目的（此处一度写成查 old 还在，五组全被自己拦下）。
        for relpath, _old, new in case["edits"]:
            path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
            if new not in read(path):
                raise RuntimeError("注入没落上（替换写歪了）: %s" % relpath)


def build():
    log = os.path.join(ROOT, "unity_build_m53_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                    "-logFile", log.replace("\\", "/")],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    txt = io.open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run(scene):
    """跑一遍 visual_probe 的单个场景，返回 (退出码, 输出)。"""
    try:
        p = subprocess.run([PY, os.path.join(ROOT, "visual_probe.py"), "--only", scene],
                           cwd=ROOT, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=420)
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return 99, out + "\n（超时）"


def red_judges(scene, out):
    """从探针输出里收集**变红**的判据名（只看本场景的判据全集）。"""
    red = set()
    for line in out.splitlines():
        m = JUDGE_RE.search(line)
        if not m:
            continue
        k = m.group("key").strip()
        if k not in JUDGES[scene]:
            continue
        if m.group("mark") == "x":
            red.add(k)
    return red


def run_case(case):
    print("=" * 74)
    print("%s %s —— %s" % (case["id"], case["name"], case["note"]))
    print("=" * 74)

    inject(case)
    try:
        assert_clean(case)
        build()
        code, out = run(case["scene"])
        red = red_judges(case["scene"], out)
        verdict = [l.strip() for l in out.splitlines() if l.strip().startswith("→")]
        print("  场景 %s / 探针退出码 %d / %s"
              % (case["scene"], code, verdict[-1] if verdict else "（无结论行）"))
        print("  期望失败面 %s" % (sorted(case["expect"]) or "（全绿）"))
        print("  实际失败面 %s" % (sorted(red) or "（全绿）"))
        ok = (red == case["expect"])
        if red - case["expect"]:
            print("  多出来的红（注入解释不了，得回去查）: %s" % sorted(red - case["expect"]))
        if case["expect"] - red:
            print("  少了的红（判据没在看着它该看的东西）: %s" % sorted(case["expect"] - red))
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

    try:
        assert_clean(None)
    except RuntimeError as e:
        print("前置检查失败: %s" % e)
        return 2

    print("M53 看得见的那一层 · 区分力注入 %d 组" % len(cases))
    all_ok = True
    try:
        for c in cases:
            try:
                all_ok = run_case(c) and all_ok
            except RuntimeError as e:
                print("  [中断] %s\n" % e)
                all_ok = False
    finally:
        # 收尾再还原一次 + 重建：交付物必须是**没有注入**的那份
        restore_all()
        assert_clean(None)
        build()
        print("已还原源码并重建产物（交付物无注入残留）")

    print("=" * 74)
    print("总结果：%s" % ("六组全部一致" if all_ok else "有不一致，见上"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
