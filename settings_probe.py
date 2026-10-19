#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M22 设置面板「真实交互」与设置落盘往返无头实测

做什么：
    拉起 3 个无头进程，验证此前**只被"建出来看过一眼"、从没被真正点击过**的设置面板：
      TOGGLES    逐个点击 全屏/震屏/统计/云存档/青少年/画质循环/语言循环，
                 断言三件事同时成立：内存值变了、面板文字跟进了、文件里也是这个值；
                 并断言切完语言后面板**仍然可见**（旧实现重建后默认隐藏 = 把用户踢回主菜单）
      ROUNDTRIP  11 个字段经公开 Setter 写入 → 文件 → 换全新实例读回逐字段比对，
                 再断言每项真的作用到引擎（AudioListener / Quality / Screen / 语言 / 限时 / 采集）
      NEGATIVE   设置文件被写坏（半截 JSON）时：不得崩溃、必须回退默认值，
                 且**损坏文件必须被归档**——否则用户整套设置被静默清零，无处找回

为什么必须测：这四类缺陷全是"静默作废"型——按钮点了没反应、设置改了下次启动恢复默认、
文件坏了不报错只是设置没了。界面看起来一切正常，只有用户自己会发现。

判据的区分力（本脚本最要紧的地方）：
    1) "改了没生效" 与 "生效了没落盘" 分开断言：前者查内存 + 面板文字，
       后者由探针**重新走真实 Load()** 从磁盘读回比对。只查内存的话，
       一个压根不保存的实现也能全绿（M21 同理）。
    2) 画质用 QualitySettings.GetQualityLevel() 判、音量用 AudioListener.volume 判：
       只查 SettingsManager.Current 证明不了"引擎收到过"。
    3) NEGATIVE 的核心判据在**进程外**：脚本自己列目录，确认 settings.json.corrupt-* 存在
       且内容与脚本写进去的那份坏数据**逐字节相同**。由被测进程自述"我备份了"很容易变成假的兜底。
    4) 每个场景前清空落盘文件，让断言建立在已知初始态上——
       否则后一场景会建立在前一场景的副作用上，收获"因为错误原因而通过"（M21 踩过）。

用户数据保护：跑前备份 settings.json / save.json，跑后原样恢复；探针产物（含归档）全部清除。

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
    ("TOGGLES", "逐个点击控件：生效 / 文字跟进 / 落盘三者同时成立，切语言后面板仍可见"),
    ("ROUNDTRIP", "11 个字段写入→落盘→全新实例读回，且每项真的作用到引擎"),
    ("NEGATIVE", "设置文件损坏：不崩溃 / 回退默认值 / 损坏文件被归档"),
)

BACKUP_SUFFIX = ".m22bak"
CORRUPT_PREFIX = "settings.json.corrupt-"

# 刻意写坏的"半截 JSON"：模拟掉电/磁盘满时写坏的典型形态——
# 有开头的花括号、字段写了一半就断了。JsonUtility 会抛，必须被兜住。
BROKEN_SETTINGS = '{\n  "masterVolume": 0.123,\n  "language": "en-US",\n  "qual'
# 这份坏文件里"看得见"的值：用来证明回退时用的**不是**能读到的部分，而是干净默认值
BROKEN_HINT_VOLUME = 0.123


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


def clear_artifacts(data_dir):
    """清掉上一场景留下的落盘文件与归档（否则后一场景会继承前一场景的状态）。"""
    for n in ("settings.json", "save.json"):
        p = os.path.join(data_dir, n)
        if os.path.exists(p):
            os.remove(p)
    for f in os.listdir(data_dir):
        if f.startswith(CORRUPT_PREFIX):
            os.remove(os.path.join(data_dir, f))


def restore(data_dir, names):
    for n in names:
        p = os.path.join(data_dir, n)
        b = p + BACKUP_SUFFIX
        if os.path.exists(p):
            os.remove(p)          # 探针写过的测试档，直接丢弃
        if os.path.exists(b):
            os.rename(b, p)


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


def run_scenario(exe, scene, seconds):
    args = [exe, "-batchmode", "-nographics", "-settingsProbe", "-settingsScene", scene]
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=seconds + 60)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or b"").decode("utf-8", "replace") if isinstance(e.stdout, bytes) else (e.stdout or "")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def check_quarantine(data_dir):
    """进程外判据：损坏文件必须被归档，且归档内容与写进去的坏数据逐字节相同。"""
    files = [f for f in os.listdir(data_dir) if f.startswith(CORRUPT_PREFIX)]
    if not files:
        return ["未发现 settings.json.corrupt-* 归档（用户设置被静默清零）"]
    path = os.path.join(data_dir, files[0])
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        got = f.read()
    if got != BROKEN_SETTINGS:
        return ["归档内容与原始坏数据不一致（备份是假的）"]
    return []


def check_settings_not_overwritten(data_dir):
    """NEGATIVE 的另一条进程外判据：坏文件不能被"原样留下"当成功——
    产品代码必须已经用它自己的默认值重建了可用的 settings.json。"""
    path = os.path.join(data_dir, "settings.json")
    if not os.path.exists(path):
        return ["损坏后没有重建可用的 settings.json"]
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception as e:
        return ["重建后的 settings.json 仍不是合法 JSON：%s" % e]
    if isinstance(data, dict) and abs(float(data.get("masterVolume", 0)) - BROKEN_HINT_VOLUME) < 1e-6:
        return ["重建后的 settings.json 里仍是那份坏数据（没真正回退）"]
    return []


def _at_least(n):
    """期望写成谓词：用于「次数 ≥ n」这类不宜写成等值比较的判据。"""
    return lambda v: v.isdigit() and int(v) >= n


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "TOGGLES": [
            ("面板已建出", "是"), ("控件点击数", _at_least(9)),
            # 三个点击即生效/看到/落盘的关键计数必须与点击数补齐——
            # 只断言"点了 9 次"证明不了每一次都生效
            ("点击即生效", lambda v: "/" in v and v.split("/")[0] == v.split("/")[1]),
            ("落盘字段数", "8/8"), ("切语言后仍可见", "是"),
        ],
        "ROUNDTRIP": [
            ("往返字段", "全部一致"), ("往返差异", "无"),
            ("引擎侧生效", "是/是/是/是"), ("玩法项生效", "是/是/是/是"),
        ],
        "NEGATIVE": [
            ("损坏后未崩溃", "是"), ("回退默认值", "是"), ("损坏文件已归档", "是"),
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

    # 进程自述的结论也纳入（探针内部还有若干未逐项上报的判据）
    if res.get("结论") != "通过":
        ok = False
    # 失败项只要不是"无"，一律判不通过（避免探针内部判据与外部不一致时被放过）
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M22 设置面板无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 TOGGLES")
    ap.add_argument("--seconds", type=int, default=30)
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
    print("M22 设置面板无头实测 · %d 个场景" % len(scenes))
    print("  产物   %s" % rel(exe))
    print("  数据   %s" % data_dir)
    print("=" * 74)

    backed = backup(data_dir, ["settings.json", "save.json"])
    if backed:
        print("  已备份 %d 个用户文件（跑后恢复）\n" % len(backed))

    all_ok = True
    try:
        for scene, desc in scenes:
            clear_artifacts(data_dir)
            if scene == "NEGATIVE":
                # 前置：把设置文件写成半截 JSON。这份内容后续要逐字节比对归档结果。
                with open(os.path.join(data_dir, "settings.json"), "w",
                          encoding="utf-8", newline="") as f:
                    f.write(BROKEN_SETTINGS)

            print("  [%s] %s" % (scene, desc))
            log = run_scenario(exe, scene, args.seconds)
            res = parse_smoke(log)
            if not res:
                print("      → 不通过（未捕获 [SMOKE] 输出）")
                print("      " + "\n      ".join(log.splitlines()[-5:]))
                all_ok = False
                continue

            exc = count_exceptions(log)
            if exc:
                res["运行期异常"] = str(exc)

            ok, rows = check(scene, res)
            for (k, e, g, good) in rows:
                print("      %s %-16s 期望 %-10s 实际 %s" % (" " if good else "x", k, e, g))

            # NEGATIVE 的两条核心判据由脚本自己验，不采信进程自述
            if scene == "NEGATIVE":
                for prob in check_quarantine(data_dir) + check_settings_not_overwritten(data_dir):
                    print("      x %-16s %s" % ("进程外校验", prob))
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
    print("结论：%s" % ("通过 —— 控件点击即生效且落盘 / 重启后 11 个字段原样读回并作用到引擎 / "
                        "设置文件损坏时回退默认值且损坏文件被归档"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
