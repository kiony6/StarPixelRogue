#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》联机双开实测（M18）

做什么：
    一条命令同时拉起两个无头实例——**房主 + 客户端**，自动建房、自动加入，
    跑一段时间后从两边日志交叉核对「波次 / 击杀 / 玩家数」是否一致。

为什么能验证联机：
    客户端的「同步波次 / 同步击杀」读的是 NetWorldState 的 SyncVar（房主权威写入、
    网络广播），而房主读的是同一批 SyncVar 的权威侧。两端数值一致 == 房主权威同步
    链路真的在工作。这是 M10（角度同步）/ M11（断线重连）/ M14（观战）共同的运行时底座。

三种模式：
    1) 默认      —— 房主 + 普通客户端，核对波次/击杀/玩家数（共同底座）
    2) --spectator —— 客户端以观战身份加入（M14）：核对「自身玩家对象被房主回收」
                     + 仍能收到世界同步（波次/击杀 > 0）
    3) --drop-at N —— 客户端在第 N 秒模拟意外掉线（M11）：核对自动重连是否成功、
                     且波次/等级/击杀三项进度没有回退；房主侧还要观测到「2→1→2」

用法：
    python net_smoke.py [--seconds 60] [--address 127.0.0.1] [--start-delay 8] [--exe 路径]
    python net_smoke.py --spectator            # M14 观战
    python net_smoke.py --drop-at 15           # M11 断线重连

退出码：
    0 = 双端一致且判定通过；1 = 不一致 / 超时 / 异常；2 = 前置条件不满足
"""

import argparse
import os
import re
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")

SMOKE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")

# 无头环境必然出现的渲染噪声，不算运行时异常
# "screen position out of view frustum"：NullGfxDevice 下 Canvas 每帧都会报，
# 帧率不锁时一秒能刷几万条，既不是异常也会把磁盘写满。
NOISE = ("shader", "gpu", "memorysetup", "unitymemory", "curl error", "unitlets",
         "screen position out of view frustum")


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def parse_smoke(path):
    """从日志里抽出所有 [SMOKE] key -> value。"""
    data = {}
    if not os.path.exists(path):
        return data, False
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = SMOKE_RE.search(line)
            if m:
                data[m.group(1).strip()] = m.group(2).strip()
    return data, True


def count_exceptions(path):
    n = 0
    if not os.path.exists(path):
        return n
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            low = line.lower()
            if "exception" in low and not any(k in low for k in NOISE):
                n += 1
    return n


def as_int(d, key, default=-1):
    try:
        return int(str(d.get(key, default)).strip())
    except (TypeError, ValueError):
        return default


# 联机跑挂时最常出现的几条根因线索。日志动辄几十 MB，
# 靠人肉翻既慢又容易漏，这里直接把"判决性"的行筛出来。
DIAG_PATTERNS = (
    ("构建包标识", "Mono path[0]"),
    ("兜底 NetworkManager", "运行时兜底创建"),
    ("预制体集合缺失", "SpawnablePrefabs is null"),
    ("网络对象未初始化", "expected to be initialized"),
    ("传输层缺失", "Transport is null"),
    ("连接失败", "Connection failed"),
    ("联机后端不可用", "联机后端不可用"),
)


def diagnose(path, name):
    """从日志里抽取根因线索（每种最多 2 条，避免刷屏）。

    为什么需要：双开跑挂时日志常达数百 MB，人工翻不动；而这些行能直接
    指出"跑的是哪个包 / NetworkManager 是否兜底 / 预制体是否注册"，
    是判定联机链路断点的最快依据。
    """
    print("\n—— %s 关键诊断 ——" % name)
    if not os.path.exists(path):
        print("  （日志不存在）")
        return
    found = 0
    counts = {k: 0 for k, _ in DIAG_PATTERNS}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            low = line.lower()
            for label, pat in DIAG_PATTERNS:
                if counts[label] >= 2:
                    continue
                if pat.lower() in low:
                    counts[label] += 1
                    found += 1
                    print("  [%s] %s" % (label, line.strip()[:150]))
    if found == 0:
        print("  （未命中已知根因线索，见下方首个异常堆栈）")


def first_exception(path, name, limit=6):
    """打印首个异常及其后几行（即首个异常堆栈）。"""
    if not os.path.exists(path):
        return
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.readlines()
    for i, line in enumerate(lines):
        if "exception" in line.lower() and not any(k in line.lower() for k in NOISE):
            print("\n—— %s 首个异常堆栈 ——" % name)
            for j in range(i, min(i + limit, len(lines))):
                print("  " + lines[j].strip()[:160])
            return


def wait_host_ready(log_path, timeout):
    """轮询房主日志，等到「世界已建好、可接入」就立刻放行。

    为什么不等固定时长：房主若提前阵亡退出（联机冒烟已修，但保不齐别的路径），
    固定等待会让客户端对着一个已经消失的服务端干等；这里一旦探测到就绪标记就
    马上启动客户端，既缩短总时长，也让「房主没起来」更早暴露。
    返回实际等待秒数。
    """
    marker = "冒烟实测启动"
    waited = 0.0
    while waited < timeout:
        if os.path.exists(log_path):
            try:
                with open(log_path, encoding="utf-8", errors="replace") as f:
                    if marker in f.read():
                        return waited
            except OSError:
                pass
        time.sleep(1.0)
        waited += 1.0
    return waited


def launch(exe, args, log_path):
    cmd = [exe] + args + ["-logFile", log_path]
    try:
        return subprocess.Popen(cmd, cwd=ROOT,
                                stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL)
    except OSError as e:
        print("[错误] 无法启动进程: %s" % e)
        return None


def log_too_big(path, limit_mb):
    """日志体积熔断：无头环境下某类告警可能每帧刷一条，几分钟就能写满数 GB。
    本机 C 盘曾因此只剩 6GB，必须在运行期间兜底，不能等跑完再看。"""
    try:
        return os.path.getsize(path) > limit_mb * 1024 * 1024
    except OSError:
        return False


def wait_for(proc, name, timeout, watch_log=None, limit_mb=200):
    """等待进程结束；同时监视日志体积，超限立即熔断，避免写爆磁盘。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            return True
        if watch_log and log_too_big(watch_log, limit_mb):
            print("[熔断] %s 日志超过 %dMB，判定为异常刷屏，强制结束" % (name, limit_mb))
            proc.kill()
            return False
        time.sleep(1.0)
    print("[警告] %s 超时未退出，强制结束" % name)
    proc.kill()
    return False


def main():
    ap = argparse.ArgumentParser(description="联机双开实测（房主 + 客户端无头对跑）")
    ap.add_argument("--seconds", type=int, default=60, help="房主运行时长（秒）")
    ap.add_argument("--address", default="127.0.0.1", help="客户端连接地址")
    ap.add_argument("--start-delay", type=int, default=8, help="房主先启动的等待秒数")
    ap.add_argument("--exe", default=DEFAULT_EXE, help="构建产物路径")
    ap.add_argument("--log-limit-mb", type=int, default=200,
                    help="单端日志体积上限（MB），超过即熔断，防止无头刷屏写爆磁盘")
    ap.add_argument("--spectator", action="store_true",
                    help="M14：客户端以观战身份加入（不生成可操作角色）")
    ap.add_argument("--drop-at", type=float, default=0,
                    help="M11：客户端在第 N 秒模拟一次意外掉线并验证自动重连（0=不触发）")
    args = ap.parse_args()

    if args.spectator and args.drop_at > 0:
        print("[错误] --spectator 与 --drop-at 不能同时使用（观战者不参与 M11 重连路径）")
        return 2
    mode = "观战(M14)" if args.spectator else ("断线重连(M11)" if args.drop_at > 0 else "普通联机")

    exe = args.exe
    if not os.path.exists(exe):
        print("[错误] 找不到构建产物: %s" % rel(exe))
        print("       请先构建，例如：")
        print("       Unity.exe -batchmode -nographics -quit -projectPath StarPixelRogue \\")
        print("         -executeMethod StarPixel.EditorTools.BuildScript.HeadlessBuild")
        return 2

    host_log = os.path.join(ROOT, "net_host.log")
    client_log = os.path.join(ROOT, "net_client.log")
    for p in (host_log, client_log):
        if os.path.exists(p):
            os.remove(p)

    # 客户端必须比房主"早收尾、且收尾时房主还活着"。
    # 此前算成 seconds - start_delay，客户端反而比房主晚约 1s 结束——
    # 采样那一刻房主进程已退出，客户端正在拆网络对象，于是出现
    # 「房主场上 20 个敌人 / 客户端 0 个」这种比同步延迟更假的差异。
    # 余量取 3s：再小就有"客户端反而晚于房主收尾"的风险（两端启动耗时有 ±1s 抖动），
    # 再大则采样时刻差被后期击杀速率（约 10/s）放大成很大的绝对差。
    client_seconds = max(10, args.seconds - args.start_delay - 3)

    print("=" * 68)
    print("联机双开实测（M18）· 模式：%s" % mode)
    print("  产物   %s" % rel(exe))
    print("  房主   %ds   客户端 %ds（延后 %ds 启动）" % (args.seconds, client_seconds, args.start_delay))
    if args.spectator:
        print("  观战   客户端以观战身份加入，自身玩家对象应由房主回收")
    if args.drop_at > 0:
        print("  掉线   客户端在第 %.0fs 模拟意外掉线，随后验证自动重连" % args.drop_at)
    print("=" * 68)

    # 1) 房主
    host = launch(exe, ["-batchmode", "-nographics", "-netHost",
                        "-smokeSeconds", str(args.seconds)], host_log)
    if host is None:
        return 2
    print("[1/3] 房主已启动，等待服务端就绪（最多 %ds）…" % args.start_delay)
    waited = wait_host_ready(host_log, args.start_delay)
    print("       服务端就绪探测耗时 %.0fs" % waited)

    # 2) 客户端
    client_args = ["-batchmode", "-nographics", "-netClient",
                   "-netAddress", args.address,
                   "-smokeSeconds", str(client_seconds)]
    if args.spectator:
        client_args.append("-netSpectator")
    if args.drop_at > 0:
        client_args += ["-netDropAt", str(args.drop_at)]
    client = launch(exe, client_args, client_log)
    if client is None:
        host.kill()
        return 2
    print("[2/3] 客户端已启动，连接 %s" % args.address)

    # 3) 等待两端结束
    budget = args.seconds + 90
    wait_for(host, "房主", budget, watch_log=host_log, limit_mb=args.log_limit_mb)
    wait_for(client, "客户端", budget, watch_log=client_log, limit_mb=args.log_limit_mb)
    print("[3/3] 两端均已结束，开始交叉核对\n")

    host_data, host_ok = parse_smoke(host_log)
    client_data, client_ok = parse_smoke(client_log)

    if not host_ok or not client_ok or not host_data or not client_data:
        print("[失败] 至少一端没有产出 [SMOKE] 结果，无法核对")
        print("       房主日志: %s（存在=%s）" % (rel(host_log), host_ok))
        print("       客户端日志: %s（存在=%s）" % (rel(client_log), client_ok))
        return 1

    rows = [
        ("会话角色", host_data.get("会话角色", "?"), client_data.get("会话角色", "?")),
        ("网络玩家数", host_data.get("网络玩家数", "?"), client_data.get("网络玩家数", "?")),
        ("网络玩家峰值", host_data.get("网络玩家峰值", "?"), client_data.get("网络玩家峰值", "?")),
        ("同步波次", host_data.get("同步波次", "?"), client_data.get("同步波次", "?")),
        ("同步击杀", host_data.get("同步击杀", "?"), client_data.get("同步击杀", "?")),
        ("场上敌人", host_data.get("场上敌人", "?"), client_data.get("场上敌人", "?")),
        ("遇到Boss", host_data.get("遇到Boss", "?"), client_data.get("遇到Boss", "?")),
        ("运行时长", host_data.get("时长", "?"), client_data.get("时长", "?")),
    ]

    # M14 观战：观战侧特有指标
    if args.spectator:
        rows.append(("自身角色已回收", host_data.get("自身角色已回收", "-"),
                     client_data.get("自身角色已回收", "?")))

    # M11 断线重连：掉线前后各采一次样，比对进度是否回退
    if args.drop_at > 0:
        rows += [
            ("断线时刻", host_data.get("断线时刻", "-"), client_data.get("断线时刻", "?")),
            ("重连完成", host_data.get("重连完成", "-"), client_data.get("重连完成", "?")),
            ("重连耗时", host_data.get("重连耗时", "-"), client_data.get("重连耗时", "?")),
            ("断线前波次", "-", client_data.get("断线前波次", "?")),
            ("重连后波次", "-", client_data.get("重连后波次", "?")),
            ("断线前等级", "-", client_data.get("断线前等级", "?")),
            ("重连后等级", "-", client_data.get("重连后等级", "?")),
            ("断线前击杀", "-", client_data.get("断线前击杀", "?")),
            ("重连后击杀", "-", client_data.get("重连后击杀", "?")),
            ("对端掉线", host_data.get("对端掉线", "?"), "-"),
            ("对端重连", host_data.get("对端重连", "?"), "-"),
        ]

    print("%-12s %-16s %-16s" % ("指标", "房主", "客户端"))
    print("-" * 48)
    for k, hv, cv in rows:
        print("%-12s %-16s %-16s" % (k, hv, cv))
    print("-" * 48)

    # —— 判定 ——
    problems = []

    if host_data.get("会话角色") != "房主":
        problems.append("房主实例的会话角色不是「房主」（%s）" % host_data.get("会话角色"))
    if args.spectator:
        if client_data.get("会话角色") != "观战":
            problems.append("客户端实例的会话角色不是「观战」（%s）——观战身份未生效" %
                            client_data.get("会话角色"))
        # 观战的核心：Fish-Net 会为每个连接自动生成玩家对象，M14 的链路要把它回收掉。
        # 回收发生 == 客户端的 RequestSpectatorServerRpc 上达房主且房主执行了 Despawn。
        if client_data.get("自身角色已回收") != "是":
            problems.append("观战者的自身玩家对象未被回收（%s）——M14 观战链路未生效" %
                            client_data.get("自身角色已回收"))
    elif client_data.get("会话角色") != "客户端":
        problems.append("客户端实例的会话角色不是「客户端」（%s）——可能根本没连上" % client_data.get("会话角色"))

    # 用「峰值」而非收尾瞬时值：两端时长不同步（客户端更短），先退出的那一端会让
    # 对端收尾时注册表掉回 1，用瞬时值会把成功的联机误判成失败。
    h_players = as_int(host_data, "网络玩家峰值", as_int(host_data, "网络玩家数"))
    c_players = as_int(client_data, "网络玩家峰值", as_int(client_data, "网络玩家数"))
    # 观战模式下房主可能只在其对象被回收前短暂看到 2 名玩家，故放宽房主侧期望
    if h_players < (1 if args.spectator else 2):
        problems.append("房主全程最多只看到 %d 名玩家（期望 ≥%d，说明客户端未成功加入）" %
                        (h_players, 1 if args.spectator else 2))
    if c_players < (1 if args.spectator else 2):
        problems.append("客户端全程最多只看到 %d 名玩家（期望 ≥%d，说明未收到房主的玩家对象）" %
                        (c_players, 1 if args.spectator else 2))

    h_kills = as_int(host_data, "同步击杀")
    c_kills = as_int(client_data, "同步击杀")
    if h_kills <= 0:
        problems.append("房主同步击杀为 %d，战斗循环可能没跑起来" % h_kills)
    if c_kills <= 0:
        problems.append("客户端同步击杀为 %d——SyncVar 未把击杀同步过来（联机同步链路可疑）" % c_kills)

    h_wave = as_int(host_data, "同步波次")
    c_wave = as_int(client_data, "同步波次")
    if h_wave < 1:
        problems.append("房主同步波次为 %d，波次系统可能没推进" % h_wave)
    # 波次：客户端比房主早收尾（见 client_seconds 注释），采样时刻天然靠前，
    # 而后期一次清场就能推进一波，落后 1~2 波属正常网络采样差。
    # 判据要抓的是"客户端波次停在初始值不动"（同步根本没通），那种情况差值会很大。
    wave_tol = max(2, int(h_wave * 0.25))
    if abs(h_wave - c_wave) > wave_tol:
        problems.append("双端波次不一致：房主 %d vs 客户端 %d（容差 %d）" % (h_wave, c_wave, wave_tol))

    # 击杀：不要求瞬时值相等，改判"客户端是否单调跟随房主权威值"。
    # 客户端读的是广播过来的 SyncVar，采样必然早于房主终值；后期一次 AoE 清几十只，
    # 击杀速率可达每秒数十，几秒采样差就是几十上百的绝对差（实测第 15 波 458 / 419）。
    # 因此只拦三类真问题：收到 0（没同步）、超过权威值（逻辑错）、落后过半（同步中途断了）。
    if h_kills > 0:
        if c_kills > h_kills:
            problems.append("客户端击杀 %d 超过房主权威值 %d——同步逻辑可疑" % (c_kills, h_kills))
        elif c_kills < h_kills * 0.6:
            problems.append("客户端击杀 %d 明显落后于房主 %d——同步可能中途停止" % (c_kills, h_kills))

    # —— M11 断线重连专项判定 ——
    # 三层证据缺一不可：客户端自述重连成功 + 进度没回退 + 房主侧真的观测到「2→1→2」。
    # 只信客户端自述会被"根本没掉线也算通过"糊弄过去；只看房主则无法确认进度重放。
    if args.drop_at > 0:
        if client_data.get("重连完成") != "是":
            problems.append("M11：客户端在模拟掉线后未重连成功（%s）" % client_data.get("重连完成"))

        pre_w, post_w = as_int(client_data, "断线前波次"), as_int(client_data, "重连后波次")
        pre_l, post_l = as_int(client_data, "断线前等级"), as_int(client_data, "重连后等级")
        pre_k, post_k = as_int(client_data, "断线前击杀"), as_int(client_data, "重连后击杀")

        if pre_w < 0 or post_w < 0:
            problems.append("M11：掉线前后波次采样缺失（前 %d / 后 %d）" % (pre_w, post_w))
        elif post_w < pre_w:
            problems.append("M11：重连后波次 %d 低于断线前 %d——世界未续上" % (post_w, pre_w))

        if pre_l < 0 or post_l < 0:
            problems.append("M11：掉线前后等级采样缺失（前 %d / 后 %d）" % (pre_l, post_l))
        elif post_l < pre_l:
            problems.append("M11：重连后等级 %d 低于断线前 %d——进度快照未重放" % (post_l, pre_l))
        elif pre_l <= 1:
            # 区分力守卫：掉线时才 1 级的话，"重连后 >= 断线前" 会平凡成立，
            # 进度重放失败也能蒙混过关（实测就吃过这个亏）。故直接判为无效样本。
            problems.append("M11：断线时等级仅为 %d，进度重放判据无区分力——请调大 --drop-at 让客户端先升几级"
                            % pre_l)

        if pre_k < 0 or post_k < 0:
            problems.append("M11：掉线前后击杀采样缺失（前 %d / 后 %d）" % (pre_k, post_k))
        elif post_k < pre_k:
            problems.append("M11：重连后击杀 %d 低于断线前 %d——世界状态回退" % (post_k, pre_k))

        if host_data.get("对端掉线") != "是":
            problems.append("M11：房主未观测到客户端掉线（玩家数从未 2→1）")
        if host_data.get("对端重连") != "是":
            problems.append("M11：房主未观测到客户端重连（玩家数未回到 2）")

    h_exc = count_exceptions(host_log)
    c_exc = count_exceptions(client_log)
    print("\n运行时异常：房主 %d 条 / 客户端 %d 条" % (h_exc, c_exc))
    if h_exc or c_exc:
        problems.append("存在运行时异常（房主 %d / 客户端 %d），详见日志" % (h_exc, c_exc))
        # 有异常才诊断：直接给出根因线索与首个异常堆栈，省去人工翻几十 MB 的日志
        diagnose(host_log, "房主")
        first_exception(host_log, "房主")
        diagnose(client_log, "客户端")
        first_exception(client_log, "客户端")

    print("-" * 48)
    if problems:
        print("结论：未通过")
        for p in problems:
            print("  - " + p)
        print("-" * 48)
        return 1

    if args.spectator:
        print("结论：通过 —— 观战者自身角色已被房主回收，且仍能收到房主权威世界同步")
    elif args.drop_at > 0:
        print("结论：通过 —— 客户端掉线后自动重连成功，波次/等级/击杀进度均未回退")
    else:
        print("结论：通过 —— 双端玩家数/波次/击杀一致，房主权威同步链路工作正常")
    print("-" * 48)
    return 0


if __name__ == "__main__":
    sys.exit(main())
