#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M36 区分力验证：往工程里注入 4 组互不相同的缺陷，看联机交叉核对的判据是不是真的会红。

起因是用户一句追问：「联机双开 60s，房主波次 7 / 客户端波次 6，为什么波次不一样？」
查下来那是**采样时刻差**造成的假象（客户端被刻意安排早 3s 收尾），不是同步缺陷。
但它暴露了更严重的事：正因为采样天然错开，判据被迫放水成
「波次容差 25% / 击杀容差 40% / 玩家数只看峰值」——真正的同步缺陷会被判成通过。

所以这里反向验证两件事：
  1) 对齐收尾后，收紧的判据仍然不会误报（干净源码必须全绿，见基线）；
  2) 四组注入各自让**不同的判据**变红，且其中三组在**旧判据下是绿的**——
     证明收紧确实抓到了旧判据放过的东西。

  R1 收尾等级改回 PlayerController.level
     → 只「等级≠权威等级」红。旧判据根本没有这条，绿。
       （这就是 M36 修掉的真缺陷：客户端真实等级 8 被显示成 1）

  R2 客户端读到的击杀被钳到权威值的 70%
     → 只「击杀」红。旧判据容差 40%，70% 落在放行区，绿。

  R3 客户端读到的波次比权威值落后 2 波
     → 只「波次」红。旧判据容差 max(2, 25%)，落后 2 恰好卡在边界内，绿。

  R4 采样不对齐（--no-align，源码干净）
     → 双端数值本身就对不上，无需任何缺陷。这是「假象」本身的对照组：
       同样的代码，只要采样错开 3 秒，波次/击杀就必然不一致。

四组签名（触发的判据集合, 旧判据是否红）互不相同：
  R1 = ({等级}, 否)
  R2 = ({击杀}, 否)
  R3 = ({波次}, 否)
  R4 = ({采样不对齐}, —)

用法：  python m36_inject.py            # 四组全跑
        python m36_inject.py R2         # 只跑指定组
跑完自动还原源码（.m36inject.bak）并**重新构建**，避免产物残留上一组的注入。
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
SCRIPTS = os.path.join(PROJ, "Assets/_Project/Scripts")

SMOKE_TEST = os.path.join(SCRIPTS, "Dev/SmokeTest.cs")
NET_WORLD = os.path.join(SCRIPTS, "Net/NetWorldState.cs")

BACKUP = ".m36inject.bak"
SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")


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
        # Unity 会给新出现的源文件补 .meta；还原后残留的 .meta 会在下次导入时告警
        m = b + ".meta"
        if os.path.exists(m):
            os.remove(m)


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
         "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessNetBuild",
         "-logFile", os.path.join(ROOT, "unity_build_m36_inject.log")],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0


def parse_smoke(path):
    d = {}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = SMOKE_RE.search(line.rstrip("\n"))
                if m:
                    d[m.group(1).strip()] = m.group(2).strip()
    except OSError:
        pass
    return d


def as_int(v, default=-1):
    try:
        return int(str(v).strip())
    except (TypeError, ValueError):
        return default


def run_net(align=True):
    """跑一次联机双开，返回（是否通过, 失败项列表, 房主数据, 客户端数据）。"""
    cmd = [PY, os.path.join(ROOT, "net_smoke.py"),
           "--seconds", "60", "--start-delay", "10",
           "--exe", os.path.join(PROJ, "Build", "SmokeNet", "StarPixelRogue.exe")]
    if not align:
        cmd.append("--no-align")
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT)
    out = (r.stdout or "") + (r.stderr or "")
    problems = [l.strip()[2:] for l in out.splitlines() if l.strip().startswith("- ")]
    host = parse_smoke(os.path.join(ROOT, "net_host.log"))
    client = parse_smoke(os.path.join(ROOT, "net_client.log"))
    return r.returncode == 0, problems, host, client


def legacy_verdict(h_wave, c_wave, h_kills, c_kills):
    """套用 M36 之前的旧判据公式，看它会不会红。"""
    wave_tol = max(2, int(h_wave * 0.25))
    w_red = abs(h_wave - c_wave) > wave_tol
    k_red = (c_kills > h_kills) or (c_kills < h_kills * 0.6)
    return w_red, k_red


def classify(problems, host, client):
    """把失败项归到判据类别上。"""
    hit = set()
    for p in problems:
        if "等级" in p and "权威" in p:
            hit.add("等级")
        elif "波次" in p:
            hit.add("波次")
        elif "击杀" in p:
            hit.add("击杀")
        elif "玩家" in p:
            hit.add("玩家数")
        elif p.strip():
            hit.add("其他")
    return hit


# ── 四组注入 ────────────────────────────────────────────────

def r1():
    """收尾输出「等级」改回 PlayerController.level——M36 修掉的真缺陷。

    联机下客户端的 PlayerController 不参与 XP 结算、恒为初始值 1，
    于是真实等级（实测 8）被显示成 1。只有「等级 vs 权威等级」这条抓得住。
    """
    edit(SMOKE_TEST,
         "            int level = AuthoritativeLevel();",
         "            int level = PlayerController.Instance != null "
         "? PlayerController.Instance.level : 0;   // R1 注入：读错来源")
    return [SMOKE_TEST]


def r2():
    """客户端读到的击杀被钳到权威值的 70%——模拟同步中途落后。

    70% 恰好落在旧判据「不得低于 60%」的放行区里，是旧判据放过的典型形态。
    """
    edit(NET_WORLD,
         "        public int Kills => _kills.Value;",
         "        // R2 注入：客户端只读到 70%，模拟同步中途落后\n"
         "        public int Kills => IsServerInitialized ? _kills.Value : _kills.Value * 7 / 10;")
    return [NET_WORLD]


def r3():
    """客户端读到的波次比权威值落后 2 波。

    旧判据容差 max(2, 25%)，落后 2 恰好卡在边界内被放行。
    """
    edit(NET_WORLD,
         "        public int Wave => _wave.Value;",
         "        // R3 注入：客户端波次落后 2 波\n"
         "        public int Wave => IsServerInitialized ? _wave.Value "
         ": Mathf.Max(1, _wave.Value - 2);")
    return [NET_WORLD]


def r4():
    """不注入源码，只把采样退回「客户端早 3s 收尾」——假象对照组。"""
    return []


CASES = {
    "R1": (r1, [SMOKE_TEST], True),
    "R2": (r2, [NET_WORLD], True),
    "R3": (r3, [NET_WORLD], True),
    "R4": (r4, [], False),
}


def run_case(name):
    fn, paths, align = CASES[name]
    print("=" * 74)
    print("注入 %s" % name)
    print("=" * 74)

    for p in paths:
        backup(p)
    fn()

    try:
        if not build():
            print("  构建失败——注入代码有语法错误")
            return None
        ok, problems, host, client = run_net(align=align)

        h_wave = as_int(host.get("同步波次"))
        c_wave = as_int(client.get("同步波次"))
        h_kills = as_int(host.get("同步击杀"))
        c_kills = as_int(client.get("同步击杀"))

        print("  双端数值：波次 %d / %d    击杀 %d / %d" % (h_wave, c_wave, h_kills, c_kills))
        if not align:
            print("  等级：%s / %s" % (host.get("等级"), client.get("等级")))
            print("  采样：不对齐（客户端早 3s 收尾）——源码是干净的")
        print("  新判据：%s" % ("通过" if ok else "未通过"))
        for p in problems:
            print("      - %s" % p)

        hit = classify(problems, host, client)
        if not align:
            # 对照组：不看通过与否，看双端数值本身对不对得上
            diff = (h_wave != c_wave) or (h_kills > 0 and abs(h_kills - c_kills) > max(2, int(h_kills * 0.02)))
            hit = {"采样不对齐"} if diff else set()
            print("  双端数值是否对得上：%s" % ("否（这就是假象本身）" if diff else "是"))
            return (name, sorted(hit), None)

        lw, lk = legacy_verdict(h_wave, c_wave, h_kills, c_kills)
        legacy_red = lw or lk
        print("  旧判据：%s（波次%s / 击杀%s）"
              % ("未通过" if legacy_red else "通过",
                 "红" if lw else "绿", "红" if lk else "绿"))
        return (name, sorted(hit), legacy_red)
    finally:
        for p in paths:
            restore(p)


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    names = [only] if only else ["R1", "R2", "R3", "R4"]

    results = []
    for n in names:
        r = run_case(n)
        if r:
            results.append(r)
        print()

    if only:
        return 0

    print("=" * 74)
    print("互不相交检查")
    print("=" * 74)
    for name, hit, legacy in results:
        print("  %s  触发判据=%-14s 旧判据=%s"
              % (name, hit or "（无）",
                 "红" if legacy else ("绿" if legacy is False else "—")))
    sigs = [(tuple(h), l) for _n, h, l in results]
    ok = len(set(sigs)) == len(sigs)
    all_red = all(h for _n, h, _l in results)
    print("\n结论：%s"
          % ("四组失败面互不相同，且每组都真的让判据红了"
             if ok and all_red
             else "有两组失败面重合，或某组没让判据红——判据是摆设"))

    # 收尾必须重新构建：否则产物还带着最后一组的注入，后续探针会莫名其妙变红
    print("\n还原源码并重新构建…")
    build()
    print("完成。")
    return 0 if (ok and all_red) else 1


if __name__ == "__main__":
    sys.exit(main())
