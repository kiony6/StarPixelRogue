#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M24 掉落/磁吸/结算 与 重开局清理 无头实测

做什么：
    拉起 4 个无头进程，把此前**从没被真正驱动过**的掉落闭环与重开局清理走一遍：
      MAGNET    半径之外的宝石必须一动不动（距离不变、经验不入账）；
                改成大半径后必须被吸走，且 3 点经验精确入账（初始升级线 5 → ratio 0.6）
      CREDIT    经验倍率 xpGainMult=2 必须逐颗生效（每颗 1 点实际给 2 点）；
                回血必须精确 +30，且远超上限时封顶在 maxHp（不得溢出）
      LEFTOVER  走真实重开路径 GameOverPanel.OnRestart（TeardownWorld → Build），
                上一局留下的宝石/回血球/敌人必须清零
      REPEAT    连续 3 次重开，残留总数不得随轮次累积

为什么必须测：
    掉落闭环此前只在"跑一局看等级涨了没"里间接发生过——从没断言过"半径之外吸不动"。
    于是"忽略 pickupRadius、全局吸附"这种实现也能全绿（等级照样涨）。
    更关键的是重开局：GameBuilder.TeardownWorld 只销毁 Track 进去的管理器对象，
    而宝石/回血球/敌人都是 PoolManager 从模板克隆出来的实例，挂在场景根节点、
    不在 _created 里 —— 上一局的实体会整批残留在新世界里（看得见捡不着的宝石、
    围着旧玩家坐标转的敌人）。PickupManager.ResetAll / EnemyManager.ResetAll
    两个清理方法写好了却全仓库无 caller，就是没接上。

判据的区分力（本脚本最要紧的地方）：
    1) 计数一律要求探针走 Resources.FindObjectsOfTypeAll 扫场景、
       **排除模板本体**之后再报数，并复读"残留构成"——不采信被管理器的内部账本。
    2) 半径用**两态对比**：小半径必须不动、大半径必须被吸走。
       只测单边的话，"永远吸附"和"永远不吸附"各能骗过一半。
    3) 经验用**精确的 XpRatio**（初始 _xpToNext=5）而不是"等级涨了"，
       这样"入账数量不对"能被立刻指出来，而不只是"好像没升级"。
    4) LEFTOVER 先断言"成功构造出 6 个残留"，再审"重开后为 0"。
       少了前一步，"残留永远是 0"也可能只是压根没造出来（假通过）。
    5) REPEAT 逐轮打印 放→残留，泄漏表现为单调增长，一眼可见，不靠单个布尔。

用户数据保护：跑前备份 save.json / settings.json，跑后原样恢复；探针产物全部清除。

退出码：0 = 全部通过；1 = 有场景未通过；2 = 前置条件不满足
"""

import argparse
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")

SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")

# 无头环境必然出现的渲染噪声，不算运行时异常
NOISE = ("shader", "gpu", "memorysetup", "unitymemory", "curl error", "unitlets",
         "screen position out of view frustum")

SCENARIOS = (
    ("MAGNET", "半径外必须吸不动 / 改成大半径必须被吸走且 3 点经验精确入账"),
    ("CREDIT", "经验倍率逐颗生效 / 回血 +30 / 超额回血封顶在上限"),
    ("LEFTOVER", "真实重开局（TeardownWorld→Build）后上一局实体必须清零"),
    ("RESTART", "真实重开局后必须拿得到新世界的玩家，且新世界掉落闭环仍然通"),
    ("REPEAT", "连续 3 次重开，残留不得累积"),
)

BACKUP_SUFFIX = ".m24bak"

# CREDIT 有 4 段等待（每段最多 4s），REPEAT 要跑 3 轮完整 Build+Teardown
TIMEOUT = {"MAGNET": 90, "CREDIT": 90, "LEFTOVER": 90, "RESTART": 90, "REPEAT": 150}


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def find_data_dir(explicit=None):
    """定位 persistentDataPath（company/product 目录）。

    依据『目录里有 save.json 或 settings.json』从 LocalLow 逐层找，
    避免把公司名/产品名硬编码进脚本（改名即失效）。
    """
    if explicit:
        return explicit if os.path.isdir(explicit) else None
    low = os.path.join(os.environ.get("USERPROFILE", os.path.expanduser("~")),
                       "AppData", "LocalLow")
    if not os.path.isdir(low):
        return None
    best = None
    for company in sorted(os.listdir(low)):
        cpath = os.path.join(low, company)
        if not os.path.isdir(cpath):
            continue
        for product in sorted(os.listdir(cpath)):
            ppath = os.path.join(cpath, product)
            if not os.path.isdir(ppath):
                continue
            if (os.path.exists(os.path.join(ppath, "save.json"))
                    or os.path.exists(os.path.join(ppath, "settings.json"))):
                return ppath
            if best is None:
                best = ppath
    return best


def backup(data_dir, names):
    made = []
    for n in names:
        p = os.path.join(data_dir, n)
        if os.path.exists(p):
            b = p + BACKUP_SUFFIX
            if os.path.exists(b):
                os.remove(b)
            os.rename(p, b)
            made.append(n)
    return made


def restore(data_dir, names):
    for n in names:
        p = os.path.join(data_dir, n)
        b = p + BACKUP_SUFFIX
        if os.path.exists(p):
            os.remove(p)          # 探针写过的测试档，直接丢弃
        if os.path.exists(b):
            os.rename(b, p)


def clear_artifacts(data_dir):
    for n in ("save.json", "settings.json"):
        p = os.path.join(data_dir, n)
        if os.path.exists(p):
            os.remove(p)


def parse_smoke(log):
    out = {}
    for line in log.splitlines():
        m = SMOKE_RE.search(line)
        if m:
            out[m.group(1).strip()] = m.group(2).strip()
    return out


def count_exceptions(log):
    n = 0
    for line in log.splitlines():
        low = line.lower()
        if "exception" not in low:
            continue
        if any(k in low for k in NOISE):
            continue
        n += 1
    return n


def run_scenario(exe, scene):
    args = [exe, "-batchmode", "-nographics", "-pickupProbe", "-pickupScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _zero(v):
    """必须为 0 的计数（残留/泄漏类判据）。"""
    return v.isdigit() and int(v) == 0


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "MAGNET": [
            # 两态都要成立：只测一边的话，"永远吸附"/"永远不吸附"各能骗过一半
            ("半径外不动", "是"),
            ("半径内回收并入账", "是"),
            ("断点通过", "2/2"),
        ],
        "CREDIT": [
            ("入账通过", "4/4"),
        ],
        "LEFTOVER": [
            # 先证明"确实造出了 6 个残留"，否则"残留=0"可能只是没造出来 —— 假通过
            ("重开前存量", "6"),
            ("重开后残留", _zero),
        ],
        "RESTART": [
            # ① 拿得到玩家 ② 必须是新世界的那个 ③ 管理器都接上了 ④ 新世界掉落闭环真的通
            ("重开后拿到玩家", "是"),
            ("是新世界玩家", "是"),
            ("新世界掉落闭环", "是"),
            ("重开通过", "4/4"),
        ],
        "REPEAT": [
            ("重开轮次通过", "3/3"),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got) if callable(expect) else (got == expect)
        rows.append((key, "0" if callable(expect) else expect, got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M24 掉落/磁吸/结算与重开局清理无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 MAGNET")
    ap.add_argument("--keep", action="store_true", help="保留探针产物（排查用）")
    args = ap.parse_args()

    exe = args.exe
    if not os.path.exists(exe):
        print("产物不存在: %s" % rel(exe))
        return 2

    data_dir = find_data_dir(args.data_dir)
    if not data_dir:
        print("找不到 persistentDataPath，用 --data-dir 指定")
        return 2

    scenes = SCENARIOS
    if args.only:
        scenes = [s for s in SCENARIOS if s[0] == args.only]
        if not scenes:
            print("未知场景: %s" % args.only)
            return 2

    print("=" * 74)
    print("M24 掉落/磁吸/结算 与 重开局清理 无头实测 · %d 个场景" % len(scenes))
    print("  产物   %s" % rel(exe))
    print("  数据   %s" % data_dir)
    print("=" * 74)

    backed = backup(data_dir, ["save.json", "settings.json"])
    if backed:
        print("  已备份 %d 个用户文件（跑后恢复）\n" % len(backed))

    all_ok = True
    try:
        for scene, desc in scenes:
            clear_artifacts(data_dir)
            print("  [%s] %s" % (scene, desc))
            log = run_scenario(exe, scene)
            res = parse_smoke(log)
            if not res:
                print("      → 不通过（未捕获 [SMOKE] 输出）")
                print("      " + "\n      ".join(log.splitlines()[-8:]))
                all_ok = False
                continue

            exc = count_exceptions(log)
            if exc:
                res["运行期异常"] = str(exc)

            ok, rows = check(scene, res)
            for (k, e, g, good) in rows:
                print("      %s %-16s 期望 %-10s 实际 %s" % (" " if good else "x", k, e, g))

            # 把探针的明细行与失败项也打出来：它们不是独立判据，
            # 但失败时能直接指出断点在哪一轮、哪一颗宝石上（M22 教训：分开计数才能指到断在哪一环）
            for extra in ("明细", "残留构成", "逐轮明细"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-14s %s" % (extra, res[extra]))
            if res.get("失败项", "无") != "无":
                print("        %-14s %s" % ("失败项", res["失败项"]))

            if res.get("运行期异常", "0") != "0":
                print("      x %-16s 实际 %s" % ("运行期异常", res["运行期异常"]))
                ok = False
            print("      → %s\n" % ("通过" if ok else "不通过"))
            all_ok = all_ok and ok
    finally:
        if args.keep:
            print("  已保留探针产物（--keep）")
        else:
            clear_artifacts(data_dir)
        restore(data_dir, backed)
        print("  用户文件已恢复原样")

    print("=" * 74)
    print("结论：%s" % ("通过 —— 磁吸半径真的决定能否拾取 / 经验倍率与回血入账精确且封顶 / "
                        "重开局拿得到新玩家且不再残留上一局实体 / 反复重开不累积"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
