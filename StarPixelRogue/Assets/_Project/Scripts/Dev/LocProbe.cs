using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M33 本地化覆盖完整性无头实测。
    ///
    /// 动机：本地化表本身是自洽的（zh/en 键集合完全一致），切到英文后绝大多数界面
    /// 也确实变英文了——于是"看起来没问题"。漏网的只有绕过 Localization 直接写
    /// 字面量的地方。
    ///
    /// 所以本探针的定位是：① 守住单机可见的那一整片 UI 不退化；
    /// ② 覆盖静态查不到的盲区——**动态拼接的键**（"combo." + id、"codex.tab_" + tab），
    /// 拼错时 T() 静默返回键名本身，界面上直接显示 "combo.fire_storm" 这种裸键；
    /// ③（M34）覆盖联机专属 UI——它们的文案是**一次性写入**的，切语言不会自己重设。
    ///
    /// M34 订正一条 M33 写错的结论：当时认为 Net 层整段在 #if FISHNET_ENABLED 里、
    /// "没装联机插件时不参与编译"，所以运行时扫不到。事实是 Fish-Net 一直以内置包
    /// 形式在 Packages/ 下、ProjectSettings 的 Standalone 也一直带着 FISHNET_ENABLED，
    /// 那些代码**是编译的**。真正扫不到的原因是：模式标识在单机下 SetActive(false)、
    /// 断线遮罩只在掉线时才出现——是"没让它上屏"，不是"没编译"。
    /// 把怀疑当成结论写进注释，会让后面每一轮都跳过这块。
    ///
    /// 场景：
    ///   BASE    扫描器自检（先证明"数得出中文"，否则后面全绿是假的）
    ///   ZH      中文态基线（证明 UI 真的建出来了）
    ///   EN      英文态不得有任何中文（核心判据）
    ///   KEY     动态拼接键不得回退成裸键
    ///   RESTORE 切回中文后中文必须回来（证明 EN 的 0 不是因为 UI 没了）
    ///   NETUI   联机专属 UI（模式标识 / 断线遮罩）切英文后不得残留中文
    /// </summary>
    public class LocProbe : MonoBehaviour
    {
        private string _scene = "BASE";
        private bool _done;

        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // BASE
        private bool _baCanSeeZh;      // 自造的中文文本能被扫描器数出来
        private bool _baCanSeeEn;      // 同一文本改成英文后不再被数出来
        private string _baDetail = "";

        // ZH / EN / RESTORE
        private int _zhTexts;          // 中文态扫到的 UI 文本总数
        private int _zhCjk;            // 其中含中文的条数
        private int _enTexts;
        private int _enCjk;
        private int _rsTexts;
        private int _rsCjk;
        private string _cjkSample = "";   // 英文态第一条漏网中文（排查用）

        // KEY
        private int _keyTexts;         // 参与裸键检查的文本数
        private int _keyBare;          // 其中疑似裸键的条数
        private string _keySample = "";
        private bool _comboFired;      // 组合技广播真的发出去了（否则"没裸键"是假绿）
        private bool _codexOpened;     // 图鉴真的打开了（同上，5 个 tab 键要被拼出来）

        // NETUI（M34）：联机专属 UI 的切语言刷新
        private bool _netUiShown;      // 模式标识与断线遮罩真的上屏了（否则"0 条中文"是假绿）
        private int _netTexts;         // 联机 UI 文本条数（模式标识 + 遮罩上的全部文本）
        private int _netEnTexts;       // 英文态再数一次：切语言若把遮罩弄没了，这里会掉到 1
        private int _netZhCjk;         // 中文态其中含中文的条数
        private int _netEnCjk;         // 英文态其中含中文的条数（核心判据：必须为 0）
        private int _netRsCjk;         // 切回中文后含中文的条数（排除"UI 没了"的假绿）
        private string _netSample = "";   // 英文态第一条漏网中文（排查用）

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
            var go = new GameObject("LocProbe");
            go.SetActive(false);
            var p = go.AddComponent<LocProbe>();
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
                case "KEY":     body = Key();     break;
                case "RESTORE": body = Restore(); break;
                case "NETUI":   body = NetUi();   break;
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

        private static readonly Regex Cjk = new Regex("[一-鿿]");

        /// <summary>
        /// 形如 combo.fire_storm / codex.tab_weapons 的"键形词"。
        /// 首段要求 ≥3 个字符，是为了放过 "Damage x1.5" 里的 x1.5（首段仅 2 字符）
        /// 和 "Time 12.3" 里的 12.3（非字母开头）——纯形态判断误报太多，
        /// 所以这里只做粗筛，真正的判定交给下面那条"回查"规则。
        /// </summary>
        private static readonly Regex KeyLike = new Regex(@"\b([a-z][A-Za-z0-9_]{2,}(?:\.[A-Za-z0-9_]+)+)\b");

        /// <summary>
        /// 取当前场景里"玩家看得到"的 UI 文本。
        /// 排除池模板与未激活对象——模板上的中文是数据、不是界面。
        /// </summary>
        private static List<Text> VisibleTexts()
        {
            var list = new List<Text>();
            var all = Resources.FindObjectsOfTypeAll<Text>();
            foreach (var t in all)
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (!go.activeInHierarchy) continue;
                if (go.name.EndsWith("Template")) continue;      // M28：池模板不是"在用的"
                if (go.hideFlags != HideFlags.None) continue;
                list.Add(t);
            }
            return list;
        }

        private static int CountCjk(List<Text> texts, ref string sample)
        {
            int n = 0;
            foreach (var t in texts)
            {
                var s = t.text ?? "";
                if (!Cjk.IsMatch(s)) continue;
                n++;
                if (string.IsNullOrEmpty(sample)) sample = go_path(t) + " = " + Trim(s);
            }
            return n;
        }

        private static int CountBare(List<Text> texts, ref string sample)
        {
            int n = 0;
            foreach (var t in texts)
            {
                var s = t.text ?? "";
                if (s.Length == 0) continue;
                foreach (Match m in KeyLike.Matches(s))
                {
                    var tok = m.Groups[1].Value;
                    // 本质判据：**T() 缺键时返回键名本身**。拿这个词回查一次，
                    // 查出来还是它自己，就说明它没进表、是回退出来的裸键。
                    // 比"猜键长什么样"可靠得多——拼错前缀（combo_x.fire_storm）
                    // 同样能被抓住，而正常文案里的 "x1.5" 会被上面那条粗筛排除。
                    if (Core.Localization.T(tok) != tok) continue;
                    n++;
                    if (string.IsNullOrEmpty(sample)) sample = go_path(t) + " = " + Trim(s);
                    break;
                }
            }
            return n;
        }

        private static string go_path(Text t)
        {
            var n = t.gameObject.name;
            var p = t.transform.parent;
            return p != null ? p.name + "/" + n : n;
        }

        private static string Trim(string s)
        {
            s = s.Replace("\n", " ");
            return s.Length > 48 ? s.Substring(0, 48) + "…" : s;
        }

        // ── 场景 ──────────────────────────────────────────────────

        /// <summary>
        /// BASE：先证明"扫描器数得出中文"。
        /// 这一步不做的话，EN 场景的 0 可能只是因为扫描器压根没扫到任何东西（假绿）。
        /// </summary>
        private IEnumerator Base()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Core.Localization.SetLanguage(Core.Localization.LangZh, silent: true);

            var host = new GameObject("LocProbeBase");
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(host.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
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
            int cjk = CountCjk(texts, ref sample);
            _baCanSeeZh = cjk == 1;
            if (!_baCanSeeZh) Fail("扫描器数不出刚写进去的中文（数到 " + cjk + " 条，期望 1）");

            txt.text = "Probe";
            yield return null;
            sample = "";
            cjk = CountCjk(texts, ref sample);
            _baCanSeeEn = cjk == 0;
            if (!_baCanSeeEn) Fail("同一条文本改成英文后仍被数成中文（数到 " + cjk + " 条）——判据把英文也当中文了");
            _baDetail = "中文态=" + (_baCanSeeZh ? "1" : "0") + " 英文态=" + (_baCanSeeEn ? "0" : "非0");

            UnityEngine.Object.Destroy(host);
        }

        private IEnumerator Zh()
        {
            yield return BuildWorld();
            yield return OpenUi(Core.Localization.LangZh);

            var texts = VisibleTexts();
            _zhTexts = texts.Count;
            _zhCjk = CountCjk(texts, ref _cjkSample);
        }

        private IEnumerator En()
        {
            yield return BuildWorld();
            yield return OpenUi(Core.Localization.LangEn);

            var texts = VisibleTexts();
            _enTexts = texts.Count;
            _enCjk = CountCjk(texts, ref _cjkSample);
        }

        private IEnumerator Restore()
        {
            yield return BuildWorld();

            yield return OpenUi(Core.Localization.LangEn);
            _enTexts = VisibleTexts().Count;
            _enCjk = CountCjk(VisibleTexts(), ref _cjkSample);

            yield return OpenUi(Core.Localization.LangZh);
            var texts = VisibleTexts();
            _rsTexts = texts.Count;
            _rsCjk = CountCjk(texts, ref _cjkSample);
        }

        /// <summary>
        /// KEY：动态拼接键（"combo." + id、"codex.tab_" + tab）不得回退成裸键。
        /// 这是静态检查的盲区——键是运行时拼出来的，扫源码看不见拼完是什么。
        /// 必须先把组合技广播与图鉴都真的触发一遍，否则"没裸键"只是因为那条路没走过。
        /// </summary>
        private IEnumerator Key()
        {
            // 先用 OpenUi 把图鉴打开（5 个 codex.tab_* 动态键要真的被拼出来），
            // 再触发组合技——两条动态键路径都必须真的走过，"没有裸键"才有意义。
            yield return BuildWorld();
            yield return OpenUi(Core.Localization.LangZh);

            // ① 组合技提示：HUD 的 OnComboActivated 会拼 "combo." + key
            Core.EventBus.RaiseComboActivated("fire_storm");
            _comboFired = true;
            yield return null;

            // ② 图鉴：AddTab 每次都拼 "codex.tab_" + key.ToLowerInvariant()
            var codex = UI.CodexPanel.Instance;
            if (codex == null)
                Fail("图鉴面板实例缺失——5 个 codex.tab_* 动态键没被拼出来，" +
                     "后面的「没有裸键」是假绿");
            else if (!codex.gameObject.activeInHierarchy)
                Fail("图鉴没显示出来——tab 键文本不在 activeInHierarchy 里，扫不到");
            else
                _codexOpened = true;

            var texts = VisibleTexts();
            _keyTexts = texts.Count;
            _keyBare = CountBare(texts, ref _keySample);
        }

        /// <summary>
        /// NETUI（M34）：联机专属 UI 切到英文后不得残留中文。
        ///
        /// M33 的判断是"Net 层的浮层编译不到、运行时扫不到，只能靠静态检查"——
        /// 这个结论**错了**：Fish-Net 从一开始就以内置包的形式在 Packages/ 里，
        /// ProjectSettings 的 Standalone 也一直带着 FISHNET_ENABLED，那些代码是编译的。
        /// 真正扫不到的原因是另外两条：
        ///   ① 模式标识 `modeText` 在单机下被 SetActive(false)，扫描器按 activeInHierarchy
        ///      过滤，等于从来没看过它一眼；
        ///   ② 断线遮罩只在掉线时才显示，单机流程里根本不会走到。
        /// 两条都是"没让它上屏"，不是"没编译"——手动把它们显示出来就能扫。
        ///
        /// 这也是静态层的盲区：modeText 的文案经 CreateText(… ModeLabel()) 写入，
        /// 扫源码看不见"这个返回值是本地化文案"，只有运行时看成品才知道。
        /// </summary>
        private IEnumerator NetUi()
        {
#if FISHNET_ENABLED
            yield return BuildWorld();
            yield return OpenUi(Core.Localization.LangZh);

            // ① 模式标识：单机下是隐藏的，先强制上屏
            var hud = FindObjectOfType<UI.HUDController>();
            if (hud == null || hud.modeText == null)
            {
                Fail("拿不到 HUD 的模式标识——联机 UI 没上屏，「0 条中文」是假绿");
                yield break;
            }
            hud.modeText.gameObject.SetActive(true);

            // ② 断线遮罩：单机流程走不到，直接驱动重连流程把它显示出来
            var rc = Net.NetReconnect.Ensure();
            rc.BeginReconnect(false, "", 99, 9999f);   // 9999s：本场景内不会真的重试
            yield return null;
            yield return null;                          // Object.Destroy 延迟到帧末，多等一帧

            var texts = NetUiTexts();
            _netTexts = texts.Count;
            _netUiShown = _netTexts >= 2;
            if (!_netUiShown)
            {
                Fail("联机 UI 只扫到 " + _netTexts + " 条文本（期望 ≥2：模式标识 + 遮罩状态/按钮）——" +
                     "说明遮罩没显示出来，后面的 0 是假绿");
                yield break;
            }
            _netZhCjk = CountCjk(texts, ref _netSample);
            if (_netZhCjk < 1)
                Fail("中文态联机 UI 一条中文都没有（" + _netTexts + " 条）——扫描器失灵，EN 的 0 是假绿");

            // ③ 切英文：这是核心判据。旧的 modeText 与遮罩都不会自己重设
            yield return OpenUi(Core.Localization.LangEn);
            texts = NetUiTexts();
            _netEnTexts = texts.Count;
            _netSample = "";      // 中文态那条样本要清掉，否则"首条中文"会挂着中文态的残留
            _netEnCjk = CountCjk(texts, ref _netSample);
            // 切语言若把遮罩弄没了（重建顺序写反就是这种表现），条数会掉下来，
            // 此时的"0 条中文"是假绿——必须连带看条数。
            if (_netEnTexts < _netTexts)
                Fail("切英文后联机 UI 只剩 " + _netEnTexts + " 条文本（切之前 " + _netTexts +
                     " 条）——遮罩在切语言时没了，此时的「0 条中文」是假绿");

            // ④ 切回中文：证明上面的 0 是因为刷新生效，不是因为 UI 没了。
            //    样本另用一个变量——中文态必然有中文，若复用 _netSample 会把"首条中文"
            //    顶成中文态的残留，判据因此永远红（实测踩到）。
            yield return OpenUi(Core.Localization.LangZh);
            texts = NetUiTexts();
            string rsSample = "";
            _netRsCjk = CountCjk(texts, ref rsSample);
#else
            Fail("FISHNET_ENABLED 未定义——联机 UI 根本没编译，本场景的任何" +
                 "「0 条中文」都是假绿，必须先确认条件编译符号");
            yield break;
#endif
        }

        /// <summary>
        /// 联机专属 UI 上的文本：HUD 的模式标识 + 断线遮罩里的全部文本。
        /// 只取这两处，是为了让判据**可归因**——扫全屏的话，别处的漏网会混进来，
        /// 修完也说不清到底是哪个组件没刷新。
        /// </summary>
        private List<Text> NetUiTexts()
        {
#if FISHNET_ENABLED
            var list = new List<Text>();
            var hud = FindObjectOfType<UI.HUDController>();
            if (hud != null && hud.modeText != null && hud.modeText.gameObject.activeInHierarchy)
                list.Add(hud.modeText);

            var rc = Net.NetReconnect.Instance;
            if (rc != null && rc.OverlayRoot != null && rc.OverlayRoot.activeSelf)
                list.AddRange(rc.OverlayRoot.GetComponentsInChildren<Text>(true));
            return list;
#else
            return new List<Text>();
#endif
        }

        // ── 公共步骤 ──────────────────────────────────────────────

        private IEnumerator BuildWorld()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();

            GameBuilder.PrepareStage();
            yield return null;
            GameBuilder.Build();
            yield return null;
            yield return null;

            if (Player.PlayerController.Instance == null)
                Fail("建世界后拿不到 PlayerController.Instance——后面扫到的 UI 不完整，结果不可信");
        }

        /// <summary>
        /// 切到指定语言，并把主菜单与图鉴都打开——扫描时刻它们必须**保持可见**，
        /// 一旦 Hide 掉，activeInHierarchy 为 false，VisibleTexts 就扫不到了。
        ///
        /// 切语言的手法：SetLanguage 对同语言不广播（changed == false），
        /// 所以先切到另一门语言再切回来，逼 MainMenu 的 Rebuild 真的跑一遍
        /// ——它订阅的是 LanguageChanged，不广播就不会按新语言重建面板。
        /// 这也正好模拟玩家在设置里来回切语言的真实操作。
        /// </summary>
        private IEnumerator OpenUi(string lang)
        {
            string other = lang == Core.Localization.LangZh
                ? Core.Localization.LangEn
                : Core.Localization.LangZh;

            Core.Localization.SetLanguage(other, silent: false);
            yield return null;
            Core.Localization.SetLanguage(lang, silent: false);
            yield return null;

            UI.MainMenu.Ensure();
            UI.CodexPanel.Ensure();
            yield return null;

            if (UI.MainMenu.Instance != null) UI.MainMenu.Instance.Show();
            if (UI.CodexPanel.Instance != null) UI.CodexPanel.Instance.Show();
            // Object.Destroy 延迟到帧末：多等两帧，别扫到尚未销毁的旧面板
            yield return null;
            yield return null;
        }

        // ── 判定 + 报告 ───────────────────────────────────────────

        private void Judge()
        {
            switch (_scene)
            {
                case "BASE":
                    if (_baCanSeeZh) Ok("扫描器能数出自造的中文文本");
                    if (_baCanSeeEn) Ok("同一文本改英文后不再被数成中文");
                    break;

                case "ZH":
                    // 中文态必须真的有中文：证明 UI 建出来了、扫描也扫到了
                    if (_zhCjk >= 1) Ok("中文态扫到 " + _zhCjk + "/" + _zhTexts + " 条含中文的 UI 文本");
                    else Fail("中文态一条中文都没扫到（UI 文本 " + _zhTexts + " 条）——" +
                              "要么 UI 没建出来，要么扫描器失灵，EN 场景的 0 会变成假绿");
                    break;

                case "EN":
                    if (_enTexts < 1)
                        Fail("英文态一条 UI 文本都没扫到——UI 没建出来，「0 条中文」是假绿");
                    if (_enCjk == 0) Ok("英文态 " + _enTexts + " 条 UI 文本，无一含中文");
                    else Fail("英文态仍有 " + _enCjk + " 条 UI 文本含中文（首条: " + _cjkSample + "）——" +
                              "玩家切到英文界面会看到中文");
                    break;

                case "KEY":
                    if (!_comboFired)
                        Fail("组合技广播没发出去——动态键 combo.* 没被拼出来，「没有裸键」是假绿");
                    if (!_codexOpened)
                        Fail("图鉴没打开——codex.tab_* 动态键没被拼出来，「没有裸键」是假绿");
                    if (_keyTexts < 1)
                        Fail("一条 UI 文本都没扫到——裸键检查是假绿");
                    if (_keyBare == 0) Ok(_keyTexts + " 条 UI 文本中无本地化裸键");
                    else Fail("有 " + _keyBare + " 条 UI 文本是本地化裸键（首条: " + _keySample + "）——" +
                              "键拼错时 T() 静默返回键名本身，玩家会看到 combo.fire_storm 这种原始键");
                    break;

                case "NETUI":
                    if (!_netUiShown)
                        Fail("联机 UI 没上屏——模式标识/断线遮罩不可见，下面的 0 是假绿");
                    else if (_netZhCjk < 1)
                        Fail("中文态联机 UI 一条中文都没有（" + _netTexts + " 条）——扫描器失灵");
                    else if (_netEnCjk == 0)
                        Ok("联机 UI 切英文后 " + _netTexts + " 条文本无一含中文");
                    else
                        Fail("联机 UI 切英文后仍有 " + _netEnCjk + " 条含中文（首条: " + _netSample + "）——" +
                             "模式标识/断线遮罩是一次性写入的，切语言不会自己重设");
                    if (_netUiShown && _netRsCjk < 1)
                        Fail("切回中文后联机 UI 的中文没回来（" + _netRsCjk + " 条）——" +
                             "说明英文态的 0 是因为 UI 没了，不是因为刷新生效");
                    break;

                case "RESTORE":
                    if (_enCjk == 0) Ok("英文态 0 条中文");
                    else Fail("英文态仍有 " + _enCjk + " 条中文（首条: " + _cjkSample + "）");
                    if (_rsCjk >= 1) Ok("切回中文后恢复 " + _rsCjk + " 条中文");
                    else Fail("切回中文后中文没回来（" + _rsCjk + " 条）——" +
                              "说明 EN 的 0 是因为 UI 没了，不是因为本地化生效");
                    break;
            }

            if (_errors > 0)
                Fail("运行期异常 " + _errors + " 条（首条: " + Trim(_firstError) + "）");
        }

        // 与 GuardProbe 一致：键值之间用多个空格对齐——驱动脚本的解析正则按 \s{2,} 切分，
        // 只写一个空格的话整行都匹配不上，表现是"未捕获 [SMOKE] 输出"（看着像探针没跑）。
        private void Line(string k, string v)
        {
            Debug.Log("[SMOKE] " + k + "              " + v);
        }

        private void Report()
        {
            Judge();

            Debug.Log("[SMOKE] ===== M33 本地化覆盖实测 (" + _scene + ") =====");
            switch (_scene)
            {
                case "BASE":
                    Line("扫描器自检", _baDetail);
                    break;
                case "ZH":
                    Line("UI 文本总数", _zhTexts.ToString());
                    Line("含中文条数", _zhCjk.ToString());
                    break;
                case "EN":
                    Line("UI 文本总数", _enTexts.ToString());
                    Line("含中文条数", _enCjk.ToString());
                    Line("首条中文", _cjkSample == "" ? "无" : _cjkSample);
                    break;
                case "KEY":
                    Line("组合技已触发", _comboFired ? "是" : "否");
                    Line("图鉴已打开", _codexOpened ? "是" : "否");
                    Line("UI 文本总数", _keyTexts.ToString());
                    Line("裸键条数", _keyBare.ToString());
                    Line("首条裸键", _keySample == "" ? "无" : _keySample);
                    break;
                case "RESTORE":
                    Line("英文态文本/中文", _enTexts + " / " + _enCjk);
                    Line("切回后文本/中文", _rsTexts + " / " + _rsCjk);
                    break;
                case "NETUI":
                    Line("联机UI已上屏", _netUiShown ? "是" : "否");
                    Line("联机UI文本总数", _netTexts.ToString());
                    Line("中文态含中文", _netZhCjk.ToString());
                    Line("英文态文本总数", _netEnTexts.ToString());
                    Line("英文态含中文", _netEnCjk.ToString());
                    Line("首条中文", _netSample == "" ? "无" : _netSample);
                    Line("切回后含中文", _netRsCjk.ToString());
                    break;
            }

            Line("失败项", _fail.Count.ToString());
            Line("结论", _fail.Count == 0 ? "通过" : "未通过");
            Debug.Log("[SMOKE] " + (_fail.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL"));
        }
    }
}
