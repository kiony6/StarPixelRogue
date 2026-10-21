using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 排行榜面板（M54）：本机榜单 Top 8 + 当前账号的名次与最佳一局。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 榜上写的是「本机榜单」而不是「全球榜」：<see cref="Core.RankStore"/> 没有服务端，
    /// 看不见别人的成绩。名字说得比实际大一点，玩家点开就发现只有自己一个人，
    /// 那是比"没有排行榜"更糟的体验。
    /// </para>
    /// <para>
    /// 节点沿用其它面板的规矩（<c>RankCanvas / Panel / CloseBtn</c>）：
    /// 探针里"进面板—测一遍—点关闭退出"这一段是三块面板共用的，路径不一致就用不上。
    /// </para>
    /// </remarks>
    public class RankPanel : Core.MonoSingleton<RankPanel>
    {
        /// <summary>榜上最多列几行。</summary>
        public const int VisibleRows = 8;

        private GameObject _root;
        private Text _myRankValue;
        private Text _myBestValue;
        private Text _emptyHint;
        private readonly List<Row> _rows = new List<Row>();

        private struct Row
        {
            public Text rank;
            public Text account;
            public Text wave;
            public Text level;
            public Text time;
        }

        public GameObject PanelRoot => _root;

        public bool IsVisible => _root != null && _root.activeSelf;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("RankPanel").AddComponent<RankPanel>();
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
            // 必须紧接着刷一遍：名次与最佳是"值"，不是"标签"，重建只把标签换成了新语言，
            // 值还停在旧语言（"未上榜"会一直是中文）。M34 漏的就是这一类。
            Refresh();
            _root.SetActive(was);
        }

        public void Show()
        {
            Core.RankStore.Ensure();
            if (_root == null) Build();
            _root.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        private void Build()
        {
            if (_root != null) Core.UIFactory.Destroy(_root);

            var canvas = Core.UIFactory.CreateCanvas("RankCanvas", null,
                Core.UIFactory.ModalSortingOrder);
            _root = canvas.gameObject;

            var panel = Core.UIFactory.CreateRoundPanel(_root.transform, "Panel",
                new Vector2(0, 0), new Vector2(740, 600), Core.UIFactory.Palette.Deep);

            Core.UIFactory.CreateTitle(panel.transform, "Title", Core.Localization.T("rank.title"),
                new Vector2(0, 248), 34, new Vector2(660, 52), Core.UIFactory.Palette.Gold);
            var note = Core.UIFactory.CreateText(panel.transform, "Note",
                Core.Localization.T("rank.note"), new Vector2(0, 210), 16);
            note.color = Core.UIFactory.Palette.TextDim;

            // 我的名次 / 我的最佳：先看自己，再看别人——榜的价值一半在"我排第几"
            _myRankValue = StatLine(panel.transform, "MyRank", Core.Localization.T("rank.my_rank"), 168);
            _myBestValue = StatLine(panel.transform, "MyBest", Core.Localization.T("rank.my_best"), 132);

            Core.UIFactory.CreateDivider(panel.transform, "Rule", new Vector2(0, 104), 660,
                Core.UIFactory.Tint(Core.UIFactory.Palette.Gold, 1f, 0.45f));

            // 表头
            float hy = 76;
            Head(panel.transform, "HeadRank", Core.Localization.T("rank.col_rank"), ColX(0), hy, TextAnchor.MiddleLeft);
            Head(panel.transform, "HeadAccount", Core.Localization.T("rank.col_account"), ColX(1), hy, TextAnchor.MiddleLeft);
            Head(panel.transform, "HeadWave", Core.Localization.T("rank.col_wave"), ColX(2), hy, TextAnchor.MiddleRight);
            Head(panel.transform, "HeadLevel", Core.Localization.T("rank.col_level"), ColX(3), hy, TextAnchor.MiddleRight);
            Head(panel.transform, "HeadTime", Core.Localization.T("rank.col_time"), ColX(4), hy, TextAnchor.MiddleRight);

            _rows.Clear();
            float y = 40;
            for (int i = 0; i < VisibleRows; i++)
            {
                var row = new Row
                {
                    rank = Cell(panel.transform, "Row" + i + "Rank", ColX(0), y, TextAnchor.MiddleLeft),
                    account = Cell(panel.transform, "Row" + i + "Account", ColX(1), y, TextAnchor.MiddleLeft),
                    wave = Cell(panel.transform, "Row" + i + "Wave", ColX(2), y, TextAnchor.MiddleRight),
                    level = Cell(panel.transform, "Row" + i + "Level", ColX(3), y, TextAnchor.MiddleRight),
                    time = Cell(panel.transform, "Row" + i + "Time", ColX(4), y, TextAnchor.MiddleRight),
                };
                _rows.Add(row);
                y -= 38;
            }

            _emptyHint = Core.UIFactory.CreateText(panel.transform, "EmptyHint",
                Core.Localization.T("rank.empty"), new Vector2(0, 0), 18,
                TextAnchor.MiddleCenter, new Vector2(660, 40));
            _emptyHint.color = Core.UIFactory.Palette.TextDim;

            Core.UIFactory.CreateButton(panel.transform, "CloseBtn", Core.Localization.T("common.close"),
                new Vector2(0, -252), new Vector2(240, 52), Hide, 20,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.Danger);
        }

        /// <summary>五列的 x 坐标。</summary>
        private static float[] _cols = { -312f, -252f, 60f, 190f, 306f };
        private static float ColX(int i) => _cols[i];

        private static Text StatLine(Transform parent, string node, string label, float y)
        {
            var lab = Core.UIFactory.CreateText(parent, node + "Label", label,
                new Vector2(-160, y), 18, TextAnchor.MiddleRight, new Vector2(220, 32));
            lab.color = Core.UIFactory.Palette.TextDim;
            var val = Core.UIFactory.CreateText(parent, node + "Value", "-",
                new Vector2(10, y), 18, TextAnchor.MiddleLeft, new Vector2(380, 32));
            val.color = Core.UIFactory.Palette.Text;
            return val;
        }

        private static Text Head(Transform parent, string node, string label, float x, float y, TextAnchor anchor)
        {
            var t = Core.UIFactory.CreateText(parent, node, label, new Vector2(x, y), 16, anchor,
                new Vector2(150, 28));
            t.color = Core.UIFactory.Palette.TextDim;
            return t;
        }

        private static Text Cell(Transform parent, string node, float x, float y, TextAnchor anchor)
        {
            var t = Core.UIFactory.CreateText(parent, node, "", new Vector2(x, y), 17, anchor,
                new Vector2(150, 32));
            t.color = Core.UIFactory.Palette.Text;
            return t;
        }

        /// <summary>按当前榜单刷新每一行。</summary>
        public void Refresh()
        {
            if (_root == null) return;
            var store = Core.RankStore.Instance;
            var account = AccountGate.Account;

            if (store != null && !string.IsNullOrEmpty(account))
            {
                int r = store.RankOf(account);
                _myRankValue.text = r > 0 ? r.ToString() : Core.Localization.T("rank.unranked");
                var best = store.BestFor(account);
                _myBestValue.text = best != null
                    ? Core.Localization.T("rank.stat", best.wave, best.level,
                        Core.SaveManager.FormatTime(best.time))
                    : Core.Localization.T("menu.none");
            }
            else
            {
                _myRankValue.text = Core.Localization.T("rank.unranked");
                _myBestValue.text = Core.Localization.T("menu.none");
            }

            var top = store != null ? store.Top(VisibleRows) : new List<Core.RankStore.Entry>();
            if (_emptyHint != null) _emptyHint.gameObject.SetActive(top.Count == 0);

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool has = i < top.Count;
                var e = has ? top[i] : null;
                row.rank.text = has ? (i + 1).ToString() : "";
                row.account.text = has ? e.account : "";
                row.wave.text = has ? e.wave.ToString() : "";
                row.level.text = has ? e.level.ToString() : "";
                row.time.text = has ? Core.SaveManager.FormatTime(e.time) : "";

                // 自己那一行点个色：榜上找自己是最常见的一件事，不该靠逐行读名字
                var c = has && string.Equals(e.account, account, System.StringComparison.Ordinal)
                    ? Core.UIFactory.Palette.Gold
                    : Core.UIFactory.Palette.Text;
                row.rank.color = row.account.color = row.wave.color = row.level.color = row.time.color = c;
            }
        }
    }
}
