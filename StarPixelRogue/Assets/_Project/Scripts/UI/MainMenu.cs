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

            // 标题区
            Core.UIFactory.CreateText(_root.transform, "Title", Core.Localization.T("app.title"),
                new Vector2(0, 260), 56);
            Core.UIFactory.CreateText(_root.transform, "Subtitle", Core.Localization.T("app.subtitle"),
                new Vector2(0, 214), 20);
            Core.UIFactory.CreateText(_root.transform, "Version", Core.VersionInfo.Display,
                new Vector2(0, -300), 16);

            // 提示行（默认空）：联机提示 / 青少年到点提示
            Core.UIFactory.CreateText(_root.transform, "Notice", "",
                new Vector2(0, 176), 18);

            if (Net.NetBackend.Instance != null)
            {
                var notice = _root.transform.Find("Notice")?.GetComponent<UnityEngine.UI.Text>();
                if (notice != null) notice.text = Core.Localization.T("menu.hint_net");
            }

            // 战绩面板
            var panel = Core.UIFactory.CreatePanel(_root.transform, "Records", new Vector2(300, 0),
                new Vector2(380, 300), new Color(0.08f, 0.09f, 0.14f, 0.92f));
            Core.UIFactory.CreateText(panel.transform, "RecordsTitle", Core.Localization.T("menu.records"),
                new Vector2(0, 122), 24);
            Core.UIFactory.CreateText(panel.transform, "BestWave",
                Core.Localization.T("menu.best_wave") + "：-", new Vector2(0, 82), 18,
                TextAnchor.MiddleCenter, new Vector2(340, 30));
            Core.UIFactory.CreateText(panel.transform, "BestTime",
                Core.Localization.T("menu.best_time") + "：-", new Vector2(0, 52), 18,
                TextAnchor.MiddleCenter, new Vector2(340, 30));
            Core.UIFactory.CreateText(panel.transform, "BestLevel",
                Core.Localization.T("menu.best_level") + "：-", new Vector2(0, 22), 18,
                TextAnchor.MiddleCenter, new Vector2(340, 30));
            Core.UIFactory.CreateText(panel.transform, "TotalRuns",
                Core.Localization.T("menu.total_runs") + "：-", new Vector2(0, -8), 18,
                TextAnchor.MiddleCenter, new Vector2(340, 30));
            Core.UIFactory.CreateText(panel.transform, "LastRun",
                Core.Localization.T("menu.last_run") + "：-", new Vector2(0, -38), 18,
                TextAnchor.MiddleCenter, new Vector2(340, 30));

            // 主按钮列
            Core.UIFactory.CreateButton(_root.transform, "StartBtn", Core.Localization.T("menu.start"),
                new Vector2(-160, 40), new Vector2(280, 64), OnStartGame, 24,
                new Color(0.20f, 0.45f, 0.28f, 1f));
            Core.UIFactory.CreateButton(_root.transform, "CharacterBtn", Core.Localization.T("menu.character"),
                new Vector2(-160, -40), new Vector2(280, 56),
                () => { CharacterSelectPanel.Ensure(); CharacterSelectPanel.Instance.Show(); });
            Core.UIFactory.CreateButton(_root.transform, "CodexBtn", Core.Localization.T("menu.codex"),
                new Vector2(-160, -110), new Vector2(280, 56),
                () => { CodexPanel.Ensure(); CodexPanel.Instance.Show(); });
            Core.UIFactory.CreateButton(_root.transform, "SettingsBtn", Core.Localization.T("menu.settings"),
                new Vector2(-160, -180), new Vector2(280, 56),
                () => { SettingsPanel.Ensure(); SettingsPanel.Instance.Show(); });
            Core.UIFactory.CreateButton(_root.transform, "QuitBtn", Core.Localization.T("menu.quit"),
                new Vector2(-160, -250), new Vector2(280, 56), OnQuit);

            // 当前角色提示（M6.2）
            Core.UIFactory.CreateText(_root.transform, "CurrentCharacter",
                Core.Localization.T("menu.character") + "：" +
                Core.Localization.T(Player.CharacterManager.Selected.nameKey),
                new Vector2(160, 40), 18, TextAnchor.MiddleLeft, new Vector2(280, 40));

            RefreshRecords();
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
