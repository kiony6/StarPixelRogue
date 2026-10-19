using UnityEngine;

namespace StarPixel.Compliance
{
    /// <summary>
    /// 首启合规门（M5，对应国内发行强制要求）：
    /// 首次启动（或法律文本版本升级后）展示「健康游戏忠告 + 协议阅读」模态，
    /// 用户点击"同意并继续"后才进入主菜单；"不同意"则退出游戏。
    /// 已同意记录于 <see cref="Core.SettingsManager.Data.agreedTermsVersion"/>。
    /// </summary>
    public class ComplianceGate : MonoBehaviour
    {
        /// <summary>合规门通过后的回调（GameBuilder 用于显示主菜单）。</summary>
        public static System.Action OnPassed;

        /// <summary>
        /// M21 实测：退出行为的可替换钩子。默认就是真的 <c>Application.Quit()</c>；
        /// 探针替换它以便**在不终止进程的前提下**断言"点不同意确实走到了退出"——
        /// 否则进程一退出，剩下的断言结果就再也打印不出来了。
        /// </summary>
        public static System.Action QuitAction = () =>
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        };

        /// <summary>M21 实测：模态是否真的建出来了（门有没有弹）。</summary>
        public static bool Shown { get; private set; }

        /// <summary>M21 实测：同意 / 不同意 各被点了几次（合规行为必须可计数，否则"没弹门"与"弹了没点"无法区分）。</summary>
        public static int AgreeCount { get; private set; }
        public static int DeclineCount { get; private set; }

        /// <summary>M21 实测：当前门面板根（语言切换会重建，须走实例而非 GameObject.Find）。</summary>
        public GameObject PanelRoot => _modalRoot;

        private GameObject _modalRoot;

        void Awake()
        {
            // 已同意当前版本协议：直接放行
            if (Core.SettingsManager.Instance != null &&
                Core.SettingsManager.Instance.TermsAgreed(Core.VersionInfo.ComplianceVersion))
            {
                OnPassed?.Invoke();
                Destroy(this);
                return;
            }
            Build();
            Shown = true;
        }

        /// <summary>M21 实测：把静态计数复位（每个场景独立进程，但复位可避免探针自身误判）。</summary>
        public static void ResetCountersForTest()
        {
            Shown = false;
            AgreeCount = 0;
            DeclineCount = 0;
        }

        /// <summary>
        /// M21 实测：点击「同意并继续」按钮（走真实 Agree 路径，不复制判定逻辑）。
        /// 返回是否点到按钮——点不到本身就是缺陷（按钮缺失/层级不对）。
        /// </summary>
        public bool ClickAgreeForTest()
        {
            var btn = _modalRoot != null ? _modalRoot.transform.Find("Mask/Panel/AgreeBtn") : null;
            var b = btn != null ? btn.GetComponent<UnityEngine.UI.Button>() : null;
            if (b == null) return false;
            b.onClick.Invoke();
            return true;
        }

        /// <summary>M21 实测：点击「不同意并退出」（走真实 Disagree 路径）。</summary>
        public bool ClickDeclineForTest()
        {
            var btn = _modalRoot != null ? _modalRoot.transform.Find("Mask/Panel/DisagreeBtn") : null;
            var b = btn != null ? btn.GetComponent<UnityEngine.UI.Button>() : null;
            if (b == null) return false;
            b.onClick.Invoke();
            return true;
        }

        private void Build()
        {
            var (canvasGo, mask) = Core.UIFactory.CreateModal("ComplianceGate",
                new Color(0.03f, 0.03f, 0.06f, 1f));
            _modalRoot = canvasGo;

            var panel = Core.UIFactory.CreatePanel(mask.transform, "Panel", Vector2.zero,
                new Vector2(760, 560), new Color(0.09f, 0.10f, 0.16f, 1f));

            Core.UIFactory.CreateText(panel.transform, "Title",
                Core.Localization.T("compliance.title"), new Vector2(0, 232), 34);

            // 健康游戏忠告（国标三行，置顶展示）
            Core.UIFactory.CreateText(panel.transform, "Notice1",
                Core.Localization.T("compliance.notice1"), new Vector2(0, 178), 19);
            Core.UIFactory.CreateText(panel.transform, "Notice2",
                Core.Localization.T("compliance.notice2"), new Vector2(0, 148), 19);
            Core.UIFactory.CreateText(panel.transform, "Notice3",
                Core.Localization.T("compliance.notice3"), new Vector2(0, 118), 19);

            Core.UIFactory.CreateText(panel.transform, "Read",
                Core.Localization.T("compliance.read"), new Vector2(0, 70), 18,
                TextAnchor.MiddleCenter, new Vector2(680, 30));

            Core.UIFactory.CreateText(panel.transform, "MinorNotice",
                Core.Localization.T("compliance.minor_notice"), new Vector2(0, 36), 15,
                TextAnchor.MiddleCenter, new Vector2(680, 26));

            // 三个法律文本入口
            Core.UIFactory.CreateButton(panel.transform, "TermsBtn",
                Core.Localization.T("compliance.terms"), new Vector2(-180, -26), new Vector2(210, 46),
                () => LegalViewer.Open(Core.Localization.T("compliance.terms"), LegalTexts.Get(LegalTexts.TermsKey)));
            Core.UIFactory.CreateButton(panel.transform, "PrivacyBtn",
                Core.Localization.T("compliance.privacy"), new Vector2(60, -26), new Vector2(210, 46),
                () => LegalViewer.Open(Core.Localization.T("compliance.privacy"), LegalTexts.Get(LegalTexts.PrivacyKey)));
            Core.UIFactory.CreateButton(panel.transform, "ChildrenBtn",
                Core.Localization.T("compliance.children"), new Vector2(0, -84), new Vector2(260, 46),
                () => LegalViewer.Open(Core.Localization.T("compliance.children"), LegalTexts.Get(LegalTexts.ChildrenKey)));

            // 同意 / 不同意
            Core.UIFactory.CreateButton(panel.transform, "AgreeBtn",
                Core.Localization.T("compliance.agree"), new Vector2(-130, -160), new Vector2(230, 56),
                Agree, 20, new Color(0.20f, 0.45f, 0.28f, 1f));
            Core.UIFactory.CreateButton(panel.transform, "DisagreeBtn",
                Core.Localization.T("compliance.disagree"), new Vector2(130, -160), new Vector2(230, 56),
                Disagree, 20, new Color(0.45f, 0.20f, 0.22f, 1f));

            Core.UIFactory.CreateText(panel.transform, "Version",
                Core.VersionInfo.Display + "  |  " + Core.VersionInfo.ComplianceVersion,
                new Vector2(0, -232), 14, TextAnchor.MiddleCenter, new Vector2(680, 24));
        }

        private void Agree()
        {
            AgreeCount++;
            Core.SettingsManager.Ensure();
            Core.SettingsManager.Instance.AgreeTerms(Core.VersionInfo.ComplianceVersion);
            Core.Telemetry.Log(Core.Telemetry.EvAgreeTerms,
                "version", Core.VersionInfo.ComplianceVersion);
            Core.UIFactory.Destroy(_modalRoot);

            // M21：协议版本升级后重新征得同意的当次会话里，云存档此前是「未同意」而停用的，
            // 它的首次拉取（Start 里那次）已经错过；此时必须补一次拉取再谈上传，
            // 否则本地旧档会直接把云端新档覆盖掉（静默的数据丢失）。
            Core.CloudSave.CloudSaveService.Instance?.OnConsentGranted();

            OnPassed?.Invoke();
            Destroy(this);
        }

        private void Disagree()
        {
            DeclineCount++;
            Debug.Log("[Compliance] 用户不同意协议，退出游戏");
            QuitAction?.Invoke();
        }
    }
}
