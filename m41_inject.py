#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M41 长局判据的区分力注入验证

做什么：
    把"修复"逐个改回缺陷态，确认长局判据真的会红——而且**只红该红的那些**。
    四组失败面互不相交：

      R1 摘掉宝石上限回收      → 宝石有界 / 后半程不加速堆积 红，回血球仍绿
      R2 摘掉回血球上限回收    → 只有 回血球有界 红
      R3 让加速倍率不生效      → 只有 波次确实推进 红（世界几乎不动，增长类判据假绿）
      R4 摘掉 TeardownWorld 的池清理 → 只有 重开后根对象回落 红

为什么必须做：
    判据"能过"不等于"能抓"。M38 第 16 项曾出现"8 个夹具全过、注入却一片绿"，
    原因是最初把裸 `ack` 当成了确认语义——检查写得再漂亮，抓不到东西就是装饰。
    长局判据尤其危险：它跑 5 分钟才出一次结果，肉眼根本看不出"是因为修复才绿的，
    还是本来就不会红"。

为什么 R1/R2 要分开：
    宝石与回血球共用"只进不出"的写法。只修一半是最容易发生的漏修，
    若两组判据合并成一条，这种漏修会静默通过。

用法：python m41_inject.py [R1|R2|R3|R4 ...]   （不带参数跑全部）
"""

import io
import os
import re
import shutil
import subprocess
import sys

# 只认长局探针自己的断点名。SmokeTest 也会打 `[SMOKE] 对端掉线  否` 这类行，
# 若不加白名单，它们会被当成"判据失败"混进失败面（R3 第一版就混进了 3 条）。
JUDGES = {
    "波次确实推进", "宝石有界", "回血球有界", "后半程不加速堆积",
    "托管堆收敛", "长程数值不越界", "全程零异常",
    "重开后根对象回落", "重开后波次重置", "重开后玩家可控",
}

ROOT = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")
UNITY = "E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PROJECT = os.path.join(ROOT, "StarPixelRogue")
BAK = ".m41bak"

CASES = [
    dict(id="R1", name="摘掉宝石上限回收", f="Pickups/PickupManager.cs",
         old="            TrimGems();",
         new="            // TrimGems();   // 注入：宝石账本重新只进不出",
         scene="GROWTH", seconds=240,
         expect={"宝石有界", "后半程不加速堆积"},
         note="宝石堆积回来；回血球那条仍在管，必须保持绿"),
    dict(id="R2", name="摘掉回血球上限回收", f="Pickups/PickupManager.cs",
         old="            TrimHealth();",
         new="            // TrimHealth();   // 注入：回血球账本重新只进不出",
         scene="GROWTH", seconds=300,
         expect={"回血球有界"},
         note="只摘一半——宝石必须仍然有界，证明两条判据各自独立有牙"),
    dict(id="R3", name="让加速倍率不生效", f="Core/HitstopManager.cs",
         old="            s_baseScale = Mathf.Max(0.01f, scale);",
         new="            s_baseScale = 1f;   // 注入：倍率被丢掉",
         scene="GROWTH", seconds=90,
         expect={"波次确实推进"},
         note="世界几乎不动：增长类判据会假绿，只有自检那条能抓"),
    # R4 打过一次弯路：最初想用"摘掉掉落物清理"让重开后的根对象红，
    # 但 TeardownWorld 的清理是**三重兜底**（PoolManager.DestroyAll 销毁全部池实例、
    # PickupManager.ResetAll 销毁账本活动项、最后 _created 里的管理器容器被 Destroy
    # 时把挂在它下面的实例一起带走），摘一两处另一个会顶上，判据"永远绿"。
    #
    # 换成打单例引用：这一处不清，重开时 Build 会被「Instance != null」守卫拦下，
    # 新世界根本建不起来——玩家拿不到，这才是"重开失败"最直接的症状。
    dict(id="R4", name="重开时不清单例引用", f="GameBuilder.cs",
         edits=[("                    if (mb is Core.ISingletonInstance s) s.ClearInstance();",
                 "                    // if (mb is Core.ISingletonInstance s) s.ClearInstance();"
                 "   // 注入：单例引用不清")],
         scene="RESTART", seconds=120,
         # 两条红是**同一个根因的两个症状**：世界没建起来 → 玩家拿不到、波次也停在 -1。
         # 它们不是「判据互相串扰」（与 R1/R2/R3 的失败面完全不重叠），而是同一条因果链的两端。
         expect={"重开后玩家可控", "重开后波次重置"},
         note="新世界建不起来：玩家拿不到、波次也停在 -1（同一根因）"),
]


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    return io.open(p, encoding="utf-8").read()


def write(p, t):
    io.open(p, "w", encoding="utf-8").write(t)


def assert_clean(case=None):
    """注入前后的干净性。

    case=None  —— 全库必须干净（无注入标记、无备份残留）；
    case=某组  —— 允许**且只允许**该组目标文件带备份与注入标记。
    （M37 的教训：上一组的残留会让下一组的"干净"变成假的，所以每组前后都要查。）
    """
    allowed_bak = None
    if case is not None:
        allowed_bak = os.path.normpath(os.path.join(SCRIPTS, case["f"] + BAK))

    for dirpath, _dirs, files in os.walk(SCRIPTS):
        for n in files:
            if not n.endswith(BAK):
                continue
            p = os.path.normpath(os.path.join(dirpath, n))
            if allowed_bak is None or p != allowed_bak:
                raise RuntimeError("残留备份: %s" % rel(p))

    # 按**文件**判而不是按组：R1 与 R2 落在同一个 PickupManager.cs 的两处，
    # 按组比对会把同文件的本组合规注入误判成上一组的残留
    target = None if case is None else os.path.normpath(os.path.join(SCRIPTS, case["f"]))
    seen = set()
    for c in CASES:
        p = os.path.normpath(os.path.join(SCRIPTS, c["f"]))
        if p in seen:
            continue
        seen.add(p)
        if "// 注入：" not in read(p):
            continue
        if target is None or p != target:
            raise RuntimeError("残留注入: %s" % rel(p))


def inject(case):
    """替换前先备份；每处替换都必须恰好命中 1 次。"""
    edits = case.get("edits") or [(case["old"], case["new"])]
    p = os.path.join(SCRIPTS, case["f"])
    backup(p)
    t = read(p)
    for old, new in edits:
        n = t.count(old)
        if n != 1:
            raise RuntimeError("%s: 期望命中 1 处，实际 %d 处 —— 源码已变，注入失效" % (case["id"], n))
        t = t.replace(old, new)
    write(p, t)


def backup(p):
    b = p + BAK
    if not os.path.exists(b):
        shutil.copy2(p, b)


def restore_all():
    for dirpath, _dirs, files in os.walk(SCRIPTS):
        for n in files:
            if not n.endswith(BAK):
                continue
            src = os.path.join(dirpath, n)
            dst = src[:-len(BAK)]
            shutil.move(src, dst)


def build():
    log = os.path.join(ROOT, "unity_build_m41_inject.log")
    r = subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                        "-projectPath", PROJECT.replace("\\", "/"),
                        "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                        "-logFile", log.replace("\\", "/")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    # Unity 的日志混着 GBK 字节（中文路径/驱动信息），不能按纯 UTF-8 读
    txt = (io.open(log, encoding="utf-8", errors="replace").read()
           if os.path.exists(log) else "")
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run(scene, seconds):
    args = [EXE, "-batchmode", "-nographics", "-longrunProbe", "-longrunScene", scene,
            "-smokeSeconds", str(seconds), "-smokeSpeed", "6"]
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=seconds + 180)
        return (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return out + "\n[SMOKE] 结论              不通过（超时）"


def fails_of(log):
    """从**探针原始日志**里取判为"否"的断点名。

    注意不能去解析 longrun_probe.py 渲染出来的 `x 名字 期望 …` 表格——
    这里跑的是 exe 本体，它只输出 `[SMOKE] 名字   是/否`。
    第一版就是照抄了 py 的表格格式，于是四组注入全部"看起来全绿"。
    """
    out = set()
    for line in log.splitlines():
        m = re.match(r"\[SMOKE\]\s*(.+?)\s{2,}(是|否)\s*$", line)
        if m and m.group(2) == "否" and m.group(1).strip() in JUDGES:
            out.add(m.group(1).strip())
    return out


def run_case(case, keep_build=False):
    print("=" * 70)
    print("%s %s —— %s" % (case["id"], case["name"], case["note"]))
    print("=" * 70)

    inject(case)
    try:
        assert_clean(case)     # 只许这一处被改
        build()
        log = run(case["scene"], case["seconds"])
        fails = fails_of(log)
        ok = (fails == case["expect"])
        print("  场景 %s %ds" % (case["scene"], case["seconds"]))
        print("  期望失败面 %s" % (sorted(case["expect"])))
        print("  实际失败面 %s" % (sorted(fails) if fails else "（全绿）"))
        if not fails:
            for line in log.splitlines():
                if "断点通过" in line or "结论" in line:
                    print("    " + line.strip())
        print("  → %s\n" % ("通过" if ok else "不一致"))
        return ok
    finally:
        restore_all()          # 每组必还原，否则下一组的"干净"就是假的
        assert_clean(None)


def main():
    ids = sys.argv[1:]
    cases = [c for c in CASES if not ids or c["id"] in ids]
    if not cases:
        print("未知组: %s" % " ".join(ids))
        return 2

    assert_clean(None)
    all_ok = True
    try:
        for c in cases:
            all_ok = run_case(c) and all_ok
    finally:
        restore_all()
        build()                # 恢复产物：别把注入版留给后续回归
        print("=" * 70)
        print("合计：%s" % ("四组失败面均与预期一致" if all_ok else "有组不一致"))
        print("=" * 70)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
