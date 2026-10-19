using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M21 合规门（<see cref="Compliance.ComplianceGate"/>）与同意链路无头实测。
    ///
    /// 为什么必须测：这是继 M19 防沉迷之后**最后一个零实测的合规模块**，而合规功能的失效
    /// 是静默的——门没弹出来，游戏一切正常，只是"用户其实从没同意过协议"。
    /// 其中「协议版本升级后必须重新征得同意」是最关键的一条：文本换了而用户没重新确认，
    /// 等于拿旧同意去覆盖新条款，这正是监管最关心的点。
    ///
    /// 六个场景（每个独立进程，因为同意态是进程级单例 + 落盘状态）：
    ///   FIRST         首启：门必须弹出 → 点同意 → 同意态**落盘**（重新 Load 校验）→ 主菜单解锁
    ///   AGREED        已同意当前版本：门不该重复弹
    ///   BUMP          已同意**旧**版本：门必须重新弹出（协议更新后重新征得同意）
    ///   DECLINE       点不同意：必须走到退出，且同意态不得写入
    ///   CONSENT_CLOUD 同意前云存档零请求；同意后必须补一次拉取（否则本地旧档覆盖云端）
    ///   L10N          英文界面下合规门文案既无裸键、也无中文残留
    ///
    /// 判据的区分力：
    /// 1) 同意态是否落盘，由探针调真实 <c>SettingsManager.Load()</c> 读回比对，
    ///    而不是看内存里的字段——只查内存的话，一个根本不保存的实现也能全绿。
    /// 2) "门弹了没有"用 <c>ComplianceGate.Shown</c> 计数，与"点了没有"（AgreeCount）分开：
    ///    两者都断言，才能区分「门压根没弹」和「门弹了但按钮点不动」。
    /// 3) CONSENT_CLOUD 用**拉取次数**而非笼统的"联网了没"来判定：只断言请求数 >0 的话，
    ///    一次都不拉、直接上传覆盖云端的实现照样能过——那正是本轮要抓的缺陷。
    /// </summary>
    public class ComplianceProbe : MonoBehaviour
    {
        private string _scene = "FIRST";
        private string _lang = Core.Localization.LangZh;
        private bool _done;
        private int _errors;

        // 采集结果
        private bool _gateShown;
        private bool _clickedAgree;
        private bool _clickedDecline;
        private bool _menuVisibleBefore;
        private bool _menuVisibleAfter;
        private bool _persistedOk;
        private string _persistedVersion = "";
        private int _legalMissing;
        private int _reqBefore;
        private int _reqAfter;
        private int _pullAfter;
        private bool _quitInvoked;
        private string _firstRawKey = "";
        private string _firstCjk = "";
        private int _textCount;

        /// <summary>启动一次合规门实测。</summary>
        public static void Start(string scene, string lang)
        {
            // 顺序很关键：AddComponent 会**同步执行 Awake**，而本探针的初始状态
            // （法律文本版本 / 已同意版本 / 云存档开关）正是在 Awake 里摆好的——
            // 若在 AddComponent 之后才给字段赋值，Awake 读到的就是默认值，
            // 表现为"每个场景都按 FIRST 跑"（门永远弹、语言永远是中文）。
            // 先置为非激活可把 Awake 推迟到 SetActive(true)。
            var go = new GameObject("ComplianceProbe");
            go.SetActive(false);
            var p = go.AddComponent<ComplianceProbe>();
            p._scene = scene;
            p._lang = lang;
            go.SetActive(true);
        }

        /// <summary>
        /// 状态必须在 <b>Awake</b> 里摆好，不能放进 <see cref="Start"/>：
        /// <c>CloudSaveService.Start()</c> 会在那里判定 Enabled 并决定是否发起首次拉取，
        /// 而所有 Start 都排在任意 Awake 之后——若等到本探针的 Start 再改同意态，
        /// 云存档早已按"已同意"跑过一次拉取，CONSENT_CLOUD 场景就失去区分力
        /// （请求数从一开始就不是 0，那个缺陷会被自己的测试糊弄过去）。
        /// </summary>
        private void Awake()
        {
            Application.logMessageReceived += OnLog;
            Core.Localization.SetLanguage(_lang, silent: true);
            Compliance.ComplianceGate.ResetCountersForTest();
            // 探针不能真退出：进程一结束，剩下的断言就再也打印不出来了
            Compliance.ComplianceGate.QuitAction = () => _quitInvoked = true;
            SetupState();
        }

        private IEnumerator Start()
        {
            // —— 复刻 GameBuilder 的正常启动顺序（顺序本身也是被测对象）——
            UI.MainMenu.Ensure();
            var gateGo = new GameObject("ComplianceGate");
            var gate = gateGo.AddComponent<Compliance.ComplianceGate>();
            Compliance.ComplianceGate.OnPassed = () =>
            {
                if (UI.MainMenu.Instance != null) UI.MainMenu.Instance.Show();
            };

            _menuVisibleBefore = MenuVisible();
            _gateShown = Compliance.ComplianceGate.Shown;

            // 与 GameBuilder 保持一致：已同意时门根本不建，由 GameBuilder 显式 Show 菜单。
            // 探针必须复刻这一步，否则 AGREED 场景会因为"没人去 Show"而假失败。
            if (!_gateShown) UI.MainMenu.Instance?.Show();

            // 门弹出后等一帧，确保面板层级建完再点
            yield return null;

            if (_gateShown)
            {
                CollectTexts(gate);
                CheckLegalTexts();

                if (_scene == "DECLINE")
                {
                    _clickedDecline = gate.ClickDeclineForTest();
                }
                else
                {
                    _reqBefore = CloudRequests();
                    _clickedAgree = gate.ClickAgreeForTest();
                }
            }
            else
            {
                _reqBefore = CloudRequests();
            }

            // 云存档拉取是协程，给足时间（CONSENT_CLOUD 需要它真的走完）
            yield return new WaitForSeconds(_scene == "CONSENT_CLOUD" ? 6f : 1f);

            _reqAfter = CloudRequests();
            var svc = Core.CloudSave.CloudSaveService.Instance;
            _pullAfter = svc != null ? svc.PullCount : 0;

            // 同意态是否真的落盘：重新走一次真实 Load()，与内存值比对
            VerifyPersisted();

            _menuVisibleAfter = MenuVisible();
            Report();
            _done = true;
            Application.logMessageReceived -= OnLog;
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }

        /// <summary>按场景摆好初始状态（协议版本 / 已同意版本 / 云存档开关）。</summary>
        private void SetupState()
        {
            var s = Core.SettingsManager.Instance;
            if (s == null) { Core.SettingsManager.Ensure(); s = Core.SettingsManager.Instance; }
            if (s == null) return;

            switch (_scene)
            {
                case "AGREED":
                    s.Current.agreedTermsVersion = Core.VersionInfo.ComplianceVersion;
                    break;

                case "BUMP":
                    // 用户同意的是**旧**版本，而当前法律文本版本已升级 → 门必须重新弹
                    Core.VersionInfo.ComplianceVersionOverrideForTest = "2027.01.1";
                    s.Current.agreedTermsVersion = "2026.10.1";
                    break;

                case "CONSENT_CLOUD":
                    // 用户此前已开启云存档，但协议更新后尚未重新同意
                    Core.VersionInfo.ComplianceVersionOverrideForTest = "2027.01.1";
                    s.Current.agreedTermsVersion = "2026.10.1";
                    s.Current.cloudSave = true;
                    break;

                default:
                    s.Current.agreedTermsVersion = "";
                    break;
            }
        }

        private void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception)
            {
                var lower = condition != null ? condition.ToLower() : "";
                // 无头环境必然出现的渲染噪声（与既有探针同一套白名单）
                if (lower.Contains("shader") || lower.Contains("gpu") ||
                    lower.Contains("screen position out of view frustum") ||
                    lower.Contains("curl error") || lower.Contains("memorysetup"))
                    return;
                _errors++;
                if (_errors == 1) Debug.Log("[Probe] 首条错误: " + condition);
            }
        }

        private int CloudRequests()
        {
            var svc = Core.CloudSave.CloudSaveService.Instance;
            return svc != null ? svc.RequestCount : 0;
        }

        private bool MenuVisible()
        {
            var m = UI.MainMenu.Instance;
            return m != null && m.IsVisible;
        }

        /// <summary>重新执行真实 Load()，验证同意态确实写进了文件而非只在内存里。</summary>
        private void VerifyPersisted()
        {
            var s = Core.SettingsManager.Instance;
            if (s == null) { _persistedVersion = "<no settings>"; return; }
            try
            {
                s.Load();                                   // 真实反序列化，不复制判定逻辑
                _persistedVersion = s.Current.agreedTermsVersion ?? "";
                var expected = Core.VersionInfo.ComplianceVersion;
                bool shouldHaveAgreed = _scene != "DECLINE" &&
                                        (_scene == "FIRST" || _scene == "BUMP" ||
                                         _scene == "CONSENT_CLOUD" || _scene == "AGREED");
                _persistedOk = shouldHaveAgreed ? _persistedVersion == expected : _persistedVersion != expected;
            }
            catch (System.Exception e)
            {
                _persistedVersion = "<load failed: " + e.Message + ">";
            }
        }

        /// <summary>三份法律文本都必须真实存在（缺失时 LegalTexts 会返回占位串）。</summary>
        private void CheckLegalTexts()
        {
            _legalMissing = 0;
            foreach (var key in Compliance.LegalTexts.AllKeys())
            {
                var t = Compliance.LegalTexts.Get(key);
                if (string.IsNullOrEmpty(t) || t.Contains("法律文本缺失")) _legalMissing++;
            }
        }

        private void CollectTexts(Compliance.ComplianceGate gate)
        {
            var list = new List<string>();
            var root = gate != null ? gate.PanelRoot : null;
            if (root != null)
            {
                foreach (var t in root.GetComponentsInChildren<Text>(true))
                {
                    if (t != null && !string.IsNullOrEmpty(t.text)) list.Add(t.text);
                }
            }
            _textCount = list.Count;
            _firstRawKey = FirstMatch(list, IsRawKey);
            _firstCjk = FirstMatch(list, HasCjk);
        }

        private static string FirstMatch(List<string> list, System.Func<string, bool> pred)
        {
            foreach (var s in list) if (pred(s)) return s;
            return "";
        }

        /// <summary>裸键判定：Localization 漏翻时 T() 原样返回键名（形如 compliance.notice1）。</summary>
        private static bool IsRawKey(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Contains(" ")) return false;
            if (s[0] < 'a' || s[0] > 'z') return false;
            bool hasDot = false;
            foreach (var c in s)
            {
                if (c == '.') { hasDot = true; continue; }
                if (c == '_' || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) continue;
                return false;
            }
            return hasDot;
        }

        private static bool HasCjk(string s)
        {
            foreach (var c in s)
            {
                if (c >= '\u4e00' && c <= '\u9fff') return true;
                if (c >= '\u3000' && c <= '\u303f') return true;
                if (c >= '\uff00' && c <= '\uffef') return true;
            }
            return false;
        }

        private void Report()
        {
            bool pass;
            var extra = new List<string>();

            switch (_scene)
            {
                case "AGREED":
                    pass = !_gateShown && _menuVisibleAfter;
                    extra.Add("已同意不重复弹门  " + (!_gateShown ? "是" : "否"));
                    break;

                case "BUMP":
                    pass = _gateShown && _clickedAgree && _persistedOk && _menuVisibleAfter;
                    extra.Add("协议升级重新弹门  " + (_gateShown ? "是" : "否"));
                    extra.Add("同意态落盘        " + (_persistedOk ? "是" : "否"));
                    break;

                case "DECLINE":
                    pass = _gateShown && _clickedDecline && _quitInvoked && _persistedOk && !_menuVisibleAfter;
                    extra.Add("走到退出          " + (_quitInvoked ? "是" : "否"));
                    extra.Add("同意态未写入      " + (_persistedOk ? "是" : "否"));
                    extra.Add("主菜单未解锁      " + (!_menuVisibleAfter ? "是" : "否"));
                    break;

                case "CONSENT_CLOUD":
                    pass = _gateShown && _reqBefore == 0 && _pullAfter >= 1 && _persistedOk;
                    extra.Add("同意前请求数      " + _reqBefore);
                    extra.Add("同意后拉取次数    " + _pullAfter);
                    extra.Add("同意后请求数      " + _reqAfter);
                    break;

                case "L10N":
                    pass = _gateShown && string.IsNullOrEmpty(_firstRawKey) && string.IsNullOrEmpty(_firstCjk);
                    extra.Add("文案条数          " + _textCount);
                    extra.Add("裸键              " + (string.IsNullOrEmpty(_firstRawKey) ? "无" : _firstRawKey));
                    extra.Add("中文残留          " + (string.IsNullOrEmpty(_firstCjk) ? "无" : _firstCjk));
                    break;

                default: // FIRST
                    pass = _gateShown && _clickedAgree && _persistedOk && _menuVisibleAfter && _legalMissing == 0;
                    extra.Add("首启弹门          " + (_gateShown ? "是" : "否"));
                    extra.Add("法律文本齐全      " + (_legalMissing == 0 ? "是" : "否（缺 " + _legalMissing + " 份）"));
                    extra.Add("同意态落盘        " + (_persistedOk ? "是" : "否"));
                    break;
            }

            if (_errors != 0) pass = false;

            Debug.Log("[SMOKE] ===== 合规门实测结果 =====");
            Debug.Log("[SMOKE] 场景              " + _scene);
            Debug.Log("[SMOKE] 门已弹出          " + (_gateShown ? "是" : "否"));
            Debug.Log("[SMOKE] 点同意            " + (_clickedAgree ? "是" : "否"));
            Debug.Log("[SMOKE] 点不同意          " + (_clickedDecline ? "是" : "否"));
            Debug.Log("[SMOKE] 同意前菜单可见    " + (_menuVisibleBefore ? "是" : "否"));
            Debug.Log("[SMOKE] 同意后菜单可见    " + (_menuVisibleAfter ? "是" : "否"));
            Debug.Log("[SMOKE] 落盘版本          " + _persistedVersion);
            foreach (var e in extra) Debug.Log("[SMOKE] " + e);
            Debug.Log("[SMOKE] 运行期异常        " + _errors);
            Debug.Log("[SMOKE] 结论              " + (pass ? "通过" : "不通过"));
        }

        void Update()
        {
            // 兜底：协程意外没跑完也要出结论（与既有探针一致，避免"没结果"被当成通过）
            if (!_done && Time.timeSinceLevelLoad > 60f)
            {
                _done = true;
                Debug.Log("[SMOKE] 结论              不通过（超时未产出结果）");
                Application.Quit(1);
            }
        }
    }
}
