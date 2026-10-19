#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》git 推送自愈入口（M31 之后的流程补强）

背景：M31 收尾时连续 6 次 `git push` 报 403。当时 `gh auth status` 显示账号已登录、
scope 也齐全，很容易误判成"GitHub 抽风"或者"代理 502"。真正的原因是两条凭据链路各管各的：

  - `gh` 自己的令牌存在 keyring 里，gh 命令能正常调 API；
  - `git` 走的是另一套 credential helper，它并不知道 gh 有令牌，
    于是拿着旧凭据去推，被服务端拒了 —— 这才是那串 403。

修法是 `gh auth setup-git`：它会往 ~/.gitconfig 写入
`credential.https://github.com.helper = !gh auth git-credential`，
让 git 在推送时向 gh 索取令牌，两条链路就此对齐。

本脚本把这套排查固化下来，做了四件事：

  1. 推送前预检  gh 登录状态 / git credential helper 是否指向 gh / 远程可达 / 分支状态
  2. 自动修复   helper 缺失或没指向 gh 时，自动补一次 `gh auth setup-git`
  3. 失败自愈   推送撞上认证类错误（403 / 401 / Permission denied）时，
                重跑一次 setup-git 再重试一次推送
  4. 分类诊断   把常见失败归成 6 类并给出对应的下一步动作，
                避免再把"凭据失效"误读成"网络抖动"

用法：
    python push_safe.py                 # 预检 + 推送当前分支
    python push_safe.py --check         # 只做体检，不推送
    python push_safe.py --branch main   # 显式指定分支
    python push_safe.py --no-heal       # 关掉自动修复，只看诊断

退出码：0 成功 / 1 推送失败 / 2 预检不通过
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent

# 认证类错误特征：命中这些才值得重跑 setup-git 重试
AUTH_PATTERNS = [
    r"\b403\b",
    r"\b401\b",
    r"authentication failed",
    r"permission denied",
    r"could not read username",
    r"invalid username or password",
    r"bad credentials",
    r"the requested url returned error",
]

# 其余失败按特征归类，给出对应的处理建议
KNOWN_FAILURES = [
    # SSL 要排在"网络"前面：证书失败的输出里也带 unable to access，
    # 让网络类先匹配会把证书问题误报成网络抖动，给出的排查方向完全不同。
    (
        "SSL 证书问题",
        [r"ssl certificate", r"certificate verify failed", r"schannel"],
        "本机 git 配了自定义 CA（http.sslcainfo）。确认该证书文件未被替换或过期。",
    ),
    (
        "网络/代理不通",
        [r"failed to connect", r"timed? ?out", r"could not resolve host",
         r"connection reset", r"unable to access", r"recv failure"],
        "检查代理与网络连通性：`git ls-remote origin` 能否返回；"
        "注意 http.sslcainfo 指向的自定义 CA bundle 是否仍然存在。",
    ),
    (
        "远程有新提交（非快进）",
        [r"non-fast-forward", r"rejected.*fetch first", r"updates were rejected"],
        "先 `git pull --rebase origin <分支>` 再推送。切勿直接 force push 共享分支。",
    ),
    (
        "仓库不存在或无权限",
        [r"repository not found", r"could not find repository"],
        "确认 remote url 正确、仓库未被删、且当前账号是该仓库的成员。",
    ),
    (
        "走了 SSH 但没配密钥",
        [r"permission denied \(publickey\)", r"git@github\.com.*denied"],
        "remote 用的是 SSH 地址但本机没有可用密钥；改用 https 远程，或配置 SSH key。",
    ),
]


def run(cmd, cwd=REPO_ROOT, timeout=180):
    """执行命令，返回 (returncode, stdout+stderr 合并文本)。"""
    try:
        p = subprocess.run(
            cmd, cwd=str(cwd), timeout=timeout,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
        out = p.stdout.decode("utf-8", errors="replace")
        return p.returncode, out
    except FileNotFoundError:
        return 127, "命令不存在: %s" % cmd[0]
    except subprocess.TimeoutExpired:
        return 124, "命令超时(%ss): %s" % (timeout, " ".join(cmd))


def ok(msg):
    print("  [OK]   %s" % msg)


def warn(msg):
    print("  [WARN] %s" % msg)


def bad(msg):
    print("  [FAIL] %s" % msg)


def section(title):
    print("\n== %s ==" % title)


# --------------------------------------------------------------------------
# 预检项
# --------------------------------------------------------------------------

def check_git_repo():
    """1. 确认当前目录确实是一个 git 仓库。"""
    rc, out = run(["git", "rev-parse", "--is-inside-work-tree"])
    if rc == 0 and out.strip() == "true":
        ok("git 仓库: %s" % REPO_ROOT)
        return True
    bad("当前目录不是 git 仓库")
    return False


def check_remote(remote):
    """2. 确认 remote 存在且可达。"""
    rc, out = run(["git", "remote", "get-url", remote])
    if rc != 0:
        bad("没有名为 %r 的 remote" % remote)
        return None
    url = out.strip()
    rc2, _ = run(["git", "ls-remote", remote, "HEAD"], timeout=60)
    if rc2 != 0:
        bad("remote 不可达: %s" % url)
        return None
    ok("remote %s -> %s （可达）" % (remote, url))
    return url


def check_gh():
    """3. 确认 gh 已安装并登录。"""
    if shutil.which("gh") is None:
        warn("未安装 gh，无法使用 gh 令牌通道（将依赖系统自带凭据）")
        return False
    rc, out = run(["gh", "auth", "status"], timeout=60)
    if rc != 0 or "Logged in to github.com" not in out:
        bad("gh 未登录：先执行 `gh auth login`")
        return False
    login = re.search(r"account ([^\s]+)", out)
    ok("gh 已登录: %s" % (login.group(1) if login else "?"))
    # repo scope 是推送的必要条件
    if "repo" not in out.replace("'", ""):
        warn("令牌 scope 里没看到 repo，推送可能被拒")
    return True


def check_helper():
    """4. 确认 git credential helper 指向 gh —— 这是 403 的根因所在。"""
    rc, out = run(["git", "config", "--get-all", "credential.https://github.com.helper"])
    values = [v.strip() for v in out.splitlines() if v.strip()]
    if rc != 0 or not values:
        bad("github.com 未配置 credential helper（git 拿不到 gh 的令牌 -> 典型 403）")
        return False
    if any("gh" in v and "git-credential" in v for v in values):
        ok("credential helper 已指向 gh")
        return True
    bad("credential helper 存在但不是 gh: %s" % "; ".join(values))
    return False


def check_credential_fill():
    """5. 端到端验证：真的能从 helper 取到一枚令牌。"""
    url_rc, url_out = run(["git", "remote", "get-url", "origin"])
    if url_rc != 0:
        return None
    m = re.match(r"https?://([^/]+)/(.+?)(?:\.git)?$", url_out.strip())
    if not m:
        warn("remote 不是 http(s)，跳过凭据取用测试")
        return None
    host, path = m.group(1), m.group(2)
    payload = "protocol=https\nhost=%s\npath=%s.git\n\n" % (host, path)
    try:
        p = subprocess.run(
            ["git", "credential", "fill"], cwd=str(REPO_ROOT), timeout=60,
            input=payload.encode(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
        out = p.stdout.decode("utf-8", errors="replace")
    except subprocess.TimeoutExpired:
        warn("凭据取用超时")
        return None
    if "password=" in out:
        pwd = [l for l in out.splitlines() if l.startswith("password=")]
        # 只显示前缀，绝不打印完整令牌
        shown = pwd[0][len("password="):][:6] + "***"
        ok("可取到令牌: %s" % shown)
        return True
    bad("helper 没能返回令牌：推送大概率 403")
    return False


def check_branch_state(branch):
    """6. 报告分支与待推送提交数。"""
    if branch is None:
        rc, out = run(["git", "rev-parse", "--abbrev-ref", "HEAD"])
        branch = out.strip() if rc == 0 else "(detached)"
    ok("当前分支: %s" % branch)
    rc, out = run(["git", "rev-list", "--count", "origin/%s..%s" % (branch, branch)])
    if rc == 0 and out.strip():
        n = out.strip()
        if n == "0":
            warn("没有领先 origin 的提交（无需推送）")
        else:
            ok("领先 origin/%s %s 个提交" % (branch, n))
    rc2, out2 = run(["git", "status", "--porcelain"])
    if rc2 == 0 and out2.strip():
        warn("有未提交的工作区改动（%s 个文件），它们不会被推送"
             % len([l for l in out2.splitlines() if l.strip()]))
    return branch


def setup_git():
    """执行 gh auth setup-git，把 git 的凭据通道接到 gh 上。"""
    print("    执行: gh auth setup-git")
    rc, out = run(["gh", "auth", "setup-git"], timeout=60)
    if rc == 0:
        ok("已重写 credential helper")
        return True
    bad("gh auth setup-git 失败:\n%s" % out.strip())
    return False


# --------------------------------------------------------------------------
# 诊断
# --------------------------------------------------------------------------

def classify(text):
    """把失败输出归类，返回 (类别名, 建议)；命中认证类返回 None 表示可自愈。"""
    low = text.lower()

    # SSH 密钥错误里也带 "Permission denied" 字样，但那是密钥缺失、不是 https 凭据失效，
    # 必须在认证判断之前先拦下来，否则会白白跑一次 setup-git 重试（两者症状不同、修法不同）。
    # 这里只认 "(publickey)" 这一确切特征：像 "Could not read from remote repository"
    # 这种通用尾巴会跟 Repository not found 一起出现，拿它当判据会把仓库不存在误判成 SSH 问题。
    if re.search(r"permission denied \(publickey\)", low):
        return ("走了 SSH 但没配密钥",
                "remote 用的是 SSH 地址但本机没有可用密钥；改用 https 远程，或配置 SSH key。")

    for pat in AUTH_PATTERNS:
        if re.search(pat, low):
            return None  # 认证类 -> 走自愈重试
    for name, pats, advice in KNOWN_FAILURES:
        for pat in pats:
            if re.search(pat, low):
                return name, advice
    return "未分类", "把下面的原始输出贴出来排查。"


def do_push(remote, branch, dry_run):
    cmd = ["git", "push"]
    if dry_run:
        cmd.append("--dry-run")
    cmd += [remote, branch]
    return run(cmd, timeout=300)


# --------------------------------------------------------------------------
# 自测：分类判据的区分力
# --------------------------------------------------------------------------
# 每条用例给出 (名称, 原始输出片段, 期望)。期望为 None 表示"认证类、应走自愈重试"。
# 重点在于互不串扰：带 Permission denied 的 SSH 密钥错误不能落进认证类，
# 网络超时也不能被 403 之类误吞。
SELFTEST_CASES = [
    ("https 403 凭据失效",
     "remote: Permission to kiony6/x.git denied to kiony6.\n"
     "fatal: unable to access 'https://github.com/x/': The requested URL returned error: 403",
     None),
    ("Authentication failed",
     "fatal: Authentication failed for 'https://github.com/kiony6/x.git/'",
     None),
    ("401 坏凭据",
     "remote: Bad credentials\nerror: 401 Unauthorized",
     None),
    ("SSH 密钥缺失（不该走自愈）",
     "git@github.com: Permission denied (publickey).\n"
     "fatal: Could not read from remote repository.",
     "走了 SSH 但没配密钥"),
    ("网络超时",
     "fatal: unable to access 'https://github.com/x/': Failed to connect to github.com "
     "port 443: Timed out",
     "网络/代理不通"),
    ("非快进被拒",
     "! [rejected] main -> main (non-fast-forward)\n"
     "error: failed to push some refs to 'https://github.com/x/'",
     "远程有新提交（非快进）"),
    ("仓库不存在",
     "ERROR: Repository not found.\nfatal: Could not read from remote repository.",
     "仓库不存在或无权限"),
    ("SSL 证书失败",
     "fatal: unable to access 'https://github.com/x/': SSL certificate problem: "
     "certificate verify failed",
     "SSL 证书问题"),
]


def selftest():
    print("=" * 62)
    print("push_safe.py 分类判据自测")
    print("=" * 62)
    passed = failed = 0
    for name, text, expect in SELFTEST_CASES:
        got = classify(text)
        got_name = "认证类(自愈重试)" if got is None else got[0]
        expect_name = "认证类(自愈重试)" if expect is None else expect
        if got_name == expect_name:
            print("  [OK]   %-24s -> %s" % (name, got_name))
            passed += 1
        else:
            print("  [FAIL] %-24s -> 得到 %s，期望 %s" % (name, got_name, expect_name))
            failed += 1
    print("\n结果: %d/%d 通过" % (passed, len(SELFTEST_CASES)))
    return 0 if failed == 0 else 1


# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description="带自愈的 git 推送入口")
    ap.add_argument("--selftest", action="store_true", help="只跑分类判据自测")
    ap.add_argument("--remote", default="origin")
    ap.add_argument("--branch", default=None, help="默认推送当前分支")
    ap.add_argument("--check", action="store_true", help="只体检，不推送")
    ap.add_argument("--dry-run", action="store_true", help="演练推送，不真正写入远程")
    ap.add_argument("--no-heal", action="store_true", help="关闭自动修复")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    print("=" * 62)
    print("git 推送自愈入口  仓库: %s" % REPO_ROOT.name)
    print("=" * 62)

    # ---------- 预检 ----------
    section("预检")
    if not check_git_repo():
        return 2
    url = check_remote(args.remote)
    if url is None:
        return 2
    gh_ok = check_gh()
    helper_ok = check_helper()
    _ = check_credential_fill()
    branch = check_branch_state(args.branch)

    # ---------- 自动修复 ----------
    if gh_ok and not helper_ok and not args.no_heal:
        section("自动修复")
        helper_ok = setup_git()
        if helper_ok:
            check_credential_fill()

    if args.check:
        section("体检结论")
        if gh_ok and helper_ok:
            ok("凭据链路正常，可以推送")
            return 0
        bad("凭据链路有问题，推送预计会 403")
        if args.no_heal:
            print("    提示：当前开了 --no-heal，去掉该参数可自动修复")
        return 2

    # ---------- 推送 ----------
    section("推送" + ("（演练）" if args.dry_run else ""))
    rc, out = do_push(args.remote, branch, args.dry_run)
    print("    退出码: %d" % rc)
    if out.strip():
        for line in out.strip().splitlines():
            print("    | %s" % line)

    if rc == 0:
        ok("推送成功")
        return 0

    # ---------- 失败诊断 + 自愈重试 ----------
    kind = classify(out)
    if kind is None:
        if args.no_heal:
            section("诊断")
            bad("认证被拒（403/401）。开着 --no-heal，未自动修复。")
            print("    手动修法: gh auth login && gh auth setup-git")
            return 1
        section("自愈重试")
        warn("撞上认证类错误，重接 gh 凭据通道后重试一次")
        if setup_git():
            rc2, out2 = do_push(args.remote, branch, args.dry_run)
            print("    重试退出码: %d" % rc2)
            if out2.strip():
                for line in out2.strip().splitlines():
                    print("    | %s" % line)
            if rc2 == 0:
                ok("重试推送成功")
                return 0
            out = out2
            kind = classify(out) or ("认证仍被拒", "确认 gh 令牌未过期且含 repo scope；"
                                                    "必要时 `gh auth refresh -s repo` 后重跑。")

    name, advice = kind
    section("诊断")
    bad("推送失败：%s" % name)
    print("    建议：%s" % advice)
    return 1


if __name__ == "__main__":
    sys.exit(main())
