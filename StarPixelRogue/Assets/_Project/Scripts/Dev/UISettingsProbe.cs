using StarPixel.Core;
using StarPixel.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// 无头 UI 探针（M18）：仅当进程带 <c>-uiProbe</c> 时由 <c>GameBuilder</c> 创建。
    ///
    /// 覆盖三块在普通无头冒烟里**根本不会被创建**的界面——不建出来就等于零验证：
    ///   ① 设置面板（云存档开关 / 状态文案）；
    ///   ② 联机大厅（M4 建的界面，此前整屏硬编码中文）；
    ///   ③ 升级选卡（核心玩法 UI，武器名与升级文案此前同样硬编码）。
    ///
    /// 判据设计（本轮的关键在 ② ③）：
    /// - 漏翻时 <c>Localization.T</c> 会原样返回键名，界面上就冒出 <c>net.lobby_title</c> 这种裸键；
    ///   故所有收集到的文案都要断言"不是裸键"。
    /// - 更强的判据是：**英文界面下不允许出现任何中日韩字符**。
    ///   "中英两条不同"只能证明本地化生效，无法证明"没有中文漏出来"——
    ///   一条硬编码中文 + 一条英文同样能满足"两者不同"。不含 CJK 才是缺陷的直接反向指标。
    /// - 升级卡不直接调 <c>BuildOffers</c>（它要真实的 PlayerController），而是逐项取它
    ///   拼接文案所用的全部素材：10 个武器名 / 类型描述 / 各级升级描述 / 7 个属性强化，
    ///   再另建一次真实面板验证渲染路径确实把它们画了出来。
    ///
    /// 正常启动不带该参数时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class UISettingsProbe : MonoBehaviour
    {
        private int _errors;
        private string _firstError = "";
        private bool _done;
        private float _t;

        private bool _stage1, _stage2, _stage3, _stage4, _stage5;
        private bool _toggleFound, _statusFound, _syncFound;
        private bool _switchTookEffect;
        private bool _switchClicked;   // M51：开关真的被"点"到了（没被任何东西挡住）
        private string _zhStatus = "";
        private string _enStatus = "";

        // ② 联机大厅
        private List<string> _lobbyZh = new List<string>();
        private List<string> _lobbyEn = new List<string>();

        // ③ 升级卡：面板上真实渲染出的文案 + 供选文案的全部素材
        private List<string> _cardsEn = new List<string>();
        private List<string> _piecesZh = new List<string>();
        private List<string> _piecesEn = new List<string>();

        public static void Start()
        {
            var go = new GameObject("UISettingsProbe");
            var p = go.AddComponent<UISettingsProbe>();
            Application.logMessageReceived += p.OnLog;
            // 云存档服务必须先就位：面板的状态文本读的就是它，
            // 否则阶段 1 读到的只是"未启用"这个初始值，而非服务真实的门禁判定结果。
            Core.CloudSave.CloudSaveService.Ensure();
            // 实测环境不污染真实设置：先把云存档开关复位到默认关（不落盘），
            // 探针结束时会再复位一次。
            var s = SettingsManager.Instance;
            if (s != null) s.Current.cloudSave = false;
        }

        /// <summary>
        /// 无头环境必然出现的渲染噪声。NullGfxDevice 下 Canvas 会每帧报同一条错误，
        /// 几秒就能刷出上百条——既不是缺陷，也会把真正的错误淹没，故单列白名单。
        /// </summary>
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
                if (low.Contains(n)) return;      // 噪声不计数
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            // 还原语言与云存档开关，避免实测改动留在用户的 settings.json 里
            Localization.SetLanguage(Localization.LangZh, silent: true);
            var s = SettingsManager.Instance;
            if (s != null) s.SetCloudSave(false);
        }

        void Update()
        {
            if (_done) return;
            _t += Time.unscaledDeltaTime;

            if (!_stage1 && _t >= 0.5f) { _stage1 = true; Stage1(); }
            if (!_stage2 && _t >= 1.5f) { _stage2 = true; Stage2(); }
            if (!_stage3 && _t >= 2.5f) { _stage3 = true; Stage3(); }
            if (!_stage4 && _t >= 3.5f) { _stage4 = true; Stage4(); }
            if (!_stage5 && _t >= 4.5f) { _stage5 = true; Stage5(); }
            if (_t >= 6.0f) Finish();
        }

        /// <summary>阶段 1：建出设置面板并确认云存档三件套都在。</summary>
        private void Stage1()
        {
            SettingsPanel.Ensure();
            SettingsPanel.Instance?.Show();

            var panel = FindPanel();
            _toggleFound = panel != null && panel.Find("CloudSaveToggle") != null;
            _statusFound = panel != null && panel.Find("CloudStatus") != null;
            _syncFound = panel != null && panel.Find("CloudSyncNow") != null;

            _piecesZh.AddRange(UpgradePieces());     // 中文下先取一份升级文案素材
        }

        /// <summary>阶段 2：真的点一下开关，确认它写进了设置（而不是只画了个样子）。</summary>
        private void Stage2()
        {
            var panel = FindPanel();
            var toggle = panel != null ? panel.Find("CloudSaveToggle") : null;
            var btn = toggle != null ? toggle.GetComponent<Button>() : null;
            if (btn == null) { _switchClicked = false; }
            else
            {
                // M51：真实点击路径。旧写法 `if (btn != null) btn.onClick.Invoke()` 有两层躲避：
                // ① 绕过 raycast（盖住也照点）；② 按钮不在时**静默跳过**，连失败都不报。
                string why;
                _switchClicked = UIHitTest.Click(btn, out why);
                // 点不到就**当错误报**（而不是悄悄跳过）：本类没有 Fail()，
                // 而 Debug.LogError 正好会进 _errors / _firstError——
                // 红的时候日志里带得出"是被什么挡住的"，不用再去猜。
                if (!_switchClicked) Debug.LogError("[SMOKE] 云存档开关点不到：" + why);
            }

            var s = SettingsManager.Instance;
            _switchTookEffect = s != null && s.Current.cloudSave;

            // 后端未配置时，即便开关打开也仍是"未启用"——这正是三重开关该有的行为
            _zhStatus = ReadStatusText();

            _lobbyZh = BuildLobbyAndCollect();       // 中文大厅
        }

        /// <summary>阶段 3：切到英文，确认状态文案跟着变（而不是硬编码中文、也不是裸键名）。</summary>
        private void Stage3()
        {
            Localization.SetLanguage(Localization.LangEn);   // 触发 LanguageChanged → 面板重建并 Hide
            SettingsPanel.Instance?.Show();                  // 重建后默认隐藏，这里重新显示
            _enStatus = ReadStatusText();
            _piecesEn.AddRange(UpgradePieces());
        }

        /// <summary>阶段 4：英文下重建大厅，拿一份英文文案与中文对照。</summary>
        private void Stage4()
        {
            _lobbyEn = BuildLobbyAndCollect();
        }

        /// <summary>阶段 5：英文下建出升级选卡面板，收集真实渲染出来的文案。</summary>
        private void Stage5()
        {
            _cardsEn = BuildUpgradePanelAndCollect();
        }

        // —— 联机大厅 ——

        /// <summary>建出大厅 UI、收集所有可见文案，然后销毁（避免与下一语言的那份重名冲突）。</summary>
        private List<string> BuildLobbyAndCollect()
        {
            var lobby = FindObjectOfType<Net.NetLobby>();
            if (lobby != null) Destroy(lobby.gameObject);
            var go = new GameObject("NetLobbyProbe");
            var fresh = go.AddComponent<Net.NetLobby>();
            var list = CollectTexts(fresh.PanelRoot != null ? fresh.PanelRoot.transform : null);
            Destroy(go);
            return list;
        }

        // —— 升级选卡 ——

        /// <summary>
        /// 建出升级面板并收集文案。不用 <c>BuildOffers</c>（它依赖真实 PlayerController），
        /// 而是直接喂 <c>StatOffer</c> 与一张解锁卡——面板渲染路径与真实对局完全一致。
        /// </summary>
        private List<string> BuildUpgradePanelAndCollect()
        {
            var lum = FindObjectOfType<LevelUpManager>();
            if (lum == null) lum = new GameObject("LevelUpManager").AddComponent<LevelUpManager>();

            var offers = new List<Combat.UpgradeOffer>();
            for (int i = 0; i < Combat.UpgradeCatalog.StatOfferCount; i++)
                offers.Add(Combat.UpgradeCatalog.StatOffer(i, null));
            var def = Combat.UpgradeCatalog.CreateById(2);
            offers.Add(new Combat.UpgradeOffer
            {
                title = Localization.T("upgrade.unlock", def.DisplayName),
                desc = Combat.UpgradeCatalog.WeaponDesc(def),
            });

            lum.PresentExternal(offers, _ => { });

            var canvas = GameObject.Find("LevelUpCanvas");
            return CollectTexts(canvas != null ? canvas.transform : null);
        }

        /// <summary>
        /// 升级卡文案的全部素材——<c>BuildOffers</c> 能拼出的字符串都由这些部件组成，
        /// 逐个断言可覆盖它未来拼出的任意一张卡（不依赖随机抽卡结果）。
        /// </summary>
        private static List<string> UpgradePieces()
        {
            var list = new List<string> { Localization.T("levelup.title") };
            foreach (int id in Combat.UpgradeCatalog.AllWeaponIds)
            {
                var def = Combat.UpgradeCatalog.CreateById(id);
                list.Add(def.DisplayName);
                list.Add(Combat.UpgradeCatalog.WeaponDesc(def));
                for (int lv = 2; lv <= Combat.Skill.SkillDef.MaxLevel; lv++)
                    list.Add(def.NextLevelDesc(lv));
            }
            for (int i = 0; i < Combat.UpgradeCatalog.StatOfferCount; i++)
            {
                var o = Combat.UpgradeCatalog.StatOffer(i, null);
                list.Add(o.title);
                if (!string.IsNullOrEmpty(o.desc)) list.Add(o.desc);
            }
            list.Add(Localization.T("upgrade.unlock", Combat.UpgradeCatalog.CreateById(1).DisplayName));
            list.Add(Localization.T("upgrade.level", Combat.UpgradeCatalog.CreateById(1).DisplayName, 2));
            return list;
        }

        // —— 采集与判据辅助 ——

        private static List<string> CollectTexts(Transform root)
        {
            var list = new List<string>();
            if (root == null) return list;
            var texts = root.GetComponentsInChildren<Text>(true);
            foreach (var t in texts)
            {
                if (t == null) continue;
                var s = t.text ?? "";
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// 裸键判定：Localization 漏翻时 T() 原样返回键名（形如 weapon.name.3）。
        /// 键名特征是"无空格 + 含点 + 全小写"，成品文案不会长这样。
        /// </summary>
        private static bool IsRawKey(string s)
        {
            // 必须以小写字母开头：否则 "127.0.0.1"（大厅地址输入框的默认值）会被误判成键名
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

        /// <summary>是否含中日韩字符或全角标点——英文界面出现它就说明有中文漏出来了。</summary>
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

        private static string FirstMatch(List<string> list, System.Func<string, bool> pred)
        {
            foreach (var s in list)
                if (pred(s)) return s;
            return "";
        }

        private static Transform FindPanel()
        {
            // 必须走 SettingsPanel.PanelRoot 而不是 GameObject.Find("SettingsCanvas")：
            // 语言切换会整体重建面板，而 Destroy 延迟到帧末——Find 很可能命中
            // 尚未销毁的**旧**画布，于是读到的还是重建前（切换前语言）的文案。
            var sp = SettingsPanel.Instance;
            var root = sp != null ? sp.PanelRoot : null;
            return root != null ? root.Find("Panel") : null;
        }

        private static string ReadStatusText()
        {
            var panel = FindPanel();
            var node = panel != null ? panel.Find("CloudStatus") : null;
            var text = node != null ? node.GetComponent<Text>() : null;
            return text != null ? text.text : "";
        }

        private void Finish()
        {
            _done = true;

            // —— 判据 ——
            // 1) 设置面板控件齐全 + 开关真的生效
            bool controls = _toggleFound && _statusFound && _syncFound;
            // 2) 中文文案不是裸键名
            bool zhOk = !string.IsNullOrEmpty(_zhStatus) && !_zhStatus.StartsWith("cloud.status.");
            // 3) 英文文案不是裸键名，且**确实与中文不同**——
            //    若两条相同，说明本地化没生效（回退成了同一份硬编码文案）
            bool enOk = !string.IsNullOrEmpty(_enStatus) && !_enStatus.StartsWith("cloud.status.")
                        && _enStatus != _zhStatus;
            // 4) 联机大厅：两种语言都建得出来（数量 ≥ 6：标题/副标题/提示/地址/4 个按钮）
            bool lobbyOk = _lobbyZh.Count >= 6 && _lobbyEn.Count >= 6;
            // 5) 升级卡：面板渲染出了文案（标题 + 至少 3 张卡）
            bool cardsOk = _cardsEn.Count >= 4 && _piecesZh.Count >= 30 && _piecesEn.Count >= 30;
            // 6) 任何一处都不许出现裸键（漏翻的直接证据）
            string rawKey = FirstMatch(_lobbyZh, IsRawKey);
            if (string.IsNullOrEmpty(rawKey)) rawKey = FirstMatch(_lobbyEn, IsRawKey);
            if (string.IsNullOrEmpty(rawKey)) rawKey = FirstMatch(_cardsEn, IsRawKey);
            if (string.IsNullOrEmpty(rawKey)) rawKey = FirstMatch(_piecesZh, IsRawKey);
            if (string.IsNullOrEmpty(rawKey)) rawKey = FirstMatch(_piecesEn, IsRawKey);
            bool noRawKey = string.IsNullOrEmpty(rawKey);
            // 7) 英文界面不许残留中文（本轮缺陷的正向判据）
            string leftover = FirstMatch(_lobbyEn, HasCjk);
            if (string.IsNullOrEmpty(leftover)) leftover = FirstMatch(_cardsEn, HasCjk);
            if (string.IsNullOrEmpty(leftover)) leftover = FirstMatch(_piecesEn, HasCjk);
            bool noCjk = string.IsNullOrEmpty(leftover);

            // M51：`_switchClicked` 必须单列。旧判据只看"点完开关值变了没有"——
            // 而开关被挡住时值也不会变，看着像"这个开关坏了"，其实是"你点不到它"。
            // 把两件事分开判，红的时候才知道该去修哪一层。
            bool pass = controls && _switchClicked && _switchTookEffect &&
                        zhOk && enOk && lobbyOk && cardsOk &&
                        noRawKey && noCjk && _errors == 0;

            Debug.Log("[SMOKE] ===== UI 实测结果 =====");
            Debug.Log("[SMOKE] 云存档开关控件  " + (_toggleFound ? "存在" : "缺失"));
            Debug.Log("[SMOKE] 云存档状态控件  " + (_statusFound ? "存在" : "缺失"));
            Debug.Log("[SMOKE] 立即同步控件    " + (_syncFound ? "存在" : "缺失"));
            Debug.Log("[SMOKE] 开关可点到      " + (_switchClicked ? "是" : "否"));
            Debug.Log("[SMOKE] 开关点击生效    " + (_switchTookEffect ? "是" : "否"));
            // 注意：键与值之间必须留 2 个以上空格——解析脚本的 [SMOKE] 正则靠它切分
            Debug.Log("[SMOKE] 中文状态文案    " + _zhStatus);
            Debug.Log("[SMOKE] 英文状态文案    " + _enStatus);
            Debug.Log("[SMOKE] 大厅中文文案数  " + _lobbyZh.Count);
            Debug.Log("[SMOKE] 大厅英文文案数  " + _lobbyEn.Count);
            Debug.Log("[SMOKE] 升级卡文案数    " + _cardsEn.Count);
            Debug.Log("[SMOKE] 升级素材数      " + _piecesEn.Count);
            Debug.Log("[SMOKE] 裸键文案        " + (noRawKey ? "无" : rawKey));
            Debug.Log("[SMOKE] 英文界面中文残留  " + (noCjk ? "无" : leftover));
            Debug.Log("[SMOKE] 运行期异常      " + _errors);
            if (_errors > 0)
                Debug.Log("[SMOKE] 首条错误        " + _firstError);
            Debug.Log("[SMOKE] 结论            " + (pass ? "通过" : "未通过"));

            Application.Quit(pass ? 0 : 1);
        }
    }
}
