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
import time

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
GROWTH_JUDGES = (
    "波次确实推进",
    "宝石有界",
    "回血球有界",
    "上限真的生效",
    "账本与场景一致",
    "末段贴顶后不再抬升",
    "玩家遍历不随掉落物增长",
    "长程数值不越界",
    "全程零异常",
)

# M43：重开局场景。判据完全不同——它问的不是"有没有堆积"，而是"拆干净了吗、重建起来了吗"。
RESTART_JUDGES = (
    # 拆完瞬间的四条：唯一能精确回答"旧实体有没有被反生成"的时点。
    # 弹丸（M44）与那三类分开判：它没有账本也没有注册表，只能靠按类型扫场才找得到，
    # 摘掉它的那一行和摘掉别人的那一行是两种不同的漏法，报同一个键就分不清了。
    "拆完立刻无残留敌人",
    "拆完立刻无残留掉落物",
    "拆完立刻无残留世界状态",
    "拆完立刻无残留弹丸",
    "重开后弹丸清零",
    # M45：反向判据——不该清的有没有被顺手清掉。前面几条都问"该清的清没清"，
    # 这两条问玩家还在不在。4 人局里掉一个人时世界照样推进，只有这两条看得见。
    "拆完玩家未被误清",
    "重开后玩家齐全",
    # 重建之后：新世界是否健康、客户端是否真的收到了广播
    "重开后波次重置",
    "重开后掉落物清零",
    "重开后世界状态唯一",
    "重开后玩家可控",
    "收到重开广播",
    "全程零异常",
)

PROBE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(.+?)\s*$")


def probe_results(path, judges):
    """抽出探针判据 -> 是/否/不适用，以及信息行。"""
    judges_map, info = {}, {}
    if not os.path.exists(path):
        return judges_map, info
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = PROBE_RE.search(line)
            if not m:
                continue
            k, v = m.group(1).strip(), m.group(2).strip()
            if k in judges:
                judges_map[k] = v
            else:
                info[k] = v
    return judges_map, info


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


def first_int(info, key, default=-1):
    """'0 宝石 / 0 回血球' → 0；'1 → 1' → 1；解析不出时返回 default。"""
    v = str(info.get(key, ""))
    m = re.search(r"(-?\d+)", v)
    return int(m.group(1)) if m else default


def last_int(info, key, default=-1):
    """'1 → 1' → 1（取箭头后的那个）；解析不出时返回 default。"""
    v = str(info.get(key, ""))
    nums = re.findall(r"-?\d+", v)
    return int(nums[-1]) if nums else default


def main():
    ap = argparse.ArgumentParser(description="联机长局 / 重开局实测（房主 + 客户端双开）")
    ap.add_argument("--scene", default="GROWTH", choices=("GROWTH", "RESTART"),
                    help="GROWTH=长局堆积；RESTART=长局后重开一局（验证反生成与重建）")
    ap.add_argument("--seconds", type=int, default=300, help="房主运行时长（真实秒）")
    ap.add_argument("--speed", type=float, default=6.0,
                    help="加速倍率（只作用于房主；客户端保持 1x 跟随权威同步）")
    ap.add_argument("--address", default="127.0.0.1", help="客户端连接地址")
    ap.add_argument("--clients", type=int, default=1,
                    help="客户端数量（默认 1 = 2 人局；3 = 4 人局）。"
                         "M45：拆世界是全项目唯一一次批量反生成，人多一个就多一份"
                         "「有没有收到广播」的证据，也多一个会被漏掉的受害者")
    ap.add_argument("--stagger", type=float, default=3.0,
                    help="多客户端之间的启动间隔（秒），避免同时连接把房主的"
                         "就绪窗口挤掉")
    ap.add_argument("--start-delay", type=int, default=20, help="房主先启动的等待秒数")
    ap.add_argument("--exe", default=NET_EXE, help="构建产物路径")
    ap.add_argument("--log-limit-mb", type=int, default=250,
                    help="单端日志体积上限（MB），超过即熔断")
    args = ap.parse_args()

    judges = GROWTH_JUDGES if args.scene == "GROWTH" else RESTART_JUDGES

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

    n_clients = max(1, args.clients)
    host_log = os.path.join(ROOT, "netlongrun_host.log")
    # N==1 时沿用旧名字，m42/m43/m44 的注入脚本都按这个名字读日志。
    client_logs = [os.path.join(ROOT, "netlongrun_client.log")] if n_clients == 1 else [
        os.path.join(ROOT, "netlongrun_c%d.log" % (i + 1)) for i in range(n_clients)]
    for p in [host_log] + client_logs:
        if os.path.exists(p):
            os.remove(p)

    print("=" * 68)
    print("联机%s实测（M45）" % ("长局稳定性" if args.scene == "GROWTH" else "重开局"))
    print("  产物   %s" % rel(exe))
    print("  场景   %s" % args.scene)
    print("  人数   %d 人局（1 房主 + %d 客户端）" % (n_clients + 1, n_clients))
    print("  房主   %ds（%sx 加速 + 保活）   客户端 1x 跟随" % (args.seconds, args.speed))
    print("  预期   游戏内约 %ds 的权威世界演化" % int(args.seconds * args.speed))
    print("=" * 68)

    # 1) 房主：加速 + 保活 + 长局探针
    host_args = ["-batchmode", "-nographics", "-netHost",
                 "-smokeSeconds", str(args.seconds),
                 "-smokeSpeed", str(args.speed),
                 "-smokeImmortal",
                 "-longrunProbe",
                 "-longrunScene", args.scene]
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
    clients = []
    for i, clog in enumerate(client_logs):
        if i > 0:
            # 错开启动：同时发起 N 个连接时，Fish-Net 的服务端在一个 tick 里
            # 处理不完，会出现"某个客户端连上但没生成玩家"的假故障，
            # 而它看起来跟"同步坏了"一模一样。
            time.sleep(args.stagger)
            client_seconds = max(10, args.seconds - int(round(waited)) - int(args.stagger * i))
        client_args = ["-batchmode", "-nographics", "-netClient",
                       "-netAddress", args.address,
                       "-smokeSeconds", str(client_seconds),
                       "-longrunProbe",
                       "-longrunScene", args.scene]
        c = launch(exe, client_args, clog)
        if c is None:
            host.kill()
            for old in clients:
                old.kill()
            return 2
        clients.append(c)
        print("[2/3] 客户端 %d/%d 已启动（%ds，与房主同时刻收尾），连接 %s"
              % (i + 1, n_clients, client_seconds, args.address))

    # 3) 等待所有端结束
    budget = args.seconds + 120
    wait_for(host, "房主", budget, watch_log=host_log, limit_mb=args.log_limit_mb)
    for i, c in enumerate(clients):
        wait_for(c, "客户端%d" % (i + 1), budget,
                 watch_log=client_logs[i], limit_mb=args.log_limit_mb)
    print("[3/3] 全部 %d 端已结束，开始交叉核对\n" % (n_clients + 1))

    h_judges, h_info = probe_results(host_log, judges)
    c_all = [probe_results(p, judges) for p in client_logs]

    if not h_judges:
        print("[失败] 房主日志里没有探针判据输出，无法核对（日志: %s）" % rel(host_log))
        return 1
    for i, (cj, _) in enumerate(c_all):
        if not cj:
            print("[失败] 客户端 %d 日志里没有探针判据输出，无法核对（日志: %s）"
                  % (i + 1, rel(client_logs[i])))
            return 1

    h_exc = count_exceptions(host_log)
    c_exc = sum(count_exceptions(p) for p in client_logs)

    width = 12 if n_clients > 1 else 16

    def multi(label, vals):
        """打印一行：第一列 16 宽，其余每列 width 宽（%-*s 需要宽度与值成对传入）。

        超宽的值必须截断：'8103 宝石 / 1076 回血球' 这种有 20 个字符，
        %-*s 只保证最小宽度、不会截断，多端时会把后面几列整体挤开，
        看起来像"某一端的值丢了"。宁可截断成 '8103 宝石 / 107…' 也要保住列对齐。
        """
        args = [label]
        for v in vals:
            s = str(v)
            if len(s) > width:
                s = s[:width - 1] + "…"
            args += [width, s]
        print(("%-16s" + "%-*s" * len(vals)) % tuple(args))

    multi("判据", ["房主"] + ["客户端%d" % (i + 1) for i in range(n_clients)])
    print("-" * (16 + width * (n_clients + 1)))
    for k in judges:
        multi(k, [h_judges.get(k, "?")] + [cj.get(k, "?") for cj, _ in c_all])
    print("-" * (16 + width * (n_clients + 1)))

    def info_row(label, fn):
        """打一行：房主 + 每个客户端各一列（读的是各端自己的信息行）。"""
        multi(label, [fn(h_info)] + [fn(ci) for _, ci in c_all])

    if args.scene == "GROWTH":
        info_row("宝石峰值", lambda i: peak_of(i, "宝石峰值"))
        info_row("回血球峰值", lambda i: as_int(i, "回血球峰值"))
        info_row("最终波次", lambda i: as_int(i, "最终波次"))
        info_row("回收次数", lambda i: i.get("回收次数", "?"))
        info_row("末次账本", lambda i: i.get("末次账本", "?"))
        info_row("快照/帧数", lambda i: i.get("快照重建/帧数", "?"))
        info_row("场上玩家", lambda i: i.get("场上玩家", "?"))
    else:
        info_row("重开前波次", lambda i: as_int(i, "重开前波次"))
        info_row("重开后波次", lambda i: as_int(i, "重开后波次"))
        info_row("重开前宝石", lambda i: first_int(i, "重开前宝石"))
        info_row("重开后宝石", lambda i: first_int(i, "重开后宝石"))
        info_row("重开前敌人", lambda i: as_int(i, "重开前敌人"))
        info_row("重开后敌人", lambda i: as_int(i, "重开后敌人"))
        info_row("重开前弹丸", lambda i: as_int(i, "重开前弹丸"))
        info_row("重开后弹丸", lambda i: as_int(i, "重开后弹丸"))
        info_row("重开前玩家", lambda i: as_int(i, "重开前玩家"))
        info_row("重开后玩家", lambda i: as_int(i, "重开后玩家"))
        info_row("世界状态数", lambda i: i.get("世界状态数", "?"))
        info_row("反生成数", lambda i: i.get("反生成数", "?"))
    info_row("托管堆MB", lambda i: i.get("托管堆MB", "?"))
    info_row("平均帧率", lambda i: i.get("平均帧率", "?"))
    print("-" * (16 + width * (n_clients + 1)))
    print("运行时异常：房主 %d 条 / 客户端合计 %d 条（%s）"
          % (h_exc, c_exc, "、".join(str(count_exceptions(p)) for p in client_logs)))

    problems = []

    # —— 每一端：探针自己的判据必须全过（"不适用"表示该判据在本端不成立，不算失败） ——
    for k in judges:
        for side, got in [("房主", h_judges)] + \
                [("客户端%d" % (i + 1), cj) for i, (cj, _) in enumerate(c_all)]:
            v = got.get(k)
            if v != "是" and v != "不适用":
                problems.append("%s侧判据「%s」= %s" % (side, k, v))

    # —— M45：人数是多人局的成色前提 ——
    #
    # 少一个人时，其余所有判据（掉落物有界 / 波次推进 / 双端一致）都是绿的：
    # 世界照样转，只是少了个玩家。**必须**先确认人齐了，否则整轮都是假通过。
    expect_players = n_clients + 1
    pkey = "场上玩家" if args.scene == "GROWTH" else "重开后玩家"
    for i, ci in enumerate([h_info] + [x for _, x in c_all]):
        side = "房主" if i == 0 else "客户端%d" % i
        got = first_int(ci, pkey, -1) if args.scene == "GROWTH" else as_int(ci, pkey)
        if got >= 0 and got != expect_players:
            problems.append(
                "%s侧%s = %d（%d 人局期望 %d）——有人没连上或被踢了；"
                "少人时世界照样推进，其余判据会一路假绿，所以这条必须单独判"
                % (side, pkey, got, expect_players, expect_players))

    if args.scene == "GROWTH":
        # —— 交叉核对（联机独有的失败面）——
        #
        # M45：逐个客户端核对，不取"任一/平均"。多人局的意义正在于
        # **每一个**端点都要独立成立——只核对一个客户端时，另外两个即便
        # 堆了僵尸对象也无人过问，成功率反而随人数上升（1/3 的人被漏掉也不影响结论）。
        h_gem = peak_of(h_info, "宝石峰值")
        h_hp = as_int(h_info, "回血球峰值")
        h_wave = as_int(h_info, "最终波次")

        for idx, (_, ci) in enumerate(c_all):
            tag = "客户端%d" % (idx + 1)
            c_gem = peak_of(ci, "宝石峰值")
            c_hp = as_int(ci, "回血球峰值")
            c_wave = as_int(ci, "最终波次")

            # 1) 宝石计数一致。容差取 max(6, 8%)：两端收尾不可能精确到同一帧，
            #    最后半秒里房主还能再掉一批、广播还在路上。
            #
            #    **必须双向**：只查"客户端 > 房主"会漏掉相反的那个面——客户端自行
            #    Object.Destroy 联机对象（静态检查第 20 项要抓的写法）时客户端反而**偏少**，
            #    单向断言下这一路会一路绿灯地溜过去。
            if h_gem >= 0 and c_gem >= 0:
                tol = max(6, int(h_gem * 0.08))
                if abs(c_gem - h_gem) > tol:
                    why = ("该端视图在房主不知情的情况下堆积僵尸对象——回收若走本地 "
                           "Object.Destroy 而非房主权威 Despawn，副本就再也收不到反生成广播"
                           if c_gem > h_gem else
                           "该端偏少——它在自行销毁联机对象（房主权威外的本地 Destroy），"
                           "静查第 20 项正是要拦这种写法")
                    problems.append(
                        "%s 宝石计数与房主不一致：%d vs 房主 %d（容差 %d）——%s"
                        % (tag, c_gem, h_gem, tol, why))

            # 2) 回血球同理
            if h_hp >= 0 and c_hp >= 0:
                tol = max(4, int(h_hp * 0.15))
                if abs(c_hp - h_hp) > tol:
                    problems.append(
                        "%s 回血球计数与房主不一致：%d vs 房主 %d（容差 %d）——"
                        "同上：该端视图与房主权威世界分叉" % (tag, c_hp, h_hp, tol))

            # 3) 客户端必须真的收到了掉落物同步。恒为 0 说明同步链路断了，
            #    此时"双端一致"会平凡成立——必须单独拦住。
            if c_gem == 0 and h_gem > 20:
                problems.append(
                    "%s 宝石峰值恒为 0（房主侧 %d）——掉落物压根没同步到这一端，"
                    "\"计数一致\"在这种情形下会平凡成立，必须单独判失败" % (tag, h_gem))

            # 4) 波次：客户端读的是同步下来的权威值，同时刻采样就该相等
            if h_wave >= 1 and c_wave >= 1 and abs(h_wave - c_wave) > 1:
                problems.append("%s 波次与房主不一致：%d vs 房主 %d" % (tag, c_wave, h_wave))

        # 5) 世界真的在动（否则上面全部判据都是假绿）
        if h_wave < 12:
            problems.append(
                "房主侧波次只到 %d（期望 ≥12）——加速可能没生效或权威世界没跑起来，"
                "此时所有增长类判据都会假绿" % h_wave)

        verdict = ("%d 人局掉落物有界、上限真的生效、每端计数与房主一致、全程零异常"
                   % (n_clients + 1))
    else:
        # —— 重开局的多端核对 ——
        # 每端的单端判据已经覆盖了"清没清干净"，这里只查每端各自的判据管不到的事。
        # M45：逐端核对（理由同 GROWTH——只核对一个客户端时，另外两个漏了也没人知道）。
        h_wave_b = as_int(h_info, "重开前波次")
        h_wave_a = as_int(h_info, "重开后波次")
        h_gem_a = first_int(h_info, "重开后宝石")
        h_state_a = last_int(h_info, "世界状态数")
        h_despawned = as_int(h_info, "反生成数")
        h_proj_b = as_int(h_info, "重开前弹丸")

        # 1) 世界在重开前确实跑起来过。否则"重开后干净"毫无意义（本来就空）。
        if h_wave_b < 5:
            problems.append(
                "重开前房主波次只有 %d（期望 ≥5）——本局压根没跑起来，"
                "\"重开后干净\"在这种情形下是平凡成立，不能算通过" % h_wave_b)

        # 2) 反生成真的发生过。这是唯一能区分"走了 Despawn 广播"与
        #    "本地 Destroy 后场上恰好没人"的证据。
        if h_despawned <= 0:
            problems.append(
                "房主侧反生成数为 %d——拆世界时没有反生成任何联机实体。"
                "本地 Destroy 不会广播，客户端副本会变成僵尸" % h_despawned)

        for idx, (_, ci) in enumerate(c_all):
            tag = "客户端%d" % (idx + 1)
            c_wave_a = as_int(ci, "重开后波次")
            c_gem_a = first_int(ci, "重开后宝石")
            c_state_a = last_int(ci, "世界状态数")

            # 3) 每端重开后都在第 1 波附近。
            #    允许相差 1 波：房主 6x、客户端 1x，两端采样点又不在同一帧，
            #    正好卡在波次边界上就会差一个数。要求精确相等是在测采样抖动，不是测产品。
            if h_wave_a >= 1 and c_wave_a >= 1 and abs(h_wave_a - c_wave_a) > 1:
                problems.append(
                    "%s 重开后波次与房主不一致：%d vs 房主 %d——"
                    "该端没收到世界状态重建（或收到了两份）" % (tag, c_wave_a, h_wave_a))

            # 4) 每端世界状态都只剩 1 个
            if c_state_a != 1:
                problems.append(
                    "%s 重开后世界状态对象数 %d（期望 1）——"
                    "旧的没被反生成时会有两份在抢 Instance，波次会在两个数字之间跳"
                    % (tag, c_state_a))

            # 5) 客户端不得比房主多出"旧世界遗留"。
            #
            #    两端采样时刻不同（房主在重开后 1.6s，客户端在看见波次回落后 1s），
            #    所以**不能**要求精确相等——首跑就是拿 33 vs 17 判了红，那是正常的时间差。
            #    真正要抓的是反方向：房主反生成了、客户端没收到广播，客户端那边会留着
            #    旧的两百多个；而新房主世界只有几十个。差一个数量级，放宽三倍也盖不住。
            if h_gem_a >= 0 and c_gem_a >= 0 and c_gem_a > h_gem_a * 3 + 10:
                problems.append(
                    "%s 重开后宝石远多于房主：%d vs 房主 %d——"
                    "反生成没广播到这一端（或房主走了本地 Destroy），旧的那批留在了这里"
                    % (tag, c_gem_a, h_gem_a))

        if h_state_a != 1:
            problems.append(
                "房主重开后世界状态对象数 %d（期望 1）——"
                "旧的没被反生成时会有两份在抢 Instance，波次会在两个数字之间跳" % h_state_a)

        # 6) M44：弹丸的两道关。
        #
        #    ① 重开前场上有弹丸，否则"拆完无残留"是平凡成立——探针放了一批哨兵弹丸
        #       （长寿命回旋镖）来代表"上一局还在飞的那些"，它们没生成出来就等于没测。
        if h_proj_b < 1:
            problems.append(
                "重开前房主侧弹丸数为 %d（哨兵弹丸没放出来）——"
                "「拆完立刻无残留弹丸」在这种情形下平凡成立，不能算通过" % h_proj_b)

        #    ② 客户端那边有没有残留，交给探针自己的「重开后弹丸清零」判据——它读的是
        #       客户端自己那一端的"重开前 vs 重开后"，跨端直接比数会被两端的采样错位
        #       和新世界的自然产出盖住（实测：房主清干净时重开后房主 38 / 客户端 12，
        #       房主没清时 64 / 24——都是房主多，差值的方向根本没有区分力）。
        #       这里只把它打出来供人看，不作判据。

        #    ③ M45：每一端重开后玩家数都必须等于人数。探针自己的「重开后玩家齐全」
        #       只比"重开前后"，比不出"本来就该是 4 个"——4 人局里如果有人一开始
        #       就没连上，两端都少一个，"齐全"照样是绿的。
        for idx, ci in enumerate([h_info] + [x for _, x in c_all]):
            side = "房主" if idx == 0 else "客户端%d" % idx
            got = as_int(ci, "重开后玩家")
            if got >= 0 and got != expect_players:
                problems.append(
                    "%s 重开后场上玩家 %d（%d 人局期望 %d）——"
                    "有人没连上，或在拆世界时被一起反生成了"
                    % (side, got, expect_players, expect_players))

        verdict = ("%d 人局联机实体真被反生成、新世界每端一致重建、玩家齐全、零残留零异常"
                   % (n_clients + 1))

    if h_exc or c_exc:
        problems.append("存在运行时异常（房主 %d / 客户端 %d），详见日志" % (h_exc, c_exc))

    print("-" * (16 + width * (n_clients + 1)))
    if problems:
        print("结论：未通过")
        for p in problems:
            print("  [问题] " + p)
        print("-" * 50)
        return 1
    print("结论：通过 —— " + verdict)
    print("-" * 50)
    return 0


if __name__ == "__main__":
    sys.exit(main())
