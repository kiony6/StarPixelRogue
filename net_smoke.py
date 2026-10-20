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
    3) --drop-at N —— 客户端在第 N 秒模拟意外掉线（M11 客户端分支）：先拆本地镜像，
                     重连后重建；核对自动重连成功 + 进度未回退 + 房主观测到「2→1→2」
    4) --host-drop-at N —— 房主在第 N 秒模拟意外掉线（M11 房主分支）：**服务端仍在运行、
                     权威世界保留不拆**，只重连房主的本地客户端；核对掉线期间
                     场上敌人/波次仍在（世界未被拆）+ 进度未回退 + 对端客户端不受影响

用法：
    python net_smoke.py [--seconds 60] [--address 127.0.0.1] [--start-delay 8] [--exe 路径]
    python net_smoke.py --spectator                # M14 观战
    python net_smoke.py --drop-at 50               # M11 客户端掉线
    python net_smoke.py --host-drop-at 50          # M11 房主掉线（权威世界保留）

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
# 联机冒烟必须跑 BuildScript.HeadlessNetBuild 的产物（Build/SmokeNet）。
# 这里原先默认指向单机的 Build/Smoke，结果整套联机断言都建在单机二进制上：
# 双端照常启动、照常跑满 60s、异常 0，只是"网络玩家数 0 / 同步波次 -1"——
# 一个看起来像联机同步坏了的假故障，实际是压根没测联机版本。
NET_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "SmokeNet", "StarPixelRogue.exe")
SOLO_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")
DEFAULT_EXE = NET_EXE if os.path.exists(NET_EXE) else SOLO_EXE

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


def _lvl_pair(d):
    """「等级 / 权威等级」并排显示。

    M36：客户端的「等级」一度恒为 1 而权威值是 3——收尾输出读了不参与 XP 结算的
    PlayerController.level，把真实进度掩盖成初始值。两个数并排放，一眼看出谁在撒谎。
    单机产物没有权威等级（那条输出在 #if FISHNET_ENABLED 里），显示为 -。
    """
    lv = d.get("等级")
    auth = d.get("权威等级")
    if auth is None:
        return str(lv) if lv is not None else "?"
    return "%s / %s" % (lv, auth)


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
    ap.add_argument("--no-align", action="store_true",
                    help="M36：退回旧的「客户端早 3s 收尾」采样（仅用于区分力对照，"
                         "默认已改为双端同时刻收尾）")
    ap.add_argument("--spectator", action="store_true",
                    help="M14：客户端以观战身份加入（不生成可操作角色）")
    ap.add_argument("--drop-at", type=float, default=0,
                    help="M11：客户端在第 N 秒模拟一次意外掉线并验证自动重连（0=不触发）")
    ap.add_argument("--host-drop-at", type=float, default=0,
                    help="M11：房主在第 N 秒模拟一次意外掉线（权威世界保留、不拆世界）")
    args = ap.parse_args()

    if args.spectator and (args.drop_at > 0 or args.host_drop_at > 0):
        print("[错误] --spectator 与 --drop-at / --host-drop-at 不能同时使用（观战者不参与 M11 重连路径）")
        return 2
    if args.drop_at > 0 and args.host_drop_at > 0:
        print("[错误] --drop-at 与 --host-drop-at 不能同时使用（M11 两条分支需分别验证）")
        return 2
    if args.spectator:
        mode = "观战(M14)"
    elif args.drop_at > 0:
        mode = "断线重连·客户端掉线(M11)"
    elif args.host_drop_at > 0:
        mode = "断线重连·房主掉线(M11)"
    else:
        mode = "普通联机"

    exe = args.exe
    if not os.path.exists(exe):
        print("[错误] 找不到构建产物: %s" % rel(exe))
        print("       请先构建，例如：")
        print("       Unity.exe -batchmode -nographics -quit -projectPath StarPixelRogue \\")
        print("         -executeMethod StarPixel.EditorTools.BuildScript.HeadlessNetBuild")
        return 2

    # 用单机产物跑联机冒烟会得到一个极具误导性的结果：双端都正常启动、跑满时长、异常 0，
    # 只是网络玩家数恒为 0、同步波次恒为 -1——看起来像同步链路坏了，其实是没测联机版本。
    # 宁可在这里直接拒绝，也不要让这种假故障浪费一轮排查。
    if os.path.normcase(os.path.abspath(os.path.dirname(exe))) == \
            os.path.normcase(os.path.abspath(os.path.dirname(SOLO_EXE))):
        print("[错误] 这是单机产物（Build/Smoke），不含联机装配，跑联机冒烟只会得到假故障。")
        print("       联机产物在 %s。" % rel(NET_EXE))
        print("       请先构建：-executeMethod StarPixel.EditorTools.BuildScript.HeadlessNetBuild")
        return 2

    host_log = os.path.join(ROOT, "net_host.log")
    client_log = os.path.join(ROOT, "net_client.log")
    for p in (host_log, client_log):
        if os.path.exists(p):
            os.remove(p)

    # M36：客户端时长改成「让双端在同一时刻收尾」。
    #
    # 旧算法是 seconds - start_delay - 3（刻意让客户端早 3s 收尾），理由是"客户端收尾时
    # 房主必须还活着"。但代价是**两端采样落在了不同瞬间**：后期一次 AoE 清几十只，
    # 击杀速率可达 18/s，3 秒差就是 54 的绝对差。实测 60s 局：房主波次 7/击杀 140，
    # 客户端波次 6/击杀 86——看着像"同步落后"，实际只是采样早了 3 秒。
    #
    # 更糟的是它逼着判据放水：波次容差放到 25%、击杀容差放到 40%、玩家数改看峰值，
    # 于是"同步真的在中途断了但客户端仍收到 65% 的击杀"这种缺陷会被判成通过。
    #
    # 对齐后实测（两次）：波次 7==7、击杀 114==114 与 109==109、场上敌人 15==15、
    # 玩家数 2==2——全部严格相等，证明同步链路本身是干净的，差异纯粹是采样时刻差。
    # 因此默认对齐；--no-align 保留旧行为，只用于 M36 区分力对照（证明放水的判据有多松）。
    client_seconds = max(10, args.seconds - args.start_delay - 3)

    print("=" * 68)
    print("联机双开实测（M18）· 模式：%s" % mode)
    print("  产物   %s" % rel(exe))
    print("  房主   %ds   客户端 %ds（延后 %ds 启动）" % (args.seconds, client_seconds, args.start_delay))
    if args.spectator:
        print("  观战   客户端以观战身份加入，自身玩家对象应由房主回收")
    if args.drop_at > 0:
        print("  掉线   客户端在第 %.0fs 模拟意外掉线（先拆本地镜像，重连后重建）" % args.drop_at)
    if args.host_drop_at > 0:
        print("  掉线   房主在第 %.0fs 模拟意外掉线（服务端仍在，权威世界保留不拆）" % args.host_drop_at)
    print("=" * 68)

    # 1) 房主
    host_args = ["-batchmode", "-nographics", "-netHost", "-smokeSeconds", str(args.seconds)]
    if args.host_drop_at > 0:
        host_args += ["-netHostDropAt", str(args.host_drop_at)]
    host = launch(exe, host_args, host_log)
    if host is None:
        return 2
    print("[1/3] 房主已启动，等待服务端就绪（最多 %ds）…" % args.start_delay)
    waited = wait_host_ready(host_log, args.start_delay)
    print("       服务端就绪探测耗时 %.0fs" % waited)

    # 客户端在房主时间轴上的收尾时刻 = waited（延后启动）+ client_seconds，
    # 房主的收尾时刻 = args.seconds。令两者相等即可让采样落在同一瞬间。
    if not args.no_align:
        old = client_seconds
        client_seconds = max(10, args.seconds - int(round(waited)))
        print("       双端收尾对齐：客户端时长 %ds → %ds（房主已跑 %.0fs）"
              % (old, client_seconds, waited))

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
        ("等级/权威等级", _lvl_pair(host_data), _lvl_pair(client_data)),
        ("网络玩家数", host_data.get("网络玩家数", "?"), client_data.get("网络玩家数", "?")),
        # M37：判据取这一条（收尾前 1 秒）；上面那条是收尾瞬时值，受退出竞态影响
        ("收尾前玩家数", host_data.get("收尾前玩家数", "?"), client_data.get("收尾前玩家数", "?")),
        ("网络玩家峰值", host_data.get("网络玩家峰值", "?"), client_data.get("网络玩家峰值", "?")),
        ("同步波次", host_data.get("同步波次", "?"), client_data.get("同步波次", "?")),
        ("同步击杀", host_data.get("同步击杀", "?"), client_data.get("同步击杀", "?")),
        ("场上敌人", host_data.get("场上敌人", "?"), client_data.get("场上敌人", "?")),
        # M37：升级推卡收支。房主推几批 ↔ 客户端收到几批，中间的差额就是被吞掉的强化
        ("推卡批次", host_data.get("房主推卡批次", "?"), client_data.get("推卡收到", "?")),
        ("推卡上屏", host_data.get("推卡上屏", "?"), client_data.get("推卡上屏", "?")),
        ("推卡入队（累计）", host_data.get("推卡入队", "?"), client_data.get("推卡入队", "?")),
        ("队列残余（当前）", host_data.get("队列残余", "?"), client_data.get("队列残余", "?")),
        ("推卡丢弃", host_data.get("推卡丢弃", "?"), client_data.get("推卡丢弃", "?")),
        ("批次淘汰", host_data.get("批次淘汰", "?"), client_data.get("批次淘汰", "?")),
        ("升级选卡", host_data.get("升级选卡", "?"), client_data.get("升级选卡", "?")),
        # M38：双端对账。**房主权威应用**在房主侧采、**本地镜像应用**在客户端侧采——
        # 两边必须一致：客户端多一次就是"自己先加了成"，房主多一次就是回执丢了。
        ("房主已应用", host_data.get("房主已应用", "?"), "-"),
        ("本地已应用", "-", client_data.get("本地已应用", "?")),
        ("本地强化次数", "-", client_data.get("本地强化次数", "?")),
        ("收场总数", "-", client_data.get("收场总数", "?")),
        ("通知上屏", "-", client_data.get("通知上屏", "?")),
        ("通知被吞", "-", client_data.get("通知被吞", "?")),
        ("房主已拒绝", host_data.get("房主已拒绝", "?"), "-"),
        ("本地被拒", "-", client_data.get("本地被拒", "?")),
        ("待确认残留", "-", client_data.get("待确认残留", "?")),
        ("面板作废", host_data.get("面板作废", "?"), client_data.get("面板作废", "?")),
        ("遇到Boss", host_data.get("遇到Boss", "?"), client_data.get("遇到Boss", "?")),
        ("运行时长", host_data.get("时长", "?"), client_data.get("时长", "?")),
    ]

    # M14 观战：观战侧特有指标
    if args.spectator:
        rows.append(("自身角色已回收", host_data.get("自身角色已回收", "-"),
                     client_data.get("自身角色已回收", "?")))

    # M11 断线重连：掉线前 / 掉线期间 / 重连后各采一次样，比对进度是否回退
    if args.drop_at > 0 or args.host_drop_at > 0:
        # 掉线发生在哪一端，指标就从哪一端取；另一端显示 "-"
        d, o = (host_data, "-") if args.host_drop_at > 0 else (client_data, "-")
        rows += [
            ("断线时刻", d.get("断线时刻", "?"), o),
            ("重连完成", d.get("重连完成", "?"), o),
            ("重连耗时", d.get("重连耗时", "?"), o),
            ("断线前波次", d.get("断线前波次", "?"), o),
            ("断线期间波次", d.get("断线期间波次", "?"), o),
            ("断线期间敌人", d.get("断线期间敌人", "?"), o),
            ("断线期间击杀", d.get("断线期间击杀", "?"), o),
            ("断线期间世界存活", d.get("断线期间世界存活", "?"), o),
            ("重连后波次", d.get("重连后波次", "?"), o),
            ("断线前等级", d.get("断线前等级", "?"), o),
            ("重连后等级", d.get("重连后等级", "?"), o),
            ("断线前击杀", d.get("断线前击杀", "?"), o),
            ("重连后击杀", d.get("重连后击杀", "?"), o),
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

    # M36：对齐收尾后双端采样落在同一瞬间，判据可以（也必须）收紧到接近严格相等。
    # 旧判据因为采样天然差 3s，被迫放水成「波次容差 25% / 击杀容差 40% / 玩家数只看峰值」，
    # 于是"同步中途断了但客户端仍收到 65% 击杀"这种真缺陷会被判成通过。
    aligned = not args.no_align

    # —— 等级输出源 ——
    # 联机下「等级」必须等于权威等级。M36 实测：客户端真实等级 3 却被输出成 1，
    # 因为收尾读了不参与 XP 结算的 PlayerController.level。两个数不等 == 读错了来源。
    for who, d in (("房主", host_data), ("客户端", client_data)):
        lv, auth = d.get("等级"), d.get("权威等级")
        if auth is None:          # 单机产物没有这一行（在 #if FISHNET_ENABLED 里）
            continue
        if as_int(d, "等级") != as_int(d, "权威等级"):
            problems.append(
                "M36：%s的等级 %s 与权威等级 %s 不一致——收尾读了错误来源"
                "（联机应读 NetPlayer 的等级 SyncVar，PlayerController.level 不参与 XP 结算）"
                % (who, lv, auth))

    # —— 玩家数 ——
    # 对齐后两端都在对端还活着时收尾，瞬时值可以直接判；否则仍看峰值。
    if aligned:
        # M37：改用「收尾前 1 秒」的采样，而不是收尾瞬时值。
        # 两个进程的结束时刻对齐到同一秒，谁先退出谁就把对方的注册表打到 1——
        # 同一份源码跑两次，一次「房主 2 / 客户端 2」、一次「房主 1 / 客户端 2」。
        # 那是测试台的退出竞态，不是产品属性；拿它当判据会把成功的联机判成失败，
        # 反过来又逼着判据放水（这正是 M36 之前被迫放水的同一类原因）。
        # 收尾前 1 秒两端必然都还活着，取那一刻才是稳的；瞬时值仍输出供诊断。
        h_players = as_int(host_data, "收尾前玩家数", as_int(host_data, "网络玩家数"))
        c_players = as_int(client_data, "收尾前玩家数", as_int(client_data, "网络玩家数"))
        want = 1 if args.spectator else 2
        if h_players != want:
            problems.append("房主收尾前看到 %d 名玩家（期望 %d）" % (h_players, want))
        if c_players != want:
            problems.append("客户端收尾前看到 %d 名玩家（期望 %d）" % (c_players, want))
    else:
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

    if aligned:
        # 波次是离散事件、几秒才推进一次，两端同时刻采样就该读到同一个值。
        wave_tol = 0
    else:
        # 波次：客户端比房主早收尾（见 client_seconds 注释），采样时刻天然靠前，
        # 而后期一次清场就能推进一波，落后 1~2 波属正常网络采样差。
        # 判据要抓的是"客户端波次停在初始值不动"（同步根本没通），那种情况差值会很大。
        wave_tol = max(2, int(h_wave * 0.25))
    if abs(h_wave - c_wave) > wave_tol:
        problems.append("双端波次不一致：房主 %d vs 客户端 %d（容差 %d）" % (h_wave, c_wave, wave_tol))

    if h_kills > 0:
        if c_kills > h_kills:
            problems.append("客户端击杀 %d 超过房主权威值 %d——同步逻辑可疑" % (c_kills, h_kills))
        if aligned:
            # 同时刻采样，击杀就该是同一个数。留 2% + 至少 2 的余量：
            # 两端收尾不可能精确到同一帧，房主在最后 100ms 里还能再杀几只。
            kill_tol = max(2, int(h_kills * 0.02))
            if h_kills - c_kills > kill_tol:
                problems.append(
                    "M36：双端同时刻采样击杀却相差 %d（房主 %d / 客户端 %d，容差 %d）"
                    "——同步链路在中途落后或停止" % (h_kills - c_kills, h_kills, c_kills, kill_tol))
        else:
            # 击杀：不要求瞬时值相等，改判"客户端是否单调跟随房主权威值"。
            # 客户端读的是广播过来的 SyncVar，采样必然早于房主终值；后期一次 AoE 清几十只，
            # 击杀速率可达每秒数十，几秒采样差就是几十上百的绝对差（实测第 15 波 458 / 419）。
            # 因此只拦三类真问题：收到 0（没同步）、超过权威值（逻辑错）、落后过半（同步中途断了）。
            if c_kills < h_kills * 0.6:
                problems.append("客户端击杀 %d 明显落后于房主 %d——同步可能中途停止" % (c_kills, h_kills))

    # —— M37 升级推卡收支 ——
    # 客户端是唯一接收方：房主推几批，客户端就该收到几批。
    # 光看"客户端选了几次卡"看不出问题——M37 之前那个统计方式本身就是坏的
    # （只在 timeScale<=0 时统计，而外部推卡刻意不暂停，于是客户端恒为 0 次）。
    # 必须把「收到 / 上屏 / 入队 / 丢弃 / 完成」拆开对账：收到 ≠ 上屏 + 入队 就是被吞了。
    h_offered = as_int(host_data, "房主推卡批次", -1)
    c_recv = as_int(client_data, "推卡收到", -1)
    c_pres = as_int(client_data, "推卡上屏", -1)
    c_queue = as_int(client_data, "推卡入队", -1)
    c_drop = as_int(client_data, "推卡丢弃", -1)
    c_picked = as_int(client_data, "升级选卡", -1)
    h_evict = as_int(host_data, "批次淘汰", -1)

    if c_drop > 0:
        problems.append(
            "M37：客户端丢弃了 %d 组推卡——面板占用期间推来的卡被扔掉，"
            "玩家这次升级永久拿不到强化，房主也永远收不到选择回传" % c_drop)

    # 用**队列残余**而不是累计入队：入过队的那批最终也会上屏，用累计值会把它算两遍。
    # （M37 原判据用的就是累计入队，一直绿只是因为那时联机场景队列没积压、入队恒为 0；
    #  客户端一旦真的卡住两张，等式立刻不成立——那是判据盲区，不是产品回归。）
    c_pending_queue = as_int(client_data, "队列残余", -1)
    if (c_recv >= 0 and c_pres >= 0 and c_pending_queue >= 0
            and c_recv != c_pres + c_pending_queue):
        problems.append(
            "M37：客户端推卡收支不平：收到 %d ≠ 上屏 %d + 队列残余 %d——差额就是被吞掉的强化"
            % (c_recv, c_pres, c_queue))

    if h_evict > 0:
        problems.append(
            "M37：房主侧有 %d 个供选批次被上限淘汰——客户端长时间不选卡，或选择回传链路断了"
            % h_evict)

    # 房主推了就该收到。留 1 的余量：客户端可能比房主早半拍收尾，最后一批来不及送达。
    if h_offered >= 1 and c_recv < 1:
        problems.append(
            "M37：房主为远程玩家推了 %d 批卡，客户端一组都没收到——TargetRpc 推卡链路未通"
            % h_offered)
    elif aligned and h_offered >= 1 and c_recv >= 0 and c_recv < h_offered - 1:
        problems.append(
            "M37：房主推了 %d 批、客户端只收到 %d 批——推卡在中途丢失" % (h_offered, c_recv))

    # 上屏的卡必须被选掉（收尾那一组可能来不及点，留 1 的余量）
    if aligned and c_pres >= 0 and c_picked >= 0 and c_picked < c_pres - 1:
        problems.append(
            "M37：客户端上屏了 %d 组卡却只完成 %d 次选卡——面板弹出后没人/没法选，"
            "游戏会卡在选卡界面里" % (c_pres, c_picked))

    # —— M38 双端对账：本地镜像必须与房主权威逐次相等 ——
    # 这是「先斩后奏」唯一的直接证据：修复前客户端在面板回调里**无条件**先把本地权威数据
    # （移速 / 磁吸范围 / 生命上限）加上，房主那边一旦拒绝（批次淘汰 / 越界 / 重放），
    # 客户端就比房主多一次强化，而没有任何日志——表现出来只是"客户端好像跑得快一点"。
    #
    # 为什么用不等式而不是判相等：两侧都在**收尾前 1 秒**各采各的，允许有一个回执还在路上。
    # 在途的那笔在客户端侧是"待确认残留"，所以上界要把它算进去——这样既不漏判也不假红。
    h_applied = as_int(host_data, "房主已应用", -1)
    c_local = as_int(client_data, "本地已应用", -1)
    c_pending = as_int(client_data, "待确认残留", -1)
    h_rejected = as_int(host_data, "房主已拒绝", -1)
    c_rejected = as_int(client_data, "本地被拒", -1)

    if aligned and h_applied >= 0 and c_local >= 0:
        # 最硬的一条：本机**每一次**应用都必须来自一次确认。
        # 只比"确认数 vs 房主应用数"抓不到先斩后奏——那种写法是在确认之外又加了一次，
        # 确认计数照样对得上，只有"实际应用次数"多出来的那几个才无处可藏。
        c_apply_total = as_int(client_data, "本地强化次数", -1)
        if c_apply_total >= 0 and c_local >= 0 and c_apply_total > c_local:
            problems.append(
                "M38：本机实际应用了 %d 次强化，其中只有 %d 次来自房主确认——"
                "多出的 %d 次是客户端自己加给自己的（先斩后奏），双端权威数据已分叉。"
                "这类分叉不报错、不崩，表现出来只是「客户端好像跑得快一点」"
                % (c_apply_total, c_local, c_apply_total - c_local))
        if c_local > h_applied:
            problems.append(
                "M38：客户端本地应用了 %d 次强化，房主只权威应用了 %d 次——"
                "客户端把强化先加给了自己（先斩后奏），双端权威数据已经分叉。"
                "修复前就是这么静默发生的：房主拒绝得越干净，客户端比房主多越多"
                % (c_local, h_applied))
        slack = max(0, c_pending) if c_pending >= 0 else 1
        if h_applied > c_local + slack:
            problems.append(
                "M38：房主权威应用了 %d 次，客户端只应用了 %d 次（在途 %d）——"
                "确认回执在链路上丢了，玩家选了卡却没拿到强化" % (h_applied, c_local, c_pending))
        if c_pending > 2:
            problems.append(
                "M38：收尾时客户端还压着 %d 笔选择没等到回执——房主不回或回执丢了，"
                "这些选择既没生效也没被拒" % c_pending)
        if c_rejected > h_rejected:
            problems.append(
                "M38：客户端自述被拒 %d 次，房主只拒绝过 %d 次——有选择被客户端单方面判死"
                % (c_rejected, h_rejected))

    # —— M39：收场必须看得见 ——
    #
    # 为什么 M38 那几条之外还要单独这一组：M38 保证的是「每一次应用都来自确认」，
    # 即**数据正确**；但玩家看到的是另一回事——确认/拒绝/作废/超时这四种收场
    # 在修复前全都只进日志。M37「宁可少发一次」、M38「宁可等确认」都是在主动
    # 牺牲成功率换正确性，牺牲得对，可代价必须让玩家知道：否则他只会认为游戏
    # 吞了他的升级，而且无从报告（他连发生了什么都不知道）。**静默的收场等于没发生。**
    c_nt_total = as_int(client_data, "收场总数", -1)
    c_nt_shown = as_int(client_data, "通知上屏", -1)
    c_nt_sup = as_int(client_data, "通知被吞", -1)
    c_picked = as_int(client_data, "升级选卡", -1)

    if aligned and c_nt_total >= 0:
        if c_nt_sup > 0:
            problems.append(
                "M39：有 %d 条收场通知没能上屏（共 %d 条收场、只上屏 %d 条）——"
                "玩家点了卡却什么都没看到：生效了不知道生效，没生效也不知道为什么。"
                "HUD 出口缺失时通知只会进日志，等于这次收场对玩家不存在"
                % (c_nt_sup, c_nt_total, c_nt_shown))
        if c_nt_shown >= 0 and c_nt_shown != c_nt_total:
            problems.append(
                "M39：收场 %d 条、上屏 %d 条——两者必须相等，"
                "少一条就说明有一次收场没走到玩家眼前" % (c_nt_total, c_nt_shown))
        # 守恒：四类收场各出一条，加起来必须等于总数
        parts = 0
        for k in ("本地已应用", "本地被拒", "本地作废", "回执超时"):
            v = as_int(client_data, k, -1)
            if v > 0:
                parts += v
        if parts != c_nt_total:
            problems.append(
                "M39：收场通知 %d 条，但四类收场之和只有 %d（确认/拒绝/作废/超时）——"
                "有收场根本没进通知链路，玩家看不到它发生过" % (c_nt_total, parts))
        if c_picked > 0 and c_nt_total == 0:
            problems.append(
                "M39：客户端选了 %d 次卡，却没有产生任何一条收场通知——"
                "通知链路整体空转，选卡的成败对玩家全是静默的" % c_picked)

    # —— M11 断线重连专项判定 ——
    # 三层证据缺一不可：客户端自述重连成功 + 进度没回退 + 房主侧真的观测到「2→1→2」。
    # 只信客户端自述会被"根本没掉线也算通过"糊弄过去；只看房主则无法确认进度重放。
    if args.drop_at > 0 or args.host_drop_at > 0:
        # 掉线端 / 对端：房主掉线时掉线端是房主，反之是客户端
        d = host_data if args.host_drop_at > 0 else client_data
        who = "房主" if args.host_drop_at > 0 else "客户端"

        if d.get("重连完成") != "是":
            problems.append("M11：%s在模拟掉线后未重连成功（%s）" % (who, d.get("重连完成")))

        pre_w, post_w = as_int(d, "断线前波次"), as_int(d, "重连后波次")
        pre_l, post_l = as_int(d, "断线前等级"), as_int(d, "重连后等级")
        pre_k, post_k = as_int(d, "断线前击杀"), as_int(d, "重连后击杀")
        mid_w = as_int(d, "断线期间波次")

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
            problems.append("M11：断线时等级仅为 %d，进度重放判据无区分力——请调大 --%s 先升几级"
                            % (pre_l, "host-drop-at" if args.host_drop_at > 0 else "drop-at"))

        if pre_k < 0 or post_k < 0:
            problems.append("M11：掉线前后击杀采样缺失（前 %d / 后 %d）" % (pre_k, post_k))
        elif post_k < pre_k:
            problems.append("M11：重连后击杀 %d 低于断线前 %d——世界状态回退" % (post_k, pre_k))

        if args.host_drop_at > 0:
            # 房主分支的核心判据：权威世界必须保留（不调 TeardownWorld）。
            # 判据用「EnemyManager 是否仍在」，**不能**用「场上敌人是否 > 0」——
            # 房主本地客户端断开时 Fish-Net 会清空本地网络视图（NetEnemy 被销毁），
            # 敌人数归零是预期行为，与服务端的权威世界是否存活无关。
            # 一旦误走 TeardownWorld：EnemyManager 被销毁、波次重置回 1、重连后等级也会掉回 1。
            if d.get("断线期间世界存活") != "是":
                problems.append("M11：房主掉线期间权威世界管理器已消失——世界被拆掉了（房主分支本不该拆）")
            if mid_w < 0:
                problems.append("M11：掉线期间波次采样缺失（%d）" % mid_w)
            elif mid_w < pre_w:
                problems.append("M11：房主掉线期间波次 %d 低于断线前 %d——权威世界回退" % (mid_w, pre_w))
            if 0 <= as_int(d, "断线期间击杀") < pre_k:
                problems.append("M11：房主掉线期间击杀 %d 低于断线前 %d——权威世界回退"
                                % (as_int(d, "断线期间击杀"), pre_k))
            # 对端（客户端）挂在同一个服务端上，房主本地客户端掉线不应波及它
            if client_data.get("会话角色") != "客户端":
                problems.append("M11：房主掉线后对端客户端不再是「客户端」（%s）——服务端疑似一并停了"
                                % client_data.get("会话角色"))
            if as_int(client_data, "同步击杀") <= 0:
                problems.append("M11：房主掉线后对端客户端同步击杀为 %d——对端被波及"
                                % as_int(client_data, "同步击杀"))
        else:
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
    elif args.host_drop_at > 0:
        print("结论：通过 —— 房主掉线后自动重连成功，权威世界全程保留（敌人/波次未归零）且对端未受波及")
    else:
        print("结论：通过 —— 双端玩家数/波次/击杀一致，房主权威同步链路工作正常")
    print("-" * 48)
    return 0


if __name__ == "__main__":
    sys.exit(main())
