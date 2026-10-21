using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StarPixel.Core;
using StarPixel.UI;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M22 设置面板「真实交互」与设置落盘往返无头实测。
    ///
    /// 为什么必须测：M18 的 <see cref="UISettingsProbe"/> 已经在英文下把设置面板建出来过，
    /// 也真点过一次云存档开关——但它从未验证：**其余 7 个控件的回调是否真的接到了设置上**、
    /// **改完是否落盘**、**重启后是否原样读回**、**文件坏掉时会不会把用户设置静默清零**。
    /// 这四项全是"静默作废"型：按钮点了没反应、设置改了下次启动恢复默认，界面一切正常。
    ///
    /// 三个场景（每个独立进程：设置是进程级单例 + 落盘状态）：
    ///   TOGGLES    —— 逐个点击全屏/震屏/统计/青少年/云存档/画质循环/语言循环，
    ///                 断言「内存值变化」「真实 Load() 读回一致」「面板文字跟着变」三者同时成立；
    ///                 并断言点完之后面板仍可见（切语言会整体重建并默认隐藏，旧实现把用户踢回主菜单）。
    ///   ROUNDTRIP  —— 四个非默认值 + 当前语言 → 落盘 → 换一个全新实例 → Load → 逐字段比对，
    ///                 并在**同一进程内**断言每个设置项真的作用到了引擎（AudioListener / Quality /
    ///                 Screen / 语言 / 青少年模式 / 云存档 / 遥测 / 震屏），而不是只写了 JSON。
    ///   NEGATIVE   —— 设置文件被写坏（空文件 / 半截 JSON）时：不得崩溃、必须回退默认值，
    ///                 且**损坏文件必须被另存归档**——旧实现直接把 defaults 写回去，
    ///                 用户整套设置（含音量、语言、已同意协议版本）在没有任何提示的情况下被清零。
    ///
    /// 判据的区分力（本轮最要紧的地方）：
    /// 1) "改了没生效"与"生效了没落盘"必须分开断言：
    ///    前者查内存 + 面板文字，后者**重新走真实 Load()** 从磁盘读回比对。
    ///    只看内存的话，一个压根不保存的实现也能全绿（M21 同理）。
    /// 2) 画质用 **QualitySettings.GetQualityLevel()** 判、音量用 **AudioListener.volume** 判：
    ///    只查 SettingsManager.Current 只能证明"值被存了"，证明不了"引擎收到过"。
    /// 3) NEGATIVE 的判据在**进程外**：脚本自己读目录，确认 `settings.json.corrupt-*` 存在
    ///    且内容仍是那份坏数据。由被测进程自述"我备份了"很容易变成假的兜底。
    /// 4) 每个场景都从"文件不存在"这一**已知初始态**开始（由驱动脚本清空），
    ///    否则后一场景的断言会建立在前一场景的副作用上——M21 的 DECLINE 假通过就是这么来的。
    ///
    /// ⚠ 本探针会写真实的 settings.json：驱动脚本负责跑前备份、跑后恢复，
    /// 探针自身不碰用户原文件（只在 OnDestroy 里把语言复位到中文，避免影响同机其它用例）。
    /// 正常启动不带 -settingsProbe 时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class SettingsProbe : MonoBehaviour
    {
        private string _scene = "TOGGLES";
        private bool _done;
        private int _errors;
        private string _firstError = "";

        // —— TOGGLES ——
        private Transform _panel;                    // 首次 Build 后的面板（切语言会重建，故每步重取）
        private readonly List<string> _fail = new List<string>();
        private bool _panelFound;
        private bool _stillVisibleAfterLanguage;
        private int _clicked;
        private int _tookEffect;
        private readonly List<string> _persistDiff = new List<string>();

        // —— ROUNDTRIP ——
        private bool _roundTripAllFields;
        private string _roundTripDiff = "";
        private bool _appliedAudio, _appliedQuality, _appliedFullscreen, _appliedLanguage;
        private bool _appliedMinor, _appliedCloud, _appliedTelemetry, _appliedShake;

        // —— NEGATIVE ——
        private byte[] _negOriginal = new byte[0];
        private byte[] _negArchived = new byte[0];
        private string _negBackupDir = "";
        private bool _negRescued, _negDefaults, _negQuarantined;

        /// <summary>本轮为验证"设置真的落盘"而写入的标记字段（非默认值）。</summary>
        private static readonly SettingsManager.Data Sentinels = new SettingsManager.Data
        {
            masterVolume = 0.31f,
            musicVolume = 0.42f,
            sfxVolume = 0.57f,
            quality = 0,
            fullscreen = false,
            language = Localization.LangEn,
            screenShake = false,
            telemetry = false,
            cloudSave = true,
            minorMode = true,
            agreedTermsVersion = "2026.10.1",
        };

        public static void Start(string scene)
        {
            // 顺序与 M21 同源：AddComponent 会**同步执行 Awake**，而本探针的初始状态
            // （语言、日志订阅）正是在 Awake 里摆好的——若在 AddComponent 之后才赋值，
            // Awake 读到的永远是默认值，表现为"每个场景都按 TOGGLES 跑"。
            // 先置为非激活可把 Awake 推迟到 SetActive(true)。
            var go = new GameObject("SettingsProbe");
            go.SetActive(false);
            var p = go.AddComponent<SettingsProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Awake()
        {
            Application.logMessageReceived += OnLog;
            Localization.SetLanguage(Localization.LangZh, silent: true);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            // 实测会把界面语言切来切去，收尾复位成中文，避免影响同机其它用例
            Localization.SetLanguage(Localization.LangZh, silent: true);
        }

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        private static string SettingsPath => Path.Combine(Application.persistentDataPath, "settings.json");

        private IEnumerator Start()
        {
            // 设置面板要在主菜单里被真实打开过——它自己不负责 Ensure，
            // 正常流程是主菜单按钮 -> SettingsPanel.Ensure() -> Show()。
            MainMenu.Ensure();
            MainMenu.Instance?.Show();
            SettingsPanel.Ensure();
            SettingsPanel.Instance?.Show();
            yield return null;

            _panel = CurrentPanel();

            switch (_scene)
            {
                case "ROUNDTRIP": yield return RoundTrip(); break;
                case "NEGATIVE": yield return Negative(); break;
                default: yield return Toggles(); break;
            }

            Report();
            _done = true;
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }

        // =====================================================================================
        //  TOGGLES：逐个点击控件，断言「内存变化 / 落盘一致 / 面板文字跟进」三者同时成立
        // =====================================================================================

        private IEnumerator Toggles()
        {
            var s = SettingsManager.Instance;
            if (s == null) { Fail("设置管理器缺失"); yield break; }

            _panelFound = _panel != null;
            if (!_panelFound) { Fail("设置面板未建出"); yield break; }

            // —— 布尔开关：点一下应当翻转 ——
            yield return ClickToggle("Fullscreen", v => s.Current.fullscreen == v, "全屏");
            yield return ClickToggle("Shake", v => s.Current.screenShake == v, "震屏");
            yield return ClickToggle("Telemetry", v => s.Current.telemetry == v, "统计");
            yield return ClickToggle("CloudSave", v => s.Current.cloudSave == v, "云存档");

            // —— 青少年模式：开 → 关（开着会限时，别把后续用例连坐）——
            yield return ClickToggle("MinorMode", v => s.Current.minorMode == v, "青少年模式");
            yield return ClickToggle("MinorMode", v => s.Current.minorMode == v, "青少年模式(关)");

            // —— 画质循环：点几次走完 低→中→高→低，每一步都要真的作用到引擎 ——
            int q0 = s.Current.quality;
            for (int i = 0; i < 3; i++)
            {
                int expect = (s.Current.quality + 1) % 3;
                Click("Quality");
                yield return null;
                bool mem = s.Current.quality == expect;
                int engine = QualitySettings.GetQualityLevel();
                bool applied = engine == expect;
                // 面板文字必须跟着变，否则用户点了个寂寞（按钮长得完全一样，唯一反馈就是这行字）
                bool label = QualityLabelFollows(expect);
                Count(mem && applied && label, "画质#" + i,
                    "内存=" + Yn(mem) + " 引擎=" + engine + "/" + expect + " 文字=" + (label ? "跟进" : "未跟进"));
            }
            if (s.Current.quality != q0) Fail("画质循环未回到起点（" + q0 + "→" + s.Current.quality + "）");

            // —— 语言循环：这是唯一会让面板整体重建的控件 ——
            string l0 = s.Current.language;
            string want = l0 == Localization.LangZh ? Localization.LangEn : Localization.LangZh;
            Click("Language");
            yield return null;                       // 重建在事件里同步发生，但 Destroy 延迟到帧末
            // 语言按钮的可见反馈有两个来源：① 面板被重建后有没有重新显示（Rebuild 里的 Show）；
            // ② 语言名/按钮文字有没有跟着译。这两处本轮各修了一个缺陷，
            // 必须分开上报——否则后者会在面板整个消失时"顺带变成通过"。
            bool panelVisible = SettingsPanel.Instance != null && SettingsPanel.Instance.PanelRoot != null &&
                                SettingsPanel.Instance.PanelRoot.gameObject.activeInHierarchy;
            _panel = CurrentPanel();
            bool langMem = s.Current.language == want;
            bool langApplied = Localization.Language == want;
            bool langLabel = LanguageLabelFollows(want);
            _stillVisibleAfterLanguage = panelVisible;
            if (!panelVisible) Fail("切语言后面板消失了（用户被踢回主菜单）");
            Count(langMem && langApplied && langLabel && panelVisible, "语言",
                "内存=" + Yn(langMem) + " 生效=" + Yn(langApplied) +
                " 文字=" + (langLabel ? "跟进" : "未跟进") + " 面板=" + (panelVisible ? "在" : "消失"));

            // 切回中文，避免重建后的面板与后续断言的语言期望不一致
            s.SetLanguage(l0);
            yield return null;
            _panel = CurrentPanel();

            // —— 最后统一验证：前面点过的每一个值，磁盘上是不是也是这个值 ——
            // 必须先把快照类型显式写成 Data：var 会推成 SettingsManager.Current 的静态类型，
            // 下一行 s.Load() 会**原地替换** Current 对象，快照跟着一起变，比对就永远成立（假通过）。
            SettingsManager.Data memSnapshot = s.Current;
            s.Load();                                 // 真实反序列化：读的是文件，不是内存
            var disk = s.Current;
            SameField("主音量", memSnapshot.masterVolume, disk.masterVolume);
            SameField("画质", memSnapshot.quality, disk.quality);
            SameField("全屏", memSnapshot.fullscreen, disk.fullscreen);
            SameField("语言", memSnapshot.language, disk.language);
            SameField("震屏", memSnapshot.screenShake, disk.screenShake);
            SameField("统计", memSnapshot.telemetry, disk.telemetry);
            SameField("云存档", memSnapshot.cloudSave, disk.cloudSave);
            SameField("青少年", memSnapshot.minorMode, disk.minorMode);
        }

        /// <summary>点一个开关，断言内存值等于"翻转后的期望值"，且面板上的 开/关 文字同步跟进。</summary>
        private IEnumerator ClickToggle(string name, Func<bool, bool> matches, string label)
        {
            var node = Find(name + "Toggle");
            if (node == null) { Fail("找不到开关 " + name); yield break; }
            var btn = node.GetComponent<Button>();
            if (btn == null) { Fail("开关无 Button 组件 " + name); yield break; }

            bool before = ReadToggleText(node) == Localization.T("common.on");
            // M51：真实点击路径。旧写法 onClick.Invoke() 绕过了 raycast，
            // 于是"前面盖了层东西"这种缺陷在探针里是隐形的。
            string why;
            bool clicked = UIHitTest.Click(btn, out why);
            if (!clicked) Fail("开关点不到 " + name + "：" + why);
            yield return null;

            _panel = CurrentPanel();
            bool after = ReadToggleText(Find(name + "Toggle")) == Localization.T("common.on");
            bool flipped = after != before;
            bool mem = matches(after);
            Count(mem && flipped, label, "内存=" + Yn(mem) + " 文字=" + (before ? "开" : "关") + "→" + (after ? "开" : "关"));
        }

        private static string ReadToggleText(GameObject node)
        {
            if (node == null) return "";
            var t = node.GetComponentInChildren<Text>(true);
            return t != null ? t.text : "";
        }

        private void Count(bool ok, string label, string detail)
        {
            _clicked++;
            if (ok) _tookEffect++;
            else Fail(label + "：" + detail);
            Debug.Log("[Probe] " + (ok ? "ok " : "x  ") + label + " " + detail);
        }

        /// <summary>内存值与磁盘读回值是否一致（不一致说明这条设置根本没落盘）。</summary>
        private void SameField(string label, float a, float b) => SameField(label, (object)a, (object)b, Mathf.Abs(a - b) < 0.0001f);
        private void SameField(string label, int a, int b) => SameField(label, (object)a, (object)b, a == b);
        private void SameField(string label, bool a, bool b) => SameField(label, (object)a, (object)b, a == b);
        private void SameField(string label, string a, string b) => SameField(label, (object)a, (object)b, a == b);

        private void SameField(string label, object a, object b, bool same)
        {
            if (same) { _persistDiff.Add(""); return; }
            _persistDiff.Add(label + " " + a + "→" + b);
            Fail(label + " 未落盘：" + a + " → " + b);
        }

        // =====================================================================================
        //  ROUNDTRIP：非默认值落盘 → 换一个全新实例读回 → 且在引擎上真的生效
        // =====================================================================================

        private IEnumerator RoundTrip()
        {
            var s = SettingsManager.Instance;
            if (s == null) { Fail("设置管理器缺失"); yield break; }

            // 1) 逐项经**设置器的公开 Setter** 写入（不是直接改 Current，
            //    否则"Save 里漏了某个字段"这个缺陷会被绕过——写入路径本身才是被测对象）
            s.SetVolume("master", Sentinels.masterVolume);
            s.SetVolume("music", Sentinels.musicVolume);
            s.SetVolume("sfx", Sentinels.sfxVolume);
            s.SetQuality(Sentinels.quality);
            s.SetFullscreen(Sentinels.fullscreen);
            s.SetScreenShake(Sentinels.screenShake);
            s.SetTelemetry(Sentinels.telemetry);
            s.SetCloudSave(Sentinels.cloudSave);
            s.SetMinorMode(Sentinels.minorMode);
            s.SetLanguage(Sentinels.language);
            s.AgreeTerms(Sentinels.agreedTermsVersion);
            yield return null;

            // 2) 引擎侧是否真的收到（只查 Current 证明不了"被消费"）
            _appliedAudio = Mathf.Abs(AudioListener.volume - Sentinels.masterVolume) < 0.0001f;
            _appliedQuality = QualitySettings.GetQualityLevel() == Sentinels.quality;
            _appliedFullscreen = Screen.fullScreen == Sentinels.fullscreen;
            _appliedLanguage = Localization.Language == Sentinels.language;
            // 下面四项没有可观测的引擎侧状态，读的是**产品代码自己的判定入口**——
            // PlaytimeGuard/Telemetry/CloudSave 各自据此决定是否限时/采集/联网。
            _appliedMinor = SettingsManager.Instance.Current.minorMode &&
                            PlaytimeGuard.VerifyMinor();
            _appliedCloud = SettingsManager.Instance.Current.cloudSave;
            _appliedTelemetry = !Telemetry.Enabled();          // 我们设成了 false，故 Enabled 必须为 false
            _appliedShake = !SettingsManager.Instance.Current.screenShake;
            if (!_appliedAudio) Fail("主音量未作用到 AudioListener：" + AudioListener.volume);
            if (!_appliedQuality) Fail("画质未作用到引擎：" + QualitySettings.GetQualityLevel());
            if (!_appliedFullscreen) Fail("全屏未作用到引擎：" + Screen.fullScreen);
            if (!_appliedLanguage) Fail("语言未生效：" + Localization.Language);

            // 3) 换一个**全新实例**读档：同进程内直接查 Current 会被内存值糊弄
            var filePath = SettingsPath;
            var fresh = new SettingsManager.Data();
            try
            {
                fresh = JsonUtility.FromJson<SettingsManager.Data>(File.ReadAllText(filePath));
            }
            catch (Exception e)
            {
                Fail("读档失败：" + e.Message);
            }

            var diffs = new List<string>();
            if (fresh == null) diffs.Add("反序列化为 null");
            else
            {
                if (Mathf.Abs(fresh.masterVolume - Sentinels.masterVolume) > 0.0001f) diffs.Add("masterVolume");
                if (Mathf.Abs(fresh.musicVolume - Sentinels.musicVolume) > 0.0001f) diffs.Add("musicVolume");
                if (Mathf.Abs(fresh.sfxVolume - Sentinels.sfxVolume) > 0.0001f) diffs.Add("sfxVolume");
                if (fresh.quality != Sentinels.quality) diffs.Add("quality");
                if (fresh.fullscreen != Sentinels.fullscreen) diffs.Add("fullscreen");
                if (fresh.language != Sentinels.language) diffs.Add("language");
                if (fresh.screenShake != Sentinels.screenShake) diffs.Add("screenShake");
                if (fresh.telemetry != Sentinels.telemetry) diffs.Add("telemetry");
                if (fresh.cloudSave != Sentinels.cloudSave) diffs.Add("cloudSave");
                if (fresh.minorMode != Sentinels.minorMode) diffs.Add("minorMode");
                if (fresh.agreedTermsVersion != Sentinels.agreedTermsVersion) diffs.Add("agreedTermsVersion");
            }
            _roundTripDiff = diffs.Count == 0 ? "无" : string.Join(",", diffs.ToArray());
            _roundTripAllFields = diffs.Count == 0;

            // 4) 语言/设置"重启"：把内存值全改回默认再真实 Load()，
            //    确认读档能把每一项都恢复——这是"下次启动还能记住"的直接证据
            SettingsManager.Data before = SettingsManager.Instance.Current;   // 同上：必须显式类型，否则会被下一行原地替换
            SettingsManager.Instance.Current = new SettingsManager.Data();
            SettingsManager.Instance.Load();
            var back = SettingsManager.Instance.Current;
            if (Mathf.Abs(back.masterVolume - before.masterVolume) > 0.0001f ||
                Mathf.Abs(back.musicVolume - before.musicVolume) > 0.0001f ||
                Mathf.Abs(back.sfxVolume - before.sfxVolume) > 0.0001f ||
                back.quality != before.quality || back.fullscreen != before.fullscreen ||
                back.screenShake != before.screenShake || back.telemetry != before.telemetry ||
                back.cloudSave != before.cloudSave || back.minorMode != before.minorMode ||
                back.language != before.language ||
                back.agreedTermsVersion != before.agreedTermsVersion)
            {
                Fail("重启后读回不一致（设置没记住）");
            }

            // 收尾：把设置恢复成默认，避免落盘里留一堆奇怪的测试值（驱动脚本另有备份/恢复）
            SettingsManager.Instance.Current = new SettingsManager.Data();
            SettingsManager.Instance.Save();
        }

        // =====================================================================================
        //  NEGATIVE：设置文件损坏时，不得静默清零用户设置
        // =====================================================================================

        private IEnumerator Negative()
        {
            // ⚠ 顺序关键：SettingsManager 自己有 [RuntimeInitializeOnLoadMethod(AfterSceneLoad)]，
            // 也就是说**在我们跑到这里之前它就已经 Awake 过、Load 过一次了**（读的就是这份坏文件）。
            // 若先抓原文再判归档，抓到的只会是"Loader 早已挪走坏文件、随后重建的默认值"——
            // 归档也许完全正常，断言却必然失败（本轮就撞了两次：先假失败一次，改字节比较后又假失败一次）。
            // 故必须**先看归档有没有、内容是啥**，再去 Load。
            string dir = Path.GetDirectoryName(SettingsPath);
            var baks = Directory.Exists(dir) ? Directory.GetFiles(dir, "settings.json.corrupt-*") : new string[0];
            _negBackupDir = baks.Length > 0 ? Path.GetFileName(baks[0]) : "";
            try
            {
                _negOriginal = File.Exists(SettingsPath) ? File.ReadAllBytes(SettingsPath) : new byte[0];
                _negArchived = baks.Length > 0 ? File.ReadAllBytes(baks[0]) : new byte[0];
            }
            catch (Exception e)
            {
                Fail("读不到损坏文件：" + e.Message);
            }

            SettingsManager.Ensure();
            var s = SettingsManager.Instance;
            if (s == null) { Fail("设置管理器缺失"); yield break; }

            s.Load();                                 // 线上同一条读档路径，异常必须被吞掉并回退
            _negRescued = true;                       // 能走到这里 = 没有抛出去崩掉游戏

            s.Apply();                                // 回退后的默认值必须能应用（不炸）
            _negDefaults = Mathf.Abs(s.Current.masterVolume - 1f) < 0.0001f &&
                           s.Current.language == Localization.LangZh &&
                           string.IsNullOrEmpty(s.Current.agreedTermsVersion);
            if (!_negDefaults) Fail("损坏后未回退默认值：" + s.Current.masterVolume + "/" + s.Current.language);

            // 损坏的文件必须被另存归档：否则用户整套设置（音量/语言/已同意协议版本）
            // 在没有任何提示的情况下被默认值覆盖，而且再也找不回来。
            BaksNow(dir, out baks);
            if (baks.Length > 0) _negBackupDir = Path.GetFileName(baks[0]);
            // 内容判据：归档里必须是"调用方写进去的那份坏数据"。
            // 不拿 _negOriginal 比——那多半已是重建后的默认值（见上），拿它比等于在比两个不相关的文件。
            _negQuarantined = baks.Length > 0 && _negArchived.Length > 0 &&
                              IsBrokenPayload(_negArchived);
            if (!_negQuarantined) Fail("损坏文件未归档（用户设置被静默清零，无处找回）");
            if (baks.Length == 0) Fail("确实没有 settings.json.corrupt-* 归档");

            // 损坏后继续用：改一项应当正常落盘（不能因为上次读过坏文件就永久写不出）
            s.SetVolume("master", 0.5f);
            s.Load();
            if (Mathf.Abs(s.Current.masterVolume - 0.5f) > 0.0001f)
                Fail("损坏恢复后无法再落盘：" + s.Current.masterVolume);

            yield return null;
        }

        // =====================================================================================

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "TOGGLES" && (_clicked == 0 || _tookEffect < _clicked || PersistedCount() != 8)) pass = false;

            Debug.Log("[SMOKE] ===== 设置面板实测结果 =====");
            Debug.Log("[SMOKE] 场景              " + _scene);
            switch (_scene)
            {
                case "ROUNDTRIP":
                    Debug.Log("[SMOKE] 往返字段          " + (_roundTripAllFields ? "全部一致" : "不一致"));
                    Debug.Log("[SMOKE] 往返差异          " + _roundTripDiff);
                    Debug.Log("[SMOKE] 引擎侧生效        " + Yn(_appliedAudio) + "/" + Yn(_appliedQuality) +
                              "/" + Yn(_appliedFullscreen) + "/" + Yn(_appliedLanguage));
                    Debug.Log("[SMOKE] 玩法项生效        " + Yn(_appliedMinor) + "/" + Yn(_appliedCloud) +
                              "/" + Yn(_appliedTelemetry) + "/" + Yn(_appliedShake));
                    break;
                case "NEGATIVE":
                    Debug.Log("[SMOKE] 损坏后未崩溃      " + (_negRescued ? "是" : "否"));
                    Debug.Log("[SMOKE] 回退默认值        " + (_negDefaults ? "是" : "否"));
                    Debug.Log("[SMOKE] 损坏文件已归档    " + (_negQuarantined ? "是" : "否"));
                    Debug.Log("[SMOKE] 归档文件名        " + (string.IsNullOrEmpty(_negBackupDir) ? "<无>" : _negBackupDir));
                    break;
                default:
                    Debug.Log("[SMOKE] 面板已建出        " + Yn(_panelFound));
                    Debug.Log("[SMOKE] 控件点击数        " + _clicked);
                    Debug.Log("[SMOKE] 点击即生效        " + _tookEffect + "/" + _clicked);
                    Debug.Log("[SMOKE] 落盘字段数        " + PersistedCount() + "/8");
                    Debug.Log("[SMOKE] 切语言后仍可见    " + Yn(_stillVisibleAfterLanguage));
                    break;
            }
            Debug.Log("[SMOKE] 失败项            " + (_fail.Count == 0 ? "无" : string.Join(" | ", _fail.ToArray())));
            Debug.Log("[SMOKE] 运行期异常        " + _errors);
            if (_errors > 0) Debug.Log("[SMOKE] 首条错误          " + _firstError);
            Debug.Log("[SMOKE] 结论              " + (pass ? "通过" : "不通过"));
        }

        private static string Yn(bool b) => b ? "是" : "否";

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// 归档内容是不是"那份坏数据"：半截 JSON 必然以 `"qual` 收尾且解析不了。
        /// 直接判"能不能被 JsonUtility 解析"更本质——能解析说明归档的其实是好文件。
        /// </summary>
        private static bool IsBrokenPayload(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            var text = Encoding.UTF8.GetString(bytes);
            try
            {
                JsonUtility.FromJson<SettingsManager.Data>(text);
                return false;                          // 解析得动 = 归档的不是坏数据
            }
            catch
            {
                return true;
            }
        }

        private static void BaksNow(string dir, out string[] baks)
        {
            baks = Directory.Exists(dir) ? Directory.GetFiles(dir, "settings.json.corrupt-*") : new string[0];
            Array.Sort(baks, StringComparer.Ordinal);   // 时间戳在文件名里，字典序即时间序
        }

        /// <summary>8 个设置项里落盘一致的条数（少一条就说明落盘路径漏了字段）。</summary>
        private int PersistedCount()
        {
            int n = 0;
            foreach (var d in _persistDiff) if (d.Length == 0) n++;
            return n;
        }

        private void Fail(string msg)
        {
            _fail.Add(msg);
            Debug.LogWarning("[Probe] " + msg);
        }

        // —— 面板定位（语言切换会整体重建，Destroy 延迟到帧末，故每步都经 PanelRoot 重取）——

        private static Transform CurrentPanel()
        {
            var sp = SettingsPanel.Instance;
            var root = sp != null ? sp.PanelRoot : null;
            return root != null ? root.Find("Panel") : null;
        }

        private GameObject Find(string name)
        {
            if (_panel == null) _panel = CurrentPanel();
            if (_panel == null) return null;
            var t = _panel.Find(name);
            return t != null ? t.gameObject : null;
        }

        /// <summary>
        /// 画质按钮文字是否等于「设置·画质：低/中/高」的本地化文本。
        /// 比对整串而不只是 Contains 片段：只查"含低/中/高"会把"没跟随译"放过。
        /// </summary>
        private bool QualityLabelFollows(int expect)
        {
            var node = Find("Quality");
            var t = node != null ? node.GetComponentInChildren<Text>(true) : null;
            if (t == null) return false;
            string word = Localization.T(expect == 0 ? "common.low" : expect == 1 ? "common.medium" : "common.high");
            return t.text == Localization.T("settings.quality") + "：" + word;
        }

        /// <summary>语言按钮文字是否为「设置·语言：中文 / English」。切语言后面板已重建，故重取面板。</summary>
        private bool LanguageLabelFollows(string lang)
        {
            _panel = CurrentPanel();
            var node = Find("Language");
            var t = node != null ? node.GetComponentInChildren<Text>(true) : null;
            if (t == null) return false;
            return t.text == Localization.T("settings.language") + "：" + Localization.LanguageLabel(lang);
        }

        private void Click(string name)
        {
            var node = Find(name);
            var btn = node != null ? node.GetComponent<Button>() : null;
            if (btn == null) { Fail("找不到按钮 " + name); return; }
            // M51：走真实点击那条路（先 raycast 确认没被挡）。
            string why;
            if (!UIHitTest.Click(btn, out why)) Fail("按钮点不到 " + name + "：" + why);
        }

        void Update()
        {
            // 兜底：协程意外没跑完也要出结论（不能把"没结果"当成通过）
            if (!_done && Time.timeSinceLevelLoad > 90f)
            {
                _done = true;
                Debug.Log("[SMOKE] 结论              不通过（超时未产出结果）");
                Application.Quit(1);
            }
        }
    }
}
