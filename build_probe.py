#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M23 选角/成长链路无头实测

做什么：
    拉起 3 个无头进程，把此前**从没被真正驱动过**的两条链路走一遍：
      SELECT       从主菜单点「角色」→ 点「选择」→ 断言四处同时跟上：
                   内存 id / save.json 落盘 / 卡片「◉ 已选择」标记 / 主菜单角色名；关闭按钮有效
      APPLY        **跨进程**读回上一步存下的角色（这才叫"重启后还记得"），
                   走真实开局 GameBuilder.Build，断言 7 个乘区与 CharacterDef 一致、
                   起始武器被替换、重复 Apply 幂等、换角色后数值不叠乘
      PROGRESSION  10 把武器逐个开火（用真实敌人做靶，看血量掉没掉）、
                   4 条组合技逐条断言、7 项属性强化逐项断言、武器等级封顶且满级不再供选

为什么必须测：
    选角按钮从未被点过（M21 只把面板画出来 dump 了文案）；10 把武器里 6 把从未在实战中开火；
    4 条组合技规则没有一条被逐条断言过。这些缺陷全都是"界面一切正常、数值静默不对"：
    选了角色下次启动被换回默认、解锁的武器打不出伤害——只有玩家自己会发现。

判据的区分力（本脚本最要紧的地方）：
    1) 选角用与默认不同的角色（id=1）。默认是 0，若断言写成 0，一个"什么都没做"的实现也能过。
    2) 落盘判据在**进程外**：脚本自己解析 save.json 的 lastCharacterId，不采信进程自述。
       而"重启后还记得"由 APPLY 这**另一个进程**从文件读回验证——同进程读内存证明不了任何事。
    3) 开火判据必须落在"伤害真的打出"上：探针用真实敌人当靶子看血量差，
       不是查武器列表长度（那样"解锁了但开火抛异常/打不中"会全绿）。
    4) 每个场景前清空 save.json，让断言建立在已知初始态上（M21 的 DECLINE 假通过教训）。

用户数据保护：跑前备份 save.json / settings.json，跑后原样恢复；探针产物全部清除。

退出码：0 = 全部通过；1 = 有场景未通过；2 = 前置条件不满足
"""

import argparse
import json
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
    ("SELECT", "点「角色」→点「选择」：内存/落盘/卡片标记/主菜单 四处同时跟上，关闭有效"),
    ("APPLY", "跨进程读回角色后真实开局：7 个乘区 / 起始武器 / 幂等 / 换角色不叠乘"),
    ("PROGRESSION", "10 把武器开火 / 4 条组合技 / 7 项属性强化 / 等级封顶"),
    # M52：主菜单是玩家按下次数最多的界面，却长期没有任何探针真的去按它上面的按钮。
    # M51 已经见过"两块画布同层 → 谁压谁听 Unity 内部排序"能把一颗按钮变成摆设，
    # 这里把主菜单与两个面板里每一颗 Button 都用射线点一遍。
    ("MENU", "主菜单四颗按钮（单人/联机大厅/排行榜/设置）都真的点得到，且每颗都有回应"),
    # M54：登录页是玩家开机看到的第一屏，此前一个判据都没有。
    # 它属于"看着对"最危险的一类：填错账号弹一句提示就算完事，
    # 可如果那句提示背后其实已经登录成功了，没有任何自动化测试会发现。
    ("AUTH", "登录注册链路：未登录进不去主菜单 / 四种错误填法都被挡下 / 记住密码能回填并自动登录"),
)

BACKUP_SUFFIX = ".m23bak"

# 每个场景的超时（秒）：PROGRESSION 要等 10 轮开火（每轮 3.4 秒）+ 断言，给足余量
TIMEOUT = {"SELECT": 90, "APPLY": 90, "PROGRESSION": 180, "MENU": 90, "AUTH": 120}


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
    """清掉落盘文件：让每个场景都从"文件不存在"这一已知初始态开始。"""
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
    args = [exe, "-batchmode", "-nographics", "-buildProbe", "-buildScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def read_save(data_dir):
    path = os.path.join(data_dir, "save.json")
    if not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def _at_least(n):
    """谓词式期望：用于「次数 ≥ n」这类不宜写成等值比较的判据。"""
    return lambda v: v.isdigit() and int(v) >= n


def _ratio_full(v):
    """'10/10' 这类计数：分子必须等于分母（只断言分母证明不了每一项都达标）。

    M54 补的一条：**分母还必须大于 0**。此前 "0/0" 也算通过，
    于是"面板压根没建出来、一颗按钮都没有"会被判成"全部可达"——
    摘掉某颗按钮的回调后，面板不出现，这条判据反而更绿了。
    """
    if "/" not in v:
        return False
    a, b = v.split("/", 1)
    return a.isdigit() and a == b and int(a) > 0


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "SELECT": [
            ("面板已建出", "是"), ("已点击选择", "是"),
            # 四处一致必须同时成立：只查 id 证明不了玩家看得见，只查菜单证明不了下次还记得
            ("选中态四处一致", lambda v: "卡片=是" in v and "菜单=是" in v and "落盘=是" in v),
            ("关闭按钮有效", "是"),
        ],
        "APPLY": [
            # 7 个乘区必须全部匹配（少乘一项 = 该角色的特性没生效）
            ("乘区匹配", "7/7"), ("乘区差异", "无"),
            ("起始武器已替换", lambda v: v.startswith("是")),
            ("重复应用幂等", "是"), ("换角色不叠乘", "是"), ("换角色差异", "无"),
        ],
        "MENU": [
            # 主菜单四颗（M54）：一颗点不到就等于那个功能没有入口。
            # --- 注意这一条是怎么定出来的 ---
            # 期望不能写死 4/4：菜单上究竟有几颗按钮是界面的事，不是判据的事，
            # 写死数字会让"在别处加了一颗按钮"这件正经事变成一次莫名的红。
            # 判的是"全部可达"，不是"恰好四颗"。
            ("主菜单按钮可达", _ratio_full),
            ("不可达明细", "<无>"),
            ("排行榜已建出", "是"),
            ("排行榜按钮可达", _ratio_full),
            ("排行榜不可达明细", "<无>"),
            ("排行榜关闭已收", "是"),
            ("设置面板已建出", "是"),
            ("设置按钮可达", _ratio_full),
            ("设置不可达明细", "<无>"),
            ("设置关闭已收", "是"),
            # 单机构建里没有联机模块：按钮"挂了回调"的证据不是"点了不报错"，
            # 而是"点了有回应，且那个回应关得掉"。
            ("联机按钮有回应", "是"),
            ("联机入口已退出", "是"),
            ("单人二级页已建出", "是"),
            ("开始游戏已点击", "是"),
            # 按下去世界必须真的来了：按钮能点到只说明"入口没被挡住"，
            # 不说明这条链路还通着。
            ("开局世界已建出", "是"),
        ],
        "AUTH": [
            ("登录页已建出", "是"),
            # 闸门的全部意义就在这条：同意协议之后、登录之前，主菜单必须是不可见的
            ("未登录已拦截", "是"),
            ("首屏为注册页", "是"),
            ("提交按钮可达", "是"),
            ("注册后进主菜单", "是"),
            ("注册后已登录", "是"),
            ("密码错误被挡", "是"),
            ("记住密码已回填", "是"),
            ("自动登录成功", "是"),
            ("退出登录已生效", "是"),
            # 四种错误填法 + 注册/记住/自动/退出，逐条报明细而不是合成一个总数
            ("断点通过", _ratio_full),
            ("被挡下明细", "<无>"),
        ],
        "PROGRESSION": [
            ("武器开火覆盖", "10/10"),
            # 10 把都必须真的打出伤害：这是"开火分支可用"的唯一硬证据
            ("打出伤害", "10/10"),
            ("组合技达标", "4/4"), ("属性强化达标", "7/7"),
            ("等级封顶", "是"), ("满级不再供选", "是"),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got) if callable(expect) else (got == expect)
        rows.append((key, "判据" if callable(expect) else expect, got, good))
        ok = ok and good

    # 进程自述的结论也纳入
    if res.get("结论") != "通过":
        ok = False
    # 失败项只要不是"无"，一律判不通过（避免探针内部判据与外部不一致时被放过）
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M23 选角/成长链路无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 SELECT")
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
    print("M23 选角/成长链路无头实测 · %d 个场景" % len(scenes))
    print("  产物   %s" % rel(exe))
    print("  数据   %s" % data_dir)
    print("=" * 74)

    backed = backup(data_dir, ["save.json", "settings.json"])
    if backed:
        print("  已备份 %d 个用户文件（跑后恢复）\n" % len(backed))

    # APPLY 的前提是"上一个进程点出来的角色已经落盘"。单跑 APPLY（--only）时 SELECT 不会执行，
    # 直接跑必然红在"跨进程读回不是 1"——那不是产品缺陷，而是少跑了前置步骤。
    # 这里显式把它补上，而不是让失败看起来像是功能坏了。
    scenes_to_run = [s for s in scenes]
    if any(s[0] == "APPLY" for s in scenes) and not any(s[0] == "SELECT" for s in scenes):
        scenes_to_run.insert(0, [s for s in SCENARIOS if s[0] == "SELECT"][0])
        print("  （APPLY 依赖 SELECT 落盘，已自动补跑一次 SELECT）\n")

    all_ok = True
    try:
        for scene, desc in scenes_to_run:
            # SELECT 必须从"存档不存在"这一已知初始态开始；
            # 而 APPLY 的前提恰恰相反——它要读 SELECT 写下的存档，**绝不能**在这里清掉，
            # 否则会得到"选角没落盘"的假失败（本轮就这样绕了一圈才找到）。
            if scene != "APPLY":
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

            # 进程外判据：落盘的角色必须真的是 1（不采信进程自述）
            if scene == "SELECT":
                rec = read_save(data_dir)
                got = rec.get("lastCharacterId") if isinstance(rec, dict) else None
                if got != 1:
                    print("      x %-16s 实际 %s（save.json 里没有选中的角色）" % ("进程外落盘", got))
                    ok = False
            if scene == "APPLY":
                # APPLY 跑完时存档可能**还不存在**：GameBuilder.Build → SaveManager 只在有写入动作时才落盘，
                # 而"选中的角色"是上一个进程写进去的。所以这里只做非致命提示，真正的重启判据
                # 是探针自己跨进程读回的那一条（已在 SMOKE 的"乘区匹配"里体现）。
                rec = read_save(data_dir)
                got = rec.get("lastCharacterId") if isinstance(rec, dict) else None
                if got is not None and got != 1:
                    print("      x %-16s 实际 %s（开局把选中的角色改写了）" % ("进程外存档", got))
                    ok = False

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
    print("结论：%s" % ("通过 —— 选角真的落盘且重启后沿用 / 角色数值与起始武器真的生效且不叠乘 / "
                        "10 把武器都能打出伤害 / 4 条组合技与 7 项属性强化各自生效 / 等级封顶"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
