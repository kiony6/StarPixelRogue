#if FISHNET_ENABLED
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Net
{
    /// <summary>
    /// 观战相机（M14）：观战客户端没有自己的角色，本组件接管相机跟随与提示。
    /// - 复用既有 <see cref="Core.CameraShake"/> 的跟随能力（其 LateUpdate 按 target 定位），
    ///   每帧把跟随目标设为「当前被观察的玩家」，因此无需另造相机、也不改动单机/联机的既有相机链路；
    /// - 默认跟随首个存活玩家（通常是房主），按 Tab 在存活玩家间循环切换；
    /// - 被观察者死亡/离场时自动切到下一个存活玩家；暂时无人可选则相机停在原处；
    /// - 顶部常驻「观战中」提示条，避免玩家误以为卡住。
    /// 观战者不参与任何战斗结算——它只是接收世界同步的观察者（见 NetPlayer.RequestSpectatorServerRpc）。
    /// </summary>
    public class SpectatorCamera : MonoBehaviour
    {
        private readonly List<NetPlayer> _buffer = new List<NetPlayer>();
        private NetPlayer _observed;
        private float _rescanTimer;
        private GameObject _root;
        private Text _banner;

        void Awake()
        {
            BuildBanner();
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root);
        }

        void Update()
        {
            // 被观察者失效（死亡/离场/被销毁）→ 立即重挑；否则低频复核
            _rescanTimer -= Time.deltaTime;
            bool lost = _observed == null || !IsAlive(_observed);
            if (lost || _rescanTimer <= 0f)
            {
                _rescanTimer = 0.5f;
                if (lost) _observed = PickFirstAlive();
            }

            // Tab 循环切换视角
            if (Input.GetKeyDown(KeyCode.Tab)) _observed = PickNextAlive();

            // 把跟随目标交给既有 CameraShake（PrepareStage 已保证主相机上挂载了它）
            var shake = Core.CameraShake.Instance;
            if (shake != null) shake.Configure(_observed != null ? _observed.transform : null);

            UpdateBanner();
        }

        private static bool IsAlive(NetPlayer p)
        {
            return p != null && p.Damageable != null && !p.Damageable.isDead;
        }

        private void CollectAlive()
        {
            _buffer.Clear();
            NetPlayerRegistry.ForEachAlive(p => _buffer.Add(p));
        }

        private NetPlayer PickFirstAlive()
        {
            CollectAlive();
            return _buffer.Count > 0 ? _buffer[0] : null;
        }

        private NetPlayer PickNextAlive()
        {
            CollectAlive();
            if (_buffer.Count == 0) return null;
            int idx = _buffer.IndexOf(_observed);
            return _buffer[(idx + 1) % _buffer.Count];
        }

        private void BuildBanner()
        {
            _root = new GameObject("SpectatorBanner");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);

            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            _banner = go.AddComponent<Text>();
            _banner.font = Core.UIFactory.UIFont;
            _banner.color = new Color(0.80f, 0.90f, 1f, 0.95f);
            _banner.alignment = TextAnchor.MiddleCenter;
            _banner.fontSize = 20;
            _banner.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -16);
            rt.sizeDelta = new Vector2(700, 40);
        }

        private void UpdateBanner()
        {
            if (_banner == null) return;
            // M33：观战横幅原本硬编码中文，英文玩家加入观战时看到的是中文提示。
            _banner.text = _observed != null
                ? Core.Localization.T("net.spectating_switch")
                : Core.Localization.T("net.spectating_wait");
        }
    }
}
#endif
