using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 极简音效管理器（对应 TDD M3 音频反馈）。
    /// 从 Resources/Audio/&lt;name&gt; 加载 AudioClip 并 PlayOneShot；资源缺失时静默跳过，
    /// 因此不会因为缺少音频而报错或打断游戏。
    /// </summary>
    public class SfxManager : MonoSingleton<SfxManager>
    {
        private AudioSource _src;
        private string _lastName;
        private float _lastPlay = -999f;

        protected override void Awake()
        {
            base.Awake();
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            ApplyVolume();

            // 关键事件自动配音
            EventBus.OnEnemyKilled += OnEnemyKilled;
            EventBus.OnLevelUp += OnLevelUp;
            EventBus.OnGameOver += OnGameOver;
            EventBus.OnBossDefeated += OnBossDefeated;
            EventBus.OnPlayerHurt += OnPlayerHurt;
            EventBus.OnComboActivated += OnComboActivated;   // M7：组合技激活

            // M5：设置变更时实时应用音量
            SettingsManager.Changed += ApplyVolume;
        }

        /// <summary>M5：按设置应用音效音量（主音量经 AudioListener 统一处理）。</summary>
        private void ApplyVolume()
        {
            if (_src == null) return;
            float sfx = SettingsManager.Instance != null ? SettingsManager.Instance.Current.sfxVolume : 1f;
            _src.volume = 0.55f * Mathf.Clamp01(sfx);
        }

        void OnDestroy()
        {
            // M4.5：断线拆世界后重建时避免旧实例泄漏订阅
            EventBus.OnEnemyKilled -= OnEnemyKilled;
            EventBus.OnLevelUp -= OnLevelUp;
            EventBus.OnGameOver -= OnGameOver;
            EventBus.OnBossDefeated -= OnBossDefeated;
            EventBus.OnPlayerHurt -= OnPlayerHurt;
            EventBus.OnComboActivated -= OnComboActivated;
            SettingsManager.Changed -= ApplyVolume;
        }

        private void OnEnemyKilled(int _) => Play("hit");
        private void OnLevelUp(int _) => Play("levelup");
        private void OnGameOver() => Play("gameover");
        private void OnBossDefeated() => Play("levelup");
        private void OnPlayerHurt() => Play("hurt");
        private void OnComboActivated(string _) => Play("combo");   // M7

        /// <summary>播放具名音效；同名音效 30ms 内去重，避免密集触发爆音。</summary>
        public void Play(string name, float volume = 1f)
        {
            if (name == _lastName && Time.unscaledTime - _lastPlay < 0.03f) return;
            _lastName = name;
            _lastPlay = Time.unscaledTime;

            if (_src == null) return;
            var clip = Resources.Load<AudioClip>("Audio/" + name);
            if (clip != null) _src.PlayOneShot(clip, volume);
        }
    }
}
