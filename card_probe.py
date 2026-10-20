#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M37–M40 联机升级选卡链路无头实测

做什么：
    拉起 6 个无头进程，把「房主推卡 — 客户端选卡」这条链路上两处**只维护一份状态**的
    缺陷钉死：
      BASE    地基自检：外部模式真的生效（本地升级不弹卡）/ 单组能上屏 / 文本真是那一组 / 空候选不计入
      QUEUE   面板占用时连推 3 组：收到 3、上屏 1、入队 2、丢弃 0，且上屏的必须是第一组
      DRAIN   依次选掉：第 2、3 组按顺序自动补上，全选完面板关闭、队列清零
      ORDER   推 4 组再全部选掉：上屏顺序与回调顺序都必须等于推送顺序（防错配）
      BATCH   批次表纯逻辑：各批取回不串 / 一批只能取一次 / 非法 id 与越界都拒绝 / 上限淘汰
      NOTICE  M39 收场通知：四种收场各出一条提示 / 零被吞 / 摘掉出口必须记为被吞 / 切语言重画
      LOCALE  M40 文案按接收端语言现算：中文态该空的空 / 英文态零中文零键名 / 等级按本机 / 面板真实英文
      RESTORE 收尾与反向：队列清零、切回本地后本地升级仍能弹卡（防修过头把本地堵死）、全程零异常

为什么必须测：
    房主推卡是**多次**的（每升一级推一组），而被推的两处状态都只维护**一份**：
      ① 客户端 LevelUpManager.PresentExternal 开头就是 `if (_active) return;`——
         面板正开着时推来的卡直接扔掉。实测 60s 双开：房主选卡 5 次、客户端 0 次。
      ② 房主 NetPlayer._cachedOffers 每次推卡 Clear() 再填新一组，客户端回传的却只有
         一个 index：还没选完就又升一级时，玩家点的是旧组第 1 张、房主应用的是新组
         第 1 张。不报错、不崩，只有把两次推卡的标题并排看才看得出来。
    两处都不是"联机专属"那么简单：本地路径早就有排队（_pending + TryPresent），
    外部路径没有——**同一件事两条路不一致**，和 M32 的 AddSkill 缺守卫是同一类。

为什么单机无头就够：
    两处成因都是纯逻辑，不是网络时序。抽成 UpgradeOfferBatches（不依赖 FishNet 的批次表）
    + 面板排队后，整条链路在单机里可断言，不必真开双开。

判据的区分力（本脚本最要紧的地方）：
    1) QUEUE 里「上屏的必须是第一组」是关键：若改回覆盖式实现，上屏的会变成最后一组，
       但"收到 3 / 上屏 1"照样成立——只数不看的判据抓不到错配。
    2) ORDER 同时断言上屏顺序与回调顺序：只排队不批次化的话，上屏顺序对、回调错配。
    3) RESTORE 必须反向断言本地路径：把 PresentExternal 改成永不弹卡也能让
       QUEUE/DRAIN/ORDER 全绿——那等于把联机升级整个砍掉。
    4) BASE 第 1 条是手法自检：若 SetExternalCards 没生效，QUEUE 会一片假绿。
    5) BATCH 全场景不搭世界，纯逻辑断言：批次表一旦被改回"单份表 + 裸 index"，
       这里会立刻红（而联机双开要跑满 60s 才看得出，且看出来的只是"数值怪"）。

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
    ("BASE", "地基自检：外部模式挡住本地弹卡 / 单组能上屏 / 文本真是那一组 / 空候选不计入"),
    ("QUEUE", "面板占用时连推 3 组：收到 3、上屏 1、入队 2、丢弃 0，且上屏的是第一组"),
    ("DRAIN", "依次选掉：第 2、3 组按顺序自动补上，全选完面板关闭、队列清零"),
    ("ORDER", "推 4 组全选掉：上屏顺序与回调顺序都必须等于推送顺序"),
    ("BATCH", "批次表纯逻辑：乱序取回不串 / 一批只取一次 / 非法与越界拒绝 / 上限淘汰"),
    ("GATE", "M38 待确认表纯逻辑：确认才生效 / 一笔只结一次 / 回执要对得上 / 拒绝与超时都有出口"),
    ("INVAL", "M38 批次作废：正在展示的被撤掉并补弹下一批 / 队列里的被移除 / 不存在的批次不误伤"),
    ("NOTICE", "M39 收场通知：四种收场各出一条提示 / 零被吞 / 摘掉出口必须记为被吞 / 切语言重画 / 短超时可压测"),
    ("LOCALE", "M40 推卡文案：按 kind 现算 / 中文态该空的空 / 英文态零中文零键名 / 等级按本机 / 面板真实英文"),
    ("RESTORE", "收尾与反向：队列清零、切回本地后本地升级仍能弹卡、全程零异常"),
)

BACKUP_SUFFIX = ".m37bak"

# 每个场景的超时（秒）。BATCH/GATE 不搭世界，秒完；其余要 Build 一整个世界。
TIMEOUT = {"BASE": 90, "QUEUE": 90, "DRAIN": 90, "ORDER": 90, "BATCH": 60,
           "GATE": 60, "INVAL": 90, "NOTICE": 90, "LOCALE": 90, "RESTORE": 90}


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
    args = [exe, "-batchmode", "-nographics", "-cardProbe", "-cardScene", scene]
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


def _zero(v):
    return v == "0"


def _eq(s):
    return lambda v: v == s


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        # 地基：证明外部模式手法本身成立，否则 QUEUE/DRAIN 全绿是假的
        "BASE": [
            ("外部模式挡住本地弹卡", _yes),
            ("单组成功上屏", _yes),
            ("单组上屏计数", _eq("1")),
            ("上屏文本是这一组", _yes),
            ("空候选未计入", _yes),
            ("收到增量", _eq("1")),
            ("断点通过", _eq("4/4")),
        ],
        # 排队：最关键的是"上屏的必须是第一组"，只数不看顺序抓不到覆盖式错配
        "QUEUE": [
            ("收到", _eq("3")),
            ("上屏", _eq("1")),
            ("入队", _eq("2")),
            ("队列残余", _eq("2")),
            ("丢弃", _zero),
            ("收支正确", _yes),
            ("上屏的是第一组", _yes),
            ("零丢弃", _yes),
            ("断点通过", _eq("4/4")),
        ],
        # 排空：队列必须按顺序自动补上，且全选完面板要关掉
        "DRAIN": [
            ("补上第二组", _yes),
            ("补上第三组", _yes),
            ("选完面板关闭", _yes),
            ("上屏总数", _eq("3")),
            ("上屏三组齐", _yes),
            ("零丢弃", _yes),
            ("断点通过", _eq("5/5")),
        ],
        # 顺序：上屏与回调两条序列都必须等于推送顺序
        "ORDER": [
            ("上屏顺序", _eq("O1,O2,O3,O4")),
            ("回调顺序", _eq("O1,O2,O3,O4")),
            ("上屏顺序正确", _yes),
            ("回调顺序正确", _yes),
            ("零丢弃", _yes),
            ("断点通过", _eq("3/3")),
        ],
        # 批次表：纯逻辑，全部必须直接命中
        "BATCH": [
            ("乱序取回正确", _yes),
            ("一批只取一次", _yes),
            ("非法id被拒", _yes),
            ("越界被拒", _yes),
            ("上限淘汰", _eq("1")),
            ("淘汰后残余", _eq("3")),
            ("淘汰正确", _yes),
            ("淘汰有出口", _yes),
            ("应用的是本批那张", _yes),
            ("断点通过", _eq("7/7")),
        ],
        # M38 待确认表：一次选择只有被房主点头才许生效，且一笔只许结一次
        "GATE": [
            ("确认后才生效", _yes),
            ("重复确认被拒", _yes),
            ("伪造回执被拒", _yes),
            ("回执对不上被拒", _yes),
            ("拒绝路径正确", _yes),
            ("超时清理正确", _yes),
            ("批次作废生效", _yes),
            ("断点通过", _eq("7/7")),
        ],
        # M38 作废：撤得准（撤该撤的），也撤得稳（不误伤正在选的那张）
        "INVAL": [
            ("上屏批次id正确", _yes),
            ("作废展示中的批次", _yes),
            ("作废队列中的批次", _yes),
            ("不存在的批次不误伤", _yes),
            ("其余卡片仍在", _yes),
            ("零丢弃", _yes),
            ("断点通过", _eq("6/6")),
        ],
        # 恢复与反向：队列要排空，本地路径不能被修过头堵死
        "NOTICE": [
            ("出口已注册", _yes),
            ("收场/上屏/被吞", _eq("4/4/0")),
            ("收支守恒", _yes),
            ("四种分类计数", _yes),
            ("无出口时记为被吞", _yes),
            ("无出口时被吞计数", _eq("1")),
            ("切语言重画", _yes),
            ("按 kind 取标题", _yes),
            ("短超时可压测", _yes),
            ("断点通过", _eq("7/7")),
        ],
        "RESTORE": [
            ("队列残余", _zero),
            ("队列已排空", _yes),
            ("面板已关闭", _yes),
            ("本地仍能弹卡", _yes),
            ("全程零异常", _yes),
            ("断点通过", _eq("4/4")),
        ],
        # M40 文案：按 kind 现算，且必须按**接收端自己的语言**——
        # 房主中文 + 客户端英文时，卡片不能跟着房主走
        "LOCALE": [
            ("本地化键判定", _yes),
            ("中文态卡片完整", _yes),
            ("英文态零中文零键名", _yes),
            ("等级按本机现算", _yes),
            ("非法kind安全", _yes),
            ("面板真实英文", _yes),
            ("断点通过", _eq("6/6")),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        if callable(expect):
            good = expect(got)
            shown = _label(expect)
        else:
            good = (got == expect)
            shown = expect
        rows.append((key, shown, got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def _label(f):
    if f is _yes:
        return "是"
    if f is _zero:
        return "0"
    return "达标"


def main():
    ap = argparse.ArgumentParser(description="M37–M40 联机升级选卡链路无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 QUEUE")
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
    print("M37–M40 联机升级选卡链路无头实测 · %d 个场景" % len(scenes))
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
                print("      %s %-16s 期望 %-14s 实际 %s" % (" " if good else "x", k, e, g))

            # 实测数值行全部复读：失败时能直接看出是丢单还是错配
            for extra in ("收到", "上屏", "入队", "丢弃", "队列残余",
                          "上屏总数", "上屏顺序", "回调顺序",
                          "取回内容", "上限淘汰", "淘汰后残余",
                          "单组上屏计数", "收到增量",
                          "中/英属性卡标题", "本机/无玩家标题", "首个失败"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-16s %s" % (extra, res[extra]))

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
    print("结论：%s" % ("通过 —— 面板占用时推来的卡全部排队不丢弃 / 队列按顺序补弹 / "
                        "批次表取回不错配 / 本地路径未被堵死"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
