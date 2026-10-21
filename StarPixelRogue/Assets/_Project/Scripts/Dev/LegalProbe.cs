using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M35 法务文本双语无头实测。
    ///
    /// 动机：三份法律正文（用户协议 / 隐私政策 / 儿童个人信息保护规则）住在
    /// Resources/Legal/*.txt，是**唯一一类不在 .cs 里的玩家可见文案**。于是前面每一层
    /// 断言都绕过了它：
    ///   · 静态第 12 项扫 .cs 里的中文字面量——txt 不是 .cs；
    ///   · 静态第 13 项扫 Text 字段赋值——正文是 Resources.Load 出来的整段字符串；
    ///   · 运行时 L10N 场景只扫 ComplianceGate.PanelRoot 下的按钮标签——
    ///     **三个法律文档从来没有被点开过**。
    /// 而 LegalTexts.Get() 原本只认 Legal/&lt;key&gt;.txt 一条路径、那三份是纯中文的：
    /// 英文界面下点开「Terms of Service」，弹出来的是整篇中文。
    ///
    /// 本探针覆盖两个层次，各自抓不同的东西：
    ///   数据层（ZH/EN）——Resources.Load 真的取到了对应语言的资源（路径拼错时
    ///     Load 返回 null，静态检查看不出来，只有跑起来才知道）；
    ///   UI 层（UI/REFRESH）——文案真的上了屏、且切语言时会被重设。
    ///
    /// 场景：
    ///   BASE    扫描器自检（先证明"数得出中文"，否则后面全绿是假的）
    ///   ZH      中文态：三份文档都取得到、且都含中文
    ///   EN      英文态：三份文档零中文，且一次都没回退到中文（核心判据）
    ///   UI      三份法务窗逐个打开上屏，切英文后窗内零中文
    ///   REFRESH 已打开的窗**不关掉重开**，切语言时必须就地刷新
    /// </summary>
    public class LegalProbe : MonoBehaviour
    {
        private string _scene = "BASE";
        private bool _done;

        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // BASE
        private bool _baCanSeeZh;
        private bool _baCanSeeEn;
        private string _baDetail = "";

        // ZH / EN（数据层）
        private int _docs;             // 取到的文档份数（必须为 3）
        private int _zhCjkDocs;        // 中文态含中文的文档份数（必须 = 3）
        private int _enCjkDocs;        // 英文态含中文的文档份数（核心判据：必须为 0）
        private int _fallback;         // 英文版缺失、被迫回退中文的次数（正常必须为 0）
        private string _enSample = ""; // 英文态第一条漏网中文（排查用）
        private int _enChars;          // 英文态三份文档的总字符数（防止"取到空串也算通过"）
        private int _zhChars;          // 中文态预取的总字符数（与英文态对比：内容必须真的换了）

        // UI
        private int _winTexts;         // 三份法务窗上的文本总条数（标题+正文+关闭）
        private int _winZhCjk;         // 中文态其中含中文的条数
        private int _winEnTexts;       // 英文态再数一次：条数掉下来说明窗没了（假绿）
        private int _winEnCjk;         // 英文态其中含中文的条数（必须 0）
        private string _winSample = "";

        // REFRESH
        private int _rfZhCjk;          // 已开的窗在中文态含中文条数
        private int _rfEnCjk;          // 就地切英文后含中文条数（必须 0）
        private int _rfRsCjk;          // 再切回中文后含中文条数（证明窗还在、且真的跟着变）
        private string _rfSample = "";

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        // ── 生命周期 ──────────────────────────────────────────────

        public static void Start(string scene)
        {
            // AddComponent 会**同步执行 Awake**；本探针的初始态（日志订阅/语言）在 Awake 里摆好，
            // 若在 AddComponent 之后才赋值，Awake 读到的永远是默认值（M21/M22 同一条坑）。
            var go = new GameObject("LegalProbe");
            go.SetActive(false);
            var p = go.AddComponent<LegalProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Awake()
        {
            Application.logMessageReceived += OnLog;
            Core.Localization.SetLanguage(Core.Localization.LangZh, silent: true);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            Core.Localization.SetLanguage(Core.Localization.LangZh, silent: true);
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        private IEnumerator Start()
        {
            yield return Drive();
            Report();
            _done = true;
            yield return new WaitForSeconds(0.2f);
            Application.Quit(_fail.Count == 0 ? 0 : 1);
        }

        /// <summary>
        /// 手动驱动迭代器：C# 不允许在带 catch 的 try 里 yield（CS1626），
        /// 但探针**必须自己吞掉异常**——协程一旦被打断，后面的断言全不执行、
        /// 日志一片空白，表现出来是"悄悄挂死"而不是"判据变红"（M29 踩过）。
        /// </summary>
        private IEnumerator Drive()
        {
            IEnumerator body;
            switch (_scene)
            {
                case "BASE":    body = Base();    break;
                case "ZH":      body = Zh();      break;
                case "EN":      body = En();      break;
                case "UI":      body = Ui();      break;
                case "REFRESH": body = Refresh(); break;
                default:        body = Base();    break;
            }

            var pump = Pump(body);
            while (pump.MoveNext())
                yield return pump.Current;
        }

        private IEnumerator Pump(IEnumerator body)
        {
            Exception caught = null;
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved;
                try
                {
                    moved = top.MoveNext();
                }
                catch (Exception e)
                {
                    caught = e;
                    break;
                }
                if (!moved) { stack.Pop(); continue; }

                var nested = top.Current as IEnumerator;
                if (nested != null) { stack.Push(nested); continue; }
                yield return top.Current;
            }

            if (caught != null)
                Fail("探针自身抛异常——协程被打断会导致日志空白、结果假绿: " + caught.Message);
        }

        // ── 断言 ──────────────────────────────────────────────────

        private void Fail(string msg)
        {
            _fail.Add(msg);
            Debug.Log("[SMOKE] x " + msg);
        }

        private void Ok(string msg)
        {
            Debug.Log("[SMOKE] √ " + msg);
        }

        // ── 扫描 ──────────────────────────────────────────────────

        /// <summary>
        /// 与静态检查第 14 项同一套字符范围：汉字 + 中文标点（\u3000-\u303f）+ 全角（\uff00-\uffef）。
        /// 只查汉字会放过书名号「《》」与全角括号那种"看着像英文、其实没翻译"的残留。
        /// </summary>
        private static readonly Regex Cjk = new Regex("[　-〿一-鿿＀-￯]");

        /// <summary>三份文档键（terms / privacy / children）。</summary>
        private static List<string> DocKeys()
        {
            var list = new List<string>();
            foreach (var k in Compliance.LegalTexts.AllKeys()) list.Add(k);
            return list;
        }

        /// <summary>
        /// 取一份文档的正文。返回 null 表示没取到（资源缺失）——与"取到空串"区分开。
        /// </summary>
        private static string Doc(string key)
        {
            var t = Compliance.LegalTexts.Get(key);
            if (string.IsNullOrEmpty(t)) return null;
            if (t.Contains("法律文本缺失")) return null;   // 占位串等于没取到
            return t;
        }

        private static bool HasCjk(string s)
        {
            return !string.IsNullOrEmpty(s) && Cjk.IsMatch(s);
        }

        private static string Trim(string s)
        {
            s = (s ?? "").Replace("\n", " ").Replace("\r", " ");
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }

        /// <summary>取一个 UI 根下"玩家看得到"的文本。</summary>
        private static List<Text> TextsUnder(GameObject root)
        {
            var list = new List<Text>();
            if (root == null) return list;
            foreach (var t in root.GetComponentsInChildren<Text>(true))
            {
                if (t == null) continue;
                if (!t.gameObject.activeInHierarchy) continue;
                list.Add(t);
            }
            return list;
        }

        private static int CountCjkTexts(List<Text> texts, ref string sample)
        {
            int n = 0;
            foreach (var t in texts)
            {
                var s = t.text ?? "";
                if (!HasCjk(s)) continue;
                n++;
                if (string.IsNullOrEmpty(sample)) sample = t.gameObject.name + " = " + Trim(s);
            }
            return n;
        }

        private static void SetLang(string lang)
        {
            // 同语言调用 SetLanguage 时 changed==false、不广播（M33 踩过），
            // 这里每次都显式指定目标语言，切回中文是另一次真实切换。
            Core.Localization.SetLanguage(lang);
        }

        // ── 场景 ──────────────────────────────────────────────────

        /// <summary>
        /// BASE：先证明扫描器数得出中文。若它压根数不出，后面每个场景的"0 条中文"
        /// 都会变成假绿——这与 M33 的 BASE、M32 翻 isLocal 前先证明翻转有效是同一条纪律。
        /// </summary>
        private IEnumerator Base()
        {
            Core.SettingsManager.Ensure();
            Core.Localization.SetLanguage(Core.Localization.LangZh, silent: true);

            var host = new GameObject("LegalProbeBase");
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(host.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = Core.UIFactory.MenuSortingOrder;   // M51：显式声明层级
            canvasGo.AddComponent<CanvasScaler>();
            var txt = Core.UIFactory.CreateText(canvasGo.transform, "Probe", "中文探针",
                new Vector2(0, 0), 24, TextAnchor.MiddleCenter, new Vector2(400, 40));
            yield return null;

            if (txt == null)
            {
                Fail("UIFactory.CreateText 返回 null——扫描器自检的前提不成立，后面全是假绿");
                yield break;
            }

            string sample = "";
            var texts = new List<Text> { txt };
            int cjk = CountCjkTexts(texts, ref sample);
            _baCanSeeZh = cjk == 1;
            if (!_baCanSeeZh)
                Fail("扫描器数不出刚写进去的中文（数到 " + cjk + " 条，期望 1）");

            txt.text = "Probe";
            yield return null;
            sample = "";
            cjk = CountCjkTexts(texts, ref sample);
            _baCanSeeEn = cjk == 0;
            if (!_baCanSeeEn)
                Fail("同一条文本改成英文后仍被数成中文（数到 " + cjk + " 条）——判据把英文也当中文了");

            // 顺带证明中文标点也算数：只查汉字的话，"《》"这种残留会漏网
            txt.text = "Terms 《Star》";
            yield return null;
            sample = "";
            cjk = CountCjkTexts(texts, ref sample);
            if (cjk != 1)
                Fail("中文标点没被数成中文（数到 " + cjk + " 条）——判据只认汉字，书名号会漏网");

            _baDetail = "中文态=" + (_baCanSeeZh ? "1" : "0") +
                        " 英文态=" + (_baCanSeeEn ? "0" : "非0") +
                        " 中文标点=" + (cjk == 1 ? "认得" : "漏网");

            UnityEngine.Object.Destroy(host);
        }

        /// <summary>
        /// ZH：中文态基线。三份文档必须都取得到、且都含中文——
        /// 否则 EN 的"0 条中文"可能是因为压根没取到内容（假绿）。
        /// </summary>
        private IEnumerator Zh()
        {
            Compliance.LegalTexts.ResetFallbackCountForTest();
            SetLang(Core.Localization.LangZh);
            yield return null;

            var keys = DocKeys();
            _docs = 0;
            _zhCjkDocs = 0;
            foreach (var k in keys)
            {
                var t = Doc(k);
                if (t == null)
                {
                    Fail("中文态取不到文档 " + k + "——资源没加载出来，EN 的「0 条中文」会是假绿");
                    continue;
                }
                _docs++;
                if (HasCjk(t)) _zhCjkDocs++;
                else Fail("中文态文档 " + k + " 一条中文都没有——拿到的不是中文版");
            }

            if (_docs != 3)
                Fail("只取到 " + _docs + " 份文档（期望 3：terms/privacy/children）");
            else Ok("中文态三份文档齐全且都含中文");

            yield return null;
        }

        /// <summary>
        /// EN：核心判据。英文态三份文档必须零中文，且一次都没回退到中文。
        /// 静态检查保证"文件成对"，这里保证"Resources.Load 真的取到了英文那份"——
        /// 路径拼错时 Load 返回 null，静态看不出来。
        /// </summary>
        private IEnumerator En()
        {
            Compliance.LegalTexts.ResetFallbackCountForTest();
            SetLang(Core.Localization.LangZh);
            yield return null;

            // 先按中文态真取一遍：这样"切语言后内容真的换了"才站得住——
            // 若缓存不按语言分区（M35 注入 R3），这里取到的中文会被后面命中，
            // 英文态拿到的还是中文，EN 场景当场变红。
            int zhChars = 0, zhCjkDocs = 0;
            foreach (var k in DocKeys())
            {
                var t = Doc(k);
                if (t == null) continue;
                zhChars += t.Length;
                if (HasCjk(t)) zhCjkDocs++;
            }
            _zhChars = zhChars;
            if (zhCjkDocs != 3)
                Fail("中文态预取时含中文的文档只有 " + zhCjkDocs + " 份（期望 3）——" +
                     "基线不成立，英文态的「0 条中文」可能是没取到内容");

            SetLang(Core.Localization.LangEn);
            yield return null;

            var keys = DocKeys();
            _docs = 0;
            _enCjkDocs = 0;
            _enChars = 0;
            _enSample = "";
            foreach (var k in keys)
            {
                var t = Doc(k);
                if (t == null)
                {
                    Fail("英文态取不到文档 " + k + "——Resources.Load(\"Legal/en/" + k + "\") 没取到资源");
                    continue;
                }
                _docs++;
                _enChars += t.Length;
                if (HasCjk(t))
                {
                    _enCjkDocs++;
                    if (string.IsNullOrEmpty(_enSample)) _enSample = k + " = " + Trim(t);
                }
            }

            _fallback = Compliance.LegalTexts.FallbackCount;

            if (_docs != 3)
                Fail("英文态只取到 " + _docs + " 份文档（期望 3）");
            if (_enCjkDocs != 0)
                Fail("英文态有 " + _enCjkDocs + " 份文档仍含中文（首条：" + _enSample + "）——" +
                     "英文界面下法律正文没翻译，这是合规问题：不同语言的用户拿到的法务文本不对等");
            if (_fallback != 0)
                Fail("英文版缺失、被回退中文 " + _fallback + " 次——Resources/Legal/en/ 漏了文件，" +
                     "玩家看到的是中文正文（静态检查第 14 项会先红）");
            if (_enChars < 300)
                Fail("英文态三份文档总共只有 " + _enChars + " 个字符——像取到了空壳，判据不能靠「没中文」蒙混");
            if (_fail.Count == 0)
                Ok("英文态三份法律正文零中文、零回退（共 " + _enChars + " 字符）");

            yield return null;
        }

        /// <summary>
        /// UI：三份法务窗逐个真的打开（L10N 场景从未点开过它们），切英文后窗内零中文。
        /// </summary>
        private IEnumerator Ui()
        {
            Compliance.LegalTexts.ResetFallbackCountForTest();
            SetLang(Core.Localization.LangZh);
            yield return null;

            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("compliance.terms", Compliance.LegalTexts.TermsKey),
                new KeyValuePair<string, string>("compliance.privacy", Compliance.LegalTexts.PrivacyKey),
                new KeyValuePair<string, string>("compliance.children", Compliance.LegalTexts.ChildrenKey),
            };

            // ① 中文态：三份窗逐个打开，累计文本条数与含中文条数
            foreach (var p in pairs)
            {
                var root = Compliance.LegalViewer.OpenLocalized(p.Key, p.Value);
                yield return null;
                var texts = TextsUnder(root);
                if (texts.Count == 0)
                {
                    Fail("法务窗 " + p.Value + " 上屏后扫到 0 条文本——窗没建出来，后面的 0 是假绿");
                    UnityEngine.Object.Destroy(root);
                    continue;
                }
                _winTexts += texts.Count;
                string s = "";
                _winZhCjk += CountCjkTexts(texts, ref s);
                UnityEngine.Object.Destroy(root);
                yield return null;   // Destroy 延迟到帧末，多等一帧再开下一扇
            }

            if (_winZhCjk < 1)
                Fail("中文态三份法务窗一条中文都没有（扫到 " + _winTexts + " 条文本）——" +
                     "扫描器失灵，EN 阶段的 0 是假绿");

            // ② 英文态：核心判据
            SetLang(Core.Localization.LangEn);
            yield return null;

            _winEnTexts = 0;
            _winSample = "";
            foreach (var p in pairs)
            {
                var root = Compliance.LegalViewer.OpenLocalized(p.Key, p.Value);
                yield return null;
                var texts = TextsUnder(root);
                _winEnTexts += texts.Count;
                string s = "";
                _winEnCjk += CountCjkTexts(texts, ref s);
                if (string.IsNullOrEmpty(_winSample)) _winSample = s;
                UnityEngine.Object.Destroy(root);
                yield return null;
            }

            // 条数掉下来说明窗没建出来——此时的"0 条中文"是假绿
            if (_winEnTexts < _winTexts)
                Fail("切英文后法务窗只剩 " + _winEnTexts + " 条文本（中文态 " + _winTexts + " 条）——" +
                     "窗没建全，「0 条中文」是假绿");
            if (_winEnCjk != 0)
                Fail("英文态法务窗有 " + _winEnCjk + " 条文本仍含中文（首条：" + _winSample + "）——" +
                     "玩家点开「Terms of Service」读到的是中文");
            if (_fail.Count == 0)
                Ok("三份法务窗英文态 " + _winEnTexts + " 条文本零中文");

            // ③ 切回中文：证明上面的 0 是因为真的换了语言，不是因为窗没了
            SetLang(Core.Localization.LangZh);
            yield return null;
            _fallback = Compliance.LegalTexts.FallbackCount;
            if (_fallback != 0)
                Fail("回退中文 " + _fallback + " 次——英文版资源缺失");
        }

        /// <summary>
        /// REFRESH：已打开的窗**不关掉重开**，切语言时必须就地刷新。
        /// 这一条是 M35 修的第二处：合规门原本传的是 T()/Get() 的成品字符串，
        /// 窗一旦打开就是死的——只有关掉重开才会变。现在传键 + 订阅 LanguageChanged。
        /// </summary>
        private IEnumerator Refresh()
        {
            Compliance.LegalTexts.ResetFallbackCountForTest();
            SetLang(Core.Localization.LangZh);
            yield return null;

            var root = Compliance.LegalViewer.OpenLocalized("compliance.terms", Compliance.LegalTexts.TermsKey);
            yield return null;

            var texts = TextsUnder(root);
            if (texts.Count == 0)
            {
                Fail("法务窗上屏后扫到 0 条文本——窗没建出来，REFRESH 的判据无从谈起");
                yield break;
            }

            string s = "";
            _rfZhCjk = CountCjkTexts(texts, ref s);
            if (_rfZhCjk < 1)
                Fail("中文态窗内一条中文都没有（" + texts.Count + " 条文本）——扫描器失灵");

            // 就地切英文：**不重开窗**
            SetLang(Core.Localization.LangEn);
            yield return null;
            yield return null;   // 给 ContentSizeFitter 一帧重排

            texts = TextsUnder(root);
            _rfSample = "";
            _rfEnCjk = CountCjkTexts(texts, ref _rfSample);
            if (_rfEnCjk != 0)
                Fail("已打开的法务窗切英文后仍有 " + _rfEnCjk + " 条中文（首条：" + _rfSample + "）——" +
                     "窗拿到的是成品字符串，切语言时不会重设，只有关掉重开才会变");

            // 再切回中文：证明窗还在、且两个方向都跟着变
            SetLang(Core.Localization.LangZh);
            yield return null;
            yield return null;

            texts = TextsUnder(root);
            string rs = "";
            _rfRsCjk = CountCjkTexts(texts, ref rs);
            if (_rfRsCjk < _rfZhCjk)
                Fail("切回中文后窗内含中文 " + _rfRsCjk + " 条（中文态原本 " + _rfZhCjk +
                     " 条）——窗被切语言弄没了，上面的「0 条中文」是假绿");
            if (_fail.Count == 0)
                Ok("已打开的法务窗就地切语言：中文 " + _rfZhCjk + " → 英文 0 → 回中文 " + _rfRsCjk);

            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        // ── 输出 ──────────────────────────────────────────────────

        // 与 GuardProbe/LocProbe 一致：键值之间用多个空格对齐——驱动脚本的解析正则
        // 按 \s{2,} 切分，只写一个空格的话整行都匹配不上，表现是"未捕获 [SMOKE] 输出"。
        private void Line(string k, string v)
        {
            Debug.Log("[SMOKE] " + k + "              " + v);
        }

        private void Report()
        {
            Debug.Log("[SMOKE] ===== M35 法务文本双语实测 (" + _scene + ") =====");
            switch (_scene)
            {
                case "BASE":
                    Line("扫描器自检", _baDetail);
                    if (!_baCanSeeZh || !_baCanSeeEn) Fail("扫描器自检未通过——后续场景的「0 条中文」不可信");
                    break;

                case "ZH":
                    Line("文档份数", _docs.ToString());
                    Line("中文态含中文份数", _zhCjkDocs.ToString());
                    if (_docs < 3) Fail("中文态只取到 " + _docs + " 份文档（期望 3）");
                    if (_zhCjkDocs < 3) Fail("中文态含中文的文档只有 " + _zhCjkDocs + " 份（期望 3）——基线不成立，EN 的 0 是假绿");
                    break;

                case "EN":
                    Line("文档份数", _docs.ToString());
                    Line("英文态含中文份数", _enCjkDocs.ToString());
                    Line("首条中文", _enSample == "" ? "无" : _enSample);
                    Line("英文版回退次数", _fallback.ToString());
                    Line("中文态总字符", _zhChars.ToString());
                    Line("英文态总字符", _enChars.ToString());
                    // 两种语言的正文长度本就不同，但同一语言下的两次取值必须一致：
                    // 若这里等长，说明切语言根本没换掉内容（缓存串味）。
                    if (_zhChars > 0 && _enChars == _zhChars)
                        Fail("英文态总字符 " + _enChars + " 与中文态预取完全相同——" +
                             "切语言没换掉正文，拿到的还是中文那份");
                    if (_enCjkDocs != 0) Fail("英文态有 " + _enCjkDocs + " 份文档仍含中文");
                    if (_fallback != 0) Fail("英文版缺失、回退中文 " + _fallback + " 次");
                    break;

                case "UI":
                    Line("中文态窗内文本", _winTexts.ToString());
                    Line("中文态含中文", _winZhCjk.ToString());
                    Line("英文态窗内文本", _winEnTexts.ToString());
                    Line("英文态含中文", _winEnCjk.ToString());
                    Line("首条中文", _winSample == "" ? "无" : _winSample);
                    if (_winZhCjk < 1) Fail("中文态法务窗一条中文都没有——扫描器失灵，EN 的 0 是假绿");
                    if (_winEnCjk != 0) Fail("英文态法务窗有 " + _winEnCjk + " 条文本仍含中文");
                    if (_winEnTexts < _winTexts) Fail("切英文后窗内文本从 " + _winTexts + " 掉到 " + _winEnTexts + " 条");
                    break;

                case "REFRESH":
                    Line("中文态含中文", _rfZhCjk.ToString());
                    Line("就地切英文后含中文", _rfEnCjk.ToString());
                    Line("切回中文后含中文", _rfRsCjk.ToString());
                    Line("首条中文", _rfSample == "" ? "无" : _rfSample);
                    if (_rfEnCjk != 0) Fail("已打开的窗切英文后仍有 " + _rfEnCjk + " 条中文——窗不会就地刷新");
                    if (_rfRsCjk < _rfZhCjk) Fail("切回中文后中文条数没恢复——窗在切语言时被弄没了");
                    break;
            }

            if (_errors != 0)
                Fail("运行期错误 " + _errors + " 条（首条：" + Trim(_firstError) + "）");

            Line("失败项", _fail.Count.ToString());
            Line("结论", _fail.Count == 0 ? "通过" : "未通过");
            Debug.Log("[SMOKE] " + (_fail.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL"));
        }
    }
}
