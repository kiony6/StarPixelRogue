using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 选角面板（M6.2）：两个角色的属性/特性对比，点击即选（持久化到存档）。
    /// 语言切换整体重建；主菜单通过「角色」按钮打开。
    /// </summary>
    public class CharacterSelectPanel : Core.MonoSingleton<CharacterSelectPanel>
    {
        private GameObject _root;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("CharacterSelectPanel").AddComponent<CharacterSelectPanel>();
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
            Build();                       // 打开时刷新选中标记
            if (_root != null) _root.SetActive(true);
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
            var canvas = Core.UIFactory.CreateCanvas("CharacterSelectCanvas");
            _root = canvas.gameObject;

            var panel = Core.UIFactory.CreatePanel(_root.transform, "Panel", Vector2.zero,
                new Vector2(860, 560), new Color(0.08f, 0.09f, 0.14f, 0.97f));

            Core.UIFactory.CreateText(panel.transform, "Title", Core.Localization.T("char.title"),
                new Vector2(0, 248), 32);

            AddCard(panel.transform, Player.CharacterManager.ById(0), new Vector2(-210, 0));
            AddCard(panel.transform, Player.CharacterManager.ById(1), new Vector2(210, 0));

            Core.UIFactory.CreateButton(panel.transform, "CloseBtn", Core.Localization.T("settings.close"),
                new Vector2(0, 248), new Vector2(120, 40), Hide, 18);
        }

        private void AddCard(Transform parent, Player.CharacterDef def, Vector2 center)
        {
            bool selected = Player.CharacterManager.SelectedId == def.id;
            var card = Core.UIFactory.CreatePanel(parent, "Card" + def.id, center,
                new Vector2(360, 420),
                selected ? new Color(0.16f, 0.24f, 0.18f, 1f) : new Color(0.11f, 0.12f, 0.18f, 1f));

            Core.UIFactory.CreateText(card.transform, "Name",
                (selected ? "◉ " : "○ ") + Core.Localization.T(def.nameKey),
                new Vector2(0, 176), 26);

            Core.UIFactory.CreateText(card.transform, "Desc", Core.Localization.T(def.descKey),
                new Vector2(0, 132), 15, TextAnchor.MiddleCenter, new Vector2(320, 56));
            Core.UIFactory.CreateText(card.transform, "Trait", Core.Localization.T(def.traitKey),
                new Vector2(0, 92), 15, TextAnchor.MiddleCenter, new Vector2(320, 30));

            // 属性摘要（相对基准的百分比）
            Core.UIFactory.CreateText(card.transform, "Stats",
                Core.Localization.T("char.stats",
                    Pct(def.hpMult), Pct(def.speedMult), Pct(def.damageMult), Pct(def.areaMult)),
                new Vector2(0, 46), 14, TextAnchor.MiddleCenter, new Vector2(330, 30));

            Core.UIFactory.CreateText(card.transform, "Weapon",
                Core.Localization.T("char.start_weapon") + "：" +
                Combat.UpgradeCatalog.CreateById(def.startingWeaponId).DisplayName,
                new Vector2(0, 12), 14, TextAnchor.MiddleCenter, new Vector2(330, 26));

            Core.UIFactory.CreateButton(card.transform, "SelectBtn",
                selected ? Core.Localization.T("char.selected") : Core.Localization.T("char.select"),
                new Vector2(0, -160), new Vector2(200, 52),
                () =>
                {
                    Player.CharacterManager.SelectedId = def.id;
                    Build();               // 刷新选中标记
                    Core.Telemetry.Log("character_select", "id", def.id);
                }, selected ? 20 : 18,
                selected ? new Color(0.20f, 0.45f, 0.28f, 1f) : new Color(0.25f, 0.27f, 0.36f, 1f));
        }

        private static int Pct(float mult) => Mathf.RoundToInt(mult * 100f);
    }
}
