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
            // M25：基准缩放必须**当场**就记下来。
            // 此前只在 Configure() 里记录，而 Configure 要等开局（AttachCameraShake/NetPlayer）才被调用；
            // 这中间的菜单/大厅停留期里，LateUpdate 已经在把 orthographicSize 朝 _baseOrtho（初值 0）拉了——
            // 每帧 Lerp(dt*9)，与帧率无关地按 exp(-9t) 衰减，主菜单停留 1 秒就只剩千分之一。
            // 等开局时 Configure 读到的就是这个被压过的值并被记成基准，
            // 于是整局游戏的镜头都是无限推近的（画面糊成几个像素）。
            if (_cam != null) _baseOrtho = _cam.orthographicSize;
        }

        /// <summary>跟随目标（通常是玩家），使相机基础位置始终跟随玩家。</summary>
        public void Configure(Transform target)
        {
            _target = target;
            if (_cam == null) return;
            // M25：缩放脉冲进行中不得改写基准。
            // 此刻的 orthographicSize 是被压过的临时值，把它记成基准会让镜头永久推近，且逐次叠乘
            // （Boss 出场推近有 0.55s 窗口，期间只要有人加入/重连就会命中）。
            if (_zoomTimer > 0f) return;
            _baseOrtho = _cam.orthographicSize;
        }

        /// <summary>
        /// 请求一次镜头缩放脉冲：在 duration 秒内把正交尺寸压到 baseSize*scale（更小=推近），
        /// 之后平滑回弹到基准尺寸。用于 Boss 出场等需要"推脸"的瞬间。
        /// </summary>
        public void ZoomPulse(float scale, float duration)
        {
            if (_cam == null) return;
            // M25：与 Shake 一致，尊重「屏幕震动」开关。
            // 该开关在 HitstopManager 里已被复用为"整体打击反馈开关"，推近同属打击反馈；
            // 此前关掉开关后 Boss 出场推近照旧，形成"关了一半"的不一致。
            if (SettingsManager.Instance != null && !SettingsManager.Instance.Current.screenShake) return;
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
                // M25：震屏衰减必须走**未缩放**的时间（与同函数里 _zoomTimer 的口径一致）。
                // 用 Time.deltaTime 的话，timeScale=0（选卡/结算暂停）时 deltaTime 恒为 0，
                // _remaining 永不减少，而 LateUpdate 照跑——每帧还会重新随机一次偏移，
                // 于是面板背后的世界一直以全幅度抽搐，直到玩家选完卡才停。
                // 顿帧（timeScale=0.04）下同理：一次 0.3s 的震屏会被拖成好几秒。
                _remaining -= Time.unscaledDeltaTime;
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
