#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M54 · 区分力注入（登录注册 / 主菜单四颗按钮）

做什么：
    每组只改一处产品代码 → 重新构建无头产物 → 跑一遍 build_probe.py 的对应场景
    → 看**哪几条判据变红**。期望列事先写死在这里，事后再挑就变成了解释而不是检验。

为什么这轮要注入：
    M54 加的是"进门"这一层，而"进门"最容易被假绿蒙过去：
      · 登录页弹出来了，和"弹出来同时人已经进去了"，在"页面建出来了"上完全一样；
      · 提示写了密码错误，和"提示写了但其实已经登录成功"，在"有提示"上完全一样；
      · 四颗按钮都在，和"四颗都在但有一颗没挂回调"，在"按钮可达"上完全一样。
    "可达"只看射线挡没挡住，不看回调挂没挂——所以本轮专门有两组去摘回调。

    Q3 与 Q5 的分工是本轮最要紧的一处：
      Q3 掐掉"注册后登录" → 专属判据「注册后已登录」红
      Q5 掐掉"密码比对"   → 专属判据「密码错误被挡」红
    两者都会拉红「断点通过」，只数总数分不出是谁；各有一条专属判据才抓得住。

    Q8 的红面**故意包含** Q7 的三条：摘掉大厅的返回按钮之后人就关在大厅里，
    走不到单人模式——这个连锁是真实因果，不是判据陪跑；
    本组专属的是多出来的那条「联机入口已退出」。

用法：
    python m54_inject.py                # 全部八组
    python m54_inject.py --only Q2,Q7   # 只跑指定组
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
EXE = os.path.join(PROJECT, "Build", "Smoke", "StarPixelRogue.exe")
UNITY = r"E:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe"
PY = sys.executable
BAK = ".m54bak"

ACCOUNT = "Core/AccountStore.cs"
GATE = "UI/AccountGate.cs"
LOGIN = "UI/LoginPanel.cs"
MENU = "UI/MainMenu.cs"
LOBBY = "Net/NetLobby.cs"

# 两个场景各自的判据全集（只收 build_probe.py 里逐行打印的那些判据）
JUDGES = {
    "AUTH": {"登录页已建出", "未登录已拦截", "首屏为注册页", "提交按钮可达",
             "注册后进主菜单", "注册后已登录", "密码错误被挡", "记住密码已回填",
             "自动登录成功", "退出登录已生效", "断点通过", "被挡下明细"},
    "MENU": {"主菜单按钮可达", "不可达明细", "排行榜已建出", "排行榜按钮可达",
             "排行榜不可达明细", "排行榜关闭已收", "设置面板已建出", "设置按钮可达",
             "设置不可达明细", "设置关闭已收", "联机按钮有回应", "联机入口已退出",
             "单人二级页已建出", "开始游戏已点击", "开局世界已建出"},
}

# build_probe.py 的判据行形如：
#   "      x 未登录已拦截           期望 是          实际 否"
# 第一个字符是 x（不达标）或空格（达标）
JUDGE_RE = re.compile(r"^\s{4,}(?P<mark>x| )\s+(?P<key>\S+)\s+期望\s+(?P<exp>\S+)\s+实际\s+(?P<got>.*)$")

CASES = [
    dict(
        id="Q1", scene="AUTH", name="什么都不改（对照组）", edits=[], expect=set(),
        note="确认 AUTH 场景本身不产生假红——否则后面几组的红都可能是这一组自带的",
    ),
    dict(
        id="Q2", scene="AUTH", name="未登录时不收主菜单（登录页照弹，底下的菜单照露）",
        edits=[(GATE,
                "            MainMenu.Instance.Hide();\n"
                "            _panel.Show(() => MainMenu.Instance.Show());",
                "            // 注入：登录页照弹，但主菜单不收——闸门形同虚设。\n"
                "            // 玩家看到登录页盖在菜单上，而菜单其实已经可用。\n"
                "            _panel.Show(() => MainMenu.Instance.Show());")],
        expect={"未登录已拦截"},
        note="只有「未登录已拦截」这一条看得见它：登录页照样建出来、照样能注册登录，\n"
             "     其余判据全绿是**对的**——它们问的本来就不是这件事。",
    ),
    dict(
        id="Q3", scene="AUTH", name="注册完不再登录（回退本轮修掉的那个 bug）",
        edits=[(LOGIN,
                "            if (_registerMode)\n"
                "            {\n"
                "                var logged = store.Login(AccountText, PasswordText, _remember);",
                "            if (false)   // 注入：注册完不登录。按钮上写着注册并登录，\n"
                "            {            // 而后面每一处（战绩/排行榜）都按当前账号取数。\n"
                "                var logged = store.Login(AccountText, PasswordText, _remember);")],
        expect={"注册后已登录", "断点通过", "被挡下明细"},
        note="专属判据是「注册后已登录」：主菜单照样进得去（回调照样调），\n"
             "     所以「注册后进主菜单」仍然绿——这正是不能只看那一条的原因。",
    ),
    dict(
        id="Q4", scene="AUTH", name="记住密码不再落盘（勾了也白勾）",
        edits=[(ACCOUNT,
                '            SetRemember(remember ? name : "", remember ? pwd : "");',
                '            // 注入：记住的凭据不再落盘——勾了记住密码，下次还是空的。\n'
                '            SetRemember("", "");')],
        expect={"记住密码已回填", "自动登录成功", "断点通过", "被挡下明细"},
        note="「记住密码已回填」必须红：探针在退出登录前会先把两个输入框清空，\n"
             "     所以框里剩下的东西只可能来自记住的凭据——不改这一处的话，\n"
             "     这条判据读到的只是上次填过的残留，注入前它本来就是假绿。",
    ),
    dict(
        id="Q5", scene="AUTH", name="登录不再比对密码（任何人都能登）",
        edits=[(ACCOUNT,
                "            if (!string.Equals(acc.hash, Hash(acc.salt, pwd), StringComparison.Ordinal)) return Code.WrongPassword;",
                "            // 注入：密码不比对——填什么都登得进来。\n"
                "            if (false) return Code.WrongPassword;")],
        expect={"密码错误被挡", "记住密码已回填", "自动登录成功", "断点通过", "被挡下明细"},
        note="多红的「自动登录成功」不是陪跑，是连锁：密码错误那一次被放进来之后，\n"
             "     后面每一步都站在已经登录的错状态上，登录页被藏起来、提交再也点不到，\n"
             "     记住与自动登录自然全废（「记住密码已回填」因此一起红）——\n"
             "     一个放水的校验能把整条链路带歪，这正是要看到的。",
    ),
    dict(
        id="Q6", scene="MENU", name="排行榜按钮留着但不挂回调",
        edits=[(MENU,
                "                new Vector2(180, -88), new Vector2(320, 58), OnRank, 21,",
                "                // 注入：按钮还在、还点得到，就是点完没反应（一颗摆设）。\n"
                "                new Vector2(180, -88), new Vector2(320, 58), null, 21,")],
        expect={"排行榜已建出", "排行榜按钮可达", "排行榜不可达明细", "排行榜关闭已收"},
        note="「主菜单按钮可达」必须仍然 4/4：可达只看射线挡没挡住，不看回调挂没挂——\n"
             "     所以按钮都在从来证明不了按钮都能用。",
    ),
    dict(
        id="Q7", scene="MENU", name="单人模式按钮留着但不挂回调",
        edits=[(MENU,
                "                new Vector2(180, 70), new Vector2(320, 68), OnSolo, 24,",
                "                // 注入：整条单人链路的入口被摘掉。\n"
                "                new Vector2(180, 70), new Vector2(320, 68), null, 24,")],
        expect={"单人二级页已建出", "开始游戏已点击", "开局世界已建出"},
        note="与 Q6 的失败面完全不相交：两颗按钮各自那条链路互不影响，\n"
             "     相交就说明有一条判据是在陪跑。",
    ),
    dict(
        id="Q8", scene="MENU", name="摘掉大厅的「返回主菜单」",
        edits=[(LOBBY,
                '            CreateButton(_root.transform, Core.Localization.T("net.back"), new Vector2(260, -70), OnBack, "BackBtn");',
                "            // 注入：大厅变回单向门——进来只能选一种模式开局，出不去。")],
        expect={"联机入口已退出", "单人二级页已建出", "开始游戏已点击", "开局世界已建出"},
        note="红面**故意**含 Q7 那三条：人关在大厅里就走不到单人模式，这是真实因果。\n"
             "     本组专属的是多出来的「联机入口已退出」。",
    ),
]


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def read(p):
    return io.open(p, encoding="utf-8").read()


def case_files(case):
    out = set()
    for relpath, _o, _n in case["edits"]:
        out.add(os.path.normpath(os.path.join(SCRIPTS, relpath.replace("/", os.sep))))
    return out


def inject(case):
    """把改动写进源码（先备份原文件）。"""
    for relpath, old, new in case["edits"]:
        path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
        bak = path + BAK
        if not os.path.exists(bak):
            shutil.copy2(path, bak)
        src = read(path)
        if old not in src:
            raise RuntimeError("锚点未命中: %s\n---\n%s" % (relpath, old))
        src = src.replace(old, new, 1)
        io.open(path, "w", encoding="utf-8", newline="").write(src)


def restore_all():
    """把所有 .m54bak 还原回去（顺带删掉 Unity 为备份文件生成的 .meta）。"""
    for dirpath, _dirs, names in os.walk(SCRIPTS):
        for n in names:
            if not n.endswith(BAK):
                continue
            src = os.path.join(dirpath, n)
            shutil.move(src, src[:-len(BAK)])
            meta = src + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def assert_clean(case=None):
    """注入前后都不许有**上一组**的备份 / 注入标记残留。

    本组自己刚生成的备份是允许的——否则"注入完立刻自检"永远报错。
    """
    allowed = case_files(case) if case is not None else set()
    for dirpath, _dirs, names in os.walk(SCRIPTS):
        for n in names:
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
    if case:
        # 注入**之后**要查的是"改动真的落上了"，不是"锚点还在"——
        # 锚点被替换掉本来就是注入的目的。
        for relpath, _old, new in case["edits"]:
            path = os.path.join(SCRIPTS, relpath.replace("/", os.sep))
            if new not in read(path):
                raise RuntimeError("注入没落上（替换写歪了）: %s" % relpath)


def build():
    log = os.path.join(ROOT, "unity_build_m54_inject.log")
    subprocess.run([UNITY, "-batchmode", "-nographics", "-quit",
                    "-projectPath", PROJECT.replace("\\", "/"),
                    "-executeMethod", "StarPixel.EditorTools.BuildScript.HeadlessBuild",
                    "-logFile", log.replace("\\", "/")],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    txt = io.open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    if "error CS" in txt:
        errs = [l for l in txt.splitlines() if "error CS" in l][:5]
        raise RuntimeError("注入后编译失败:\n" + "\n".join(errs))


def run(scene):
    """跑一遍 build_probe 的单个场景，返回 (退出码, 输出)。"""
    try:
        p = subprocess.run([PY, os.path.join(ROOT, "build_probe.py"), "--only", scene],
                           cwd=ROOT, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=420)
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = e.stdout if isinstance(e.stdout, str) else (e.stdout or b"").decode("utf-8", "replace")
        return 99, out + "\n（超时）"


def red_judges(scene, out):
    """从探针输出里收集**变红**的判据名（只看本场景的判据全集）。"""
    red = set()
    for line in out.splitlines():
        m = JUDGE_RE.search(line)
        if not m:
            continue
        k = m.group("key").strip()
        if k not in JUDGES[scene]:
            continue
        if m.group("mark") == "x":
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
        red = red_judges(case["scene"], out)
        verdict = [l.strip() for l in out.splitlines() if l.strip().startswith("→")]
        print("  场景 %s / 探针退出码 %d / %s"
              % (case["scene"], code, verdict[-1] if verdict else "（无结论行）"))
        print("  期望失败面 %s" % (sorted(case["expect"]) or "（全绿）"))
        print("  实际失败面 %s" % (sorted(red) or "（全绿）"))
        ok = (red == case["expect"])
        if red - case["expect"]:
            print("  多出来的红（注入解释不了，得回去查）: %s" % sorted(red - case["expect"]))
        if case["expect"] - red:
            print("  少了的红（判据没在看着它该看的东西）: %s" % sorted(case["expect"] - red))
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

    print("M54 登录注册与主菜单 · 区分力注入 %d 组" % len(cases))
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
    print("总结果：%s" % ("八组全部一致" if all_ok else "有不一致，见上"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
