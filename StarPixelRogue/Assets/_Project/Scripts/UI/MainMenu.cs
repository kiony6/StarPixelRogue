using UnityEngine;

namespace StarPixel.UI
{
    /// <summary>
    /// 主菜单（M5）：开始游戏 / 设置 / 退出 + 战绩展示。
    /// - 合规门通过后由 GameBuilder 显示；一局结束后由 GameOverPanel 调 Show() 返回；
    /// - 语言切换时整体重建（订阅 <see cref="Core.Localization.LanguageChanged"/>）；
    /// - 检测到 Fish-Net 后端时给出提示：退出主菜单的入口在联机大厅（NetLobby）。
    /// </summary>
    public class MainMenu : Core.MonoSingleton<MainMenu>
    {
        private GameObject _root;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("MainMenu").AddComponent<MainMenu>();
        }

        protected override void Awake()
        {
            base.Awake();
            Core.Localization.LanguageChanged += Rebuild;
            Build();
            Hide();                                    // 默认隐藏，等合规门放行
        }

        void OnDestroy()
        {
            Core.Localization.LanguageChanged -= Rebuild;
        }

        /// <summary>显示菜单（刷新战绩）。</summary>
        public void Show()
        {
            if (_root != null) _root.SetActive(true);
            RefreshRecords();
        }

        /// <summary>显示菜单并附一行提示（如青少年模式到点强制退出）。</summary>
        public void ShowWithNotice(string notice)
        {
            Show();
            var noticeText = _root != null ? _root.transform.Find("Notice")?.GetComponent<UnityEngine.UI.Text>() : null;
            if (noticeText != null) noticeText.text = notice;
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>
        /// M19 实测：读取**当前**面板上的提示行文案（青少年模式到点提示就写在这里）。
        /// 必须经 Instance 的 _root 取，不能用 GameObject.Find("MainMenuCanvas")：
        /// 语言切换会整体重建面板而 Destroy 延迟到帧末，Find 会命中尚未销毁的旧画布。
        /// </summary>
        public string NoticeText
        {
            get
            {
                var t = _root != null ? _root.transform.Find("Notice")?.GetComponent<UnityEngine.UI.Text>() : null;
                return t != null ? t.text : "";
            }
        }

        /// <summary>
        /// M21 实测：主菜单当前是否可见。合规门的意义正是"同意前不得进入游戏"，
        /// 所以"同意前菜单是隐藏的、同意后才显示"必须能被断言，而不是靠看代码推断。
        /// </summary>
        public bool IsVisible => _root != null && _root.activeSelf;

        private void Rebuild()
        {
            Build();
            if (_root != null) _root.SetActive(true);
        }

        private void Build()
        {
            if (_root != null) Core.UIFactory.Destroy(_root);

            var canvas = Core.UIFactory.CreateCanvas("MainMenuCanvas");
            _root = canvas.gameObject;

            // M53：背景星点与压暗层。
            // 刻意**不**为它新建一个 Canvas——每多一块 Overlay 就多一次"谁压谁"的题，
            // 而放进 MainMenuCanvas 里当第一个子节点，层级关系在代码里是看得见的。
            BuildBackdrop(_root.transform);

            // 标题区
            Core.UIFactory.CreateTitle(_root.transform, "Title",
                Core.UIFactory.GradientRich(Core.Localization.T("app.title"),
                    Core.UIFactory.Palette.Accent, Core.UIFactory.Palette.AccentAlt),
                new Vector2(0, 262), 62, new Vector2(1000, 96));
            var sub = Core.UIFactory.CreateText(_root.transform, "Subtitle", Core.Localization.T("app.subtitle"),
                new Vector2(0, 210), 22);
            sub.color = Core.UIFactory.Palette.TextDim;
            var ver = Core.UIFactory.CreateText(_root.transform, "Version", Core.VersionInfo.Display,
                new Vector2(0, -300), 16);
            ver.color = Core.UIFactory.Palette.TextDim;

            // 提示行（默认空）：联机提示 / 青少年到点提示
            Core.UIFactory.CreateText(_root.transform, "Notice", "",
                new Vector2(0, 176), 18);

            if (Net.NetBackend.Instance != null)
            {
                var notice = _root.transform.Find("Notice")?.GetComponent<UnityEngine.UI.Text>();
                if (notice != null) notice.text = Core.Localization.T("menu.hint_net");
            }

            // 战绩面板（M53：圆角框 + 标题下的分隔线，与左边的按钮列形成分组）
            var panel = Core.UIFactory.CreateRoundPanel(_root.transform, "Records", new Vector2(300, 0),
                new Vector2(380, 300), Core.UIFactory.Palette.Deep);
            Core.UIFactory.CreateTitle(panel.transform, "RecordsTitle", Core.Localization.T("menu.records"),
                new Vector2(0, 122), 26, new Vector2(340, 40), Core.UIFactory.Palette.Accent);
            Core.UIFactory.CreateDivider(panel.transform, "RecordsRule", new Vector2(0, 104), 320,
                Core.UIFactory.Tint(Core.UIFactory.Palette.Accent, 1f, 0.5f));

            // 战绩行：左侧标签、右侧数值——原先挤在一行居中，数字混在文字里，
            // 玩家扫一眼读不到"最好成绩是多少"。
            var rows = new[]
            {
                new Row("BestWave", Core.Localization.T("menu.best_wave"), 74),
                new Row("BestTime", Core.Localization.T("menu.best_time"), 40),
                new Row("BestLevel", Core.Localization.T("menu.best_level"), 6),
                new Row("TotalRuns", Core.Localization.T("menu.total_runs"), -28),
                new Row("LastRun", Core.Localization.T("menu.last_run"), -62),
            };
            foreach (var row in rows)
            {
                var label = Core.UIFactory.CreateText(panel.transform, row.node + "Label", row.label,
                    new Vector2(-76, row.y), 18, TextAnchor.MiddleLeft, new Vector2(160, 30));
                label.color = Core.UIFactory.Palette.TextDim;
                var value = Core.UIFactory.CreateText(panel.transform, row.node, "-",
                    new Vector2(96, row.y), 18, TextAnchor.MiddleRight, new Vector2(190, 30));
                value.color = Core.UIFactory.Palette.Text;
            }

            // 主按钮列（M53：每颗给一条语义强调色——开始的绿、查看的青、退出的红，
            // 光看颜色就能把"推进"和"离场"分开）
            Core.UIFactory.CreateButton(_root.transform, "StartBtn", Core.Localization.T("menu.start"),
                new Vector2(-160, 40), new Vector2(280, 64), OnStartGame, 24,
                Core.UIFactory.Tint(Core.UIFactory.Palette.Vital, 0.42f, 0.95f),
                Core.UIFactory.Palette.Vital);
            Core.UIFactory.CreateButton(_root.transform, "CharacterBtn", Core.Localization.T("menu.character"),
                new Vector2(-160, -40), new Vector2(280, 56),
                () => { CharacterSelectPanel.Ensure(); CharacterSelectPanel.Instance.Show(); },
                20, Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.AccentAlt);
            Core.UIFactory.CreateButton(_root.transform, "CodexBtn", Core.Localization.T("menu.codex"),
                new Vector2(-160, -110), new Vector2(280, 56),
                () => { CodexPanel.Ensure(); CodexPanel.Instance.Show(); },
                20, Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Accent);
            Core.UIFactory.CreateButton(_root.transform, "SettingsBtn", Core.Localization.T("menu.settings"),
                new Vector2(-160, -180), new Vector2(280, 56),
                () => { SettingsPanel.Ensure(); SettingsPanel.Instance.Show(); },
                20, Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.AccentAlt);
            Core.UIFactory.CreateButton(_root.transform, "QuitBtn", Core.Localization.T("menu.quit"),
                new Vector2(-160, -250), new Vector2(280, 56), OnQuit,
                20, Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Danger);

            // 当前角色提示（M6.2）
            // 注意：这一行的文本被 BuildProbe 的 CurrentCharacterText 逐字比对，
            // 因此它保持**纯文本**——不做渐变染色、也不加任何包装。
            var currentChar = Core.UIFactory.CreateText(_root.transform, "CurrentCharacter",
                Core.Localization.T("menu.character") + "：" +
                Core.Localization.T(Player.CharacterManager.Selected.nameKey),
                new Vector2(20, 268), 18, TextAnchor.MiddleLeft, new Vector2(320, 40));
            currentChar.color = Core.UIFactory.Palette.TextDim;

            RefreshRecords();
        }

        /// <summary>战绩行的一段：[标签节点名, 标签文案, y 坐标]。</summary>
        private struct Row
        {
            public string node;
            public string label;
            public float y;

            public Row(string node, string label, float y)
            {
                this.node = node;
                this.label = label;
                this.y = y;
            }
        }

        /// <summary>
        /// M53：菜单背景。星场 + 一层压暗，两者都占满画布、
        /// 都不接射线（它们是背景，不许吃掉按钮的点击——见 UIHitTest 的由来）。
        /// </summary>
        private static void BuildBackdrop(Transform canvasRoot)
        {
            var bg = new GameObject("StarfieldBG", typeof(RectTransform));
            bg.transform.SetParent(canvasRoot, false);
            var rt = (RectTransform)bg.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var field = bg.AddComponent<StarfieldUI>();
            field.Build(110, new Color(0.72f, 0.84f, 1f));

            // 压暗层：星点在标题底下继续闪很好看，在整屏文字底下就成了干扰。
            var veilTrans = new GameObject("Veil", typeof(RectTransform));
            veilTrans.transform.SetParent(canvasRoot, false);
            var vrt = (RectTransform)veilTrans.transform;
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = Vector2.zero;
            vrt.offsetMax = Vector2.zero;
            var veil = veilTrans.AddComponent<UnityEngine.UI.Image>();
            veil.color = new Color(0.02f, 0.03f, 0.07f, 0.45f);
            veil.raycastTarget = false;
        }

        /// <summary>刷新战绩文本（进入菜单/一局结束时调用）。</summary>
        public void RefreshRecords()
        {
            if (_root == null) return;
            var save = Core.SaveManager.Instance;
            if (save == null) return;
            var d = save.Data;

            SetText("CurrentCharacter", Core.Localization.T("menu.character") + "：" +
                Core.Localization.T(Player.CharacterManager.Selected.nameKey));
            SetText("BestWave", Core.Localization.T("menu.best_wave") + "：" +
                (d.bestWave > 0 ? d.bestWave.ToString() : Core.Localization.T("menu.none")));
            SetText("BestTime", Core.Localization.T("menu.best_time") + "：" +
                (d.bestTime > 0f ? Core.SaveManager.FormatTime(d.bestTime) : Core.Localization.T("menu.none")));
            SetText("BestLevel", Core.Localization.T("menu.best_level") + "：" +
                (d.bestLevel > 0 ? d.bestLevel.ToString() : Core.Localization.T("menu.none")));
            SetText("TotalRuns", Core.Localization.T("menu.total_runs") + "：" + d.totalRuns);
            SetText("LastRun", Core.Localization.T("menu.last_run") + "：" +
                (d.totalRuns > 0
                    ? Core.Localization.T("gameover.stats", d.lastWave, d.lastLevel, 0, Core.SaveManager.FormatTime(d.lastTime))
                    : Core.Localization.T("menu.records_empty")));
        }

        private void SetText(string childName, string content)
        {
            var t = _root.transform.Find(childName)?.GetComponent<UnityEngine.UI.Text>();
            if (t != null) t.text = content;
        }

        private void OnStartGame()
        {
            Core.Telemetry.Log(Core.Telemetry.EvRunStart,
                "mode", Net.NetSession.ModeLabel());
            Hide();
            GameBuilder.TeardownWorld();
            GameBuilder.Build();
        }

        private void OnQuit()
        {
            Debug.Log("[MainMenu] 退出游戏");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
