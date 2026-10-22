#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M56「传输层」无头实测

做什么：
    拉起无头进程，把"换传输层"这条路的三段各钉一遍：
      SPEC    说明书本身：哪种能跨公网、哪种要登录、哪种有端口；
              以及本轮调研的核心事实——EOS **不走** SetClientAddress
      INSTALL 装配：装上后 TransportManager 认的是不是它；重复装会不会变成两个；
              缺 SDK 时必须如实失败、且**不许回落**成 Tugboat；能力提示要两面都对
      TARGET  目标与端口：设得上、读得回；种类不符时设不上且不留副作用；
              不适用端口的种类不许顺手改掉端口

为什么必须测：
    换传输层的失败模式不是崩溃，是**静默失效**。FishyEOS 的源码里写着
        public override void SetClientAddress(string address) { _ = address; }
        public override void SetPort(ushort port) { _ = port; }
    参数被直接扔掉。而项目此前正是靠 SetClientAddress 告诉传输层"连谁"。
    换过去之后：不报错、没有日志、点了加入没反应，看起来和"网络本来就不通"一模一样。
    这类缺陷没有例外，只能在无头环境里提前钉死。

判据的区分力（本脚本最要紧的地方）：
    1) 「装配成功」必须配「反查得到同一个」。只测前者的话，一个只 AddComponent、
       却从不给 TransportManager.Transport 赋值的实现照样全绿——**那正是 M56 之前的代码**。
    2) 「缺 SDK 时装配失败」必须配「失败时不许回落」。只测前者的话，
       失败后偷偷塞一个 Tugboat 的实现照样绿，而那会让"我在跑 EOS"变成
       "其实在跑局域网直连"，现象只有一个：跨公网连不上。
    3) 「端口设得上」必须配「不适用时不改」。只测前者的话，
       对所有种类无脑 SetPort 的实现看不出来。
    4) 「仅限局域网」那句提示要两面都断言：恒显示的提示等于没提示。

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
    ("SPEC", "说明书：能力位、类型名、EOS 不走客户端地址"),
    ("INSTALL", "装配：认的是不是它 / 幂等 / 缺 SDK 如实失败不回落 / 提示两面"),
    ("TARGET", "目标与端口：设得上读得回 / 不符时设不上 / 不适用时不许改"),
)

BACKUP_SUFFIX = ".m56bak"

TIMEOUT = {"SPEC": 120, "INSTALL": 120, "TARGET": 120}


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
    args = [exe, "-batchmode", "-nographics", "-transportProbe", "-transportScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _yes(v):
    return v == "是"


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "SPEC": [
            # 纯逻辑：能力位错一位，UI 与流程就跟着错一片
            ("未知种类无说明书", _yes),
            ("Tugboat 不能跨公网", _yes),
            ("EOS 能跨公网", _yes),
            ("Tugboat 不用登录", _yes),
            ("EOS 要登录", _yes),
            ("Tugboat 有端口", _yes),
            ("EOS 无端口", _yes),
            # 核心事实：FishyEOS 的 SetClientAddress 是空实现
            ("EOS 不走客户端地址", _yes),
            ("类型名互不相同", _yes),
            ("Tugboat 类型可找到", _yes),
            ("断点通过", "10/10"),
        ],
        "INSTALL": [
            # 「装上了」与「管理器认的是它」是两件事，后者才是真正生效
            ("装配后管理器认它", _yes),
            ("装配类型正确", _yes),
            ("重复装配不产生第二个", _yes),
            # 缺 SDK 必须失败 + 失败后不许回落到别的传输层（对偶，缺一半就是假绿）
            ("缺 SDK 时装配失败", _yes),
            ("失败时不许回落", _yes),
            ("反查得到 Tugboat", _yes),
            ("空管理器反查未知", _yes),
            # 提示两面：恒显示等于没显示
            ("仅限局域网提示上屏", _yes),
            ("能跨公网时提示为空", _yes),
            ("断点通过", "9/9"),
        ],
        "TARGET": [
            ("Tugboat 目标设得上", _yes),
            ("目标能读回", _yes),
            # 种类不符时不许"反正都是 Transport"一律 SetClientAddress
            ("种类不符时设不上", _yes),
            ("设不上时不留副作用", _yes),
            ("端口设得上", _yes),
            # 对偶：不适用端口的种类不许动实例
            ("端口不适用时不改", _yes),
            ("非法端口被挡", _yes),
            ("断点通过", "7/7"),
        ],
    }.get(scene, [])

    rows = []
    ok = True
    for key, want in spec:
        got = res.get(key, "<缺失>")
        if callable(want):
            good = want(got)
            want_s = "是"
        else:
            good = (got == want)
            want_s = want
        rows.append((key, want_s, got, good))
        if not good:
            ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M56 传输层无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 SPEC")
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
    print("M56 传输层无头实测 · %d 个场景" % len(scenes))
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
                print("      %s %-16s 期望 %-8s 实际 %s" % (" " if good else "x", k, e, g))

            if res.get("失败明细", "无") != "无":
                print("        %-14s %s" % ("失败明细", res["失败明细"]))

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
    print("总计：%s" % ("全部通过" if all_ok else "存在未通过场景"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
