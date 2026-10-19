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
  7. 新建对象归属            运行时 new GameObject / Instantiate 出来的东西若既无父物体、
                             也无登记表、也不属于某个会被销毁的字段，就会永远堆在场景根节点上。
                             M24（池化实体）/ M26（警示圈）/ M27（空壳管理器）连续三轮踩到，
                             每次都靠人肉复查才找到——本项把它变成机器检查。

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
    # 先剥行注释、再剥块注释：正文里出现 `Resources/Legal/*.txt` 这种写在 /// 行注释里的
    # "伪块注释起点"时，若反过来先扫块注释，就会从 Legal/* 一路找到下一个真的 */ ，
    # 把中间几十行代码整段吞掉（实测表现为 LegalTexts.cs 括号不平衡）。
    text = re.sub(r'//[^\n]*', '', text)
    text = _strip_block_comments(text)                     # 最后：块注释（保留换行，见下）
    return text


def _strip_block_comments(text):
    """
    删 /* ... */，但**补回等量换行**。

    用 re.sub(r'/\\*.*?\\*/', '', ..., re.S) 会把注释内部的换行一起吃掉，
    于是"去噪后的第 N 行"不再对应"原文的第 N 行"。括号平衡看不出来，
    但第 7 项检查要用原文行去读 @owner 注解，行号一错就读到别的行上
    （实测 FishNetBackend 的 8 行块注释让全文错位 8 行，注解被判成"没写"）。
    """
    out = []
    i = 0
    while True:
        j = text.find('/*', i)
        if j < 0:
            out.append(text[i:])
            break
        out.append(text[i:j])
        k = text.find('*/', j + 2)
        if k < 0:                      # 未闭合：剩下的当作代码，不能截断（截断=凭空少半个括号）
            out.append(text[j:])
            break
        out.append('\n' * text[j:k + 2].count('\n'))
        i = k + 2
    return "".join(out)


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


# ── 7. 新建对象归属 ────────────────────────────────────────────
# M28 新增。动机见文件头第 7 条：M24 / M26 / M27 连续三轮踩到「new GameObject 出来的
# 东西没人负责回收」，每次都是靠人肉复查才找到，不该再碰运气。
#
# 判定分两级：
#   (a) 自动推断——父物体 / 登记表 / 被销毁的字段 / 应用级单体，任一成立即视为有归属；
#   (b) 显式声明——以上都判不出来时，必须在创建点写 `// @owner: <kind>`。
# 为避免注解沦为逃脱口，kind 是封闭词汇且**逐项机器验证**（见 OWNER_KINDS 的 verifier）；
# 特别地「自毁」不算归属：M26 的警示圈本来就会自毁，但结算面板的 timeScale=0 把它冻住，
# 重开后才播完——自毁盖不住归属缺口，所以这里没有 `@owner: self` 这一档。
OWNER_ANNOT = re.compile(r'//\s*@owner\s*:\s*([A-Za-z]+)(?:\s*\(\s*([^)]*)\))?', re.I)

# 既不挂父物体、也不进登记表的合法形态：整局/整应用生命周期的单体宿主。
# 判据是该文件里存在 `Instance = `（写入静态单例引用）——它随应用共存亡，不参与拆世界。
APP_LIFETIME = re.compile(r'\bInstance\s*=(?!=)')
# 登记表：某个集合字段，且存在会 Destroy 它内容的方法（DestroyAll / ResetAll / Clear 等）
# 注意包含 OnDestroy：很多类是在自己的 OnDestroy 里清登记表（如 PlayerController 清 orbitBlades），
# 只认 DestroyAll/ResetAll 会把这类真有归属的误判成孤儿。
TEARDOWN_METHOD = re.compile(
    r'\b(?:public|private|protected|internal|static)?[ \t\w]*(?:void|int|bool)?[ \t]*'
    r'(DestroyAll|ResetAll|ClearAll|Teardown|Dispose|OnDestroy)\w*\s*\(')


def method_spans(lines):
    """
    切出各方法的行区间 [(start, end, decl), ...]。

    本工程统一 4 空格缩进（check_duplicate_members 已经基于同一假设）：
    类声明缩进 4、成员（方法/属性）缩进 8、方法体 >= 12。
    因此「缩进恰为 8 且含 '('」即方法声明，直到下一个缩进 8 的 `}` 或新声明为止。
    """
    spans, cur = [], None
    for i, ln in enumerate(lines):
        if not ln.strip():
            continue
        ind = len(ln) - len(ln.lstrip())
        s = ln.strip()
        if ind == 4 and re.match(r'(?:public|internal|private|protected|static|sealed|abstract|'
                                 r'partial|\s)*(?:class|struct|interface)\s', s):
            if cur:
                spans.append(cur)
                cur = None
            continue
        if ind == 8 and '(' in s and not s.startswith('//'):
            if cur:
                # 必须就地收尾：初版把 end 留成文件末尾、只在遇到缩进 8 的 `}` 时才回填，
                # 于是"最后一个方法"的区间会一路吞到文件底（实测 97 行），
                # 把别处的 Destroy 当成"本方法内就地销毁"，造成漏报。
                cur[1] = i - 1
                spans.append(cur)
            cur = [i, len(lines) - 1, s]
            continue
        if ind == 8 and cur is not None and s.startswith('}'):
            cur[1] = i
            spans.append(cur)
            cur = None
    if cur:
        spans.append(cur)
    return [tuple(x) for x in spans]


def _owner_of(raw_lines, i):
    """
    第 i 行（0-based）上的 @owner 注解；本行没有就看上一行（注解常被写在创建点上方）。

    必须读**未去注释的原文**：注解本身就是注释，用 strip_noise 之后的行去查必然一无所获
    （初版正是这么写的，于是 5 处注解全部"看不到"，改完还是照样报红）。
    """
    for j in (i, i - 1):
        if 0 <= j < len(raw_lines):
            m = OWNER_ANNOT.search(raw_lines[j])
            if m:
                return m.group(1).lower(), (m.group(2) or "").strip()
    return None, None


def _body_for(lines, spans, i):
    """返回 (方法体, 方法名)。切不出方法时方法名为空。"""
    for s, e, decl in spans:
        if s <= i <= e:
            m = re.search(r'(\w+)\s*\(', decl)
            return "\n".join(lines[s:e + 1]), (m.group(1) if m else "")
    return lines[i], ""


def _own_types(code):
    """本文件声明的类型名（缩进 4 的 class/struct），用于识别"单体宿主"。"""
    return set(re.findall(
        r'^ {4}(?:public|internal|private|protected|static|sealed|abstract|partial|\s)*'
        r'(?:class|struct)\s+(\w+)', code, re.M))


def _classify(body, code, target, line, own_types=(), method="", file_code=""):
    """返回 (是否可判定有归属, 自动判定的理由)。target 为 None 表示匿名/内联创建。"""
    if re.search(r'\bTrack\s*\(', body):
        return True, "Track"
    # 工厂方法：`CreateEnemyTemplate()` 里 new 出来、return 给调用方，由调用方 Track(...)。
    # 只看方法体会把这类误报成孤儿（GameBuilder 里 11 处模板与玩家都是这个形态）。
    # （CreatePlayer 返回的是组件而非 go，所以这里认"方法里有 return"，不苛求返回的就是 target。）
    if method and re.search(r'\breturn\b', body) \
            and re.search(r'\bTrack\s*\(\s*' + re.escape(method) + r'\s*\(', file_code or code):
        return True, "工厂方法(调用方 Track)"
    t = re.escape(target) if target else None
    if t:
        if re.search(t + r'\.transform\.SetParent\s*\(', body):
            return True, "SetParent"
        if re.search(r'SetParent\s*\(\s*' + t + r'\s*[,)]', body):
            return True, "SetParent"
        if re.search(t + r'\.transform\.parent\s*=', body):
            return True, "parent="
        if re.search(r'\b\w+\.(?:Add|Enqueue|Push)\s*\(\s*' + t + r'\s*\)', body):
            # 进了登记表还不够——登记表自己也得有人清。M24 的原始缺陷正是"池化实体都进了
            # 队列、却没有 DestroyAll"，只是换了个地方堆积。
            if TEARDOWN_METHOD.search(code):
                return True, "登记表"
            return False, ("进了登记表，但这个类没有 DestroyAll / ResetAll 之类的清理方法，"
                           "——登记表只是换了个地方堆积（M24 的原始缺陷形态）")
        # 赋给某个字段/属性，且该类里有销毁它的地方。
        # 左侧允许带点：`w.auraVisual = go;` + `Destroy(w.auraVisual)`（PlayerController 光环环属此类）。
        # 初版左侧只写 \w*，点号写法匹配不到，于是把真有归属的对象误报成孤儿。
        lhs = re.search(r'([\w\.]+)\s*=\s*' + t + r'\s*;', body)
        if lhs and re.search(r'\bDestroy\s*\(\s*' + re.escape(lhs.group(1)) + r'\b', code):
            return True, "字段+显式销毁"
        if re.search(r'\bDestroy\s*\(\s*' + t + r'\b', body):
            return True, "就地销毁"
        # 本类的字段，且该类里有销毁它的地方（典型：OnDestroy 里 Destroy(_root)）。
        # 这条刻意看**整个文件**而不是方法体——清理通常写在另一个方法（OnDestroy）里，
        # 只看方法体会把它漏判成孤儿（SpectatorCamera._root 就是这类）。
        if re.search(r'^\s*(?:private|public|protected|internal)?\s*'
                     r'(?:static\s+)?(?:readonly\s+)?[\w<>\[\],\.\?]+\s+' + t + r'\s*;', code, re.M) \
           and re.search(r'\bDestroy\s*\(\s*' + t + r'\b', code):
            return True, "字段+显式销毁"
    # 应用级单体宿主：这个 GameObject 挂的正是**本文件自己的类**（`new GameObject("X")
    # .AddComponent<X>()`），而该类是单体（文件里写了 `Instance =`，或继承 MonoSingleton<T>，
    # 其 Instance 在基类里）。它随应用共存亡，不参与拆世界。
    #
    # 必须限定"挂的是自己的类"：第一版只要求 body 里出现过 AddComponent 就放行，
    # 于是 TouchJoystick / SpectatorCamera 里 `_root.AddComponent<Canvas>()` 这类
    # "给自己的画布挂组件"也被当成单体宿主放过了——而那个 _root 恰恰是漏的对象。
    if own_types and (APP_LIFETIME.search(code) or 'MonoSingleton<' in code):
        altern = "|".join(re.escape(t) for t in own_types)
        if target:
            if re.search(re.escape(target) + r'\.AddComponent\s*<\s*(?:' + altern + r')\s*>', body):
                return True, "应用级单体"
        elif re.search(r'\.AddComponent\s*<\s*(?:' + altern + r')\s*>', line):
            return True, "应用级单体"
    return False, None


def _verify_owner(kind, arg, code, line, in_fishnet=False, method="", body="", raw=""):
    """验证 @owner 注解是否名副其实。返回 None 表示通过，否则返回不通过的原因。"""
    if kind == 'parent':
        if 'SetParent' not in line and 'SetParent' not in code:
            return "声明 @owner: parent 但全文找不到 SetParent"
        return None
    if kind == 'field':
        name = arg.strip()
        if not name:
            return "@owner: field 需要写明字段名，如 // @owner: field(_root)"
        if not re.search(re.escape(name) + r'\s*=', code):
            return "声明 @owner: field(%s) 但全文没有给 %s 赋值" % (name, name)
        if not re.search(r'\bDestroy\s*\(\s*[^)]*\b' + re.escape(name.lstrip('_')), code):
            return "声明 @owner: field(%s) 但全文找不到销毁 %s 的地方" % (name, name)
        return None
    if kind == 'registry':
        name = arg.strip()
        if not name:
            return "@owner: registry 需要写明登记表名，如 // @owner: registry(_alive)"
        if not re.search(r'\b' + re.escape(name) + r'\b', code):
            return "声明 @owner: registry(%s) 但全文没有这个登记表" % name
        if not TEARDOWN_METHOD.search(code):
            return "声明 @owner: registry(%s) 但没有 DestroyAll/ResetAll 之类的清理方法" % name
        return None
    if kind == 'app':
        # 应用级单体的三种形态：自己写 Instance = / 继承 MonoSingleton<T>（Instance 在基类）/
        # 处于 #if FISHNET_ENABLED 段内的联机服务对象（随 NetSession 拆装，由联机冒烟覆盖）。
        if APP_LIFETIME.search(code) or 'MonoSingleton<' in code or in_fishnet \
                or 'DontDestroyOnLoad' in (raw or ""):
            return None
        return ("声明 @owner: app 但全文既没有 `Instance =`、也没有 MonoSingleton<T>、"
                "没有 DontDestroyOnLoad、也不在联机条件编译段")
    if kind == 'stage':
        # 幂等装配：只在"缺失时"建一次，全程存活、不随世界拆装（相机 / EventSystem / 大厅）。
        # 验证方式即它的定义本身：所在方法是 Ensure*/Setup*/Prepare*/Open*，
        # 或方法体内创建之前有 == null / != null 守卫。
        if method and re.match(r'(Ensure|Setup|Prepare|Open)', method):
            return None
        if re.search(r'(?:==|!=)\s*null', body):
            return None
        return "声明 @owner: stage 但所在方法既不是 Ensure*/Setup*/Prepare*/Open*，方法体内也没有 null 守卫"
    return "未知的 @owner 类型 '%s'（可用：parent / field(名) / registry(名) / app）" % kind


def check_orphan_gameobjects():
    """
    运行时创建的对象必须有明确的回收路径。

    刻意只检查运行时脚本（_Project/Scripts，排除 Dev/）：
      - Dev/ 下是实测探针，它们自己负责收尾，且形态与生产代码差异很大；
      - Assets/Editor 下是构建脚本，不参与运行时生命周期。
    """
    creation = re.compile(r'\bnew\s+GameObject\s*\(')
    instantiate = re.compile(r'(?<![\w.])Instantiate\s*\(')
    # 赋值目标：`var go = ...` / `_root = ...` / `w.visual = ...`；`==` 不算。
    assign = re.compile(r'(\w+)\s*=(?!=)[^=]*?$')

    for path in cs_files():
        rp = rel(path)
        norm = rp.replace('\\', '/')
        if '/Dev/' in norm or '/Editor/' in norm:
            continue
        raw = read(path)            # 未去注释的原文：DontDestroyOnLoad 常写在注释里说明生命周期
        code = strip_noise(raw)
        lines = code.split('\n')
        spans = method_spans(lines)
        net_ranges = fishnet_ranges(lines)
        own_types = _own_types(code)
        raw_lines = raw.split('\n')

        for i, ln in enumerate(lines):
            if not (creation.search(ln) or instantiate.search(ln)):
                continue
            body, method = _body_for(lines, spans, i)
            in_fishnet = in_ranges(i, net_ranges)

            # 赋值目标：取 creation 之前、最近的那个 `x =`
            target = None
            head = ln.split('new GameObject(')[0].split('Instantiate(')[0]
            m = assign.search(head.rstrip())
            if m and not m.group(1) in ('var', 'GameObject', 'Object', 'UnityEngine'):
                target = m.group(1)

            ok, why = _classify(body, code, target, ln, own_types, method, code)
            kind, arg = _owner_of(raw_lines, i)
            if kind:
                bad = _verify_owner(kind, arg, code, ln, in_fishnet, method, body, raw)
                if bad:
                    err("归属声明无效: %s 第 %d 行 —— %s" % (rp, i + 1, bad))
                else:
                    info("归属声明: %s 第 %d 行 @owner: %s%s（人工确认）"
                         % (rp, i + 1, kind, "(%s)" % arg if arg else ""))
                continue
            if ok:
                continue
            if why:
                err("归属无效: %s 第 %d 行 —— %s\n      %s"
                    % (rp, i + 1, ln.strip()[:90], why))
                continue
            err("新建对象无归属: %s 第 %d 行 —— %s\n"
                "      该对象既没有 SetParent、也没有进任何登记表、也不属于会被销毁的字段；\n"
                "      拆世界时没人回收它，会永远堆在场景根节点上（M24/M26/M27 连续三轮的真实缺陷）。\n"
                "      修法三选一：挂到管理器 transform 下 / 登记进带 DestroyAll 的表 / 写 // @owner: <kind>。"
                % (rp, i + 1, ln.strip()[:90]))


# ============================================================================
# 第 8 项：EventBus 订阅对称性（M29）
# ============================================================================
EB_SUB_RE = re.compile(r'EventBus\.(\w+)\s*([+-])=\s*')


def _eb_rhs(code, i):
    """从订阅点右侧取处理器片段：平衡括号直到语句末尾的 `;`。"""
    depth = 0
    j = i
    while j < len(code):
        c = code[j]
        if c in '({[':
            depth += 1
        elif c in ')}]':
            depth -= 1
        elif c == ';' and depth == 0:
            break
        j += 1
    return code[i:j]


def scan_eventbus(code):
    """返回 [(事件名, '+'/'-', 处理器片段, 行号)]。"""
    out = []
    for m in EB_SUB_RE.finditer(code):
        rhs = _eb_rhs(code, m.end())
        line = code.count('\n', 0, m.start()) + 1
        out.append((m.group(1), m.group(2), rhs.strip(), line))
    return out


def check_eventbus_subscriptions():
    """
    静态事件总线的订阅必须退订得掉。

    EventBus 是静态的，订阅者却是有生命周期的 MonoBehaviour：忘了退订，
    委托链会一直攥着已经销毁的对象。这与第 7 项是同一类「跨局残留」，
    但漏的是**引用**而不是 GameObject——场景里什么都多不出来，
    扫场景根扫不到它，只能从代码层面判。

    判据刻意分两种形态：
      · 方法组 `+= Handler` —— 必须有同事件的 `-= Handler`；
      · 直接 `+= lambda`   —— 匿名委托没法按名匹配，只能要求该文件对该事件
                              有**任意**退订（正确写法是先存成字段再 -= 那个字段）。
    只按名匹配的话 lambda 形态会整个漏过去——M29 抓到的
    UI/LevelUpManager 正是 lambda：每重开一局链上就多一份，
    OnLevelUp 订阅数 3→6，升一级弹三次面板。

    同样排除 Dev/：探针自己订阅后由进程退出回收，不参与运行时生命周期。
    """
    for path in cs_files():
        rp = rel(path)
        norm = rp.replace('\\', '/')
        if '/Dev/' in norm or '/Editor/' in norm:
            continue
        code = strip_noise(read(path))
        items = scan_eventbus(code)
        if not items:
            continue

        subs = {}
        for ev, op, rhs, line in items:
            subs.setdefault(ev, {'+': [], '-': []})[op].append((rhs, line))

        for ev, d in subs.items():
            rems = [r.rstrip(';').strip() for r, _ in d['-']]
            for rhs, line in d['+']:
                body = rhs.rstrip(';').strip()
                if '=>' in body or body.startswith('delegate'):
                    if not rems:
                        err("事件总线: %s 第 %d 行 EventBus.%s += 匿名委托，该文件没有任何 -= ——"
                            " 每次重建都多一份订阅，升一级会弹 N 次面板。\n"
                            "      修法：把委托存成字段（_h = ...; += _h;），再在 OnDestroy 里 -= _h。"
                            % (rp, line, ev))
                elif body not in rems:
                    err("事件总线: %s 第 %d 行 EventBus.%s += %s，但同文件没有对应的 -= %s ——"
                        " 对象销毁后委托链仍攥着它。\n"
                        "      修法：在 OnDestroy 里 -= %s。"
                        % (rp, line, ev, body, body, body))


# ============================================================================
# 第 9 项：运行时 GPU 资源必须缓存（M30）
# ============================================================================
# 运行时 new 出来的 Texture2D / Sprite / Material 不挂在场景里、不进登记表，
# Object.Destroy 也永远不会有人对它们调用（根本没人持有引用）。
# GC 管不到 Unity 原生侧内存：托管包装被回收，显存里那张纹理还在。
# 工厂型方法若不缓存，每调一次就泄漏一份——M30 实测里
# SpriteFactory.MakeSolidSprite() 正是如此（连调 21 次只 0 次返回同一实例）。
RES_CREATE_RE = re.compile(
    r'\bnew\s+Texture2D\s*\(|\bSprite\.Create\s*\(|\bnew\s+Material\s*\(|\bnew\s+RenderTexture\s*\(')
# 缓存读取的三种形态：字典 TryGetValue / 静态字段判空复用 / 空值合并
RES_CACHE_RE = re.compile(r'\.TryGetValue\s*\(|\b\w+\s*==\s*null\b|\b\w+\s*!=\s*null\b|\?\?')
RES_FACTORY_TYPE = re.compile(r'\b(?:class|struct)\s+(\w*(?:Factory|Cache|Pool|Provider))\b')
# 只看**入口**方法：private 辅助（如 CreateSolid）的缓存责任在调用方身上，
# 要求它也自带判空会把"缓存写在调用方"的正确写法误判成未缓存（M30 实测假阳性）。
RES_FACTORY_METHOD = re.compile(
    r'(?:public|internal|protected)\s+static\s+[\w<>\[\],\s\.]+?\s+((?:Make|Create|Get|Build|Load)\w*)\s*\(')


def _res_class_of(lines, i):
    """向上找最近的类声明，返回类名。"""
    for j in range(i, -1, -1):
        m = re.match(r'\s{4}(?:public|internal|private|protected|static|sealed|abstract|partial|\s)*'
                     r'(?:class|struct)\s+(\w+)', lines[j])
        if m:
            return m.group(1)
    return ""


def check_runtime_asset_cache():
    """
    运行时创建的 Unity 资源必须缓存。

    只管**工厂型**方法：类名以 Factory/Cache/Pool/Provider 结尾，
    或方法名以 Make/Create/Get/Build/Load 开头的 static 方法。
    一次性构造（Awake 里建一张自己的贴图）不在其列——它不是被反复调用的路径。

    判据：方法体内必须出现缓存读取（TryGetValue / 字段判空复用 / ??）。
    可用 `// @nocache: 理由` 显式豁免（豁免必须写理由，机器只验格式、不验语义）。

    M30 真实缺陷：SpriteFactory.MakeSolidSprite() 每次调用都 new 一张 8x8 纹理，
    而爆发环 / 警示圈 / 链闪电全在高频路径上调用它——纹理只涨不降。
    类里明明已经有一张共享的 _whiteTex，MakeSolidSprite 却完全没用它。
    """
    for path in cs_files():
        rp = rel(path)
        norm = rp.replace('\\', '/')
        if '/Dev/' in norm or '/Editor/' in norm:
            continue
        raw = read(path)
        lines = strip_noise(raw).split('\n')
        raw_lines = raw.split('\n')
        spans = method_spans(lines)

        for i, ln in enumerate(lines):
            if not RES_CREATE_RE.search(ln):
                continue
            body, method = _body_for(lines, spans, i)
            cls = _res_class_of(lines, i)

            is_factory_cls = bool(RES_FACTORY_TYPE.search(cls or ""))
            is_factory_m = False
            decl = ""
            for s, e, d in spans:
                if s <= i <= e:
                    decl = d
                    break
            if RES_FACTORY_METHOD.search(decl):
                is_factory_m = True
            if not (is_factory_cls or is_factory_m):
                continue

            if RES_CACHE_RE.search(body):
                continue

            # 豁免必须写在创建点附近且带理由
            ok = False
            for j in range(max(0, i - 3), min(len(raw_lines), i + 2)):
                if '@nocache:' in raw_lines[j] and raw_lines[j].split('@nocache:', 1)[1].strip():
                    ok = True
                    break
            if ok:
                info("资源缓存豁免: %s 第 %d 行（人工确认）" % (rp, i + 1))
                continue

            err("运行时资源未缓存: %s 第 %d 行 %s —— 类 %s / 方法 %s\n"
                "      这是工厂型方法，每次调用都会在原生侧新建一份资源，而没人持有引用、\n"
                "      也就永远不会被 Destroy：GC 回收托管包装，显存里的纹理还在（M30 实测缺陷）。\n"
                "      修法：按 key 缓存（Dictionary + TryGetValue）或返回共享实例；\n"
                "      确属一次性构造则写 // @nocache: 理由。"
                % (rp, i + 1, ln.strip()[:70], cls or "?", method or "?"))


# 第 10 项：派生物重算必须同步广播（M31）
# ============================================================================
# M31 抓的是一类比"数值错了"更难发现的缺陷：**数值对了，副作用漏了**。
# 玩家解锁一把武器、刚好凑成组合技时，ComboManager.Recompute 会把 bonus
# （伤害/穿透/减速）全部算出来——功能层面完全正确；但"提示与音效"走的是
# 另一条路：CheckCombos 里广播的 OnComboActivated。漏了它，玩家白凑一个
# 组合、全程毫不知情，而且**没有任何报错**：看字段是对的，只有订阅事件才测得出来。
#
# 判据：同一方法体内出现"重算派生物"的调用时，必须同时出现配套的"广播"调用。
# 刻意**按方法配对而非按文件**：分开写在两个方法里照样算漏（夹具 cb06）。
RECOMPUTE_RE = re.compile(r'\bRecompute\s*\(')
BROADCAST_RE = re.compile(r'\bCheckCombos\s*\(')


def check_derive_broadcast():
    """
    重算了派生物就必须同步广播（M31）。

    只查"重算 + 广播"这一对配套动作：方法体内调了 Recompute（把 combo 加成
    写进 ComboManager）就必须同时调 CheckCombos（向 UI/音效广播激活事件）。
    只写前者，功能数值是对的、玩家却得不到任何反馈——这类缺陷不报错、不崩，
    看字段也测不出来，只能靠订阅事件（M31 的 COMBO 场景正是这么抓到的）。

    可用 `// @nobroadcast: 理由` 显式豁免，且**空理由无效**。
    """
    for path in cs_files():
        rp = rel(path)
        norm = rp.replace('\\', '/')
        if '/Dev/' in norm or '/Editor/' in norm:
            continue
        raw = read(path)
        lines = strip_noise(raw).split('\n')
        raw_lines = raw.split('\n')
        spans = method_spans(lines)

        for i, ln in enumerate(lines):
            if not RECOMPUTE_RE.search(ln):
                continue
            # 跳过**声明行自身**：叫 Recompute 的方法，它的声明行会被上面的正则
            # 当成一次"调用"，于是"重算的实现"被误判成"重算了却没广播"。
            # 实测里 ComboManager.Recompute 就是这样被误报的（M31 假阳性）。
            if any(s == i for s, _e, _d in spans):
                continue
            body, method = _body_for(lines, spans, i)

            if BROADCAST_RE.search(body):
                continue

            # 豁免必须写在重算点附近且带理由
            ok = False
            for j in range(max(0, i - 3), min(len(raw_lines), i + 2)):
                if '@nobroadcast:' in raw_lines[j] and raw_lines[j].split('@nobroadcast:', 1)[1].strip():
                    ok = True
                    break
            if ok:
                info("广播豁免: %s 第 %d 行（人工确认）" % (rp, i + 1))
                continue

            err("重算了派生物却没广播: %s 第 %d 行 %s —— 方法 %s\n"
                "      这里调了 Recompute（加成已写进 ComboManager、功能是对的），"
                "但没有调 CheckCombos——\n"
                "      于是组合技的**提示与音效**不会发出来：玩家凑齐了却毫不知情（M31 实测缺陷）。\n"
                "      这类缺陷不报错、不崩、看字段也是对的，只有订阅事件才测得出来。\n"
                "      修法：Recompute 之后补一行 CheckCombos()；"
                "确属无需反馈则写 // @nobroadcast: 理由。"
                % (rp, i + 1, ln.strip()[:70], method or "?"))


LOCAL_FIELD_RE = re.compile(r'\bbool\s+isLocal\b')
RAISE_RE = re.compile(r'EventBus\s*\.\s*Raise\w*\s*\(')
GUARD_IF_RE = re.compile(r'\bif\s*\(\s*isLocal\b')
EARLY_RE = re.compile(r'\bif\s*\(\s*!\s*isLocal\s*\)\s*(?:return|continue)\s*;')


def _span_of(spans, i):
    """返回包含第 i 行的方法区间 (start, end)；切不出方法时返回 (None, None)。"""
    for s, e, _d in spans:
        if s <= i <= e:
            return s, e
    return None, None


def _local_guard_ranges(lines, s, e):
    """
    方法体内「if (isLocal)」守卫覆盖到的行区间。

    三种写法都要认：
      if (isLocal) X();              —— 单行体，只盖本行
      if (isLocal) { ... }           —— 花括号在本行
      if (isLocal)\\n{ ... }          —— 花括号在下一行（本工程 PlayerController 就是这种）
    """
    ranges = []
    for j in range(s, e + 1):
        if not GUARD_IF_RE.search(lines[j]):
            continue
        if '{' in lines[j]:
            brace = j
        elif j + 1 <= e and lines[j + 1].strip() == '{':
            brace = j + 1
        else:
            ranges.append((j, j))      # 单行体
            continue
        depth, end = 0, e
        for k in range(brace, e + 1):
            depth += lines[k].count('{') - lines[k].count('}')
            if depth <= 0:
                end = k
                break
        ranges.append((brace, end))
    return ranges


def check_local_guard_symmetry():
    """
    本地守卫对称性（M32）。

    只查「有本地玩家概念」的类型：文件里声明了 `bool isLocal` 才纳入。
    这类文件里的每一次 EventBus.Raise 都必须处在本地守卫之下
    （`if (isLocal)` 同行 / 块内，或方法内更早处有 `if (!isLocal) return;` 早退）。

    为什么值得单列一项：
    漏守卫的后果只在联机下出现——单机时 isLocal 恒为 true，走哪条路都一样，
    于是"远程玩家串台"这类缺陷天然测不出（M31 收尾时 AddSkill 就是这么漏的：
    同类的 LevelUpWeapon 有守卫，它没有）。这类不一致不报错、不崩，
    看字段也对，只能靠"同文件内逐处比对守卫"才看得见。

    可用 `// @nolocalguard: 理由` 显式豁免，且**空理由无效**。
    """
    for path in cs_files():
        rp = rel(path)
        norm = rp.replace('\\', '/')
        if '/Dev/' in norm or '/Editor/' in norm:
            continue
        raw = read(path)
        # 本文件没有"本地玩家"这个概念，本项不管（Net 层用的是另一套 IsServer 守卫）
        if not LOCAL_FIELD_RE.search(raw):
            continue

        lines = strip_noise(raw).split('\n')
        raw_lines = raw.split('\n')
        spans = method_spans(lines)

        for i, ln in enumerate(lines):
            if not RAISE_RE.search(ln):
                continue
            if GUARD_IF_RE.search(ln):
                continue                       # 同一行的 if (isLocal) 守卫

            s, e = _span_of(spans, i)
            if s is None:
                continue

            # 早退式守卫必须出现在调用点**之前**：写在后面的 return 挡不住已经发出去的广播
            if any(EARLY_RE.search(lines[k]) for k in range(s, i)):
                continue

            if any(a <= i <= b for a, b in _local_guard_ranges(lines, s, e)):
                continue

            ok = False
            for j in range(max(0, i - 3), min(len(raw_lines), i + 2)):
                if '@nolocalguard:' in raw_lines[j] and raw_lines[j].split('@nolocalguard:', 1)[1].strip():
                    ok = True
                    break
            if ok:
                info("本地守卫豁免: %s 第 %d 行（人工确认）" % (rp, i + 1))
                continue

            err("本地事件广播缺少 isLocal 守卫: %s 第 %d 行 %s\n"
                "      这个类型区分本地/远程玩家，但这次广播没有 isLocal 守卫——"
                "联机下远程玩家对象的同一条路径也会往本地 HUD 发事件，\n"
                "      房主界面会被别人的变动带着刷新（M32 实测缺陷：AddSkill 缺守卫，"
                "而同类的 LevelUpWeapon 有）。\n"
                "      单机永远测不出来（isLocal 恒为 true），这也是它一直没被发现的原因。\n"
                "      修法：补 if (isLocal)（与同类路径保持一致）；"
                "确属需无条件广播则写 // @nolocalguard: 理由。"
                % (rp, i + 1, ln.strip()[:70]))


# ── 自检：证明"判据真的会红" ──────────────────────────────
# 静态检查最容易出现的失败是"永远不报错"——第 7 项若写成摆设，工程照样一片绿，
# 而 M24/M26/M27 那三处缺陷会继续静静地堆在场景里。
# 所以这里用一组最小夹具反向验证：该红的必须红、不该红的必须不红。
SELFTEST = [
    ("01_场景根孤儿", True, """
namespace A
{
    public class Fx
    {
        public static void Spawn()
        {
            var go = new GameObject("Ring");
            go.AddComponent<Fx>();
        }
    }
}
"""),
    ("02_挂了父物体", False, """
namespace A
{
    public class Fx
    {
        public void Build()
        {
            var go = new GameObject("Canvas");
            go.transform.SetParent(transform, false);
        }
    }
}
"""),
    ("03_进了登记表且有清理", False, """
namespace A
{
    public class Fx
    {
        private readonly List<GameObject> _alive = new List<GameObject>();
        public static void Spawn()
        {
            var go = new GameObject("Ring");
            _alive.Add(go);
        }
        public static void DestroyAll()
        {
            foreach (var g in _alive) Object.Destroy(g);
            _alive.Clear();
        }
    }
}
"""),
    ("04_进了登记表却没人清", True, """
namespace A
{
    public class Fx
    {
        private readonly List<GameObject> _alive = new List<GameObject>();
        public static void Spawn()
        {
            var go = new GameObject("Ring");
            _alive.Add(go);
        }
    }
}
"""),
    ("05_应用级单体宿主", False, """
namespace A
{
    public class Svc : Core.MonoSingleton<Svc>
    {
        public static void Ensure()
        {
            new GameObject("Svc").AddComponent<Svc>();
        }
    }
}
"""),
    ("06_字段持有且OnDestroy清理", False, """
namespace A
{
    public class Fx
    {
        private GameObject _root;
        private void BuildUI()
        {
            _root = new GameObject("Banner");
        }
        void OnDestroy()
        {
            if (_root != null) Destroy(_root);
        }
    }
}
"""),
    ("07_只靠计时自毁", True, """
namespace A
{
    public class Fx
    {
        private float _life;
        public static void Spawn()
        {
            var go = new GameObject("Flash");
            go.AddComponent<Fx>();
        }
        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Destroy(gameObject); return; }
        }
    }
}
"""),
    ("08_注解撒谎", True, """
namespace A
{
    public class Fx
    {
        private GameObject _root;
        private void BuildUI()
        {
            _root = new GameObject("Banner");   // @owner: field(_ghost)
        }
    }
}
"""),
    ("09_幂等装配声明", False, """
namespace A
{
    public class Stage
    {
        private static void SetupCamera()
        {
            var camGo = new GameObject("MainCamera");   // @owner: stage
            camGo.AddComponent<Camera>();
        }
    }
}
"""),
    ("10_工厂方法由调用方Track", False, """
namespace A
{
    public class Builder
    {
        private static GameObject MakeTemplate()
        {
            var go = new GameObject("EnemyTemplate");
            go.AddComponent<BoxCollider2D>();
            return go;
        }
        private static void Build()
        {
            var t = Track(MakeTemplate());
        }
    }
}
"""),
]


SELFTEST_EB = [
    ("eb01_方法组漏退订", True, """
namespace A
{
    public class Shake : MonoBehaviour
    {
        void Awake()
        {
            EventBus.OnShakeRequest += Shake2;
        }
        private void Shake2(float a, float b) { }
    }
}
"""),
    ("eb02_方法组对称", False, """
namespace A
{
    public class Hud : MonoBehaviour
    {
        void Awake()
        {
            EventBus.OnEnemyKilled += OnKill;
        }
        void OnDestroy()
        {
            EventBus.OnEnemyKilled -= OnKill;
        }
        private void OnKill(int n) { }
    }
}
"""),
    ("eb03_lambda直接订阅", True, """
namespace A
{
    public class Panel : MonoBehaviour
    {
        void Awake()
        {
            EventBus.OnLevelUp += lv => { Show(lv); };
        }
        private void Show(int lv) { }
    }
}
"""),
    ("eb04_lambda存字段后退订", False, """
namespace A
{
    public class Panel : MonoBehaviour
    {
        private System.Action<int> _onLevelUp;
        void Awake()
        {
            _onLevelUp = lv => { Show(lv); };
            EventBus.OnLevelUp += _onLevelUp;
        }
        void OnDestroy()
        {
            EventBus.OnLevelUp -= _onLevelUp;
        }
        private void Show(int lv) { }
    }
}
"""),
    ("eb05_跨事件同名不算", True, """
namespace A
{
    public class Sfx : MonoBehaviour
    {
        void Awake()
        {
            EventBus.OnGameOver += OnEnd;
        }
        void OnDestroy()
        {
            EventBus.OnLevelUp -= OnEnd;
        }
        private void OnEnd() { }
    }
}
"""),
    ("eb06_别的事件有退订不算", True, """
namespace A
{
    public class Sfx : MonoBehaviour
    {
        void Awake()
        {
            EventBus.OnLevelUp += lv => { Show(lv); };
            EventBus.OnGameOver += OnEnd;
        }
        void OnDestroy()
        {
            EventBus.OnGameOver -= OnEnd;
        }
        private void Show(int lv) { }
        private void OnEnd() { }
    }
}
"""),
]

# 第 9 项：运行时资源缓存。夹具同样要证明"该红的必须红"。
SELFTEST_RES = [
    ("res01_工厂有判空缓存", False, """
namespace A
{
    public static class SpriteFactory
    {
        private static Sprite _solid;
        public static Sprite MakeSolid()
        {
            if (_solid != null) return _solid;
            var tex = new Texture2D(8, 8);
            return Sprite.Create(tex, new Rect(0, 0, 8, 8), Vector2.zero);
        }
    }
}
"""),
    ("res02_工厂无缓存", True, """
namespace A
{
    public static class SpriteFactory
    {
        public static Sprite MakeSolid()
        {
            var tex = new Texture2D(8, 8);
            return Sprite.Create(tex, new Rect(0, 0, 8, 8), Vector2.zero);
        }
    }
}
"""),
    ("res03_字典缓存", False, """
namespace A
{
    public static class TexCache
    {
        private static readonly Dictionary<string, Sprite> _c = new Dictionary<string, Sprite>();
        public static Sprite Get(string k)
        {
            if (_c.TryGetValue(k, out var s)) return s;
            var tex = new Texture2D(8, 8);
            return Sprite.Create(tex, new Rect(0, 0, 8, 8), Vector2.zero);
        }
    }
}
"""),
    ("res04_一次性构造不查", False, """
namespace A
{
    public class HudBar : MonoBehaviour
    {
        private void Start()
        {
            var tex = new Texture2D(4, 4);
        }
    }
}
"""),
    ("res05_Make开头静态方法无缓存", True, """
namespace A
{
    public static class IconHelper
    {
        public static Sprite MakeIcon()
        {
            var m = new Material(Shader.Find("Sprites/Default"));
            var tex = new Texture2D(16, 16);
            return Sprite.Create(tex, new Rect(0, 0, 16, 16), Vector2.zero);
        }
    }
}
"""),
    ("res06_带理由豁免", False, """
namespace A
{
    public static class BootFactory
    {
        public static Sprite CreateSplash()
        {
            // @nocache: 启动画面只建一次，进程内不再调用
            var tex = new Texture2D(64, 64);
            return Sprite.Create(tex, new Rect(0, 0, 64, 64), Vector2.zero);
        }
    }
}
"""),
    ("res07_空理由豁免无效", True, """
namespace A
{
    public static class BootFactory
    {
        public static Sprite CreateSplash()
        {
            // @nocache:
            var tex = new Texture2D(64, 64);
            return Sprite.Create(tex, new Rect(0, 0, 64, 64), Vector2.zero);
        }
    }
}
"""),
    # M30 实测假阳性：缓存写在 public 入口、private 辅助只管造——不该判辅助方法未缓存
    ("res08_私有辅助由调用方缓存", False, """
namespace A
{
    public static class SpriteFactory
    {
        private static Sprite _solid;
        public static Sprite MakeSolid()
        {
            if (_solid != null) return _solid;
            _solid = CreateSolid();
            return _solid;
        }
        private static Sprite CreateSolid()
        {
            var tex = new Texture2D(8, 8);
            return Sprite.Create(tex, new Rect(0, 0, 8, 8), Vector2.zero);
        }
    }
}
"""),
]


# 第 10 项：重算派生物必须同步广播。
SELFTEST_CB = [
    ("cb01_重算且广播", False, """
namespace A
{
    public class Player
    {
        public void AddSkill(SkillDef def)
        {
            combo.Recompute(GetTags());
            CheckCombos();
        }
        private void CheckCombos() { }
    }
}
"""),
    # M31 真实缺陷的形态：只有 Recompute，玩家收不到任何提示
    ("cb02_只重算不广播", True, """
namespace A
{
    public class Player
    {
        public void AddSkill(SkillDef def)
        {
            combo.Recompute(GetTags());
            EventBus.RaiseWeaponsChanged();
        }
        private void CheckCombos() { }
    }
}
"""),
    # 没有重算就不查——本项只管"重算+广播"这一对
    ("cb03_无重算不查", False, """
namespace A
{
    public class Player
    {
        public void Tick()
        {
            transform.position += Vector3.one;
        }
    }
}
"""),
    ("cb04_带理由豁免", False, """
namespace A
{
    public class Player
    {
        public void SilentApply()
        {
            // @nobroadcast: 联机客户端镜像，由房主统一广播
            combo.Recompute(GetTags());
        }
        private void CheckCombos() { }
    }
}
"""),
    ("cb05_空理由豁免无效", True, """
namespace A
{
    public class Player
    {
        public void SilentApply()
        {
            // @nobroadcast:
            combo.Recompute(GetTags());
        }
        private void CheckCombos() { }
    }
}
"""),
    # M31 实测假阳性：叫 Recompute 的方法，其**声明行**会被当成一次调用，
    # 于是"重算的实现"被误判成"重算了却没广播"。声明行不算调用点。
    ("cb07_重算方法自身的声明", False, """
namespace A
{
    public class ComboManager
    {
        public void Recompute(List<string> tags)
        {
            var b = Compute(tags);
            bonusDamageMult = b.damageMult;
        }
        private void CheckCombos() { }
    }
}
"""),
    # 刻意按方法配对：写在同一个类的另一个方法里不算——那样照样漏
    ("cb06_广播在别的方法里", True, """
namespace A
{
    public class Player
    {
        public void AddSkill(SkillDef def)
        {
            combo.Recompute(GetTags());
        }
        public void LevelUpWeapon(int id)
        {
            combo.Recompute(GetTags());
            CheckCombos();
        }
        private void CheckCombos() { }
    }
}
"""),
]


SELFTEST_GUARD = [
    # M32 真实缺陷的形态：同类两条路，一条有守卫一条没有
    ("guard01_广播无守卫", True, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void AddSkill(SkillDef def)
        {
            weapons.Add(def);
            EventBus.RaiseWeaponsChanged();
        }
    }
}
"""),
    ("guard02_同行守卫", False, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void LevelUpWeapon(int id)
        {
            weapons[id].level++;
            if (isLocal) EventBus.RaiseWeaponsChanged();
        }
    }
}
"""),
    ("guard03_早退守卫", False, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void Tick()
        {
            if (!isLocal) return;
            EventBus.RaisePlayerHealthRatio(1f);
        }
    }
}
"""),
    # 花括号在下一行的块式守卫（本工程 PlayerController 就是这种写法）
    ("guard04_块式守卫", False, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void ApplyFromSave(Save s)
        {
            if (isLocal)
            {
                EventBus.RaiseWeaponsChanged();
                EventBus.RaiseLevelUp(s.level);
            }
        }
    }
}
"""),
    # 没有"本地玩家"这个概念的类型不该被本项管（Net 层用的是另一套 IsServer 守卫）
    ("guard05_无本地概念不查", False, """
namespace A
{
    public class Enemy
    {
        public void Die()
        {
            EventBus.RaiseEnemyKilled(1);
        }
    }
}
"""),
    ("guard06_带理由豁免", False, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void SyncAll()
        {
            // @nolocalguard: 断线重连需无条件刷新一次 HUD
            EventBus.RaiseWeaponsChanged();
        }
    }
}
"""),
    # 注解是"封闭词汇"：空理由等于没写理由，不能拿来绕过检查
    ("guard07_空理由豁免", True, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void SyncAll()
        {
            // @nolocalguard:
            EventBus.RaiseWeaponsChanged();
        }
    }
}
"""),
    # 顺序陷阱：早退写在调用之后，挡不住已经发出去的广播
    ("guard08_早退在调用之后", True, """
namespace A
{
    public class Player
    {
        public bool isLocal = false;

        public void AddSkill(SkillDef def)
        {
            EventBus.RaiseWeaponsChanged();
            if (!isLocal) return;
        }
    }
}
"""),
]


def _run_fixtures(fixtures, checks):
    """把夹具写进临时目录跑一遍指定检查，返回被报错的夹具名集合。"""
    import shutil
    import tempfile

    global ASSETS_DIR
    tmp = tempfile.mkdtemp(prefix="starpixel-audit-selftest-")
    saved, saved_errors, saved_infos = ASSETS_DIR, errors[:], infos[:]
    try:
        for name, _expect, code in fixtures:
            with open(os.path.join(tmp, name + ".cs"), "w", encoding="utf-8") as f:
                f.write(code.lstrip("\n"))
        ASSETS_DIR = tmp
        errors.clear()
        infos.clear()
        for c in checks:
            c()
        return {name for name, _e, _c in fixtures if any(name in e for e in errors)}
    finally:
        ASSETS_DIR = saved
        errors[:], infos[:] = saved_errors, saved_infos
        shutil.rmtree(tmp, ignore_errors=True)


def selftest():
    """把夹具写进临时目录跑一遍，逐个核对"该红/不该红"。"""
    groups = [
        ("归属检查（第 7 项）", SELFTEST, [check_orphan_gameobjects]),
        ("事件总线订阅检查（第 8 项）", SELFTEST_EB, [check_eventbus_subscriptions]),
        ("运行时资源缓存检查（第 9 项）", SELFTEST_RES, [check_runtime_asset_cache]),
        ("派生物广播检查（第 10 项）", SELFTEST_CB, [check_derive_broadcast]),
        ("本地守卫对称性检查（第 11 项）", SELFTEST_GUARD, [check_local_guard_symmetry]),
    ]

    all_bad = 0
    for title, fixtures, checks in groups:
        flagged = _run_fixtures(fixtures, checks)
        bad = [(n, e) for n, e, _c in fixtures if (n in flagged) != e]
        all_bad += len(bad)
        print("=" * 68)
        print("%s · %d 个夹具" % (title, len(fixtures)))
        print("=" * 68)
        for name, expect, _c in fixtures:
            got = name in flagged
            mark = "  OK" if got == expect else "FAIL"
            print("  [%s] %-26s 期望%s 实际%s"
                  % (mark, name, "报错" if expect else "放行", "报错" if got else "放行"))
        print("-" * 68)
        print("结果：" + ("通过" if not bad else "未通过（%d 项不符）" % len(bad)))
        print()

    print("合计：" + ("通过" if not all_bad else "未通过（%d 项不符）" % all_bad))
    return 1 if all_bad else 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] in ("--selftest", "-t"):
        return selftest()

    if not os.path.isdir(SCRIPTS_DIR):
        print("找不到脚本目录: %s" % SCRIPTS_DIR)
        return 1

    check_brackets()
    check_fishnet_guards()
    check_enum_coverage()
    check_resources()
    check_duplicate_members()
    check_missing_usings()
    check_orphan_gameobjects()
    check_eventbus_subscriptions()
    check_runtime_asset_cache()
    check_derive_broadcast()
    check_local_guard_symmetry()

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
