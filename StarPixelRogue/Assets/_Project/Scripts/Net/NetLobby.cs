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
        /// M55：玩家输入的加入目标。可以是组队码（<c>K7M-2QX</c>），也可以直接是 IP。
        /// <para>保留 IP 这条老路不是偷懒——局域网里有固定地址的朋友、以及自动化联机探针
        /// （<c>net_longrun_probe --address</c>）都依赖它。码是给**人**用的便捷路径，不是唯一路径。</para>
        /// </summary>
        private string _codeInput = "";

        private InputField _codeField;
        private Text _myCodeText;

        /// <summary>输入框里的当前内容（探针用：写进来 = 模拟玩家输入）。</summary>
        public string CodeText
        {
            get { return _codeInput; }
            set { _codeInput = value ?? ""; if (_codeField != null) _codeField.text = _codeInput; }
        }

        /// <summary>本机作为房主时的组队码（未开房为空串）。</summary>
        public string MyCode => RoomDirectory.LocalCode ?? "";

        /// <summary>大厅上展示给房主的码文案（探针用：确认码真的上屏了，而不只是生成了）。</summary>
        public string MyCodeText => _myCodeText != null ? _myCodeText.text : "";

        /// <summary>
        /// 当前大厅 UI 根（M18 续四：为无头 UI 探针提供定位入口）。
        /// 与 <see cref="UI.SettingsPanel.PanelRoot"/> 同理——不能用 GameObject.Find("NetLobbyCanvas")：
        /// 语言切换 / 断线重建后旧画布要等帧末才真正销毁，Find 会命中旧的，读到切换前的文案。
        /// </summary>
        public GameObject PanelRoot => _root;

        /// <summary>
        /// 当前状态行文案（M51：为无头实测提供定位入口）。
        /// 与 <see cref="PanelRoot"/> 同理——"断线后玩家看到的是什么"这件事，
        /// 只能从这行文字上读；读不到就只能断言"大厅建出来了"，
        /// 而大厅建出来但写着"请选择模式"（等于什么都没解释）同样是个缺陷。
        /// </summary>
        public string StatusText => _statusText != null ? _statusText.text : "";

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
            canvas.sortingOrder = Core.UIFactory.MenuSortingOrder;   // M51：显式声明层级
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
            // M54：返回主菜单。大厅此前是单向门——进来只能选一种模式开局，
            // 主菜单成了唯一入口之后，没有这颗按钮就等于把玩家关在半路上。
            CreateButton(_root.transform, Core.Localization.T("net.back"), new Vector2(260, -70), OnBack, "BackBtn");

            // 加入目标输入：优先当组队码解析，解析不了再看是不是 IP（见 CodeText 注释）
            CreateText(_root.transform, Core.Localization.T("net.room_code"), new Vector2(0, 20), 18);
            var inputGO = new GameObject("CodeInput", typeof(RectTransform));
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
            t.alignment = TextAnchor.MiddleCenter;   // 码短且居中，居中比左对齐更像"填码"
            input.textComponent = t;
            var rt = (RectTransform)inputGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0, -20);
            rt.sizeDelta = new Vector2(300, 36);
            input.onValueChanged.AddListener(v => _codeInput = v);
            _codeField = input;
            if (!string.IsNullOrEmpty(_codeInput)) input.text = _codeInput;

            // 房主的码展示区。建房成功后才填内容——
            // 空着不显示是因为"房间 K7M-2QX"这种行在还没建房时是句谎话。
            _myCodeText = CreateText(_root.transform, "", new Vector2(0, -150), 22);
            _myCodeText.color = Core.UIFactory.Palette.Gold;
            RefreshMyCode();
        }

        /// <summary>把本机的码刷到界面上（未开房则留空）。</summary>
        private void RefreshMyCode()
        {
            if (_myCodeText == null) return;
            string code = MyCode;
            _myCodeText.text = string.IsNullOrEmpty(code)
                ? ""
                : Core.Localization.T("net.my_code", RoomCode.Format(code));
        }

        /// <summary>M54：退出大厅回到主菜单（主菜单进来时才有意义）。</summary>
        private void OnBack()
        {
            JoinTarget.Unhost();            // M55：离开大厅时撤掉本机房间与广播应答
            Core.UIFactory.Destroy(_root);
            _root = null;
            Destroy(gameObject);            // 组件一并拆掉：OnDestroy 会退订语言切换
            UI.MainMenu.Instance?.Show();
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

            // M55：先生成码、先开始广播应答，**再** StartHost。
            // 顺序反了会有一个真实的窗口：服务已起但还没人应答，队友此刻问过来必然扑空，
            // 表现是"码是对的却搜不到房间"——这种偶发最难查，靠顺序消除。
            var code = JoinTarget.Host(NetSession.PlayerName, NetProtocol.DefaultPort);
            RefreshMyCode();
            if (string.IsNullOrEmpty(code)) SetStatus(Core.Localization.T("net.err_no_code"));

            backend.Ready -= OnBackendReady;
            backend.Ready += OnBackendReady;
            backend.StartHost();
            SetStatus(Core.Localization.T("net.creating"));
        }

        /// <summary>
        /// M55：加入改成协程。原因很实际——组队码找不到时要在局域网广播等两秒，
        /// 同步版本会把整个界面冻住，玩家只会看到"点了没反应"。
        /// </summary>
        private void OnJoin()
        {
            StartCoroutine(JoinCo(false));
        }

        /// <summary>
        /// M14 观战：连上房主但不生成自己的角色，只接收世界同步用于观察。
        /// 必须在 StartClient 之前声明身份——玩家预制体由 Fish-Net 连接即自动生成，
        /// 晚了就只能走「先生成再回收」的路径（见 NetPlayer.RequestSpectatorServerRpc）。
        /// </summary>
        private void OnSpectate()
        {
            StartCoroutine(JoinCo(true));
        }

        private System.Collections.IEnumerator JoinCo(bool spectator)
        {
            var backend = NetBackend.Instance;
            if (backend == null) { SetStatus(Core.Localization.T("net.err_no_fishnet")); yield break; }
            if (!CheckNetScene()) yield break;

            string raw = string.IsNullOrEmpty(_codeInput) ? _address : _codeInput;

            // ① 直接填了 IP：老路径，零延迟，不走发现
            string ip; int port;
            if (JoinTarget.TryParseDirectAddress(raw, out ip, out port))
            {
                DoConnect(ip, port, ip, spectator);
                yield break;
            }

            // ② 组队码先查本地目录：已经发现过的房间不必再等两秒
            var cached = JoinTarget.ResolveCached(raw);
            if (cached.Ok)
            {
                DoConnect(cached.Address, cached.Port, cached.HostName, spectator);
                yield break;
            }
            // 码本身就不合法（位数/校验位不对）——没必要再去搜，直接告诉玩家输错了
            if (cached.ReasonKey != "net.err_no_room")
            {
                SetStatus(Core.Localization.T(cached.ReasonKey));
                yield break;
            }

            // ③ 目录里没有：广播问一次。放到线程池里跑，主线程只轮询完成标志，
            //    否则这 2 秒会把渲染和输入一起冻住。
            SetStatus(Core.Localization.T("net.searching"));
            string code = RoomCode.Normalize(raw);
            var task = System.Threading.Tasks.Task.Run(() => RoomDiscovery.Query(code));
            float deadline = Time.unscaledTime + RoomDiscovery.QueryTimeout + 2f;
            while (!task.IsCompleted && Time.unscaledTime < deadline) yield return null;

            if (!task.IsCompleted) { SetStatus(Core.Localization.T("net.err_discover_failed")); yield break; }

            var replies = task.Result;
            if (replies == null || replies.Count == 0)
            {
                // 搜不到和搜索失败要分开报：前者是"没这个房间"，后者是"网络不让广播"，
                // 玩家能采取的补救完全不同（前者查码，后者查防火墙/网络权限）。
                SetStatus(Core.Localization.T(string.IsNullOrEmpty(RoomDiscovery.LastError)
                    ? "net.err_no_room" : "net.err_discover_failed"));
                yield break;
            }

            var best = replies[0];
            var entry = RoomDirectory.Put(best.RoomKey, best.Address, best.Port, best.HostName, best.Players, false);
            DoConnect(entry.Address, entry.Port, entry.HostName, spectator);
        }

        /// <summary>真正发起连接（解析完的最后一公里）。</summary>
        private void DoConnect(string address, int port, string label, bool spectator)
        {
            _address = address;
            NetSession.RemoteAddress = address;
            NetSession.SetSpectator(spectator);
            var backend = NetBackend.Instance;
            if (backend == null) return;
            backend.Ready -= OnBackendReady;
            backend.Ready += OnBackendReady;
            backend.StartClient(address);
            SetStatus(Core.Localization.T(spectator ? "net.connecting_spectate" : "net.connecting",
                string.IsNullOrEmpty(label) ? address : label));
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

        private static GameObject CreateButton(Transform parent, string label, Vector2 pos,
            UnityEngine.Events.UnityAction onClick, string nodeName = null)
        {
            // 默认用文案当节点名（历史行为，既有探针按名字找这几颗）；
            // 新增按钮一律显式传 nodeName——靠中文文案定位在切语言后必然失效。
            var go = new GameObject(nodeName ?? label, typeof(RectTransform));
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
