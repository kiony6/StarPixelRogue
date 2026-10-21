using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 图鉴面板（M6.4）：武器 / 敌人 / Boss / 组合技 / 统计五个标签页。
    /// 数据来自 <see cref="Core.SaveManager"/> 的发现记录（本机存档，仅枚举与数值）。
    /// 语言切换整体重建。
    /// </summary>
    public class CodexPanel : Core.MonoSingleton<CodexPanel>
    {
        private enum Tab { Weapons, Enemies, Boss, Combos, Stats }

        private GameObject _root;
        private Tab _tab = Tab.Weapons;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("CodexPanel").AddComponent<CodexPanel>();
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
            Build();                       // 打开时刷新内容（可能又有新发现）
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
            // M51：同 SettingsPanel / CharacterSelectPanel——显式站在模态层。
            var canvas = Core.UIFactory.CreateCanvas("CodexCanvas", null,
                Core.UIFactory.ModalSortingOrder);
            _root = canvas.gameObject;

            var panel = Core.UIFactory.CreatePanel(_root.transform, "Panel", Vector2.zero,
                new Vector2(920, 640), new Color(0.08f, 0.09f, 0.14f, 0.97f));

            Core.UIFactory.CreateText(panel.transform, "Title", Core.Localization.T("codex.title"),
                new Vector2(0, 288), 32);

            // 标签按钮行
            AddTab(panel.transform, "Weapons", Tab.Weapons, -330);
            AddTab(panel.transform, "Enemies", Tab.Enemies, -210);
            AddTab(panel.transform, "Boss", Tab.Boss, -90);
            AddTab(panel.transform, "Combos", Tab.Combos, 30);
            AddTab(panel.transform, "Stats", Tab.Stats, 150);

            var content = Core.UIFactory.CreatePanel(panel.transform, "Content", new Vector2(0, -40),
                new Vector2(860, 440), new Color(0.05f, 0.06f, 0.10f, 1f));
            BuildContent(content.transform);

            Core.UIFactory.CreateButton(panel.transform, "CloseBtn", Core.Localization.T("settings.close"),
                new Vector2(0, 288), new Vector2(120, 40), Hide, 18);
        }

        private void AddTab(Transform parent, string key, Tab tab, float x)
        {
            bool active = _tab == tab;
            Core.UIFactory.CreateButton(parent, "Tab" + key,
                Core.Localization.T("codex.tab_" + key.ToLowerInvariant()),
                new Vector2(x, 244), new Vector2(112, 44),
                () => { _tab = tab; Build(); }, 16,
                active ? new Color(0.20f, 0.45f, 0.28f, 1f) : new Color(0.20f, 0.21f, 0.28f, 1f));
        }

        private void BuildContent(Transform parent)
        {
            switch (_tab)
            {
                case Tab.Weapons: BuildWeapons(parent); break;
                case Tab.Enemies: BuildEnemies(parent); break;
                case Tab.Boss: BuildBoss(parent); break;
                case Tab.Combos: BuildCombos(parent); break;
                case Tab.Stats: BuildStats(parent); break;
            }
        }

        // —— 武器：9 个，未发现显示问号 ——
        private void BuildWeapons(Transform parent)
        {
            var save = Core.SaveManager.Instance;
            float y = 190;
            foreach (int id in Combat.UpgradeCatalog.AllWeaponIds)
            {
                bool found = save != null && Contains(save.Data.discoveredWeapons, id);
                var def = Combat.UpgradeCatalog.CreateById(id);
                string line = found
                    ? def.DisplayName + " — " + Combat.UpgradeCatalog.WeaponDesc(def)
                    : Core.Localization.T("codex.locked");
                var t = Core.UIFactory.CreateText(parent, "W" + id, line,
                    new Vector2(0, y), 16, TextAnchor.MiddleLeft, new Vector2(800, 30));
                if (!found) t.color = new Color(0.55f, 0.55f, 0.6f);
                y -= 40;
            }
        }

        // —— 敌人：3 种 + 精英 ——
        private void BuildEnemies(Transform parent)
        {
            var save = Core.SaveManager.Instance;
            var entries = new (int kind, string key)[]
            {
                (0, "codex.enemy_chaser"),
                (1, "codex.enemy_runner"),
                (2, "codex.enemy_tank"),
                (3, "codex.enemy_elite"),
            };
            float y = 140;
            foreach (var (kind, key) in entries)
            {
                bool found = save != null && Contains(save.Data.discoveredEnemies, kind);
                var t = Core.UIFactory.CreateText(parent, "E" + kind,
                    found ? Core.Localization.T(key) : Core.Localization.T("codex.locked"),
                    new Vector2(0, y), 20, TextAnchor.MiddleCenter, new Vector2(700, 36));
                if (!found) t.color = new Color(0.55f, 0.55f, 0.6f);
                y -= 60;
            }
        }

        // —— Boss ——
        private void BuildBoss(Transform parent)
        {
            var save = Core.SaveManager.Instance;
            int defeats = save != null ? save.Data.bossesDefeated : 0;
            bool found = defeats > 0;

            var name = Core.UIFactory.CreateText(parent, "BossName",
                found ? Core.Localization.T("codex.boss_name") : Core.Localization.T("codex.locked"),
                new Vector2(0, 140), 26);
            if (!found) name.color = new Color(0.55f, 0.55f, 0.6f);

            Core.UIFactory.CreateText(parent, "BossDesc", Core.Localization.T("codex.boss_desc"),
                new Vector2(0, 90), 15, TextAnchor.MiddleCenter, new Vector2(780, 50));
            Core.UIFactory.CreateText(parent, "BossProgress",
                Core.Localization.T("codex.boss_progress", defeats),
                new Vector2(0, 30), 18);
        }

        // —— 组合技：4 条 ——
        private void BuildCombos(Transform parent)
        {
            var save = Core.SaveManager.Instance;
            var entries = new (string key, string locKey)[]
            {
                ("fire_storm", "codex.combo_fire_storm"),
                ("ice_earth", "codex.combo_ice_earth"),
                ("lightning_fire", "codex.combo_lightning_fire"),
                ("holy_fire", "codex.combo_holy_fire"),
            };
            float y = 140;
            foreach (var (key, locKey) in entries)
            {
                bool found = save != null && Contains(save.Data.discoveredCombos, key);
                var t = Core.UIFactory.CreateText(parent, "C" + key,
                    found ? Core.Localization.T(locKey) : Core.Localization.T("codex.locked"),
                    new Vector2(0, y), 17, TextAnchor.MiddleCenter, new Vector2(800, 32));
                if (!found) t.color = new Color(0.55f, 0.55f, 0.6f);
                y -= 56;
            }
        }

        // —— 统计 ——
        private void BuildStats(Transform parent)
        {
            var save = Core.SaveManager.Instance;
            var d = save != null ? save.Data : new Core.SaveManager.Record();
            float y = 190;

            AddStatLine(parent, "S1", Core.Localization.T("codex.stat_runs"), d.totalRuns.ToString(), ref y);
            AddStatLine(parent, "S1b", Core.Localization.T("codex.stat_clears"), d.totalClears.ToString(), ref y);   // M8 方案 A
            AddStatLine(parent, "S2", Core.Localization.T("codex.stat_kills"), d.totalKills.ToString(), ref y);
            AddStatLine(parent, "S3", Core.Localization.T("codex.stat_time"), Core.SaveManager.FormatTime(d.totalTime), ref y);
            AddStatLine(parent, "S4", Core.Localization.T("codex.stat_best_wave"), d.bestWave.ToString(), ref y);
            AddStatLine(parent, "S5", Core.Localization.T("codex.stat_bosses"), d.bossesDefeated.ToString(), ref y);
            AddStatLine(parent, "S6", Core.Localization.T("codex.stat_weapons"),
                (d.discoveredWeapons?.Length ?? 0) + " / " + Combat.UpgradeCatalog.AllWeaponIds.Length, ref y);
            AddStatLine(parent, "S7", Core.Localization.T("codex.stat_enemies"),
                (d.discoveredEnemies?.Length ?? 0) + " / 4", ref y);
            AddStatLine(parent, "S8", Core.Localization.T("codex.stat_combos"),
                (d.discoveredCombos?.Length ?? 0) + " / 4", ref y);

            // 各角色使用次数
            for (int i = 0; i < Player.CharacterManager.Count; i++)
            {
                int runs = (d.characterRuns != null && i < d.characterRuns.Length) ? d.characterRuns[i] : 0;
                AddStatLine(parent, "SC" + i,
                    Core.Localization.T(Player.CharacterManager.ById(i).nameKey), runs.ToString(), ref y);
            }
        }

        private void AddStatLine(Transform parent, string name, string label, string value, ref float y)
        {
            Core.UIFactory.CreateText(parent, name + "L", label, new Vector2(-180, y), 16,
                TextAnchor.MiddleLeft, new Vector2(320, 30));
            Core.UIFactory.CreateText(parent, name + "V", value, new Vector2(180, y), 16,
                TextAnchor.MiddleRight, new Vector2(200, 30));
            y -= 34;
        }

        private static bool Contains(int[] arr, int v)
        {
            if (arr == null) return false;
            foreach (var x in arr) if (x == v) return true;
            return false;
        }

        private static bool Contains(string[] arr, string v)
        {
            if (arr == null) return false;
            foreach (var x in arr) if (x == v) return true;
            return false;
        }
    }
}
