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

            // 战绩面板（M53：圆角框 + 标题下的分隔线；M54 挪到左半区，把右半区整个让给四颗主按钮）
            var panel = Core.UIFactory.CreateRoundPanel(_root.transform, "Records", new Vector2(-330, 0),
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

            // —— M54：主按钮收敛为四颗 ——
            // 单人模式 / 联机大厅 / 排行榜 / 设置。此前是五颗（还带了角色、图鉴、退出），
            // 「角色」「图鉴」挪进了单人模式的二级页，「退出」交给窗口右上角——
            // 主菜单上摆七颗按钮的结果是：玩家第一眼找不到"开始"。
            // 这四颗每一颗都挂了真回调（见下方四个 On*），没有占位。
            Core.UIFactory.CreateButton(_root.transform, "SoloBtn", Core.Localization.T("menu.solo"),
                new Vector2(180, 70), new Vector2(320, 68), OnSolo, 24,
                Core.UIFactory.Tint(Core.UIFactory.Palette.Vital, 0.42f, 0.95f),
                Core.UIFactory.Palette.Vital);
            Core.UIFactory.CreateButton(_root.transform, "LobbyBtn", Core.Localization.T("menu.lobby"),
                new Vector2(180, -14), new Vector2(320, 58), OnLobby, 21,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Accent);
            Core.UIFactory.CreateButton(_root.transform, "RankBtn", Core.Localization.T("menu.rank"),
                new Vector2(180, -88), new Vector2(320, 58), OnRank, 21,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Gold);
            Core.UIFactory.CreateButton(_root.transform, "SettingsBtn", Core.Localization.T("menu.settings"),
                new Vector2(180, -162), new Vector2(320, 58), OnSettings, 21,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.AccentAlt);

            // 当前角色提示（M6.2）
            // 注意：这一行的文本被 BuildProbe 的 CurrentCharacterText 逐字比对，
            // 因此它保持**纯文本**——不做渐变染色、也不加任何包装。
            var currentChar = Core.UIFactory.CreateText(_root.transform, "CurrentCharacter",
                Core.Localization.T("menu.character") + "：" +
                Core.Localization.T(Player.CharacterManager.Selected.nameKey),
                new Vector2(-330, -196), 18, TextAnchor.MiddleLeft, new Vector2(360, 40));
            currentChar.color = Core.UIFactory.Palette.TextDim;

            // 当前登录账号（M54）。同样保持纯文本：它是"我是谁"的答案，不是按钮——
            // 退出登录不在这里，因为主菜单上只允许存在上面那四颗按钮。
            var accountLine = Core.UIFactory.CreateText(_root.transform, "AccountInfo",
                Core.Localization.T("menu.account") + "：" + UI.AccountGate.Account,
                new Vector2(520, 268), 18, TextAnchor.MiddleRight, new Vector2(340, 40));
            accountLine.color = Core.UIFactory.Palette.Accent;

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
            SetText("AccountInfo", Core.Localization.T("menu.account") + "：" + UI.AccountGate.Account);
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

        /// <summary>单人模式（M54）：进二级页选角色 / 看图鉴 / 开局。</summary>
        private void OnSolo()
        {
            SoloPanel.Ensure();
            SoloPanel.Instance.Show();
        }

        /// <summary>
        /// 联机大厅（M54）。没有联机模块的构建里这颗按钮<b>不会变成摆设</b>：
        /// 它如实说明"这个版本没有联机"，而不是点了没反应——
        /// 没反应会被当成卡死，而谎称"正在连接"更糟。
        /// </summary>
        private void OnLobby()
        {
            if (Net.NetBackend.Instance != null)
            {
                Hide();
                GameBuilder.OpenLobby();
                return;
            }
            ShowNotice(Core.Localization.T("net.unavailable"));
        }

        private void OnRank()
        {
            RankPanel.Ensure();
            RankPanel.Instance.Show();
        }

        private void OnSettings()
        {
            SettingsPanel.Ensure();
            SettingsPanel.Instance.Show();
        }

        /// <summary>一次性说明弹窗（联机不可用等）。</summary>
        private void ShowNotice(string text)
        {
            // CreateModal 返回 (画布根, 全屏遮罩)：要拆就拆画布根。
            var (canvasRoot, maskRoot) = Core.UIFactory.CreateModal("MainMenuNotice",
                new Color(0f, 0f, 0f, 0.55f));
            var box = Core.UIFactory.CreateRoundPanel(maskRoot.transform, "Box",
                new Vector2(0, 0), new Vector2(520, 200), Core.UIFactory.Palette.Deep);
            var msg = Core.UIFactory.CreateText(box.transform, "Message", text,
                new Vector2(0, 28), 19, TextAnchor.MiddleCenter, new Vector2(460, 90));
            msg.color = Core.UIFactory.Palette.Text;
            Core.UIFactory.CreateButton(box.transform, "OkBtn", Core.Localization.T("net.unavailable_ok"),
                new Vector2(0, -52), new Vector2(200, 50),
                () => Core.UIFactory.Destroy(canvasRoot), 20,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Accent);
        }
    }
}
