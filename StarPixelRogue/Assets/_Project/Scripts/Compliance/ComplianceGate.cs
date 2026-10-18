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
            Core.SettingsManager.Ensure();
            Core.SettingsManager.Instance.AgreeTerms(Core.VersionInfo.ComplianceVersion);
            Core.Telemetry.Log(Core.Telemetry.EvAgreeTerms,
                "version", Core.VersionInfo.ComplianceVersion);
            Core.UIFactory.Destroy(_modalRoot);
            OnPassed?.Invoke();
            Destroy(this);
        }

        private void Disagree()
        {
            Debug.Log("[Compliance] 用户不同意协议，退出游戏");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
