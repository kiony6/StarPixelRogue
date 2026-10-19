#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
《星界像素狂潮》工程静态自检（M15）

背景：本项目在没有 Unity 编译器的环境下迭代。此前每个里程碑都靠临时写一段脚本做人工静态
校验，既零散又容易漏——而真实踩到的坑恰恰都是这类"肉眼扫过去发现不了"的问题。
本脚本把这些校验固化为可重复执行的「工程体检」，覆盖过去真实踩过的五类坑：

  1. 括号平衡              手改多行代码块最容易漏括号
  2. Fish-Net 条件编译一致性 整文件 #if FISHNET_ENABLED 保护的类型，若被无保护代码引用，
                             未装插件时直接编译失败（M14 亲自核查过的一项）
  3. 枚举分支覆盖          新增枚举值忘记在某个 switch 里补分支（M9 加 Mine 时的手工核对项）
  4. Resources 资源存在性   Resources.Load("x") 找不到文件 = 运行时静默失败，不报错
  5. 同类重复成员           重复声明会"后写覆盖先写"（M9 在模拟器里真实踩过 UNLOCKABLE_IDS）
  6. 缺少 using              用了某命名空间的类型却没导入（M17/M18 实测发现 4 处，此前查不出）

用法：  python audit_project.py
退出码：0 = 无 ERROR；1 = 存在 ERROR（WARN/INFO 不计入）
"""

import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.abspath(__file__))
SCRIPTS_DIR = os.path.join(ROOT, "StarPixelRogue", "Assets", "_Project", "Scripts")
ASSETS_DIR = os.path.join(ROOT, "StarPixelRogue", "Assets")

errors, warnings, infos = [], [], []


def err(msg):
    errors.append(msg)


def warn(msg):
    warnings.append(msg)


def info(msg):
    infos.append(msg)


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def cs_files():
    """扫描 Assets 下的全部 .cs（含 Assets/Editor 构建脚本）。

    刻意不局限在 _Project/Scripts：M16 补移动端构建目标时，Assets/Editor/BuildScript.cs
    同样是会被手改出括号错误的代码，只扫运行时脚本会留下盲区。
    """
    found = []
    for dirpath, _dirs, files in os.walk(ASSETS_DIR):
        for fn in files:
            if fn.endswith(".cs"):
                found.append(os.path.join(dirpath, fn))
    return sorted(found)


def _strip_char_literals(text):
    """手写扫描 C# char 字面量，替换为 ''（见 strip_noise 注释里的两个坑）。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c != "'":
            out.append(c)
            i += 1
            continue
        # 潜在 char 字面量起点：形如 'x' / '\\n' / '\"' / '\''
        j = i + 1
        if j < n and text[j] == "\\":       # 转义字符：跳过反斜杠与其后的一个字符
            j += 2
        else:
            j += 1
        if j < n and text[j] == "'":          # 正常闭合
            out.append("''")
            i = j + 1
            continue
        out.append(c)                          # 不是 char 字面量，原样输出
        i += 1
    return "".join(out)


def strip_noise(text):
    """
    去掉注释与字符串字面量。
    顺序至关重要：必须先去字符串、再去注释。否则 "https://" 这类含 // 的字符串
    会被当成注释起点，把同行的剩余代码（往往带着半个括号）一起删掉，
    直接造成括号平衡的误报——本工具初版正是踩了这个坑。

    第二个坑（M20 踩到）：char 字面量「单引号 反斜杠 反斜杠 单引号」（即转义的反斜杠）
    与「单引号 反斜杠 双引号 单引号」这类写法，不能用 `'(?:\\.|[^'\\])*'` 去匹配——
    那个正则会把反斜杠当成"转义了后面的撇号"，于是字符串从字面量中部开始吞，
    吞掉后续若干行代码（表现为"括号不平衡"，而代码其实完全正常）。
    这里改为与 C# 语义一致的手写扫描：遇反斜杠跳过其后一个字符；撇号后只跟
    一个字符且紧接闭合撇号时，才认定为 char 字面量。

    第三个坑（顺序）：**必须先去 char 字面量、再去字符串**。
    反例是 `case '"':`——先剥字符串时，这个双引号会被当作文档里"某个字符串"的
    结束引号，正则于是从那一个引号开始配对，一路吞到几十行后，
    表现为"少了若干闭合括号"。字符串正则无法分辨"这个引号是 char 的内容"。
    """
    text = _strip_char_literals(text)                      # 先：char 字面量 'x' '\\' '"'
    text = re.sub(r'@"(?:""|[^"])*"', '""', text)          # 再：逐字字符串（"" 表示一个引号）
    text = re.sub(r'"(?:\\.|[^"\\\n])*"', '""', text)      # 再：普通字符串
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    text = re.sub(r'//[^\n]*', '', text)
    return text


# ── 1. 括号平衡 ────────────────────────────────────────────────
def check_brackets():
    for path in cs_files():
        text = strip_noise(read(path))
        stack = []
        pairs = {')': '(', '}': '{', ']': '['}
        bad = False
        for ch in text:
            if ch in '({[':
                stack.append(ch)
            elif ch in ')}]':
                if not stack or stack[-1] != pairs[ch]:
                    bad = True
                    break
                stack.pop()
        if bad or stack:
            err("括号不平衡: %s (残留=%s)" % (rel(path), stack))


# ── 2. Fish-Net 条件编译一致性 ─────────────────────────────────
def fishnet_ranges(lines):
    """返回处于 #if FISHNET_ENABLED 内的行区间列表 [(start, end), ...]（0-based, end 不含）。"""
    ranges, stack = [], []
    for i, ln in enumerate(lines):
        s = ln.strip()
        m = re.match(r'#if\s+(.*)', s)
        if m:
            stack.append((i, m.group(1)))
            continue
        if re.match(r'#endif', s):
            if stack:
                start, cond = stack.pop()
                if 'FISHNET_ENABLED' in cond:
                    ranges.append((start, i))
    return ranges


def in_ranges(i, ranges):
    return any(s <= i < e for s, e in ranges)


TYPE_DECL = re.compile(
    r'\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*'
    r'(class|interface|enum|struct)\s+(\w+)')


def check_fishnet_guards():
    """整文件被 FISHNET_ENABLED 保护的类型，若被无保护代码引用 -> 未装插件时编译失败。"""
    guarded_types = {}          # 类型名 -> 定义文件
    file_cache = {}

    for path in cs_files():
        text = read(path)
        lines = text.split('\n')
        ranges = fishnet_ranges(lines)
        clean = strip_noise(text).split('\n')
        file_cache[path] = (lines, ranges, clean)
        for i, ln in enumerate(clean):
            m = TYPE_DECL.search(ln)
            if m and in_ranges(i, ranges):
                guarded_types[m.group(2)] = path

    for path in cs_files():
        _lines, ranges, clean = file_cache[path]
        for i, ln in enumerate(clean):
            if in_ranges(i, ranges):
                continue
            for tname, tpath in guarded_types.items():
                if re.search(r'\b' + re.escape(tname) + r'\b', ln):
                    if os.path.abspath(tpath) == os.path.abspath(path):
                        continue
                    err("条件编译不一致: %s 第 %d 行引用了受保护类型 %s（定义于 %s）；"
                        "未定义 FISHNET_ENABLED 时该引用会导致编译失败"
                        % (rel(path), i + 1, tname, rel(tpath)))


# ── 3. 枚举分支覆盖 ────────────────────────────────────────────
ENUM_DECL = re.compile(r'\benum\s+(\w+)\s*\{(.*?)\n?\s*\}', re.S)


def parse_enums(text):
    enums = {}
    for m in ENUM_DECL.finditer(text):
        name = m.group(1)
        body = m.group(2)
        body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
        body = re.sub(r'//[^\n]*', '', body)
        members = []
        for part in body.split(','):
            part = part.strip()
            if not part:
                continue
            part = part.split('=')[0].strip()
            if part:
                members.append(part)
        if members:
            enums[name] = members
    return enums


def extract_switches(text):
    """提取所有 switch 块：返回 [(表达式, 块内容), ...]。"""
    out = []
    for m in re.finditer(r'\bswitch\s*\(([^)]*)\)', text):
        expr = m.group(1).strip()
        i = text.find('{', m.end())
        if i < 0:
            continue
        depth, j = 0, i
        while j < len(text):
            if text[j] == '{':
                depth += 1
            elif text[j] == '}':
                depth -= 1
                if depth == 0:
                    break
            j += 1
        out.append((expr, text[i:j + 1]))
    return out


def check_enum_coverage():
    all_enums = {}
    for path in cs_files():
        for name, members in parse_enums(read(path)).items():
            all_enums.setdefault(name, set()).update(members)

    # 每个枚举：收集各处 switch 中显式 case 到的成员
    cased = defaultdict(lambda: defaultdict(list))   # enum -> member -> [file]
    switches_by_enum = defaultdict(list)             # enum -> [(file, has_default, missing)]

    for path in cs_files():
        text = strip_noise(read(path))
        for expr, block in extract_switches(text):
            labels = re.findall(r'case\s+(\w+)\.(\w+)\s*:', block)
            if not labels:
                continue
            grouped = defaultdict(set)
            for tname, member in labels:
                grouped[tname].add(member)
            has_default = re.search(r'\bdefault\s*:', block) is not None
            for tname, members in grouped.items():
                if tname not in all_enums:
                    continue
                for mb in members:
                    cased[tname][mb].append(rel(path))
                missing = set(all_enums[tname]) - members
                switches_by_enum[tname].append((rel(path), has_default, missing))

    for name, members in sorted(all_enums.items()):
        for mb in sorted(members):
            if mb not in cased[name]:
                # 没有任何 switch 显式 case 到它：若所有相关 switch 都有 default，则属可接受
                switches = switches_by_enum.get(name, [])
                if switches and all(has_def for _f, has_def, _m in switches):
                    info("枚举 %s.%s 未被任何 case 显式处理（相关 switch 均有 default，可接受）"
                         % (name, mb))
                elif switches:
                    err("枚举分支缺失: %s.%s 未在任何 switch 中 case，且存在无 default 的 switch"
                        % (name, mb))
                else:
                    info("枚举 %s.%s 未参与任何 switch（可能仅作数据使用）" % (name, mb))


# ── 4. Resources 资源存在性 ────────────────────────────────────
# 不匹配 Resources.LoadAll（它按目录批量加载，本就没有单一文件）；
# 且跳过 "目录/" 前缀型字面量——真实路径由后续字符串拼接得到（如 "Sprites/" + name）。
RES_LOAD = re.compile(r'Resources\.Load(?!All)(?:<[^>]+>)?\s*\(\s*"([^"]+)"')


def resources_index():
    idx = {}
    for dirpath, _dirs, files in os.walk(ASSETS_DIR):
        if os.sep + "Resources" not in dirpath and "/Resources" not in dirpath.replace("\\", "/"):
            continue
        marker = dirpath.replace("\\", "/").find("/Resources/")
        if marker < 0:
            continue
        base = dirpath.replace("\\", "/")[:marker + len("/Resources/")]
        for fn in files:
            if fn.endswith(".meta"):
                continue
            full = os.path.join(dirpath, fn).replace("\\", "/")
            key = full[len(base):]
            idx[key.split('.')[0]] = full
    return idx


def check_resources():
    idx = resources_index()
    for path in cs_files():
        for i, ln in enumerate(read(path).split('\n')):
            for name in RES_LOAD.findall(ln):
                if name.endswith('/'):
                    continue   # 目录前缀，真实路径靠拼接，无法静态判定
                if name not in idx:
                    err("Resources 缺失: %s 第 %d 行 Resources.Load(\"%s\") 找不到对应资源文件"
                        % (rel(path), i + 1, name))


# ── 5. 同类重复成员 ────────────────────────────────────────────
FIELD_DECL = re.compile(
    r'^[ \t]*(?:public|private|protected|internal)?[ \t]*'
    r'(?:static[ \t]+)?(?:readonly[ \t]+)?(?:const[ \t]+)?'
    r'[\w<>\[\],\.\?]+[ \t]+(\w+)[ \t]*(?:=[^;]*)?;[ \t]*$')

CLASS_DECL = re.compile(r'\b(?:class|struct|interface)\s+(\w+)')


MEMBER_INDENT = 8          # 本工程风格：namespace(0) -> class(4) -> 成员(8)，方法内局部变量 >= 12
CLASS_AT_4 = re.compile(r'^ {4}(?:public|internal|static|sealed|abstract|partial|\s)*'
                        r'(?:class|struct)\s+(\w+)')


def check_duplicate_members():
    """
    同一类内重复字段声明：后写覆盖先写，且往往只给警告，是 M9 在模拟器里真实踩过的坑。
    判据收紧到「缩进恰为 8 空格」——方法内的同名局部变量完全合法，绝不能误报
    （本工具初版就曾把 CreateBolt/CreateScatter 各自的局部变量 s 误报成重复字段）。
    同时按 class 声明重置作用域，避免跨类同名成员误报。
    """
    for path in cs_files():
        lines = strip_noise(read(path)).split('\n')
        seen = {}
        for i, ln in enumerate(lines):
            if CLASS_AT_4.match(ln):
                seen = {}
                continue
            if len(ln) - len(ln.lstrip()) != MEMBER_INDENT:
                continue
            m = FIELD_DECL.match(ln)
            if not m:
                continue
            name = m.group(1)
            if name in seen:
                err("重复成员: %s 类成员字段 %s 重复声明（第 %d 行与第 %d 行），后写会覆盖先写"
                    % (rel(path), name, seen[name] + 1, i + 1))
            else:
                seen[name] = i


# ── 6. 使用了类型但缺少 using ─────────────────────────────────
# M18 新增。动机：M17/M18 真机实测共发现 4 处「缺 using」编译错误
# （BossBrain/NetRegistry 缺 StarPixel.Combat、NetBackend 缺 UnityEngine、
#   TouchJoystick 缺 UnityEngine.UI），而括号/枚举/守卫检查对此完全无感——
# 这是本工具此前最大的盲区。
WELL_KNOWN_TYPES = {
    "UnityEngine": ["GameObject", "Transform", "Vector2", "Vector3", "Quaternion", "Color",
                    "Mathf", "MonoBehaviour", "ScriptableObject", "Sprite", "SpriteRenderer",
                    "Camera", "Screen", "Application", "Input", "Debug", "Resources",
                    "Texture2D", "AudioClip", "RectTransform", "SerializeField",
                    "RequireComponent", "Space", "KeyCode", "TouchPhase", "Component"],
    "UnityEngine.UI": ["Text", "Image", "Button", "Slider", "Toggle", "InputField",
                       "Canvas", "CanvasScaler", "GraphicRaycaster"],
    "UnityEngine.Networking": ["UnityWebRequest", "UploadHandlerRaw", "DownloadHandlerBuffer"],
    "System": ["Action", "Func", "Guid", "Exception", "StringComparison", "DateTime",
               "StringSplitOptions", "Convert"],
    "System.Collections.Generic": ["List", "Dictionary", "HashSet", "Queue", "Stack",
                                   "IEnumerable", "KeyValuePair"],
    "System.Collections": ["IEnumerator", "ArrayList", "Hashtable"],
    "System.Text": ["StringBuilder", "Encoding"],
}


def build_project_types():
    """本工程声明的类型名 -> 命名空间集合。"""
    tmap = defaultdict(set)
    for path in cs_files():
        code = strip_noise(read(path))
        for m in re.finditer(r'\bnamespace\s+([\w\.]+)', code):
            ns = m.group(1)
            for tm in TYPE_DECL.finditer(code[m.end():]):
                tmap[tm.group(2)].add(ns)
    return tmap


def USED_AS_TYPE(tname):
    """
    「当作类型使用」的匹配式：前面不能是字母/数字/下划线/**点**。
    排除点号至关重要——否则 `save.Data`、`p.Damageable`、`Track.Boss` 这类
    「属性名/枚举成员与类型名重名」的成员访问会被误报成"缺 using"（初版 8 条全是这类误报）。
    """
    return re.compile(r'(?<![\w\.])' + re.escape(tname) + r'\b')


def qualified_referenced(code, tname, ns):
    """
    代码是否以「限定名」引用了该类型（因而不需要 using）。
    关键是必须支持**相对限定**：C# 允许从外层命名空间做部分限定，
    例如位于 StarPixel 的文件里写 `Core.EventBus` 即等价于 `StarPixel.Core.EventBus`。
    本工具初版只认全限定形式，导致把大量合法的 Core.Xxx 写法误报为"缺 using"（136 条噪音）。
    """
    parts = ns.split('.')
    for i in range(len(parts)):
        suffix = ".".join(parts[i:])                     # StarPixel.Core / Core
        if re.search(r'(?<![\w\.])' + re.escape(suffix) + r'\.' + re.escape(tname) + r'\b', code):
            return True
    return False


def check_missing_usings():
    """
    检出「用到了某个命名空间的类型，却没有 using 它」。
    判为可见的条件：已 using / 同一命名空间 / 是当前命名空间的祖先（C# 外层隐式可见）
    / 代码里以（相对或绝对）限定名引用了该类型。
    以 WARN 报告：类型名仍可能与局部变量同名，存在误报可能，需人工确认。
    """
    tmap = build_project_types()
    for path in cs_files():
        code = strip_noise(read(path))
        usings = set(re.findall(r'^\s*using\s+(?!static)(?![\w]+\s*=)([\w\.]+)\s*;', code, re.M))
        ns_m = re.search(r'\bnamespace\s+([\w\.]+)', code)
        cur_ns = ns_m.group(1) if ns_m else ""

        def accessible(ns):
            return (not ns) or (ns in usings) or (cur_ns == ns) or cur_ns.startswith(ns + ".")

        # 本文件本地声明的枚举成员（Track.Boss / Tab.Boss 之类）。
        # 它们与工程类型同名时，正则无法区分"枚举成员"与"类型"，会误报——故直接跳过。
        local_enum_members = set()
        for _en, members in parse_enums(code).items():
            local_enum_members.update(members)

        # (a) 本工程类型
        for tname, namespaces in tmap.items():
            if tname in local_enum_members:
                continue
            if not re.search(USED_AS_TYPE(tname), code):
                continue
            if any(qualified_referenced(code, tname, ns) for ns in namespaces):
                continue
            if all(not accessible(ns) for ns in namespaces):
                warn("缺少 using: %s 使用了本工程类型 %s（属于 %s）却未导入"
                     % (rel(path), tname, "/".join(sorted(namespaces))))

        # (b) 常用 Unity / 系统类型
        for ns, types in WELL_KNOWN_TYPES.items():
            if accessible(ns):
                continue
            hit = [t for t in types
                   if t not in local_enum_members
                   and re.search(USED_AS_TYPE(t), code)
                   and not qualified_referenced(code, t, ns)]
            if hit:
                warn("缺少 using: %s 使用了 %s 的类型（%s）却没有 `using %s;`"
                     % (rel(path), ns, "/".join(hit[:4]), ns))


def main():
    if not os.path.isdir(SCRIPTS_DIR):
        print("找不到脚本目录: %s" % SCRIPTS_DIR)
        return 1

    check_brackets()
    check_fishnet_guards()
    check_enum_coverage()
    check_resources()
    check_duplicate_members()
    check_missing_usings()

    n = len(cs_files())
    print("=" * 68)
    print("《星界像素狂潮》工程静态自检")
    print("扫描 .cs 文件：%d 个" % n)
    print("=" * 68)

    def dump(title, items):
        if not items:
            return
        print("\n[%s] %d 项" % (title, len(items)))
        for it in items:
            print("  - " + it)

    dump("ERROR", errors)
    dump("WARN", warnings)
    dump("INFO", infos)

    print("\n" + "-" * 68)
    print("合计：ERROR %d / WARN %d / INFO %d" % (len(errors), len(warnings), len(infos)))
    print("结果：" + ("未通过" if errors else "通过"))
    print("-" * 68)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
