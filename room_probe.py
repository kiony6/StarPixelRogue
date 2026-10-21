#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M55「组队码」无头实测

做什么：
    拉起无头进程，把组队码的三层各钉一遍：
      CODE  码本身：批量生成的码必须全部通过自己的校验位；小写/分隔符/易混字符
            必须能还原；单字符错抄、相邻位念反、位数不对必须被挡下
      WIRE  发现报文：询问与应答的编解码往返；坏报文（错前缀/字段数不对/非法地址/
            非法端口）必须被拒绝；主机名里的分隔符不许把报文切歪
      ROOM  目录与解析：命中/未命中；远端房间过期必须被清、本机房间永不过期；
            解析命中/未命中/非法码三种结果必须分开；建房要真发码、关房要真清码

为什么必须测：
    组队码是玩家之间**口头传递**的一串字符。它的失败模式不是崩溃，是"连不上"——
    而"连不上"在一个还没跑通公网的联机功能里，几乎无法与"网络本来就不通"区分。
    玩家会归咎于联机，实际是码被打错了一个字符。所以这一层的正确性必须在无头
    环境里被完整断言，不能留到"等实机联调再说"。

判据的区分力（本脚本最要紧的地方）：
    1) 校验位的能力分开断言：「单字符错抄被挡」和「相邻位念反被挡」是两件事。
       固定权重的校验和对后者无能为力（AB 与 BA 的和相同）。只测前者的话，
       把权重改成常数，判据照样全绿，而**念反恰恰是真人传码时最常见的错误**。
    2) 「能解析出正确结果」必须配「能拒绝坏输入」的对偶判据。只测前者的话，
       一个对所有输入都返回成功的解析器也能绿。
    3) 「远端房间过期被清」必须配「本机房间不过期」。只测前者的话，
       一个把本机也一起清掉的 Prune 会让房主自己从目录里消失，这条判据看不出来。
    4) 「误抄可纠正」内部带自检：若随机到的码恰好不含易混字符，它会换一条再试——
       否则这条判据会随随机码**有时根本没在测**。

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
    ("CODE", "码本体：生成合法、格式正确、误抄可还原、错抄与念反被挡"),
    ("WIRE", "发现报文：编解码往返，坏报文必须被拒绝而非解出个错的"),
    ("ROOM", "目录与解析：命中/过期/本机不过期/建房发码/关房清码"),
)

BACKUP_SUFFIX = ".m55bak"

TIMEOUT = {"CODE": 120, "WIRE": 120, "ROOM": 120}


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
    args = [exe, "-batchmode", "-nographics", "-roomProbe", "-roomScene", scene]
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


# 复读项：不是独立判据，但失败时能直接指出是"没发出码"还是"发了但没登记"
EXTRA = ("样例码", "建房-码非空", "建房-码合法", "建房-LocalCode一致",
         "建房-登记为本机", "建房-返回码")


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "CODE": [
            # 前半是"生成得对"，后半是"拒绝得对"——缺一半就成了假绿
            ("生成的码全部合法", _yes),
            ("长度恒为 6", _yes),
            ("展示格式正确", _yes),
            ("小写可还原", _yes),
            ("分隔符可还原", _yes),
            ("误抄可纠正", _yes),
            # 合法字符不许被纠正：L 在字母表里，把它收进误抄表会毁掉所有含 L 的码
            ("含L的码不被改", _yes),
            ("单字符错抄被挡", _yes),
            ("相邻位念反被挡", _yes),
            ("位数不对被挡", _yes),
            ("断点通过", "10/10"),
        ],
        "WIRE": [
            ("询问报文往返", _yes),
            ("应答报文往返", _yes),
            ("错误前缀被挡", _yes),
            ("字段数不对被挡", _yes),
            ("非法地址被挡", _yes),
            ("非法端口被挡", _yes),
            ("主机名不切歪", _yes),
            ("断点通过", "7/7"),
        ],
        "ROOM": [
            ("目录命中", _yes),
            ("目录未命中", _yes),
            # 过期清理的两面：远端该清、本机不该清，只测一面等于没测
            ("过期房间被清", _yes),
            ("本机房间不过期", _yes),
            # 三种解析结果必须能分开，混在一起就不知道该提示"查码"还是"查房主"
            ("解析命中", _yes),
            ("解析未命中", _yes),
            ("解析非法码", _yes),
            ("IP 直通仍可用", _yes),
            ("建房发出组队码", _yes),
            ("关房清掉组队码", _yes),
            ("断点通过", "10/10"),
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
    ap = argparse.ArgumentParser(description="M55 组队码无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 CODE")
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
    print("M55 组队码无头实测 · %d 个场景" % len(scenes))
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

            for extra in EXTRA:
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-14s %s" % (extra, res[extra]))
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
