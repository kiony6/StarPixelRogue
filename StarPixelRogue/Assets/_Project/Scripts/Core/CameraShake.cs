using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 屏幕震动（对应 TDD M3.5 打击感）。
    /// 挂在主相机上，LateUpdate 里在跟随目标位置的基础上叠加一个随机偏移。
    /// 只监听 OnShakeRequest 事件，因此各系统（玩家受伤/Boss 弹幕/精英死亡）都可解耦地触发。
    /// </summary>
    public class CameraShake : MonoBehaviour
    {
        public static CameraShake Instance { get; private set; }

        private Transform _target;
        private float _strength;
        private float _duration;
        private float _remaining;
        private Vector2 _offset;

        // —— M8 手感：Boss 出场镜头推近（正交相机缩放脉冲）——
        private Camera _cam;
        private float _baseOrtho;
        private float _zoomTimer;
        private float _zoomDuration;
        private float _zoomPunched;

        void Awake()
        {
            Instance = this;
            EventBus.OnShakeRequest += Shake;
            _cam = GetComponent<Camera>();
        }

        /// <summary>跟随目标（通常是玩家），使相机基础位置始终跟随玩家。</summary>
        public void Configure(Transform target)
        {
            _target = target;
            if (_cam != null) _baseOrtho = _cam.orthographicSize;   // 记录基准缩放，ZoomPulse 围绕它回弹
        }

        /// <summary>
        /// 请求一次镜头缩放脉冲：在 duration 秒内把正交尺寸压到 baseSize*scale（更小=推近），
        /// 之后平滑回弹到基准尺寸。用于 Boss 出场等需要"推脸"的瞬间。
        /// </summary>
        public void ZoomPulse(float scale, float duration)
        {
            if (_cam == null) return;
            _zoomPunched = _baseOrtho * scale;
            _zoomDuration = Mathf.Max(0.01f, duration);
            _zoomTimer = _zoomDuration;
        }

        /// <summary>请求一次震动；取「强度×时长」更大的一次，避免弱震动覆盖强震动。</summary>
        public void Shake(float strength, float duration)
        {
            // M5：尊重设置中的「屏幕震动」开关
            if (SettingsManager.Instance != null && !SettingsManager.Instance.Current.screenShake) return;
            if (strength * duration >= _strength * _remaining)
            {
                _strength = strength;
                _duration = Mathf.Max(0.01f, duration);
                _remaining = _duration;
            }
        }

        void LateUpdate()
        {
            // 基础位置：跟随目标（该帧由玩家更新逻辑写入相机，或由这里直接取目标位置）
            Vector3 basePos = transform.position - (Vector3)_offset;
            if (_target != null)
                basePos = new Vector3(_target.position.x, _target.position.y, basePos.z);

            if (_remaining > 0f)
            {
                _remaining -= Time.deltaTime;
                float t = Mathf.Clamp01(_remaining / _duration);
                _offset = Random.insideUnitCircle * (_strength * t);
            }
            else
            {
                _offset = Vector2.zero;
            }

            transform.position = basePos + (Vector3)_offset;

            // 镜头缩放脉冲（Boss 出场推近）：朝目标尺寸平滑逼近，计时结束后回弹基准
            if (_cam != null)
            {
                float targetSize = _zoomTimer > 0f ? _zoomPunched : _baseOrtho;
                if (_zoomTimer > 0f) _zoomTimer -= Time.unscaledDeltaTime;
                float cur = _cam.orthographicSize;
                _cam.orthographicSize = Mathf.Lerp(cur, targetSize,
                    Mathf.Clamp01(Time.unscaledDeltaTime * 9f));
            }
        }
    }
}
