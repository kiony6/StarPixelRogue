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

        /// <summary>M13：云存档状态文本（异步刷新，需定时重绘）。</summary>
        private Text _cloudStatus;
        private float _cloudTimer;

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

        /// <summary>
        /// M18 实测用：面板根（画布）。语言切换会整体重建，本属性随即指向新画布——
        /// 探针据此精确定位控件，避免用 <c>GameObject.Find</c> 拿到已废弃的旧画布。
        /// </summary>
        public Transform PanelRoot => _root != null ? _root.transform : null;

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

        void Update()
        {
            // M13：云存档状态由协程异步改写（拉取/上传成功或失败时才变），
            // 不定时重绘的话，用户点了「立即同步」也永远看不到结果。
            if (_root == null || !_root.activeSelf || _cloudStatus == null) return;
            _cloudTimer -= Time.unscaledDeltaTime;
            if (_cloudTimer <= 0f)
            {
                _cloudTimer = 0.5f;
                RefreshCloudStatus();
            }
        }

        private void RefreshCloudStatus()
        {
            if (_cloudStatus == null) return;
            var svc = Core.CloudSave.CloudSaveService.Instance;
            _cloudStatus.text = svc != null
                ? svc.LastStatusText
                : Core.Localization.T(Core.CloudSave.CloudSaveService.StatusIdle);
        }

        private void Rebuild()
        {
            // M22 修复：语言切换会整体重建面板，而 Build() 之后没有 Show()。
            // 旧实现里用户在设置页点一下「语言」，面板当场消失（连带回到主菜单），
            // 得再点一次「设置」才回得来——切个语言被踢出去一次，等于这条路径不可用。
            bool wasVisible = _root != null && _root.activeSelf;
            Build();
            if (wasVisible) Show();
        }

        private void Build()
        {
            if (_root != null) Core.UIFactory.Destroy(_root);
            Core.SettingsManager.Ensure();            // 设置数据必须先于控件读取
            var canvas = Core.UIFactory.CreateCanvas("SettingsCanvas");
            _root = canvas.gameObject;

            // M13：620 → 700，为云存档区块腾出空间（仍低于 720p 屏高，不会溢出）。
            // 顺带把此前溢出到面板外的「导出诊断日志」按钮与提示收进面板内。
            var panel = Core.UIFactory.CreatePanel(_root.transform, "Panel", Vector2.zero,
                new Vector2(640, 700), new Color(0.08f, 0.09f, 0.14f, 0.97f));

            Core.UIFactory.CreateText(panel.transform, "Title", Core.Localization.T("settings.title"),
                new Vector2(0, 278), 32);
            Core.UIFactory.CreateText(panel.transform, "Version", Core.VersionInfo.Display,
                new Vector2(0, -302), 14);

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
            // M22 实测修复：本对象在 lambda 里被引用，必须先声明再赋值（CS0841，同 Quality）。
            Button langBtn = null;
            langBtn = Core.UIFactory.CreateButton(panel.transform, "Language",
                Core.Localization.T("settings.language") + "：" + Core.Localization.LanguageLabel(s.language),
                new Vector2(-90, 10), new Vector2(280, 44), () =>
                {
                    string next = Core.SettingsManager.Instance.Current.language == Core.Localization.LangZh
                        ? Core.Localization.LangEn : Core.Localization.LangZh;
                    Core.SettingsManager.Instance.SetLanguage(next);   // 触发 LanguageChanged → 面板重建
                    // M22：此刻按钮已属于**被销毁的旧面板**。切到中文时 t() 恰好把「English」原样返回，
                    // 缺的译（语言名是自指的，两个方向都有）只有在"English → 中文"这一次才暴露。
                    // 于是用户看到按钮写着「Language：English」，点下去界面却变成中文——
                    // 按钮说的和实际做的不一致，是比漏翻更糟的一种"看起来正常"。
                    var label = langBtn != null ? langBtn.GetComponentInChildren<Text>() : null;
                    if (label != null)
                        label.text = Core.Localization.T("settings.language") + "：" +
                                     Core.Localization.LanguageLabel(next);
                }, 18);

            // 玩法开关改两列排布：为下方的云存档区块腾出约 80px。
            // 面板不能无限加高——720p 下超过屏高就会溢出看不见。
            AddToggle(panel.transform, "Fullscreen", Core.Localization.T("settings.fullscreen"), s.fullscreen,
                -20f, -200f, -40f, v => Core.SettingsManager.Instance.SetFullscreen(v));
            AddToggle(panel.transform, "Telemetry", Core.Localization.T("settings.telemetry"), s.telemetry,
                -20f, 120f, 260f, v => Core.SettingsManager.Instance.SetTelemetry(v));
            AddToggle(panel.transform, "Shake", Core.Localization.T("settings.shake"), s.screenShake,
                -70f, -200f, -40f, v => Core.SettingsManager.Instance.SetScreenShake(v));
            AddToggle(panel.transform, "MinorMode", Core.Localization.T("settings.minor_mode"), s.minorMode,
                -70f, 120f, 260f, v => Core.SettingsManager.Instance.SetMinorMode(v));
            Core.UIFactory.CreateText(panel.transform, "MinorHint",
                Core.Localization.T("settings.minor_hint"), new Vector2(30, -108), 13,
                TextAnchor.MiddleCenter, new Vector2(560, 24));

            // —— 云存档（M13） ——
            Core.UIFactory.CreateText(panel.transform, "CloudHeader",
                Core.Localization.T("settings.cloud_header"), new Vector2(-250, -140), 16);
            AddToggle(panel.transform, "CloudSave", Core.Localization.T("settings.cloud_save"), s.cloudSave,
                -178f, -200f, -40f, v =>
                {
                    Core.SettingsManager.Instance.SetCloudSave(v);
                    Core.CloudSave.CloudSaveService.Ensure();
                    // 从「关」拨到「开」时必须补一次拉取裁决：服务若已在 Start 里判定过
                    // 一次"未启用"并直接返回，此后 Update 不会再主动发起同步——
                    // 用户会以为开了开关却毫无反应，只能等到下次启动才生效。
                    if (v) Core.CloudSave.CloudSaveService.Instance?.SyncNow();
                    RefreshCloudStatus();
                });

            _cloudStatus = Core.UIFactory.CreateText(panel.transform, "CloudStatus",
                Core.Localization.T(Core.CloudSave.CloudSaveService.StatusIdle),
                new Vector2(-110, -212), 13, TextAnchor.MiddleLeft, new Vector2(420, 24));
            _cloudStatus.color = new Color(0.7f, 0.85f, 1f, 0.9f);
            RefreshCloudStatus();

            Core.UIFactory.CreateButton(panel.transform, "CloudSyncNow",
                Core.Localization.T("settings.cloud_sync_now"),
                new Vector2(215, -212), new Vector2(110, 32), () =>
                {
                    Core.CloudSave.CloudSaveService.Ensure();
                    Core.CloudSave.CloudSaveService.Instance?.SyncNow();   // 未启用时内部直接 return
                    RefreshCloudStatus();
                }, 13);

            // —— 法律文本与工具 ——
            Core.UIFactory.CreateText(panel.transform, "LegalHeader", Core.Localization.T("settings.legal"),
                new Vector2(-250, -242), 16);
            Core.UIFactory.CreateButton(panel.transform, "TermsBtn", Core.Localization.T("compliance.terms"),
                new Vector2(-190, -272), new Vector2(150, 38),
                () => Compliance.LegalViewer.Open(Core.Localization.T("compliance.terms"),
                    Compliance.LegalTexts.Get(Compliance.LegalTexts.TermsKey)), 14);
            Core.UIFactory.CreateButton(panel.transform, "PrivacyBtn", Core.Localization.T("compliance.privacy"),
                new Vector2(-20, -272), new Vector2(150, 38),
                () => Compliance.LegalViewer.Open(Core.Localization.T("compliance.privacy"),
                    Compliance.LegalTexts.Get(Compliance.LegalTexts.PrivacyKey)), 14);
            Core.UIFactory.CreateButton(panel.transform, "ChildrenBtn", Core.Localization.T("compliance.children"),
                new Vector2(150, -272), new Vector2(160, 38),
                () => Compliance.LegalViewer.Open(Core.Localization.T("compliance.children"),
                    Compliance.LegalTexts.Get(Compliance.LegalTexts.ChildrenKey)), 14);

            Core.UIFactory.CreateButton(panel.transform, "ExportBtn", Core.Localization.T("settings.export_log"),
                new Vector2(-230, -330), new Vector2(180, 38), OnExportLog, 14);
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

        /// <summary>
        /// 开关行。labelX / toggleX 由调用方显式给出（而非固定 -220/110），
        /// 是为了支持玩法区的两列排布——面板高度有限，云存档区块的空间只能靠横向省出来。
        /// 约定：标签左对齐、宽 200，故文本左边缘 = labelX - 100。
        /// </summary>
        private void AddToggle(Transform parent, string name, string label, bool value,
            float y, float labelX, float toggleX, System.Action<bool> onChanged)
        {
            Core.UIFactory.CreateText(parent, name + "Label", label, new Vector2(labelX, y), 16,
                TextAnchor.MiddleLeft, new Vector2(200, 30));
            Core.UIFactory.CreateToggle(parent, name + "Toggle", value, new Vector2(toggleX, y),
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
                    Core.Localization.T("settings.export_done", path), new Vector2(80, -330), 13,
                    TextAnchor.MiddleLeft, new Vector2(340, 24));
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
