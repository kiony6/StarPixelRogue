#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》M25 手感层（镜头 / 顿帧 / 暂停 / 震屏 / 反馈开关）无头实测

做什么：
    拉起 6 个无头进程，把此前**从没被判过分**的手感层逐条走一遍：
      STAGE    真实启动路径 PrepareStage → 菜单停留 1.5s → Build，
               相机正交尺寸必须始终在基准 8 附近（不得被自己拉塌）
      ZOOM     Boss 出场推近必须生效、必须回弹到基准；
               推近进行中被 Configure 不得把基准改小（否则逐波叠乘）
      HITSTOP  顿帧必须真的压 timeScale（受伤事件 0.18 / 自定义 0.04），
               且**真实时长**≈请求时长 0.30s（证明计时走 unscaledDeltaTime）
      PAUSE    选卡暂停四条保证：归零 / 暂停中的顿帧被忽略 / 放开归 1 /
               暂停必须清掉在飞的顿帧（不残留 0.04）
      SHAKE    震屏必须偏得出去、静置必须衰减完、**暂停中也必须在真实时间里衰减完**
      GATE     关掉「屏幕震动」后，顿帧 / 震屏 / 推近三条反馈通道必须全部不发生

为什么必须测：
    这是全项目唯一"输出只在画面上"的一层。M1–M24 全部用 -nographics 的无头产物验收，
    而手感层的全部产出都是"玩家看到了什么"：镜头推近多少、顿帧把时间压到多少、
    震屏偏了多少像素。于是下面这些实现在既有 24 轮实测里都能全绿：
      1) 相机正交尺寸被自己持续拉向 0，玩家看到的是糊成几个像素的画面；
      2) 顿帧计时误用被缩放的 deltaTime，一次顿帧变成十几秒慢动作；
      3) 震屏在暂停态永不衰减，选卡面板背后的世界一直抽搐；
      4) 关掉「屏幕震动」后 Boss 出场推近照旧。
    本脚本把这些"看得见"的量翻译成可断言的数值，让手感层第一次真正被判分。

判据的区分力（本脚本最要紧的地方）：
    1) 缩放一律采信探针从 Camera.orthographicSize **实测**出来的值，
       并由本脚本在进程外复算一遍（不采信被测对象自己的账本）。
    2) 顿帧时长用**墙钟**度量，并故意挑 duration=0.30 / scale=0.04 这组参数：
       计时若误用缩放后的 deltaTime，一次顿帧会被自己的 timeScale 拖成 ~7.5 秒，
       判据立刻爆红，而不是"好像慢了一点点"。
    3) 震屏幅度用**采样窗口内的最大偏移**（随机偏移每帧都换，取瞬时值会时红时绿）。
    4) 每个"必须发生"的判据都配一个"必须结束"的对偶判据：
       只测单边的话，"永远推近"和"从不推近"各能骗过一半。
    5) 暂停相关的等待在探针里一律走 unscaledDeltaTime 自累加 ——
       WaitForSeconds 会被 timeScale=0 拖成永不返回，场景会直接挂死。

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
    ("STAGE", "真实启动路径下相机基准缩放不得被自己拉塌"),
    ("ZOOM", "Boss 出场推近必须生效且回弹，中途 Configure 不得改小基准"),
    ("HITSTOP", "顿帧必须真的压时间，且真实时长≈请求时长"),
    ("PAUSE", "选卡暂停四条保证：归零 / 忽略顿帧 / 放开归 1 / 清掉在飞的顿帧"),
    ("SHAKE", "震屏必须偏得出去、静置与暂停中都必须衰减完"),
    ("GATE", "关掉反馈开关后顿帧 / 震屏 / 推近必须全部不发生"),
)

BACKUP_SUFFIX = ".m25bak"

BASE_ORTHO = 8.0      # GameBuilder.SetupCamera 里写死的正交尺寸
TIMEOUT = {"STAGE": 120, "ZOOM": 90, "HITSTOP": 90,
           "PAUSE": 90, "SHAKE": 90, "GATE": 90}


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
    args = [exe, "-batchmode", "-nographics", "-feelProbe", "-feelScene", scene]
    limit = TIMEOUT.get(scene, 120) + 60
    try:
        p = subprocess.run(args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=limit)
        log = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        log = (e.stdout or "") if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        log += "\n[SMOKE] 结论              不通过（超时）"
    return log


def _num(v):
    """把 '0.300s' / '8.00000' 之类的字符串取成 float；取不出来返回 None。"""
    try:
        return float(str(v).strip().rstrip("sS"))
    except (TypeError, ValueError):
        return None


def _yes(v):
    return v == "是"


def _near_base(v):
    """正交尺寸必须仍在基准 8 附近（进程外复算，不采信探针的自述）。"""
    n = _num(v)
    return n is not None and abs(n - BASE_ORTHO) <= 0.5


def _near_hitstop(v):
    """顿帧真实时长必须≈0.30s：误用缩放 deltaTime 时会被拖到 ~7.5s。"""
    n = _num(v)
    return n is not None and 0.20 <= n <= 0.60


def check(scene, res):
    """场景判据。返回 (通过?, [(项, 期望, 实际, 是否达标)])"""
    spec = {
        "STAGE": [
            ("菜单期缩放", _near_base),
            ("开局后缩放", _near_base),
            ("菜单期保持", _yes),
            ("开局后正确", _yes),
            ("断点通过", "2/2"),
        ],
        "ZOOM": [
            ("静置基准", _near_base),
            ("推近生效", _yes),
            ("回弹基准", _yes),
            ("基准不被改小", _yes),
            ("断点通过", "4/4"),
        ],
        "HITSTOP": [
            ("事件链路通", _yes),
            ("真实时长", _near_hitstop),
            ("时长正确", _yes),
            ("结束后归1", _yes),
            ("断点通过", "4/4"),
        ],
        "PAUSE": [
            ("暂停归零", _yes),
            ("顿帧被忽略", _yes),
            ("放开归1", _yes),
            ("不残留顿帧", _yes),
            ("断点通过", "4/4"),
        ],
        "SHAKE": [
            ("震屏生效", _yes),
            ("静置衰减", _yes),
            ("暂停中衰减", _yes),
            ("放开后归位", _yes),
            ("断点通过", "4/4"),
        ],
        "GATE": [
            ("顿帧被关", _yes),
            ("震屏被关", _yes),
            ("推近被关", _yes),
            ("断点通过", "3/3"),
        ],
    }.get(scene)

    if spec is None:
        return False, [("未知场景", "-", scene, False)]

    rows, ok = [], True
    for key, expect in spec:
        got = res.get(key, "?")
        good = expect(got) if callable(expect) else (got == expect)
        shown = "≈8" if expect is _near_base else (
            "≈0.30s" if expect is _near_hitstop else
            ("是" if expect is _yes else expect))
        rows.append((key, shown, got, good))
        ok = ok and good

    if res.get("结论") != "通过":
        ok = False
    if res.get("失败项", "无") != "无":
        ok = False
    return ok, rows


def main():
    ap = argparse.ArgumentParser(description="M25 手感层无头实测")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--data-dir", default=None)
    ap.add_argument("--only", default=None, help="只跑指定场景，如 STAGE")
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
    print("M25 手感层（镜头/顿帧/暂停/震屏/反馈开关）无头实测 · %d 个场景" % len(scenes))
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

            # 实测数值行全部复读：它们不是独立判据，但失败时能直接指出
            # 是"压根没生效"还是"生效了没收回去"（M22 教训：分开计数才能指到断在哪一环）
            for extra in ("菜单期缩放", "开局后缩放", "静置基准", "推近中", "回弹后",
                          "中途Configure后", "事件链路压到", "顿帧压到", "真实时长",
                          "结束后", "暂停时", "暂停中顿帧", "放开后", "顿帧被清掉",
                          "震屏幅度", "静置后", "暂停中", "放开后", "开关后顿帧",
                          "开关后震屏", "开关后缩放"):
                if extra in res and res[extra] not in ("", "无"):
                    print("        %-14s %s" % (extra, res[extra]))
            if res.get("失败项", "无") != "无":
                print("        %-14s %s" % ("失败项", res["失败项"]))

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
    print("结论：%s" % ("通过 —— 相机基准不再被自己拉塌 / 推近生效且回弹 / 顿帧真的压时间且时长正确 / "
                        "选卡暂停四条保证成立 / 震屏在暂停态也会衰减完 / 关掉开关后三条反馈通道全关"
                        if all_ok else "不通过"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
