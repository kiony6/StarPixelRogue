using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Net
{
    /// <summary>
    /// 联机大厅（M4）：进入 Play 后展示「单人游戏 / 创建房间 / 加入房间」。
    /// - 未安装 Fish-Net 时本界面不出现，工程行为与单机版完全一致；
    /// - 连接就绪后回调 <see cref="GameBuilder.Build"/> 构建世界；
    /// - 断线时由 <see cref="FishNetBackend"/> 调用 <see cref="NotifyDisconnected"/> 回到大厅。
    /// </summary>
    public class NetLobby : MonoBehaviour
    {
        private GameObject _root;
        private Text _statusText;
        private string _address = "127.0.0.1";

        /// <summary>
        /// 当前大厅 UI 根（M18 续四：为无头 UI 探针提供定位入口）。
        /// 与 <see cref="UI.SettingsPanel.PanelRoot"/> 同理——不能用 GameObject.Find("NetLobbyCanvas")：
        /// 语言切换 / 断线重建后旧画布要等帧末才真正销毁，Find 会命中旧的，读到切换前的文案。
        /// </summary>
        public GameObject PanelRoot => _root;

        void Awake()
        {
            BuildUI();
            // M34：大厅 UI 只在 Awake 里建一次。只要存在任意一条"进了大厅再切语言"的路径
            // （断线回大厅就是常驻实例），不订阅就会停在旧语言。
            Core.Localization.LanguageChanged += Rebuild;
        }

        void OnDestroy()
        {
            // 静态事件必须退订（M29 教训）
            Core.Localization.LanguageChanged -= Rebuild;
        }

        /// <summary>M34：按当前语言整棵重建大厅 UI。</summary>
        private void Rebuild()
        {
            if (_root != null) Destroy(_root);
            BuildUI();
        }

        private void BuildUI()
        {
            // UI 根物体与组件分离：进入游戏时只销毁 UI，组件保留以便断线后重建大厅
            _root = new GameObject("NetLobbyCanvas");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            _root.AddComponent<GraphicRaycaster>();

            CreateText(_root.transform, Core.Localization.T("net.lobby_title"), new Vector2(0, 240), 40);
            CreateText(_root.transform, Core.Localization.T("net.lobby_subtitle"), new Vector2(0, 200), 20);

            _statusText = CreateText(_root.transform, Core.Localization.T("net.pick_mode"), new Vector2(0, -230), 20);
            _statusText.color = new Color(0.8f, 0.9f, 1f, 0.9f);

            CreateButton(_root.transform, Core.Localization.T("net.single"), new Vector2(-260, 80), OnSinglePlayer);
            CreateButton(_root.transform, Core.Localization.T("net.host"), new Vector2(0, 80), OnHost);
            CreateButton(_root.transform, Core.Localization.T("net.join"), new Vector2(260, 80), OnJoin);
            CreateButton(_root.transform, Core.Localization.T("net.spectate"), new Vector2(0, -70), OnSpectate);

            // 地址输入（加入房间用；同一局域网填房主 IP）
            CreateText(_root.transform, Core.Localization.T("net.address"), new Vector2(0, 20), 18);
            var inputGO = new GameObject("AddressInput", typeof(RectTransform));
            inputGO.transform.SetParent(_root.transform, false);
            var inputImg = inputGO.AddComponent<Image>();
            inputImg.color = new Color(0.15f, 0.17f, 0.22f, 1f);
            var input = inputGO.AddComponent<InputField>();
            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(inputGO.transform, false);
            var t = textGO.AddComponent<Text>();
            t.font = GameBuilderLegacyFont();
            t.color = Color.white;
            t.fontSize = 20;
            t.alignment = TextAnchor.MiddleLeft;
            input.textComponent = t;
            input.text = _address;
            var rt = (RectTransform)inputGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0, -20);
            rt.sizeDelta = new Vector2(300, 36);
            input.onValueChanged.AddListener(v => _address = v);
        }

        // —— 模式选择 ——
        private void OnSinglePlayer()
        {
            NetSession.SetSpectator(false);   // M14：避免沿用上一局的观战身份
            NetSession.SetMode(NetMode.Offline);
            EnterGame();
        }

        /// <summary>联机前置检查：需要 NetBootstrap 场景（含 NetConfig + NetworkManager）。</summary>
        private bool CheckNetScene()
        {
            if (NetConfig.Instance != null) return true;
            SetStatus(Core.Localization.T("net.err_no_netconfig"));
            return false;
        }

        private void OnHost()
        {
            var backend = NetBackend.Instance;
            if (backend == null) { SetStatus(Core.Localization.T("net.err_no_fishnet")); return; }
            if (!CheckNetScene()) return;
            backend.Ready -= OnBackendReady;
            backend.Ready += OnBackendReady;
            backend.StartHost();
            SetStatus(Core.Localization.T("net.creating"));
        }

        private void OnJoin()
        {
            var backend = NetBackend.Instance;
            if (backend == null) { SetStatus(Core.Localization.T("net.err_no_fishnet")); return; }
            if (!CheckNetScene()) return;
            if (string.IsNullOrEmpty(_address)) _address = "127.0.0.1";
            NetSession.RemoteAddress = _address;
            NetSession.SetSpectator(false);   // M14：普通加入=参战，清除可能的观战态
            backend.Ready -= OnBackendReady;
            backend.Ready += OnBackendReady;
            backend.StartClient(_address);
            SetStatus(Core.Localization.T("net.connecting", _address));
        }

        /// <summary>
        /// M14 观战：连上房主但不生成自己的角色，只接收世界同步用于观察。
        /// 必须在 StartClient 之前声明身份——玩家预制体由 Fish-Net 连接即自动生成，
        /// 晚了就只能走「先生成再回收」的路径（见 NetPlayer.RequestSpectatorServerRpc）。
        /// </summary>
        private void OnSpectate()
        {
            var backend = NetBackend.Instance;
            if (backend == null) { SetStatus(Core.Localization.T("net.err_no_fishnet")); return; }
            if (!CheckNetScene()) return;
            if (string.IsNullOrEmpty(_address)) _address = "127.0.0.1";
            NetSession.RemoteAddress = _address;
            NetSession.SetSpectator(true);
            backend.Ready -= OnBackendReady;
            backend.Ready += OnBackendReady;
            backend.StartClient(_address);
            SetStatus(Core.Localization.T("net.connecting_spectate", _address));
        }

        private void OnBackendReady()
        {
            var backend = NetBackend.Instance;
            if (backend != null) backend.Ready -= OnBackendReady;
            EnterGame();
        }

        private void EnterGame()
        {
            if (_root != null) Destroy(_root);
            GameBuilder.Build();
        }

        /// <summary>断线回调（由 FishNetBackend 在连接断开时调用）：重建大厅并提示。</summary>
        public static void NotifyDisconnected()
        {
            // 先拆联机世界（HUD/管理器/波次），之后用户选「单人游戏」可完整重建（M4.5）
            StarPixel.GameBuilder.TeardownWorld();

            var lobby = FindObjectOfType<NetLobby>();
            if (lobby == null) return;
            if (lobby._root != null) Destroy(lobby._root);
            lobby.BuildUI();
            lobby.SetStatus(Core.Localization.T("net.disconnected"));
        }

        private void SetStatus(string s)
        {
            if (_statusText != null) _statusText.text = s;
        }

        private static Font GameBuilderLegacyFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            return f;
        }

        private static Text CreateText(Transform parent, string content, Vector2 pos, int size)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = GameBuilderLegacyFont();
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = size;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(700, 50);
            return t;
        }

        private static GameObject CreateButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
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
            t.font = GameBuilderLegacyFont();
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = 22;
            var trt = (RectTransform)textGO.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            btn.onClick.AddListener(onClick);
            return go;
        }
    }
}
