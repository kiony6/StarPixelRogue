#if FISHNET_ENABLED
using System;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Net
{
    /// <summary>
    /// M11 断线重连：客户端/房主本地客户端的自动重连驱动器 + 提示遮罩。
    /// 由 <see cref="FishNetBackend"/> 在意外掉线时调用 <see cref="BeginReconnect"/>，
    /// 经短暂延迟后通过后端重连本地客户端，并在连接就绪（Ready）时重建/恢复世界。
    /// 该物体独立于 GameBuilder 的 _created 列表，跨拆世界与重连常驻，确保重连期间遮罩不丢失。
    /// </summary>
    public class NetReconnect : MonoBehaviour
    {
        public static NetReconnect Instance;

        private INetBackend _backend;
        private bool _wasHost;
        private string _address;
        private int _maxAttempts;
        private int _attempt;
        private float _delay;
        private float _timer;
        private bool _active;

        private GameObject _overlay;
        private Text _status;

        void Awake()
        {
            if (Instance == null) Instance = this;
            // M34：遮罩是**跨拆世界常驻**的，BuildOverlay 只跑一次，
            // 切语言时若不重建，状态文案与「返回大厅」按钮会一直停在旧语言。
            Core.Localization.LanguageChanged += OnLanguageChanged;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            // 静态事件必须退订（M29 教训：死订阅不只是泄漏，已销毁对象的回调会抛异常、
            // 打断整条委托链，表现出来是"切语言后一半界面没刷新"）
            Core.Localization.LanguageChanged -= OnLanguageChanged;
        }

        /// <summary>
        /// M34：按当前语言重建遮罩。旧文案是拼接出来的（"第 N/M 次重连…"），
        /// 不能直接拿 _status.text 复用——那是旧语言的成品，只有键与参数能跨语言复用。
        /// </summary>
        private void OnLanguageChanged()
        {
            if (_overlay == null) return;          // 还没建过：下次 ShowOverlay 自然按新语言建
            bool visible = _overlay.activeSelf;
            Destroy(_overlay);
            _status = null;
            BuildOverlay();                        // 内部会重设 _status 与按钮文案，并默认隐藏
            if (_status != null)
            {
                _status.text = _active
                    ? Core.Localization.T("net.reconnect_attempt", _attempt, _maxAttempts)
                    : Core.Localization.T("net.reconnecting");
            }
            if (_overlay != null) _overlay.SetActive(visible);
        }

        /// <summary>
        /// 当前遮罩 UI 根（M34：为无头 UI 探针提供定位入口）。
        /// 与 <see cref="NetLobby.PanelRoot"/> 同理——不能用 GameObject.Find("ReconnectOverlay")：
        /// 切语言会整棵重建遮罩，而 Destroy 延迟到帧末，Find 会命中旧的那一棵。
        /// </summary>
        public GameObject OverlayRoot => _overlay;

        public static NetReconnect Ensure()
        {
            if (Instance == null)
            {
                var go = new GameObject("NetReconnect");
                Instance = go.AddComponent<NetReconnect>();
            }
            return Instance;
        }

        /// <summary>开新局/加入时重置重连计数与遮罩（避免复用上一局的尝试次数）。</summary>
        public void ResetState()
        {
            _attempt = 0;
            _active = false;
            _timer = 0f;
            if (_backend != null) _backend.Ready -= OnReconnectReady;
            HideOverlay();
        }

        /// <summary>取消自动重连（主动离开/退出房间时调用），仅隐藏遮罩与退订 Ready。</summary>
        public void Cancel()
        {
            _active = false;
            if (_backend != null) _backend.Ready -= OnReconnectReady;
            HideOverlay();
        }

        /// <summary>
        /// 开始（或继续）一轮重连。<paramref name="wasHost"/> 表示掉线的是房主本地客户端，
        /// 此时权威世界由服务端保留、无需重建；纯客户端掉线则由后端在 Ready 时重建本地世界。
        /// </summary>
        public void BeginReconnect(bool wasHost, string address, int maxAttempts, float delay)
        {
            if (_backend == null) _backend = NetBackend.Instance;
            _wasHost = wasHost;
            _address = address;
            _maxAttempts = maxAttempts;
            _delay = delay;

            _attempt++;
            if (_attempt > _maxAttempts)
            {
                GiveUp();
                return;
            }

            _active = true;
            _timer = delay;
            // M33：原本是硬编码中文拼接，英文界面下断线遮罩会冒中文。
            ShowOverlay(Core.Localization.T("net.reconnect_attempt", _attempt, _maxAttempts));
            if (_backend != null)
            {
                _backend.Ready -= OnReconnectReady;
                _backend.Ready += OnReconnectReady;
            }
        }

        void Update()
        {
            if (!_active) return;
            _timer -= Time.unscaledDeltaTime;
            if (_timer <= 0f)
            {
                _active = false;   // 防止定时器重入；BeginReconnect 会再次置 true
                if (_backend != null) _backend.RetryLastConnection();
                // 重连失败 → Stopped 再次进入 BeginReconnect；成功 → Ready → OnReconnectReady
            }
        }

        /// <summary>连接就绪：房主无需重建（世界保留），客户端重建本地世界以重新订阅房主同步。</summary>
        private void OnReconnectReady()
        {
            if (_backend != null) _backend.Ready -= OnReconnectReady;
            _active = false;
            _attempt = 0;                          // 重连成功：重置计数，后续断线可再享满额重试
            if (!_wasHost) StarPixel.GameBuilder.Build();
            HideOverlay();
        }

        /// <summary>放弃重连：回到大厅（房主额外关闭仍在运行的服务端）。</summary>
        private void GiveUp()
        {
            _active = false;
            if (_backend != null) _backend.Ready -= OnReconnectReady;
            HideOverlay();
            NetSession.ResetToOffline();
            NetLobby.NotifyDisconnected();
            if (_wasHost)
            {
                // 房主放弃：关掉仍在运行的服务端（会触发一次 Stopped，intentionalStop 已置位 → 仅回到大厅）
                _backend?.Stop();
            }
        }

        // —— 遮罩 UI ——
        private void ShowOverlay(string msg)
        {
            if (_overlay == null) BuildOverlay();
            if (_status != null) _status.text = msg;
            _overlay.SetActive(true);
        }

        private void HideOverlay()
        {
            if (_overlay != null) _overlay.SetActive(false);
        }

        private void BuildOverlay()
        {
            var root = new GameObject("ReconnectOverlay");
            root.transform.SetParent(transform, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;                 // 置顶，盖过 HUD/大厅
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            root.AddComponent<GraphicRaycaster>();

            // M33：状态文案与按钮文案原本都是硬编码中文；按钮尤其明显——
            // 英文玩家断线时面对的是一枚看不懂的「返回大厅」。
            _status = CreateText(root.transform, Core.Localization.T("net.reconnecting"), new Vector2(0, 40), 34);
            CreateButton(root.transform, Core.Localization.T("net.back_to_lobby"), new Vector2(0, -60), GiveUp);
            _overlay = root;
            _overlay.SetActive(false);
        }

        private static Text CreateText(Transform parent, string content, Vector2 pos, int size)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = GetFont();
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = size;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(720, 60);
            return t;
        }

        private static GameObject CreateButton(Transform parent, string label, Vector2 pos, Action onClick)
        {
            var go = new GameObject(label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.20f, 0.42f, 0.72f, 1f);
            var btn = go.AddComponent<Button>();
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(240, 64);

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var t = textGO.AddComponent<Text>();
            t.text = label;
            t.font = GetFont();
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = 22;
            var trt = (RectTransform)textGO.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            btn.onClick.AddListener(() => onClick());
            return go;
        }

        private static Font GetFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            return f;
        }
    }
}
#endif
