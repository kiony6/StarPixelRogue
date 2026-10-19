#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M13 云存档无头实测（M18）

做什么：
    依次拉起 5 个无头实例，各自构造一种「云存档开关组合」，跑一局单机对局，
    断言云存档在该组合下的联网行为是否符合承诺，以及**有没有把单机玩法砸了**。

为什么要专门测它：
    云存档是全项目唯一一个「默认关闭、但启用后会在运行时联网」的模块，此前零实测覆盖。
    而它最关键的承诺——未开启/未配置时**零网络请求**——光读代码只能推断、无法证明，
    所以本实测给传输层加了一个请求计数器，把"没联网"变成可断言的事实。

五个场景（每个跑一个独立进程）：
    A 未配置（真实默认态）        → 期望禁用、请求 0
    B 非 HTTPS 地址               → 期望被安全校验拦下、请求 0   ← 验证隐私承诺在代码层面成立
    C 已配置但用户未开启          → 期望禁用、请求 0
    D 已配置已开启但未同意隐私    → 期望禁用、请求 0
    E 已配置 + 已开启 + 已同意    → 期望**真的发了请求**；后端不可达时静默降级、玩法不受影响

外加一个 UI 探针 `-uiProbe`（默认一并跑），覆盖三块在普通冒烟里**根本不会被创建**的界面：
    ① 设置页：建出面板、真的点一下云存档开关、再切一次语言，
       验证控件存在 + 开关确实写进设置 + 中英文案都是成品而非本地化键；
    ② 联机大厅：中英各建一次，收集全部文案；
    ③ 升级选卡：建出面板，收集渲染文案，并逐项取遍它能拼出的全部文案素材。

② ③ 的判据比 ① 更硬：除"不许出现裸键"外，还断言**英文界面不含任何中日韩字符**。
只比对"中英两条不同"是不够的——一条硬编码中文 + 一条英文同样能满足"两者不同"，
而不含 CJK 才是"没有中文漏出来"的直接反向指标。

判据的区分力从哪来（这是本脚本最要紧的地方）：
    1) 「是否应当启用」由本脚本**独立**按 HTTPS 规则推导后传给被测进程，
       绝不读 CloudSaveService.Enabled 当期望值——否则实现若误把 http 判为已配置，
       被测代码就会自我印证（M11 已吃过一次假通过的亏）。
    2) 场景 E 断言「请求数 > 0」：只断言"没崩溃"的话，实现压根不联网也能通过。
    3) 场景 A~D 断言「请求数 == 0」同时叠加场景 E 的反向断言：
       两个方向都成立，"零请求"才不是靠"压根没实现同步"蒙出来的。
    4) 所有场景都断言「击杀 > 0 且波次 ≥ 1 且零异常」：云存档无论开关都不能砸单机体验。

用法：
    python cloudsave_probe.py                 # 跑全部 5 个场景
    python cloudsave_probe.py --only E        # 只跑指定场景
    python cloudsave_probe.py --seconds 30    # 每个场景的对局时长

退出码：
    0 = 全部场景通过；1 = 有场景未通过；2 = 前置条件不满足
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
NOISE = ("shader", "gpu", "memorysetup", "unitymemory", "curl error", "unitlets",
         "screen position out of view frustum")

# 一个必然连不上的地址（端口 9 = discard，无人监听），用于"后端不可达"场景。
# 用 127.0.0.1 而非公网域名：连接会立刻失败，不依赖外网、也不会真的把存档发出去。
UNREACHABLE = "https://127.0.0.1:9/save"

# 场景表：(编号, 名称, 后端地址, 用户开关, 已同意隐私)
# 「是否应当启用」不在表里写死，由 expect_enabled() 按规则独立推导——见文件头判据说明 1)
SCENARIOS = (
    ("A", "未配置（真实默认态）", "", 1, 1),
    ("B", "非HTTPS地址", "http://example.com", 1, 1),
    ("C", "已配置但用户未开启", UNREACHABLE, 0, 1),
    ("D", "已配置已开启但未同意隐私", UNREACHABLE, 1, 0),
    ("E", "全开但后端不可达", UNREACHABLE, 1, 1),
)


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def expect_enabled(url, user_on, agreed):
    """独立推导「云存档是否应当启用」。

    刻意在本脚本里重写一遍 HTTPS 规则，而不是去问被测对象——
    被测对象若把 http 误判为已配置，问它只会得到自我印证的答案。
    """
    if not url:
        return False
    low = url.lower()
    secure = low.startswith("https://") \
        or low.startswith("http://localhost") \
        or low.startswith("http://127.0.0.1")
    return secure and bool(user_on) and bool(agreed)


def parse_smoke(path):
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
    """日志里的异常条数（与被测进程自报的「运行期异常」交叉核对）。"""
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


def log_too_big(path, limit_mb):
    try:
        return os.path.getsize(path) > limit_mb * 1024 * 1024
    except OSError:
        return False


def run_one(exe, key, name, url, user_on, agreed, seconds, limit_mb):
    log = os.path.join(ROOT, "cloudsave_%s.log" % key)
    if os.path.exists(log):
        os.remove(log)

    expect = expect_enabled(url, user_on, agreed)
    args = [
        "-batchmode", "-nographics", "-cloudSaveProbe",
        "-cloudSaveScene", name,
        "-cloudSaveUserOn", str(user_on),
        "-cloudSaveAgree", str(agreed),
        "-cloudSaveTimeout", "3",
        "-cloudSaveExpect", "1" if expect else "0",
        "-smokeSeconds", str(seconds),
        "-logFile", log,
    ]
    if url:
        args += ["-cloudSaveUrl", url]

    print("  [%s] %s —— 期望启用=%s" % (key, name, "是" if expect else "否"))
    try:
        proc = subprocess.Popen([exe] + args, cwd=ROOT,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as e:
        print("        [错误] 无法启动进程: %s" % e)
        return None

    deadline = time.time() + seconds + 90
    killed = False
    while time.time() < deadline:
        if proc.poll() is not None:
            break
        if log_too_big(log, limit_mb):
            print("        [熔断] 日志超过 %dMB，强制结束" % limit_mb)
            proc.kill()
            killed = True
            break
        time.sleep(1.0)
    else:
        print("        [警告] 超时未退出，强制结束")
        proc.kill()
        killed = True

    code = proc.returncode
    data, ok = parse_smoke(log)
    return {
        "key": key, "name": name, "url": url, "expect": expect,
        "code": code, "killed": killed, "data": data, "has_data": bool(data),
        "log": log, "log_ok": ok,
        "exc": count_exceptions(log),
    }


def check(r):
    """对单个场景做判定，返回问题列表。"""
    problems = []
    d = r["data"]
    key, name, expect = r["key"], r["name"], r["expect"]

    if not r["has_data"]:
        return ["未产出任何 [SMOKE] 指标（日志存在=%s），无法判定" % r["log_ok"]]
    if r["killed"]:
        problems.append("进程被强制结束（未自行收尾），详见 %s" % rel(r["log"]))
    if r["code"] != 0:
        problems.append("进程退出码 %s（0=通过）" % r["code"])

    enabled = d.get("云存档启用", "?")
    requests = as_int(d, "网络请求数")

    # 判据 1：启用与否必须与期望一致
    if expect and enabled != "是":
        problems.append("期望启用但实际为「%s」（原因：%s）" % (enabled, d.get("未启用原因", "?")))
    if not expect and enabled != "否":
        problems.append("期望禁用但实际为「%s」——不该联网的组合却启用了" % enabled)

    # 判据 2：请求数必须与启用态自洽（双向，杜绝「压根没实现同步」蒙混过关）
    if expect and requests <= 0:
        problems.append("期望启用但网络请求数为 %d——同步链路根本没走到传输层" % requests)
    if not expect and requests != 0:
        problems.append("期望零请求但实际发出 %d 次网络请求——「未启用即不联网」的承诺被打破" % requests)

    # 判据 3：无论开关如何，单机玩法都不能被砸
    kills, wave = as_int(d, "击杀"), as_int(d, "波次")
    if kills <= 0:
        problems.append("击杀为 %d——对局未正常推进（云存档疑似阻塞/砸坏了玩法）" % kills)
    if wave < 1:
        problems.append("波次为 %d——波次系统未推进" % wave)

    # 判据 4：失败只允许静默降级，不允许抛异常（与日志 grep 交叉核对）
    self_exc = as_int(d, "运行期异常")
    if self_exc > 0:
        problems.append("运行期异常 %d 条——云存档失败未做到静默降级" % self_exc)
    if r["exc"] > 0 and self_exc <= 0:
        # 进程自报 0 但日志里能 grep 到：说明有异常发生在探针订阅之前（如建世界阶段）
        problems.append("日志中检出 %d 条异常，但进程自报 0——异常发生在探针订阅之前" % r["exc"])

    # 场景 B 专项：非 HTTPS 必须被安全校验拦下，且原因要指向配置而非用户开关
    if key == "B":
        reason = d.get("未启用原因", "")
        if "HTTPS" not in reason and "未配置" not in reason:
            problems.append("非 HTTPS 地址的禁用原因是「%s」——未指向安全校验，实现可疑" % reason)

    # 场景 E 专项：真的发了请求，但后端不可达 → 同步应失败且玩法不受影响
    if key == "E":
        if d.get("同步成功") == "是":
            problems.append("后端不可达却报「同步成功」——结果判定不真实")
        if requests < 2:
            problems.append("仅发出 %d 次请求——拉取与上传两条路径未都被走到（期望 ≥2）" % requests)

    return problems


def run_ui_probe(exe, limit_mb):
    """UI 探针：设置面板（云存档控件/开关/双语）+ 联机大厅 + 升级选卡。"""
    log = os.path.join(ROOT, "cloudsave_ui.log")
    if os.path.exists(log):
        os.remove(log)

    print("  [UI] 设置面板探针 —— 云存档控件 / 开关生效 / 中英双语状态")
    try:
        proc = subprocess.Popen(
            [exe, "-batchmode", "-nographics", "-uiProbe", "-logFile", log],
            cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as e:
        print("        [错误] 无法启动进程: %s" % e)
        return None

    killed = False
    deadline = time.time() + 90
    while time.time() < deadline:
        if proc.poll() is not None:
            break
        if log_too_big(log, limit_mb):
            proc.kill()
            killed = True
            break
        time.sleep(1.0)
    else:
        proc.kill()
        killed = True

    d, _ = parse_smoke(log)
    return {"code": proc.returncode, "killed": killed, "data": d, "log": log}


def check_ui(r):
    """设置面板探针的判据。判据在本脚本重写一遍，不读被测自判的结论。"""
    problems = []
    d = r["data"]
    if not d:
        return ["未产出任何 [SMOKE] 指标，无法判定"]
    if r["killed"]:
        problems.append("进程被强制结束，详见 %s" % rel(r["log"]))
    if r["code"] != 0:
        problems.append("进程退出码 %s（0=通过）" % r["code"])

    # 控件：三个都得在，缺一个就说明面板构建没走完
    for key, name in (("云存档开关控件", "开关"), ("云存档状态控件", "状态文本"),
                      ("立即同步控件", "立即同步按钮")):
        if d.get(key) != "存在":
            problems.append("云存档%s控件缺失（%s）——设置页改动未生效" % (name, d.get(key)))

    if d.get("开关点击生效") != "是":
        problems.append("点击云存档开关后设置未改变（%s）——开关只是画了个样子"
                        % d.get("开关点击生效"))

    # 联机大厅：中英各建一次，两边都得收集到成套文案（标题/副标题/提示/地址/4 个按钮 ≥ 6 条）
    for k, lang in (("大厅中文文案数", "中文"), ("大厅英文文案数", "英文")):
        n = as_int(d, k)
        if n < 6:
            problems.append("%s大厅只收集到 %s 条文案（期望 ≥6）——大厅未完整建出" % (lang, n))

    # 升级选卡：面板真渲染出文案（标题 + 至少 3 张卡），且素材枚举覆盖到位
    if as_int(d, "升级卡文案数") < 4:
        problems.append("升级选卡面板只渲染出 %s 条文案（期望 ≥4）——卡片未画出来"
                        % d.get("升级卡文案数"))
    if as_int(d, "升级素材数") < 30:
        problems.append("升级文案素材只收集到 %s 条（期望 ≥30）——武器/强化枚举未覆盖全"
                        % d.get("升级素材数"))

    # 裸键：Localization 漏翻时 T() 原样返回键名，玩家会直接看到 "weapon.name.3"
    raw = d.get("裸键文案", "")
    if raw not in ("", "无"):
        problems.append("界面出现裸本地化键「%s」——词条漏翻" % raw)

    # 英文界面不许残留中文：本轮缺陷的正向判据（"中英不同"抓不到混排漏翻）
    left = d.get("英文界面中文残留", "")
    if left not in ("", "无"):
        problems.append("英文界面残留中文「%s」——仍有硬编码中文未走本地化" % left)

    zh = d.get("中文状态文案", "")
    en = d.get("英文状态文案", "")
    # 漏翻时 Localization.T 原样返回键名，UI 上会冒出 "cloud.status.off"
    for lang, v in (("中文", zh), ("英文", en)):
        if not v:
            problems.append("%s状态文案为空" % lang)
        elif v.startswith("cloud.status."):
            problems.append("%s状态文案是本地化键而非成品文案（%s）——词条漏翻" % (lang, v))
    if zh and en and zh == en:
        problems.append("中英文状态文案完全相同（%s）——本地化未生效，疑似硬编码" % zh)

    if as_int(d, "运行期异常") > 0:
        problems.append("运行期异常 %s 条：%s" % (as_int(d, "运行期异常"), d.get("首条错误", "?")))

    return problems


def main():
    ap = argparse.ArgumentParser(description="M13 云存档无头实测（开关矩阵 + 设置面板）")
    ap.add_argument("--seconds", type=int, default=20, help="每个场景的对局时长（秒）")
    ap.add_argument("--exe", default=DEFAULT_EXE, help="构建产物路径")
    ap.add_argument("--only", default="", help="只跑指定场景编号，如 E（可逗号分隔：A,C,E）")
    ap.add_argument("--ui", action="store_true",
                    help="只跑 UI 探针（设置面板 + 联机大厅 + 升级选卡），不跑 5 个联网场景")
    ap.add_argument("--skip-ui", action="store_true", help="跳过 UI 探针")
    ap.add_argument("--log-limit-mb", type=int, default=200,
                    help="单场景日志体积上限（MB），超过即熔断")
    args = ap.parse_args()

    exe = args.exe
    if not os.path.exists(exe):
        print("[错误] 找不到构建产物: %s" % rel(exe))
        print("       请先构建，例如：")
        print("       Unity.exe -batchmode -nographics -quit -projectPath StarPixelRogue \\")
        print("         -executeMethod StarPixel.EditorTools.BuildScript.HeadlessBuild")
        return 2

    if args.ui:
        print("=" * 74)
        print("M13 云存档无头实测 · 仅 UI 探针（设置面板 / 联机大厅 / 升级选卡）")
        print("  产物   %s" % rel(exe))
        print("=" * 74)
        r = run_ui_probe(exe, args.log_limit_mb)
        if r is None:
            return 2
        for k in ("云存档开关控件", "云存档状态控件", "立即同步控件", "开关点击生效",
                  "中文状态文案", "英文状态文案",
                  "大厅中文文案数", "大厅英文文案数", "升级卡文案数", "升级素材数",
                  "裸键文案", "英文界面中文残留", "运行期异常", "首条错误"):
            if k in r["data"]:
                print("  %-16s %s" % (k, r["data"][k]))
        ps = check_ui(r)
        if ps:
            print("\n结论：未通过")
            for p in ps:
                print("  - " + p)
            return 1
        print("\n结论：通过 —— 设置页控件齐全且开关真实生效，大厅与升级卡中英双语均为成品文案，"
              "英文界面无中文残留")
        return 0

    wanted = [s.strip().upper() for s in args.only.split(",") if s.strip()]
    scene_list = [s for s in SCENARIOS if not wanted or s[0] in wanted]
    if not scene_list:
        print("[错误] --only 未匹配到任何场景（可选：%s）" % ", ".join(s[0] for s in SCENARIOS))
        return 2

    print("=" * 74)
    print("M13 云存档无头实测 · %d 个场景 · 每场景 %ds" % (len(scene_list), args.seconds))
    print("  产物   %s" % rel(exe))
    print("=" * 74)

    results = []
    for key, name, url, user_on, agreed in scene_list:
        r = run_one(exe, key, name, url, user_on, agreed, args.seconds, args.log_limit_mb)
        if r is None:
            return 2
        results.append(r)

    # —— 汇总表 ——
    print("\n%-4s %-22s %-6s %-6s %-8s %-6s %-6s" %
          ("场景", "名称", "启用", "期望", "请求数", "击杀", "波次"))
    print("-" * 74)
    for r in results:
        d = r["data"]
        print("%-4s %-22s %-6s %-6s %-8s %-6s %-6s" % (
            r["key"], r["name"],
            d.get("云存档启用", "?"), "是" if r["expect"] else "否",
            d.get("网络请求数", "?"), d.get("击杀", "?"), d.get("波次", "?"),
        ))
    print("-" * 74)

    for r in results:
        d = r["data"]
        if d:
            print("  [%s] 未启用原因=%s / 最近状态=%s / 退出码=%s" %
                  (r["key"], d.get("未启用原因", "?"), d.get("最近状态", "?"), r["code"]))

    # —— 设置面板探针（默认一并跑；--only 只关注单个联网场景时跳过）——
    ui_problems = []
    if not args.skip_ui and not wanted:
        print()
        ur = run_ui_probe(exe, args.log_limit_mb)
        if ur is None:
            return 2
        d = ur["data"]
        print("  设置面板   开关控件=%s / 状态控件=%s / 同步按钮=%s / 点击生效=%s" %
              (d.get("云存档开关控件", "?"), d.get("云存档状态控件", "?"),
               d.get("立即同步控件", "?"), d.get("开关点击生效", "?")))
        print("  双语状态   中文=「%s」" % d.get("中文状态文案", "?"))
        print("             英文=「%s」" % d.get("英文状态文案", "?"))
        print("  大厅文案   中文=%s 条 / 英文=%s 条   升级卡=%s 条 / 素材=%s 条" %
              (d.get("大厅中文文案数", "?"), d.get("大厅英文文案数", "?"),
               d.get("升级卡文案数", "?"), d.get("升级素材数", "?")))
        print("  裸键=%s   英文界面中文残留=%s" %
              (d.get("裸键文案", "?"), d.get("英文界面中文残留", "?")))
        ui_problems = check_ui(ur)

    # —— 判定 ——
    print()
    all_problems = []
    for r in results:
        ps = check(r)
        if ps:
            all_problems.append((r["key"], r["name"], ps, r["log"]))

    total_exc = sum(r["exc"] for r in results)
    print("日志异常合计：%d 条" % total_exc)

    if all_problems or ui_problems:
        print("\n结论：未通过")
        for key, name, ps, log in all_problems:
            print("  [%s] %s（日志 %s）" % (key, name, rel(log)))
            for p in ps:
                print("      - " + p)
        if ui_problems:
            print("  [UI] UI 探针（设置面板 / 联机大厅 / 升级选卡）")
            for p in ui_problems:
                print("      - " + p)
        return 1

    tail = ("，设置页云存档开关、联机大厅与升级选卡的中英双语亦已验证"
            if not args.skip_ui and not wanted else "")
    print("\n结论：通过 —— A~D 零网络请求且玩法未受影响，E 真的联网并在不可达时静默降级%s" % tail)
    print('       （双向断言成立：既不是「压根没实现同步」，也不是「关了开关还在联网」）')
    return 0


if __name__ == "__main__":
    sys.exit(main())
