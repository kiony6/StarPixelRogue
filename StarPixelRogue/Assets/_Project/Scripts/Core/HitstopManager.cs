using UnityEngine;
using StarPixel.Net;

namespace StarPixel.Core
{
    /// <summary>
    /// 打击感：时间缩放统一管理器（命中停顿 / 选卡暂停）。
    /// 作为 <see cref="Time.timeScale"/> 的唯一写者，避免多处直接写 timeScale 互相踩踏
    /// （此前只有 LevelUpManager 写 0/1，新增 hitstop 后会冲突）。
    /// 优先级：暂停（选卡）> 命中停顿（hitstop）> 正常 1。
    /// 计时一律用 <see cref="Time.unscaledDeltaTime"/>，保证 timeScale=0 时自身仍推进。
    /// 联机下不压时间（避免影响网络心跳）；并复用「屏幕震动」开关作为整体打击反馈开关。
    /// </summary>
    public class HitstopManager : MonoSingleton<HitstopManager>
    {
        private bool _paused;
        private float _hitstopTimer;
        private float _hitstopDuration;
        private float _hitstopScale;

        /// <summary>
        /// M41：正常速度基准（默认 1）。此前 <see cref="Apply"/> 的兜底分支写死 `1f`，
        /// 于是"正常速度"这个事实被复制到了两份地方——只要有人想改基准（长局压测要加速推进，
        /// 否则 30 分钟游戏时间就得真等 30 分钟），就必须在每一处硬编码里各改一遍，
        /// 漏一处就会在顿帧结束/面板关闭的瞬间被拽回 1。现在基准只有这一处。
        /// 静态而非实例字段：Instance 尚未创建时的兜底写入也要能读到同一个值。
        /// </summary>
        private static float s_baseScale = 1f;

        /// <summary>正常速度基准。仅供无头实测的加速档设置（`-smokeSpeed`），正式游戏恒为 1。</summary>
        public static float BaseScale { get { return s_baseScale; } }

        /// <summary>设置基准倍率并立刻应用一次。</summary>
        public static void SetBaseScale(float scale)
        {
            s_baseScale = Mathf.Max(0.01f, scale);
            if (Instance != null) Instance.ApplyNow();
        }

        /// <summary>选卡等需要完全冻结时调用（仅单机路径；联机客户端不走暂停）。</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused) _hitstopTimer = 0f;   // 暂停期间不叠加 hitstop
            ApplyNow();
        }

        /// <summary>
        /// 请求一次命中停顿：在 duration 秒内把 timeScale 压到 scale（极小），然后恢复，
        /// 制造"顿帧"重量感。取更长的停顿，避免弱停顿被强停顿覆盖。
        /// </summary>
        public void Pulse(float duration, float scale)
        {
            if (NetSession.IsNetworked) return;                       // 联机不压时间
            if (SettingsManager.Instance != null &&
                !SettingsManager.Instance.Current.screenShake) return; // 复用"屏幕震动"开关
            if (_paused) return;
            if (duration > _hitstopTimer)
            {
                _hitstopDuration = duration;
                _hitstopTimer = duration;
                _hitstopScale = scale;
            }
            ApplyNow();
        }

        void Update()
        {
            if (_hitstopTimer > 0f)
            {
                _hitstopTimer -= Time.unscaledDeltaTime;
                if (_hitstopTimer < 0f) _hitstopTimer = 0f;
            }
            ApplyNow();
        }

        /// <summary>把当前优先级结果写进 <see cref="Time.timeScale"/>（唯一写入点）。</summary>
        public void ApplyNow()
        {
            if (_paused) Time.timeScale = 0f;
            else if (_hitstopTimer > 0f) Time.timeScale = _hitstopScale;
            else Time.timeScale = s_baseScale;
        }

        protected override void Awake()
        {
            base.Awake();
            // 解耦订阅：玩家受伤 / 精英被击杀 / Boss 被击败 触发不同强度的顿帧
            EventBus.OnPlayerHurt += OnPlayerHurt;
            EventBus.OnEliteKilled += OnEliteKilled;
            EventBus.OnBossDefeated += OnBossDefeated;
        }

        private void OnPlayerHurt() => Pulse(0.05f, 0.18f);
        private void OnEliteKilled(Vector3 _) => Pulse(0.10f, 0.10f);
        private void OnBossDefeated() => Pulse(0.20f, 0.04f);

        protected override void OnDestroy()
        {
            EventBus.OnPlayerHurt -= OnPlayerHurt;
            EventBus.OnEliteKilled -= OnEliteKilled;
            EventBus.OnBossDefeated -= OnBossDefeated;
            base.OnDestroy();
        }
    }
}
