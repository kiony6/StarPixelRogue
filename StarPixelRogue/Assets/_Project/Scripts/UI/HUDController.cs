using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// HUD：HP / 经验 / 等级 / 波次 / 击杀 / 计时，以及 M3 的 Boss 血条与区域提示。
    /// 由 GameBuilder 在运行时自动创建，无需手动搭 UI。
    /// M5：文案经 <see cref="Core.Localization"/> 本地化；一局结束时汇总数据广播
    /// <see cref="Core.EventBus.OnRunEnded"/>（结算面板/存档/埋点消费）。
    /// M8 方案 A：存活满 <see cref="ClearTime"/> 秒即通关（victory），否则死亡结束。
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        /// <summary>M8 方案 A：通关所需存活秒数（与平衡模拟观测窗一致）。</summary>
        private const float ClearTime = 900f;
        public Slider hpSlider;
        public Slider xpSlider;
        public Slider bossSlider;
        public GameObject bossBarRoot;
        public Text waveText;
        public Text killText;
        public Text timeText;
        public Text levelText;
        public Text zoneText;
        public Text weaponText;
        public Text modeText;      // M4：联机模式标识（单机/房主/客户端）
        public Text comboToast;    // M7：组合技激活提示
        public Image lowHpOverlay; // M7：低血量全屏红晕警示

        private int _kills;
        private int _wave = 1;
        private int _level = 1;
        private int _zone;             // M33：切语言时要能重画出区域提示，故把值记下来
        private float _time;
        private float _zoneTimer;
        private float _comboTimer;     // M7
        private bool _runEnded;

        void Awake()
        {
            Core.EventBus.OnEnemyKilled += OnEnemyKilled;
            Core.EventBus.OnPlayerHealthRatio += OnPlayerHealthRatio;
            Core.EventBus.OnWaveChanged += OnWaveChanged;
            Core.EventBus.OnXpChanged += OnXpChanged;
            Core.EventBus.OnLevelUp += OnLevelUp;
            Core.EventBus.OnGameOver += OnGameOver;
            Core.EventBus.OnWeaponsChanged += RefreshWeapons;
            Core.EventBus.OnBossSpawned += OnBossSpawned;
            Core.EventBus.OnBossHp += OnBossHp;
            Core.EventBus.OnBossDefeated += OnBossDefeated;
            Core.EventBus.OnZoneChanged += OnZoneChanged;
            Core.EventBus.OnComboActivated += OnComboActivated;   // M7
            Core.Localization.LanguageChanged += RefreshLanguage; // M33：切语言要重画非每帧文本
            RefreshWeapons();
        }

        void OnDestroy()
        {
            // M4.5：断线拆世界后按同样模式重建 HUD，旧实例必须退订，否则双份计数
            Core.EventBus.OnEnemyKilled -= OnEnemyKilled;
            Core.EventBus.OnPlayerHealthRatio -= OnPlayerHealthRatio;
            Core.EventBus.OnWaveChanged -= OnWaveChanged;
            Core.EventBus.OnXpChanged -= OnXpChanged;
            Core.EventBus.OnLevelUp -= OnLevelUp;
            Core.EventBus.OnGameOver -= OnGameOver;
            Core.EventBus.OnWeaponsChanged -= RefreshWeapons;
            Core.EventBus.OnBossSpawned -= OnBossSpawned;
            Core.EventBus.OnBossHp -= OnBossHp;
            Core.EventBus.OnBossDefeated -= OnBossDefeated;
            Core.EventBus.OnZoneChanged -= OnZoneChanged;
            Core.EventBus.OnComboActivated -= OnComboActivated;
            // M33：LanguageChanged 是**静态**事件，HUD 每局由 GameBuilder 新建一个，
            // 不退订的话旧实例会一直挂着（M29 教训：死订阅不只是泄漏，
            // 还会让已销毁对象的回调抛异常、打断整条委托链）。
            Core.Localization.LanguageChanged -= RefreshLanguage;
        }

        private void OnEnemyKilled(int _) { _kills++; }
        private void OnPlayerHealthRatio(float r) { if (hpSlider) hpSlider.value = r; }
        private void OnWaveChanged(int w) { _wave = w; if (waveText) waveText.text = Core.Localization.T("hud.wave", w); }
        private void OnXpChanged(float r) { if (xpSlider) xpSlider.value = r; }
        private void OnLevelUp(int lv)
        {
            _level = lv;
            if (levelText) levelText.text = Core.Localization.T("hud.level", lv);
        }
        private void OnBossSpawned(float hp, float max)
        {
            if (bossBarRoot) bossBarRoot.SetActive(true);
            if (bossSlider) bossSlider.value = 1f;
        }
        private void OnBossHp(float r) { if (bossSlider) bossSlider.value = r; }
        private void OnBossDefeated() { if (bossBarRoot) bossBarRoot.SetActive(false); }
        private void OnZoneChanged(int z)
        {
            _zone = z;
            if (zoneText)
            {
                zoneText.text = Core.Localization.T("hud.zone", z);
                zoneText.color = new Color(1f, 1f, 1f, 1f);
                _zoneTimer = 2.5f;
            }
        }

        /// <summary>
        /// M33：切语言后重画「非每帧重绘」的那批文本。
        ///
        /// Localization 的注释原本假设「HUD 的波次/时间文本每帧重绘，自动生效」——
        /// 这个假设只对 timeText / killText 成立（它们在 Update 里每帧重画）。
        /// 波次 / 等级 / 区域 / 武器名只在各自事件触发时才赋值，切语言后
        /// 它们会一直停在旧语言，直到下一次事件到来。
        /// 实测：开局后切成英文，HUD 上仍是「波次 1」，要等到进下一波才变英文。
        ///
        /// M34 补两处：
        /// · `modeText`（联机模式标识）由 GameBuilder 一次性写入，**任何事件都不再碰它**，
        ///   所以 M33 这一轮也没带出来——它只在联机下 SetActive(true)，单机探针看不见。
        /// · `comboToast`：切语言时若正挂着一条旧语言的组合技提示，它会继续显示到淡出为止；
        ///   清掉比留着正确（提示是一次性的，下次触发自然按新语言拼）。
        /// </summary>
        private void RefreshLanguage()
        {
            if (waveText) waveText.text = Core.Localization.T("hud.wave", _wave);
            if (levelText) levelText.text = Core.Localization.T("hud.level", _level);
            if (zoneText && _zone > 0) zoneText.text = Core.Localization.T("hud.zone", _zone);
            // M34：模式标识没有对应事件，只能在这里补；ModeLabel() 内部已走本地化表。
            if (modeText) modeText.text = Net.NetSession.ModeLabel();
            // M34：清掉可能正挂着的旧语言组合技提示（并停掉淡出计时，免得改完立刻被清）
            if (comboToast) { comboToast.text = ""; _comboTimer = 0f; }
            RefreshWeapons();      // 武器名走 SkillDef.DisplayName（内含 locKey），需重建
        }

        /// <summary>M7：组合技激活提示（金色高亮 2.5s 后淡出）。</summary>
        private void OnComboActivated(string key)
        {
            if (comboToast == null) return;
            comboToast.text = Core.Localization.T("hud.combo", Core.Localization.T("combo." + key));
            comboToast.color = new Color(1f, 0.85f, 0.3f, 1f);
            _comboTimer = 2.5f;
        }

        void Update()
        {
            _time += Time.deltaTime;
            if (timeText) timeText.text = Core.Localization.T("hud.time", _time.ToString("F1"));
            if (killText) killText.text = Core.Localization.T("hud.kills", _kills);

            // M8 方案 A：存活满 ClearTime 即通关（本地计时，联机各客户端各自判定，与死亡同步不冲突）
            if (!_runEnded && _time >= ClearTime)
            {
                OnVictory();
                return;
            }

            if (_zoneTimer > 0f)
            {
                _zoneTimer -= Time.deltaTime;
                if (zoneText)
                {
                    var c = zoneText.color;
                    c.a = Mathf.Clamp01(_zoneTimer / 1f);
                    zoneText.color = c;
                }
            }

            // M7：组合技提示淡出
            if (_comboTimer > 0f)
            {
                _comboTimer -= Time.deltaTime;
                if (comboToast)
                {
                    var c = comboToast.color;
                    c.a = Mathf.Clamp01(_comboTimer / 1f);
                    comboToast.color = c;
                }
            }

            // M7：低血量红晕——HP<30% 时脉冲警示，恢复后淡出
            if (lowHpOverlay != null && hpSlider != null)
            {
                var c = lowHpOverlay.color;
                float target = hpSlider.value < 0.3f && !_runEnded
                    ? 0.10f + 0.06f * Mathf.Sin(Time.unscaledTime * 7f)
                    : 0f;
                c.a = Mathf.MoveTowards(c.a, target, Time.unscaledDeltaTime * 0.8f);
                lowHpOverlay.color = c;
            }
        }

        /// <summary>刷新武器列表面板（M3.6：让玩家随时看到自己的 build）。</summary>
        private void RefreshWeapons()
        {
            if (weaponText == null) return;
            var p = Player.PlayerController.Instance;
            if (p == null || p.Weapons == null || p.Weapons.Count == 0)
            {
                weaponText.text = "";
                return;
            }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Weapons.Count; i++)
            {
                var w = p.Weapons[i];
                if (w == null || w.def == null) continue;
                if (sb.Length > 0) sb.Append("   ");
                sb.Append(w.def.DisplayName).Append(" Lv").Append(w.level);
            }
            weaponText.text = sb.ToString();
        }

        private void OnGameOver()
        {
            if (_runEnded) return;                     // 双路径（本地死亡/联机血量同步）只结算一次
            _runEnded = true;
            if (timeText) timeText.text = "GAME OVER  " + _time.ToString("F1");
            if (bossBarRoot) bossBarRoot.SetActive(false);
            if (lowHpOverlay) { var c = lowHpOverlay.color; c.a = 0f; lowHpOverlay.color = c; }
            if (comboToast) { var c = comboToast.color; c.a = 0f; comboToast.color = c; }
            Core.EventBus.RaiseRunEnded(_wave, _time, _kills, _level, false);
        }

        /// <summary>M8 方案 A：达标通关。与死亡同一结算出口，仅文案与 victory 标志不同。</summary>
        private void OnVictory()
        {
            if (_runEnded) return;                     // 与死亡互斥，只结算一次
            _runEnded = true;
            if (timeText) timeText.text = Core.Localization.T("hud.clear", ClearTime.ToString("F1"));
            if (bossBarRoot) bossBarRoot.SetActive(false);
            if (lowHpOverlay) { var c = lowHpOverlay.color; c.a = 0f; lowHpOverlay.color = c; }
            if (comboToast) { var c = comboToast.color; c.a = 0f; comboToast.color = c; }
            Core.EventBus.RaiseRunEnded(_wave, _time, _kills, _level, true);
        }
    }
}
