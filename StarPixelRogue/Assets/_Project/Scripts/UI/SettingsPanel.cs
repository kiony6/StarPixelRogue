using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 设置面板（M5）：音频 / 玩法 / 法律文本。
    /// 所有控件实时生效（音量、画质、全屏、震屏、遥测、青少年模式），
    /// 经 <see cref="Core.SettingsManager"/> 原子落盘；语言切换后面板整体重建。
    /// </summary>
    public class SettingsPanel : Core.MonoSingleton<SettingsPanel>
    {
        private GameObject _root;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("SettingsPanel").AddComponent<SettingsPanel>();
        }

        protected override void Awake()
        {
            base.Awake();
            Core.Localization.LanguageChanged += Rebuild;
            Build();
            Hide();
        }

        void OnDestroy()
        {
            Core.Localization.LanguageChanged -= Rebuild;
        }

        public void Show()
        {
            Ensure();
            if (Instance != this) { Instance.Show(); return; }
            if (_root != null) _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        private void Rebuild()
        {
            Build();
        }

        private void Build()
        {
            if (_root != null) Core.UIFactory.Destroy(_root);
            Core.SettingsManager.Ensure();            // 设置数据必须先于控件读取
            var canvas = Core.UIFactory.CreateCanvas("SettingsCanvas");
            _root = canvas.gameObject;

            var panel = Core.UIFactory.CreatePanel(_root.transform, "Panel", Vector2.zero,
                new Vector2(640, 620), new Color(0.08f, 0.09f, 0.14f, 0.97f));

            Core.UIFactory.CreateText(panel.transform, "Title", Core.Localization.T("settings.title"),
                new Vector2(0, 278), 32);
            Core.UIFactory.CreateText(panel.transform, "Version", Core.VersionInfo.Display,
                new Vector2(0, -282), 14);

            var s = Core.SettingsManager.Instance.Current;

            // —— 音频 ——
            Core.UIFactory.CreateText(panel.transform, "AudioHeader", Core.Localization.T("settings.audio"),
                new Vector2(-220, 238), 20);
            AddSlider(panel.transform, "Master", Core.Localization.T("settings.master"), s.masterVolume,
                new Vector2(0, 206), v => Core.SettingsManager.Instance.SetVolume("master", v));
            AddSlider(panel.transform, "Music", Core.Localization.T("settings.music"), s.musicVolume,
                new Vector2(0, 168), v => Core.SettingsManager.Instance.SetVolume("music", v));
            AddSlider(panel.transform, "Sfx", Core.Localization.T("settings.sfx"), s.sfxVolume,
                new Vector2(0, 130), v => Core.SettingsManager.Instance.SetVolume("sfx", v));

            // —— 玩法 ——
            Core.UIFactory.CreateText(panel.transform, "GameplayHeader", Core.Localization.T("settings.gameplay"),
                new Vector2(-220, 92), 20);

            // 画质循环：低/中/高
            // M17 实测修复：同 UIFactory.CreateToggle——先声明再赋值，避免 lambda 内提前引用（CS0841）
            Button qualityBtn = null;
            qualityBtn = Core.UIFactory.CreateButton(panel.transform, "Quality",
                Core.Localization.T("settings.quality") + "：" + QualityLabel(s.quality),
                new Vector2(-90, 60), new Vector2(280, 44), () =>
                {
                    int next = (Core.SettingsManager.Instance.Current.quality + 1) % 3;
                    Core.SettingsManager.Instance.SetQuality(next);
                    var label = qualityBtn != null ? qualityBtn.GetComponentInChildren<Text>() : null;
                    if (label != null)
                        label.text = Core.Localization.T("settings.quality") + "：" + QualityLabel(next);
                }, 18);
            // 语言循环：中文/English
            var langBtn = Core.UIFactory.CreateButton(panel.transform, "Language",
                Core.Localization.T("settings.language") + "：" + Core.Localization.LanguageLabel(s.language),
                new Vector2(-90, 10), new Vector2(280, 44), () =>
                {
                    string next = Core.SettingsManager.Instance.Current.language == Core.Localization.LangZh
                        ? Core.Localization.LangEn : Core.Localization.LangZh;
                    Core.SettingsManager.Instance.SetLanguage(next);   // 触发 LanguageChanged → 面板重建
                }, 18);

            AddToggle(panel.transform, "Fullscreen", Core.Localization.T("settings.fullscreen"), s.fullscreen,
                new Vector2(-90, -40), v => Core.SettingsManager.Instance.SetFullscreen(v));
            AddToggle(panel.transform, "Shake", Core.Localization.T("settings.shake"), s.screenShake,
                new Vector2(-90, -90), v => Core.SettingsManager.Instance.SetScreenShake(v));
            AddToggle(panel.transform, "Telemetry", Core.Localization.T("settings.telemetry"), s.telemetry,
                new Vector2(-90, -140), v => Core.SettingsManager.Instance.SetTelemetry(v));
            AddToggle(panel.transform, "MinorMode", Core.Localization.T("settings.minor_mode"), s.minorMode,
                new Vector2(-90, -190), v => Core.SettingsManager.Instance.SetMinorMode(v));
            Core.UIFactory.CreateText(panel.transform, "MinorHint",
                Core.Localization.T("settings.minor_hint"), new Vector2(30, -222), 13,
                TextAnchor.MiddleCenter, new Vector2(560, 24));

            // —— 法律文本与工具 ——
            Core.UIFactory.CreateText(panel.transform, "LegalHeader", Core.Localization.T("settings.legal"),
                new Vector2(-250, -262), 16);
            Core.UIFactory.CreateButton(panel.transform, "TermsBtn", Core.Localization.T("compliance.terms"),
                new Vector2(-190, -296), new Vector2(150, 38),
                () => Compliance.LegalViewer.Open(Core.Localization.T("compliance.terms"),
                    Compliance.LegalTexts.Get(Compliance.LegalTexts.TermsKey)), 14);
            Core.UIFactory.CreateButton(panel.transform, "PrivacyBtn", Core.Localization.T("compliance.privacy"),
                new Vector2(-20, -296), new Vector2(150, 38),
                () => Compliance.LegalViewer.Open(Core.Localization.T("compliance.privacy"),
                    Compliance.LegalTexts.Get(Compliance.LegalTexts.PrivacyKey)), 14);
            Core.UIFactory.CreateButton(panel.transform, "ChildrenBtn", Core.Localization.T("compliance.children"),
                new Vector2(150, -296), new Vector2(160, 38),
                () => Compliance.LegalViewer.Open(Core.Localization.T("compliance.children"),
                    Compliance.LegalTexts.Get(Compliance.LegalTexts.ChildrenKey)), 14);

            Core.UIFactory.CreateButton(panel.transform, "ExportBtn", Core.Localization.T("settings.export_log"),
                new Vector2(-190, -344), new Vector2(200, 38), OnExportLog, 14);
            Core.UIFactory.CreateButton(panel.transform, "CloseBtn", Core.Localization.T("settings.close"),
                new Vector2(0, 276), new Vector2(120, 40), Hide, 18);
        }

        private void AddSlider(Transform parent, string name, string label, float value,
            Vector2 pos, System.Action<float> onChanged)
        {
            Core.UIFactory.CreateText(parent, name + "Label", label, new Vector2(-220, pos.y + 2), 16,
                TextAnchor.MiddleLeft, new Vector2(160, 30));
            var slider = Core.UIFactory.CreateSlider(parent, name + "Slider", new Vector2(40, pos.y),
                new Vector2(280, 22), value,
                new Color(0.30f, 0.62f, 1f), new Color(0.14f, 0.15f, 0.22f, 1f));
            slider.onValueChanged.AddListener(v => onChanged(v));
        }

        private void AddToggle(Transform parent, string name, string label, bool value,
            Vector2 pos, System.Action<bool> onChanged)
        {
            Core.UIFactory.CreateText(parent, name + "Label", label, new Vector2(-220, pos.y), 16,
                TextAnchor.MiddleLeft, new Vector2(240, 30));
            Core.UIFactory.CreateToggle(parent, name + "Toggle", value, new Vector2(110, pos.y),
                new Vector2(80, 36), onChanged);
        }

        private void OnExportLog()
        {
            Core.Telemetry.Flush();
            string path = Core.Telemetry.ExportPath;
            Debug.Log("[Settings] 诊断日志目录: " + path);
            var tip = _root != null ? _root.transform.Find("ExportTip") : null;
            if (tip == null && _root != null)
            {
                var t = Core.UIFactory.CreateText(_root.transform, "ExportTip",
                    Core.Localization.T("settings.export_done", path), new Vector2(0, -370), 13,
                    TextAnchor.MiddleCenter, new Vector2(700, 24));
                t.color = new Color(0.7f, 0.85f, 1f, 0.9f);
            }
        }

        private static string QualityLabel(int q)
        {
            switch (q)
            {
                case 0: return Core.Localization.T("common.low");
                case 1: return Core.Localization.T("common.medium");
                default: return Core.Localization.T("common.high");
            }
        }
    }
}
