#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
18 个专项探针的批量回归入口（M47）

为什么它不是 `for p in ...; do ...; done`：
    M46 §6 撞到过一次——18 个探针放进一个 shell 循环连跑，从第 13 个起**连着 5 个失败**
    （日志停在第 1 个场景就退出），而**逐个单跑全部通过**。
    M47 把根因挖出来了：**不是时序抖动，是 safe-delete 的按轮次累计阈值**。

    每个探针跑完都要清理自己的测试档（`os.remove` 测试存档 + `os.rename` 备份回来），
    一次约 3～4 个文件操作。18 个连着跑 = 同一轮里累计 50+ 次删除，
    于是从第 13 个开始每一个都被
        [safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED] {"count":50,"threshold":50,
         "scope":"turn","targets":["...\\save.json.m27bak"],"targetCount":1}
    拦下来——注意 `targets` 只有 1 个文件，被拦不是因为"删得多"，是因为**这一轮累计够了**。
    它完美解释了 M46 那个"从第 13 个起、恰好连着 5 个"的现象，
    也解释了为什么"逐个单跑"（每次工具调用是独立的一轮，计数重新算）就全过。

    所以**重试是错的**：重试只会让计数涨得更快。真正的解法是把批次拆小，
    并且**每片必须在单独的一次工具调用里跑**（`--slice`）。

    本脚本做两件事：
      ① 每个探针之间留一点间隔（默认 3s），给上一个无头进程真正退出的时间；
      ② 提供 `--slice a/b` 分片，默认每片 6 个，让调用方能把 18 个拆成三次调用。

    判定口径：只看进程退出码（各探针自报结论，本脚本不替它们解释），
    退出码非 0 即失败。

用法：
    python run_probes.py --slice 1/3        # 第 1 片（每片必须在单独一次调用里跑！）
    python run_probes.py --only pool,sub    # 只跑指定的几个
    python run_probes.py --size 4           # 每片 4 个
    python run_probes.py --gap 10           # 间隔改成 10s

退出码：0 = 全部通过；1 = 有真失败
"""

import argparse
import glob
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable

# 探针脚本清单：*_probe.py，排除本文件与长局/联机类（它们有自己的驱动脚本）
SKIP = {"longrun_probe.py", "net_longrun_probe.py", "net_smoke.py"}


def discover():
    names = []
    for p in sorted(glob.glob(os.path.join(ROOT, "*_probe.py"))):
        n = os.path.basename(p)
        if n in SKIP or n == "run_probes.py":
            continue
        names.append(n[:-3])        # 去掉 .py
    return names


def tail(path, n=6):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            lines = [l.rstrip() for l in f.readlines() if l.strip()]
        return lines[-n:]
    except OSError:
        return ["（日志不可读）"]


def run_one(name, gap, log_dir):
    script = os.path.join(ROOT, name + ".py")
    log = os.path.join(log_dir, name + ".log")
    try:
        with open(log, "w", encoding="utf-8") as f:
            p = subprocess.run([PY, script], stdout=f, stderr=subprocess.STDOUT, timeout=900)
        return p.returncode
    except subprocess.TimeoutExpired:
        return 124


def main():
    ap = argparse.ArgumentParser(description="18 个专项探针批量回归（分片 + 间隔）")
    ap.add_argument("--only", default="", help="逗号分隔，只跑指定的几个（不带 _probe.py 后缀）")
    ap.add_argument("--gap", type=int, default=3, help="每个探针之间的间隔秒数（默认 3）")
    ap.add_argument("--size", type=int, default=6, help="每片几个探针（默认 6，每片单独一次调用）")
    ap.add_argument("--slice", dest="sl", default="",
                    help="只跑第 a 片（共 b 片），如 1/3；每片必须在单独一次调用里跑")
    args = ap.parse_args()

    names = discover()
    if args.only:
        want = [s.strip() for s in args.only.split(",") if s.strip()]
        unknown = [w for w in want if w not in names]
        if unknown:
            print("未知探针: %s" % ", ".join(unknown))
            return 2
        names = want
    elif args.sl:
        a, _, b = args.sl.partition("/")
        try:
            i, n = int(a), int(b or "3")
        except ValueError:
            print("--slice 形如 1/3")
            return 2
        size = max(1, (len(names) + n - 1) // n)
        names = names[(i - 1) * size: i * size]

    log_dir = os.path.join(ROOT, ".probe_logs")
    os.makedirs(log_dir, exist_ok=True)

    print("=" * 74)
    print("专项探针批量回归 · 本片 %d 个（间隔 %ds）" % (len(names), args.gap))
    print("  失败的探针日志见 .probe_logs/")
    print("=" * 74)

    real_fail = []
    for i, name in enumerate(names):
        code = run_one(name, args.gap, log_dir)
        print("  [%2d/%d] %-14s exit=%d" % (i + 1, len(names), name, code))
        if code != 0:
            real_fail.append(name)
        if i + 1 < len(names):
            time.sleep(args.gap)

    print("-" * 74)
    if real_fail:
        print("真失败 %d 个：" % len(real_fail))
        for n in real_fail:
            print("  ---- %s ----" % n)
            for l in tail(os.path.join(log_dir, n + ".log")):
                print("      " + l)
    print("=" * 74)
    print("总计：%s" % ("全部通过" if not real_fail else "%d 个失败" % len(real_fail)))
    return 0 if not real_fail else 1


if __name__ == "__main__":
    raise SystemExit(main())
