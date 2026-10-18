using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 背景音乐管理器（M7 打磨：氛围反馈）。
    /// 三轨循环 BGM——菜单 / 战斗 / Boss——双 AudioSource 交叉淡入淡出切换，
    /// 音频文件由 gen_assets.py 程序化合成（22050Hz 单声道 WAV，约 1MB/轨），
    /// 经 Resources/Audio/bgm_* 加载；**资源缺失时静默跳过**（与 SfxManager 同一约定，
    /// 换用自有音乐只需把同名 .wav/.ogg 放进 Resources/Audio 即可，代码零改动）。
    /// 生命周期：由 GameBuilder.AutoBuild 创建一次并 DontDestroyOnLoad，
    /// **不纳入 Track**——跨「菜单 → 对局 → 结算 → 菜单」全程存活，断线拆世界不影响。
    /// </summary>
    public class MusicManager : MonoBehaviour
    {
        public static MusicManager Instance { get; private set; }

        private enum Track { None, Menu, Battle, Boss }

        private const float FadeSeconds = 1.2f;

        private AudioSource _srcA, _srcB;      // 交叉淡入淡出对
        private AudioSource _from, _to;        // 当前淡出 / 淡入源
        private Track _current = Track.None;
        private float _fadeT = -1f;            // >=0 表示正在淡变

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _srcA = gameObject.AddComponent<AudioSource>();
            _srcB = gameObject.AddComponent<AudioSource>();
            foreach (var s in new[] { _srcA, _srcB })
            {
                s.playOnAwake = false;
                s.loop = true;
                s.spatialBlend = 0f;
                s.volume = 0f;
            }
            ApplyVolume();

            // 战斗事件驱动切歌
            EventBus.OnBossSpawned += OnBossSpawned;
            EventBus.OnBossDefeated += OnBossDefeated;
            EventBus.OnGameOver += OnGameOver;

            // M5：设置变更时实时应用音量
            SettingsManager.Changed += ApplyVolume;

            PlayMenu();                          // 初始为菜单乐（无声资源时为空操作）
        }

        private void OnDestroy()
        {
            EventBus.OnBossSpawned -= OnBossSpawned;
            EventBus.OnBossDefeated -= OnBossDefeated;
            EventBus.OnGameOver -= OnGameOver;
            SettingsManager.Changed -= ApplyVolume;
            if (Instance == this) Instance = null;
        }

        private void ApplyVolume()
        {
            float music = SettingsManager.Instance != null ? SettingsManager.Instance.Current.musicVolume : 0.75f;
            float target = 0.5f * Mathf.Clamp01(music);
            // 非淡变中的源立即到位，避免调滑块听不到变化
            if (_fadeT < 0f)
            {
                if (_from != null) _from.volume = target;
                if (_to != null) _to.volume = 0f;
            }
            else if (_to != null)
            {
                // 淡变中：按进度插值到新目标（近似处理，保持听感连续）
                float k = Mathf.Clamp01(_fadeT / FadeSeconds);
                _to.volume = Mathf.Lerp(0f, target, 1f - k);
                if (_from != null) _from.volume = Mathf.Lerp(target, 0f, 1f - k);
            }
        }

        // —— 切歌 API（GameBuilder / 事件调用） ——

        /// <summary>菜单 / 大厅 / 结算音乐。</summary>
        public void PlayMenu() => Switch(Track.Menu, "bgm_menu");

        /// <summary>战斗音乐（开局/重开/Boss 阵亡后）。</summary>
        public void PlayBattle() => Switch(Track.Battle, "bgm_battle");

        private void Switch(Track track, string resName)
        {
            if (track == _current) return;
            _current = track;
            var clip = Resources.Load<AudioClip>("Audio/" + resName);
            if (clip == null) return;            // 资源缺失：静默（切换状态已记录，不重试）

            var to = _from == _srcA ? _srcB : _srcA;   // 用非淡出源作淡入源
            to.clip = clip;
            to.time = 0f;
            to.volume = 0f;
            to.Play();

            if (_from != null && _from.isPlaying && _from.volume > 0f)
            {
                _fadeT = FadeSeconds;            // 交叉淡变
                _to = to;
            }
            else
            {
                if (_from != null) _from.Stop();
                to.volume = TargetVolume();
                _fadeT = -1f;
                _from = to;                      // 无旧乐可淡：直接上位
                _to = null;
            }
        }

        private float TargetVolume()
        {
            float music = SettingsManager.Instance != null ? SettingsManager.Instance.Current.musicVolume : 0.75f;
            return 0.5f * Mathf.Clamp01(music);
        }

        void Update()
        {
            if (_fadeT < 0f) return;
            _fadeT -= Time.unscaledDeltaTime;
            float k = 1f - Mathf.Clamp01(_fadeT / FadeSeconds);   // 0→1
            float target = TargetVolume();
            if (_to != null) _to.volume = target * k;
            if (_from != null)
            {
                _from.volume = target * (1f - k);
                if (k >= 1f) _from.Stop();
            }
            if (k >= 1f) { _from = _to; _to = null; _fadeT = -1f; }
        }

        // —— 事件回调 ——
        private void OnBossSpawned(float hp, float max) => Switch(Track.Boss, "bgm_boss");
        private void OnBossDefeated() => Switch(Track.Battle, "bgm_battle");
        private void OnGameOver() => Switch(Track.Menu, "bgm_menu");
    }
}
