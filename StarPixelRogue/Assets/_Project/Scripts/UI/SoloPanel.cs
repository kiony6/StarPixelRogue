using UnityEngine;

namespace StarPixel.UI
{
    /// <summary>
    /// 单人模式二级页（M54）：角色 / 图鉴 / 开始游戏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么要多出这一层：主菜单收敛到四颗按钮之后，「角色」「图鉴」就没有位置了。
    /// 直接删掉它们的入口等于让一整个系统（角色乘区、图鉴收集）变成死代码，
    /// 所以收在这里——它们是"单人模式开始前的事"，语义上也站得住。
    /// </para>
    /// <para>
    /// <b>让位</b>：打开角色 / 图鉴时本页会先隐藏自己。子面板与本页同为模态层
    /// （M51 已经吃过一次"两块画布同层 → 谁压谁听 Unity 内部排序"的亏），
    /// 不隐藏就会变成两块互不相让的模态叠在一起，子面板上的按钮可能一颗都点不到。
    /// 子面板关闭时它自己不会通知任何人，所以这里用 Update 观察"子面板没了"再回到前台——
    /// 这不是最优雅的做法，但它不要求改动两个既有面板的接口。
    /// </para>
    /// </remarks>
    public class SoloPanel : Core.MonoSingleton<SoloPanel>
    {
        private GameObject _root;
        private bool _yielded;          // 已让位给子面板
        private MonoBehaviour _child;   // 正在让位给谁

        public GameObject PanelRoot => _root;

        public bool IsVisible => _root != null && _root.activeSelf;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("SoloPanel").AddComponent<SoloPanel>();
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

        private void Rebuild()
        {
            if (_root == null) return;
            bool was = _root.activeSelf;
            Build();
            _root.SetActive(was);
        }

        void Update()
        {
            // 子面板关掉后回到前台：不这么做，玩家关掉图鉴就站在一片空菜单前。
            if (!_yielded || _root == null) return;
            bool childGone = _child == null || _child == null
                || !IsPanelVisible(_child);
            if (!childGone) return;
            _yielded = false;
            _child = null;
            _root.SetActive(true);
            MainMenu.Instance?.RefreshRecords();
        }

        private static bool IsPanelVisible(MonoBehaviour p)
        {
            if (p == null) return false;
            var cs = p as CharacterSelectPanel;
            if (cs != null) return cs.IsVisible;
            var cx = p as CodexPanel;
            if (cx != null) return cx.IsVisible;
            return false;
        }

        public void Show()
        {
            if (_root == null) Build();
            _yielded = false;
            _child = null;
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        private void Build()
        {
            if (_root != null) Core.UIFactory.Destroy(_root);

            var canvas = Core.UIFactory.CreateCanvas("SoloCanvas", null,
                Core.UIFactory.ModalSortingOrder);
            _root = canvas.gameObject;

            var panel = Core.UIFactory.CreateRoundPanel(_root.transform, "Panel",
                new Vector2(0, 0), new Vector2(460, 420), Core.UIFactory.Palette.Deep);

            Core.UIFactory.CreateTitle(panel.transform, "Title", Core.Localization.T("menu.solo"),
                new Vector2(0, 162), 32, new Vector2(400, 52), Core.UIFactory.Palette.Vital);

            Core.UIFactory.CreateButton(panel.transform, "CharacterBtn", Core.Localization.T("menu.character"),
                new Vector2(0, 76), new Vector2(320, 58), OnCharacter, 21,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.AccentAlt);
            Core.UIFactory.CreateButton(panel.transform, "CodexBtn", Core.Localization.T("menu.codex"),
                new Vector2(0, 4), new Vector2(320, 58), OnCodex, 21,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Accent);
            Core.UIFactory.CreateButton(panel.transform, "StartBtn", Core.Localization.T("menu.start"),
                new Vector2(0, -68), new Vector2(320, 66), OnStart, 24,
                Core.UIFactory.Tint(Core.UIFactory.Palette.Vital, 0.42f, 0.95f),
                Core.UIFactory.Palette.Vital);
            Core.UIFactory.CreateButton(panel.transform, "CloseBtn", Core.Localization.T("settings.close"),
                new Vector2(0, -150), new Vector2(240, 50), Hide, 19,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Danger);
        }

        private void OnCharacter()
        {
            CharacterSelectPanel.Ensure();
            YieldTo(CharacterSelectPanel.Instance);
            CharacterSelectPanel.Instance.Show();
        }

        private void OnCodex()
        {
            CodexPanel.Ensure();
            YieldTo(CodexPanel.Instance);
            CodexPanel.Instance.Show();
        }

        private void YieldTo(MonoBehaviour child)
        {
            _child = child;
            _yielded = true;
            if (_root != null) _root.SetActive(false);
        }

        private void OnStart()
        {
            Core.Telemetry.Log(Core.Telemetry.EvRunStart,
                "mode", Net.NetSession.ModeLabel());
            Hide();
            MainMenu.Instance?.Hide();
            GameBuilder.TeardownWorld();
            GameBuilder.Build();
        }
    }
}
