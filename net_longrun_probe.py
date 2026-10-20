#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》联机长局稳定性实测（M42）

做什么：
    拉起房主 + 客户端两个无头实例，以 -smokeSpeed 加速跑一局"游戏内 30 分钟"，
    双端**同时刻收尾**，然后交叉核对「场上掉落物是否有界 / 上限真的生效 / 双端计数一致」。

为什么单机测完还要测联机：
    M41 在单机侧（PickupManager）修掉了"掉落物账本只进不出"——30 分钟游戏时间
    堆到 887 个宝石、每帧全表算距离 → 越玩越卡。但联机侧的掉落物走另一条路：
    NetEnemy.ServerDie() 直接调 NetWorld.SpawnGem()，整条绕过 PickupManager，
    既没有账本也没有上限。而且联机每个掉落物都带 SyncVar + NetworkTransform
    （磁吸时位置每帧在动，每个都在持续占同步带宽），比单机更贵。

三个只有联机才抓得到的失败面：
    1) 双端计数不一致——回收若不走房主权威 Despawn 而走本地 Object.Destroy，
       客户端对此一无所知，它的副本变成僵尸：位置不再更新却永远留在场上。
    2) 客户端侧无界——房主侧上限修好了，客户端视图却在悄悄堆积。
    3) 客户端同步丢失——掉落物压根没同步过来（客户端恒为 0）。

用法：
    python net_longrun_probe.py [--seconds 300] [--speed 6] [--address 127.0.0.1]

退出码：
    0 = 全部判据通过；1 = 有判据未过 / 超时 / 异常；2 = 前置条件不满足
"""

import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
NET_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "SmokeNet", "StarPixelRogue.exe")

# 复用 net_smoke.py 里已验证的双开骨架（启动 / 就绪探测 / 日志解析 / 体积熔断），
# 避免同一套逻辑出现第二份实现——M37 已经栽过一次"两份实现各自漂移"。
sys.path.insert(0, ROOT)
from net_smoke import (rel, launch, wait_host_ready, wait_for, parse_smoke,   # noqa: E402
                       count_exceptions, NOISE, SOLO_EXE)

# 探针判据名白名单（与 NetLongRunProbe.Check 的名字一一对应）。
# 为什么必须白名单：同一份日志里还有 SmokeTest 自己那批 [SMOKE] 行
# （"对端掉线 否"/"玩家阵亡 否"等），不筛的话它们会被当成长局判据混进失败面。
JUDGES = (
    "波次确实推进",
    "宝石有界",
    "回血球有界",
    "上限真的生效",
    "账本与场景一致",
    "末段贴顶后不再抬升",
    "长程数值不越界",
    "全程零异常",
)

PROBE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")


def probe_results(path):
    """抽出探针判据 -> 是/否/不适用，以及信息行。"""
    judges, info = {}, {}
    if not os.path.exists(path):
        return judges, info
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = PROBE_RE.search(line)
            if not m:
                continue
            k, v = m.group(1).strip(), m.group(2).strip()
            if k in JUDGES:
                judges[k] = v
            else:
                info[k] = v
    return judges, info


def as_int(info, key, default=-1):
    try:
        return int(str(info.get(key, default)).strip())
    except (TypeError, ValueError):
        return default


def peak_of(info, key):
    """'12 → 48（峰 241）' → 241；解析不出时返回 -1。"""
    v = info.get(key, "")
    m = re.search(r"峰\s*(\d+)", v)
    return int(m.group(1)) if m else -1


def main():
    ap = argparse.ArgumentParser(description="联机长局稳定性实测（房主 + 客户端双开）")
    ap.add_argument("--seconds", type=int, default=300, help="房主运行时长（真实秒）")
    ap.add_argument("--speed", type=float, default=6.0,
                    help="加速倍率（只作用于房主；客户端保持 1x 跟随权威同步）")
    ap.add_argument("--address", default="127.0.0.1", help="客户端连接地址")
    ap.add_argument("--start-delay", type=int, default=20, help="房主先启动的等待秒数")
    ap.add_argument("--exe", default=NET_EXE, help="构建产物路径")
    ap.add_argument("--log-limit-mb", type=int, default=250,
                    help="单端日志体积上限（MB），超过即熔断")
    args = ap.parse_args()

    exe = args.exe
    if not os.path.exists(exe):
        print("[错误] 找不到构建产物: %s" % rel(exe))
        print("       请先构建：-executeMethod StarPixel.EditorTools.BuildScript.HeadlessNetBuild")
        return 2
    # 与 net_smoke 同一条理由：拿单机产物跑联机只会得到"网络玩家数 0 / 同步波次 -1"
    # 这种看起来像同步坏了的假故障，宁可在这里直接拒绝。
    if os.path.normcase(os.path.abspath(os.path.dirname(exe))) == \
            os.path.normcase(os.path.abspath(os.path.dirname(SOLO_EXE))):
        print("[错误] 这是单机产物（Build/Smoke），不含联机装配。联机产物在 %s。" % rel(NET_EXE))
        return 2

    host_log = os.path.join(ROOT, "netlongrun_host.log")
    client_log = os.path.join(ROOT, "netlongrun_client.log")
    for p in (host_log, client_log):
        if os.path.exists(p):
            os.remove(p)

    print("=" * 68)
    print("联机长局稳定性实测（M42）")
    print("  产物   %s" % rel(exe))
    print("  房主   %ds（%sx 加速 + 保活）   客户端 1x 跟随" % (args.seconds, args.speed))
    print("  预期   游戏内约 %ds 的权威世界演化" % int(args.seconds * args.speed))
    print("=" * 68)

    # 1) 房主：加速 + 保活 + 长局探针
    host_args = ["-batchmode", "-nographics", "-netHost",
                 "-smokeSeconds", str(args.seconds),
                 "-smokeSpeed", str(args.speed),
                 "-smokeImmortal",
                 "-longrunProbe"]
    host = launch(exe, host_args, host_log)
    if host is None:
        return 2
    print("[1/3] 房主已启动，等待服务端就绪（最多 %ds）…" % args.start_delay)
    waited = wait_host_ready(host_log, args.start_delay)
    print("       服务端就绪探测耗时 %.0fs" % waited)
    if waited >= args.start_delay:
        print("[失败] 房主未能在 %ds 内就绪，无法开始长局实测" % args.start_delay)
        host.kill()
        return 1

    # 2) 客户端：与房主同时刻收尾（沿用 net_smoke 的对齐算法），**不传 -smokeSpeed**
    client_seconds = max(10, args.seconds - int(round(waited)))
    client_args = ["-batchmode", "-nographics", "-netClient",
                   "-netAddress", args.address,
                   "-smokeSeconds", str(client_seconds),
                   "-longrunProbe"]
    client = launch(exe, client_args, client_log)
    if client is None:
        host.kill()
        return 2
    print("[2/3] 客户端已启动（%ds，与房主同时刻收尾），连接 %s" % (client_seconds, args.address))

    # 3) 等待两端结束
    budget = args.seconds + 120
    wait_for(host, "房主", budget, watch_log=host_log, limit_mb=args.log_limit_mb)
    wait_for(client, "客户端", budget, watch_log=client_log, limit_mb=args.log_limit_mb)
    print("[3/3] 两端均已结束，开始交叉核对\n")

    h_judges, h_info = probe_results(host_log)
    c_judges, c_info = probe_results(client_log)

    if not h_judges:
        print("[失败] 房主日志里没有探针判据输出，无法核对（日志: %s）" % rel(host_log))
        return 1
    if not c_judges:
        print("[失败] 客户端日志里没有探针判据输出，无法核对（日志: %s）" % rel(client_log))
        return 1

    h_exc = count_exceptions(host_log)
    c_exc = count_exceptions(client_log)

    print("%-14s %-16s %-16s" % ("判据", "房主", "客户端"))
    print("-" * 50)
    for k in JUDGES:
        print("%-14s %-16s %-16s" % (k, h_judges.get(k, "?"), c_judges.get(k, "?")))
    print("-" * 50)
    print("%-14s %-16s %-16s" % ("宝石峰值", peak_of(h_info, "宝石峰值"),
                                  peak_of(c_info, "宝石峰值")))
    print("%-14s %-16s %-16s" % ("回血球峰值", as_int(h_info, "回血球峰值"),
                                  as_int(c_info, "回血球峰值")))
    print("%-14s %-16s %-16s" % ("最终波次", as_int(h_info, "最终波次"),
                                  as_int(c_info, "最终波次")))
    print("%-14s %-16s %-16s" % ("回收次数", h_info.get("回收次数", "?"),
                                  c_info.get("回收次数", "?")))
    print("%-14s %-16s %-16s" % ("末次账本", h_info.get("末次账本", "?"),
                                  c_info.get("末次账本", "?")))
    print("%-14s %-16s %-16s" % ("托管堆MB", h_info.get("托管堆MB", "?"),
                                  c_info.get("托管堆MB", "?")))
    print("%-14s %-16s %-16s" % ("平均帧率", h_info.get("平均帧率", "?"),
                                  c_info.get("平均帧率", "?")))
    print("-" * 50)
    print("运行时异常：房主 %d 条 / 客户端 %d 条" % (h_exc, c_exc))

    problems = []

    # —— 房主侧：探针自己的判据必须全过 ——
    for k in JUDGES:
        v = h_judges.get(k)
        if v != "是" and v != "不适用":
            problems.append("房主侧判据「%s」= %s" % (k, v))

    # —— 客户端侧：它自己那份视图也必须干净 ——
    # 客户端只跟随同步，理论上所有"有界/生效"类判据都不该由它承担；
    # 但它的场景扫描同样不该看到无界堆积（房主漏同步时这里是唯一线索）。
    for k in ("宝石有界", "回血球有界", "全程零异常", "长程数值不越界"):
        v = c_judges.get(k)
        if v != "是" and v != "不适用":
            problems.append("客户端侧判据「%s」= %s" % (k, v))

    # —— 双端交叉核对（联机独有的失败面）——
    h_gem = peak_of(h_info, "宝石峰值")
    c_gem = peak_of(c_info, "宝石峰值")
    h_hp = as_int(h_info, "回血球峰值")
    c_hp = as_int(c_info, "回血球峰值")
    h_wave = as_int(h_info, "最终波次")
    c_wave = as_int(c_info, "最终波次")

    # 1) 双端宝石计数一致。容差取 max(6, 8%)：两端收尾不可能精确到同一帧，
    #    最后半秒里房主还能再掉一批、广播还在路上。
    #
    #    **必须双向**：只查"客户端 > 房主"会漏掉相反的那个面——客户端自行
    #    Object.Destroy 联机对象（静态检查第 20 项要抓的写法）时客户端反而**偏少**，
    #    单向断言下这一路会一路绿灯地溜过去。
    #    两个方向的量级都远超容差（僵尸累积是几百、本地销毁是差一个数量级），
    #    容差放多宽都盖不住。
    if h_gem >= 0 and c_gem >= 0:
        tol = max(6, int(h_gem * 0.08))
        if abs(c_gem - h_gem) > tol:
            why = ("客户端视图在房主不知情的情况下堆积僵尸对象——回收若走本地 Object.Destroy "
                   "而非房主权威 Despawn，副本就再也收不到反生成广播"
                   if c_gem > h_gem else
                   "客户端侧偏少——客户端在自行销毁联机对象（房主权威外的本地 Destroy），"
                   "静查第 20 项正是要拦这种写法")
            problems.append(
                "双端宝石计数不一致：客户端峰 %d vs 房主峰 %d（容差 %d）——%s"
                % (c_gem, h_gem, tol, why))

    # 2) 回血球同理
    if h_hp >= 0 and c_hp >= 0:
        tol = max(4, int(h_hp * 0.15))
        if abs(c_hp - h_hp) > tol:
            problems.append(
                "双端回血球计数不一致：客户端峰 %d vs 房主峰 %d（容差 %d）——"
                "同上：客户端视图与房主权威世界分叉" % (c_hp, h_hp, tol))

    # 3) 客户端必须真的收到了掉落物同步。恒为 0 说明同步链路断了，
    #    此时"双端一致"会平凡成立——必须单独拦住。
    if c_gem == 0 and h_gem > 20:
        problems.append(
            "客户端侧宝石峰值恒为 0（房主侧 %d）——掉落物压根没同步到客户端，"
            "\"双端一致\"在这种情形下会平凡成立，必须单独判失败" % h_gem)

    # 4) 波次：客户端读的是同步下来的权威值，同时刻采样就该相等
    if h_wave >= 1 and c_wave >= 1 and abs(h_wave - c_wave) > 1:
        problems.append("双端波次不一致：房主 %d vs 客户端 %d" % (h_wave, c_wave))

    # 5) 世界真的在动（否则上面全部判据都是假绿）
    if h_wave < 12:
        problems.append(
            "房主侧波次只到 %d（期望 ≥12）——加速可能没生效或权威世界没跑起来，"
            "此时所有增长类判据都会假绿" % h_wave)

    if h_exc or c_exc:
        problems.append("存在运行时异常（房主 %d / 客户端 %d），详见日志" % (h_exc, c_exc))

    print("-" * 50)
    if problems:
        print("结论：未通过")
        for p in problems:
            print("  [问题] " + p)
        print("-" * 50)
        return 1
    print("结论：通过 —— 联机掉落物有界、上限真的生效、双端计数一致、全程零异常")
    print("-" * 50)
    return 0


if __name__ == "__main__":
    sys.exit(main())
