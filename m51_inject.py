#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M51 · 区分力注入（离开对局 / 房主到点）

做什么：
    每组只改一处产品代码 → 重新构建联机产物 → 跑一遍 net_longrun_probe.py
    （LEAVE 或 HOSTEXIT）→ 看**哪几条判据变红**。期望列事先写死，
    事后再挑就变成了解释而不是检验。

为什么这轮要注入：
    M51 新增的两个场景（LEAVE / HOSTEXIT）都是"整条链走到底"的判据，
    其中「离开对局」这条路 M50 修完之后**一次都没被跑过**——
    它可以一直是坏的而所有别的判据全绿。一组对照 + 三组定向破坏，
    才能说明这些判据真的在看着它们声称在看着的东西。

    Q2/Q3 是同一个场景的两种坏法，必须**分得开**：
      · Q2 让按钮点得到但回调不可能触发（旧 Stop 先退订）→ 5 红，**不含**"按钮点得到"
      · Q3 让按钮被遮罩盖住（回调本身是好的）→ 6 红，**含**"按钮点得到"
    如果这两组的失败面一样，那说明"点得到"这条判据是摆设。

用法：
    python m51_inject.py                # 全部四组
    python m51_inject.py --only Q2,Q3   # 只跑指定组
"""

import argparse
import io
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.join(ROOT, "StarPixelRogue")
SCRIPTS = os.path.join(PROJECT, "Assets", "_Project", "Scripts")
EXE = os.path.join(PROJECT, "Build", "SmokeNet", "StarPixelRogue.exe")
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PY = sys.executable
BAK = ".m51bak"

BACKEND = "Net/FishNetBackend.cs"
PANEL = "UI/GameOverPanel.cs"
RECONNECT = "Net/NetReconnect.cs"

LEAVE_HOST_JUDGES = ("开局对端在场", "对端离开后被清", "房主世界继续推进",
                     "房主自身不受影响", "全程零异常")
LEAVE_CLIENT_JUDGES = ("结算面板已建出", "「离开对局」按钮点得到", "离开后已离开会话",
                       "离开后大厅已重建", "离开后大厅有断线提示", "离开后世界已拆",
                       "离开后结算面板已收", "离开后本端玩家已清", "全程零异常")
HOSTEXIT_HOST_JUDGES = ("青少年模式且对局计时在走", "到点前弹出一次预警",
                        "到点恰好强制退出一次", "强制退出后已离开会话",
                        "强制退出后世界已拆", "强制退出后主菜单可见且有提示",
                        "全程零异常")
HOSTEXIT_CLIENT_JUDGES = ("房主到点后已离开会话", "被带回联机大厅", "大厅有断线提示",
                          "本地世界已拆", "全程零异常")

ALL_JUDGES = set(LEAVE_HOST_JUDGES) | set(LEAVE_CLIENT_JUDGES) | \
             set(HOSTEXIT_HOST_JUDGES) | set(HOSTEXIT_CLIENT_JUDGES)

# 非确定性的那一条：「全程零异常」在旧实现下取决于这一局有没有拿到链闪电
# （M50 Q2 实测 23,089 条 NRE 全部来自 PlayerController.FireChain，
# 换一局没拿到就只剩 0 条）。允许它出现，但不允许它顶替别的红。
FLAKY = {"全程零异常"}

CASES = [
    dict(
        id="Q1", scene="LEAVE", name="什么都不改（对照组）", edits=[], expect=set(),
        note="确认 LEAVE 场景本身不产生假红；也确认双开没被上一轮残留污染",
    ),
    dict(
        id="Q2", scene="LEAVE", name="离开对局：Stop 先退订回调（退回 M50 之前的形态）",
        edits=[(BACKEND,
                "            _stopHandled = false;\n"
                "            NetReconnect.Ensure()?.Cancel();\n",
                "            _stopHandled = false;\n"
                "            // 注入：M50 之前就是这样——先退订，于是 _intentionalStop 那条分支\n"
                "            // 永远走不到（模式不归零、大厅不重建、世界不拆）\n"
                "            if (_nm != null) _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;\n"
                "            NetReconnect.Ensure()?.Cancel();\n")],
        # 实际跑出来只有 4 条——差的两条是被注入证伪的假判据，改动记录在
        # NetLongRunProbe.WorldGone() 的注释里：
        #   · 「离开后本端玩家已清」：FishNet 断连会反生成 NetPlayer，
        #     OnStopClient 里那份 Unregister 无论世界拆没拆都会走到。
        #     它问的是"人走了吗"，这里注入的是"拆了没拆"，本来就不该红；
        #     它在 Q3（压根没点按钮）里才红。
        #   · 「离开后世界已拆」：原先看 EnemyManager.Instance，而联机世界里它
        #     **只建在房主侧**——客户端那边恒为 null，于是这条恒绿。
        #     改成 PoolManager.Instance（TeardownWorld 当帧同步清）之后才红得起来。
        expect={"离开后已离开会话", "离开后大厅已重建", "离开后大厅有断线提示",
                "离开后世界已拆"},
        note="按钮点得到、回调也执行了，但收尾那一整段永远不会跑——"
             "玩家眼前是：面板关了、世界还在转、还是联机态、没有大厅。",
    ),
    dict(
        id="Q3", scene="LEAVE", name="离开对局：按钮被一层全屏遮罩盖住",
        edits=[(PANEL,
                "                Core.UIFactory.CreateButton(panel.transform, \"LeaveBtn\",\n"
                "                    Core.Localization.T(\"gameover.leave\"), new Vector2(0, -50), new Vector2(260, 56),\n"
                "                    OnLeaveMatch);\n",
                "                Core.UIFactory.CreateButton(panel.transform, \"LeaveBtn\",\n"
                "                    Core.Localization.T(\"gameover.leave\"), new Vector2(0, -50), new Vector2(260, 56),\n"
                "                    OnLeaveMatch);\n"
                "                // 注入：全透明全屏遮罩盖在结算面板上（M48 那类缺陷的典型形态）\n"
                "                Core.UIFactory.CreatePanel(_root.transform, \"M51Overlay\", Vector2.zero,\n"
                "                    new Vector2(4000, 4000), new Color(0f, 0f, 0f, 0f));\n")],
        # 与 Q2 的差别就在这一条：Q2 是"点了没用"，Q3 是"根本点不到"。
        # 遮罩是不吃点击的透明层——肉眼、截图、`onClick.Invoke()` 全都看不出来。
        #
        # 「对端离开后被清」是 Q3 独有的红：盖住的按钮意味着连 Stop() 都没跑到，
        # 客户端从头到尾没断开，房主当然还数着两个人。Q2 里他已经真的断开了，
        # 房主侧照样看得见人少——这一条把"点了但没用"和"压根没点上"分开了。
        expect={"「离开对局」按钮点得到", "离开后已离开会话", "离开后大厅已重建",
                "离开后大厅有断线提示", "离开后世界已拆", "离开后本端玩家已清",
                "离开后结算面板已收", "对端离开后被清"},
        note="这条只有走真实点击路径才红：裸调 Invoke() 会照样点响、判据照样绿。",
    ),
    dict(
        id="Q4", scene="HOSTEXIT", name="房主到点：客户端重连永不放弃",
        edits=[(RECONNECT,
                "            _attempt++;\n"
                "            if (_attempt > _maxAttempts)\n",
                "            _attempt++;\n"
                "            if (false && _attempt > _maxAttempts)   // 注入：永不放弃\n")],
        # 四条红同源于一件事：客户端以为还能回去，于是永远停在重连遮罩里，
        # 一步都没走到"收尾"那一端。
        #
        # 「本地世界已拆」原先被排除在外（理由是"断线那一刻就拆了"）——注入把它证伪了：
        # 拆世界的正是 GiveUp 之后那段收尾，永不放弃就永远走不到。
        # 而且客户端那几条是**落点锁存**的（没回大厅就一次都不采样，详见 TickHostExit 注释），
        # 落点不对时它们一起停在默认值"否"，这四条必然同红。
        expect={"房主到点后已离开会话", "被带回联机大厅", "大厅有断线提示",
                "本地世界已拆"},
        note="房主把整间房带走后，客户端只会看到一次普通断线。"
             "M11 那套 5 次 × 1.5s 的全失败兜底要是坏了，玩家就永远卡在"
             "「正在重连 3/5」——而进程不崩、退出码 0，看起来一切正常。",
    ),
]

HOST_LOG = os.path.join(ROOT, "netlongrun_host.log")
CLIENT_LOG = os.path.join(ROOT, "netlongrun_client.log")
JUDGE_RE = re.compile(r"\[SMOKE\]\s*(.+?)\s{2,}(是|否)\s*$")


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    return io.open(p, encoding="utf-8").read()


def write(p, t):
    io.open(p, "w", encoding="utf-8").write(t)


def case_files(case):
    return [os.path.normpath(os.path.join(SCRIPTS, f)) for f, _o, _n in case["edits"]]


def assert_clean(case=None):
    """注入前后都不许有备份 / 注入标记残留（多组可能落在同一文件上，故按文件判）。"""
    allowed = set(case_files(case)) if case is not None else set()
    for dirpath, _dirs, files in os.walk(SCRIPTS):
        for n in files:
            if not n.endswith(BAK):
                continue
            p = os.path.normpath(os.path.join(dirpath, n))
            if p[:-len(BAK)] not in allowed:
                raise RuntimeError("残留备份: %s" % rel(p))
    seen = set()
    for c in CASES:
        for p in case_files(c):
            if p in seen:
                continue
            seen.add(p)
            if "// 注入：" in read(p) and p not in allowed:
                raise RuntimeError("残留注入: %s" % rel(p))


def inject(case):
    """每处替换必须恰好命中 1 次（命中 0 处 = 源码已变，注入失效）。"""
    for f, old, new in case["edits"]:
        p = os.path.join(SCRIPTS, f)
        if not os.path.exists(p + BAK):
            shutil.copy2(p, p + BAK)
        t = read(p)
        n = t.count(old)
        if n != 1:
            raise RuntimeError("%s: %s 期望命中 1 处，实际 %d 处 —— 源码已变，注入失效"
                               % (case["id"], f, n))
        write(p, t.replace(old, new))


def restore_all():
    for dirpath, _dirs, files in os.walk(SCRIPTS):
        for n in files:
            if not n.endswith(BAK):
                continue
            src = os.path.join(dirpath, n)
            shutil.move(src, src[:-len(BAK)])
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def build():
    log = os.path.join(ROOT, "unity_build_m51_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessNetBuild",
                    "-logFile", log.replace("\\", "/")],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    txt = io.open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run(scene):
    """跑一遍双开。返回 (退出码, 输出)。"""
    try:
        p = subprocess.run([PY, os.path.join(ROOT, "net_longrun_probe.py"),
                            "--scene", scene],
                           cwd=ROOT, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=420)
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return 99, out + "\n（超时）"


def red_judges():
    """从两端日志里收集值为「否」的判据名。

    只看**判据**（是/否 两态），不看信息行——信息行里也有"否"
    （如"首个异常 无"之外的字样），混进来会让失败面变脏。白名单再加一道。
    """
    red = set()
    for path in (HOST_LOG, CLIENT_LOG):
        if not os.path.exists(path):
            continue
        for line in io.open(path, encoding="utf-8", errors="replace"):
            m = JUDGE_RE.search(line)
            if not m:
                continue
            k, v = m.group(1).strip(), m.group(2)
            if k in ALL_JUDGES and v == "否":
                red.add(k)
    return red


def run_case(case):
    print("=" * 74)
    print("%s %s —— %s" % (case["id"], case["name"], case["note"]))
    print("=" * 74)

    inject(case)
    try:
        assert_clean(case)
        build()
        code, out = run(case["scene"])
        red = red_judges()
        verdict_lines = [l.strip() for l in out.splitlines() if l.startswith("结论：")]
        print("  场景 %s / 探针退出码 %d / %s"
              % (case["scene"], code, verdict_lines[-1] if verdict_lines else "（无结论行）"))
        print("  期望失败面 %s" % (sorted(case["expect"]) or "（全绿）"))
        print("  实际失败面 %s" % (sorted(red) or "（全绿）"))
        ok = (red == case["expect"] or
              (case["expect"] <= red and (red - case["expect"]) <= FLAKY))
        if red - case["expect"] - FLAKY:
            print("  多出来的红（注入解释不了，得回去查）: %s"
                  % sorted(red - case["expect"] - FLAKY))
        if case["expect"] - red:
            print("  少了的红（判据没在看着它该看的东西）: %s"
                  % sorted(case["expect"] - red))
        print("  → %s\n" % ("一致" if ok else "不一致"))
        return ok
    finally:
        restore_all()          # 每组必还原，否则下一组的"干净"是假的
        assert_clean(None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    args = ap.parse_args()

    ids = [s for s in args.only.split(",") if s]
    cases = [c for c in CASES if not ids or c["id"] in ids]
    if not cases:
        print("未知组: %s" % args.only)
        return 2
    if not os.path.exists(EXE):
        print("产物不存在: %s" % rel(EXE))
        return 2

    try:
        assert_clean(None)
    except RuntimeError as e:
        print("前置检查失败: %s" % e)
        return 2

    print("M51 离开对局 / 房主到点 · 区分力注入 %d 组" % len(cases))
    all_ok = True
    try:
        for c in cases:
            try:
                all_ok = run_case(c) and all_ok
            except RuntimeError as e:
                print("  [中断] %s\n" % e)
                all_ok = False
    finally:
        # 收尾再还原一次 + 重建：交付物必须是**没有注入**的那份
        restore_all()
        assert_clean(None)
        build()
        print("已还原源码并重建产物（交付物无注入残留）")

    print("=" * 74)
    print("总结果：%s" % ("四组全部一致" if all_ok else "有不一致，见上"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
