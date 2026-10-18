using UnityEngine;
using UnityEngine.UI;   // CanvasScaler（M17 实测修复：此前缺失导致编译失败）

namespace StarPixel.Core
{
    /// <summary>
    /// 触屏虚拟摇杆（M16 跨平台）。
    /// 本游戏自动开火——移动是唯一需要的玩家输入，因此补上虚拟摇杆即等于让触屏设备可完整游玩。
    /// - 仅在触屏平台由 GameBuilder 创建（<see cref="PlayerInput.TouchPlatform"/>），
    ///   桌面构建下本组件根本不存在，行为与之前完全一致；
    /// - 左半屏任意位置按下即作为摇杆原点（浮动摇杆，避免固定位置带来的别扭操作）；
    ///   右半屏刻意留空，为将来的主动技能/闪避预留；
    /// - 画布刻意使用 ConstantPixelSize，使 RectTransform 坐标与屏幕像素 1:1，
    ///   触摸点（屏幕像素）可直接用作锚点，免去分辨率换算。
    /// </summary>
    public class TouchJoystick : MonoBehaviour
    {
        public static TouchJoystick Instance { get; private set; }

        /// <summary>当前摇杆方向（已归一化，无输入时为 Vector2.zero）。</summary>
        public Vector2 Direction { get; private set; }

        /// <summary>是否有手指正在操控摇杆。</summary>
        public bool Active => _fingerId >= 0;

        private const float MaxRadius = 90f;    // 像素（屏幕坐标）
        private const float BaseSize = 150f;    // 像素

        private int _fingerId = -1;
        private Vector2 _origin;
        private GameObject _root;
        private RectTransform _baseRt;
        private RectTransform _knobRt;

        void Awake()
        {
            Instance = this;
            BuildUI();
            SetVisible(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_root != null) Destroy(_root);
        }

        void Update()
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);

                if (_fingerId < 0)
                {
                    // 新按下：仅左半屏触发
                    if (t.phase == TouchPhase.Began && t.position.x <= Screen.width * 0.5f)
                    {
                        _fingerId = t.fingerId;
                        _origin = t.position;
                        Direction = Vector2.zero;
                        PlaceBase(_origin);
                        SetVisible(true);
                    }
                    continue;
                }

                if (t.fingerId != _fingerId) continue;

                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    _fingerId = -1;
                    Direction = Vector2.zero;
                    SetVisible(false);
                }
                else
                {
                    var delta = (Vector2)t.position - _origin;
                    if (delta.magnitude > MaxRadius) delta = delta.normalized * MaxRadius;
                    Direction = delta / MaxRadius;
                    PlaceKnob(_origin + delta);
                }
            }
        }

        private void BuildUI()
        {
            _root = new GameObject("TouchJoystickCanvas");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;                 // 置于 HUD 之上
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;   // 与屏幕像素 1:1

            var baseImg = UIFactory.CreatePanel(_root.transform, "JoyBase", Vector2.zero,
                new Vector2(BaseSize, BaseSize), new Color(1f, 1f, 1f, 0.16f));
            baseImg.raycastTarget = false;            // 纯表现，不拦截触摸
            _baseRt = baseImg.rectTransform;
            PinBottomLeft(_baseRt);

            var knobImg = UIFactory.CreatePanel(_root.transform, "JoyKnob", Vector2.zero,
                new Vector2(BaseSize * 0.5f, BaseSize * 0.5f), new Color(1f, 1f, 1f, 0.42f));
            knobImg.raycastTarget = false;
            _knobRt = knobImg.rectTransform;
            PinBottomLeft(_knobRt);
        }

        /// <summary>锚点设为左下角，使 anchoredPosition 可直接按屏幕像素使用。</summary>
        private static void PinBottomLeft(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        private void PlaceBase(Vector2 screenPos)
        {
            if (_baseRt != null) _baseRt.anchoredPosition = screenPos;
            if (_knobRt != null) _knobRt.anchoredPosition = screenPos;
        }

        private void PlaceKnob(Vector2 screenPos)
        {
            if (_knobRt != null) _knobRt.anchoredPosition = screenPos;
        }

        private void SetVisible(bool on)
        {
            if (_root != null) _root.SetActive(on);
        }
    }
}
