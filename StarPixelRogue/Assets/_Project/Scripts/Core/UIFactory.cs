using System;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Core
{
    /// <summary>
    /// 运行时 UI 构件工厂（M5）：与 GameBuilder 的场景内构建风格一致——
    /// 全部用代码生成 uGUI，无需任何预制体/美术资源即可得到完整界面。
    /// 所有方法均为静态纯函数（无副作用除创建 GameObject），可在编辑期外安全调用。
    /// </summary>
    public static class UIFactory
    {
        /// <summary>兼容不同 Unity 版本的内置字体（LegacyRuntime / Arial，含 CJK 回退）。</summary>
        public static Font UIFont
        {
            get
            {
                if (_font != null) return _font;
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
                if (_font == null)
                {
                    try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
                }
                return _font;
            }
        }
        private static Font _font;

        /// <summary>全屏 Overlay Canvas（自动挂 Scaler + Raycaster）。</summary>
        public static Canvas CreateCanvas(string name, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>半透明遮罩矩形（面板背景/模态底）。</summary>
        public static Image CreatePanel(Transform parent, string name, Vector2 pos, Vector2 size, Color bg)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = bg;
            return img;
        }

        /// <summary>文本（默认居中、允许溢出以防中文被裁剪）。</summary>
        public static Text CreateText(Transform parent, string name, string content, Vector2 pos, int size,
            TextAnchor anchor = TextAnchor.MiddleCenter, Vector2? sizeDelta = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = UIFont;
            t.color = Color.white;
            t.alignment = anchor;
            t.fontSize = size;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta ?? new Vector2(600, 40);
            return t;
        }

        /// <summary>按钮（底色 + 文字子物体），返回 Button；label 变化可后续改子 Text。</summary>
        public static Button CreateButton(Transform parent, string name, string label, Vector2 pos, Vector2 size,
            Action onClick, int fontSize = 20, Color? bg = null)
        {
            var bgImg = CreatePanel(parent, name, pos, size, bg ?? new Color(0.16f, 0.18f, 0.26f, 0.95f));
            var go = bgImg.gameObject;
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.30f, 0.34f, 0.48f);
            colors.pressedColor = new Color(0.42f, 0.46f, 0.62f);
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var text = CreateText(go.transform, "Label", label, Vector2.zero, fontSize);
            text.raycastTarget = false;
            return btn;
        }

        /// <summary>滑条（底槽 + 填充），返回 Slider；fillColor 可随后改。</summary>
        public static Slider CreateSlider(Transform parent, string name, Vector2 pos, Vector2 size,
            float value, Color fillColor, Color bgColor)
        {
            var bg = CreatePanel(parent, name, pos, size, bgColor);
            var go = bg.gameObject;
            var slider = go.AddComponent<Slider>();

            var fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(go.transform, false);
            var frt = (RectTransform)fill.transform;
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.one;
            frt.offsetMin = new Vector2(2, 2);
            frt.offsetMax = new Vector2(-2, -2);
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = fillColor;

            // 遮罩挂在滑槽根上：裁剪 fill 子物体，实现从左增长的填充观感
            go.AddComponent<RectMask2D>();

            slider.fillRect = frt;
            slider.targetGraphic = fillImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = Mathf.Clamp01(value);
            slider.transition = Selectable.Transition.None;
            return slider;
        }

        /// <summary>开关：按钮式 Toggle 外观（文字显示 开/关），返回 Button 供改写文案。</summary>
        public static Button CreateToggle(Transform parent, string name, bool value, Vector2 pos, Vector2 size,
            Action<bool> onChanged)
        {
            bool current = value;
            // M17 实测修复：不能在"声明 btn 的同一条语句"里于 lambda 内引用 btn（CS0841）——先声明再赋值。
            Button btn = null;
            btn = CreateButton(parent, name, current ? Localization.T("common.on") : Localization.T("common.off"),
                pos, size, () =>
                {
                    current = !current;
                    var label = btn != null ? btn.GetComponentInChildren<Text>() : null;
                    if (label != null) label.text = current ? Localization.T("common.on") : Localization.T("common.off");
                    onChanged?.Invoke(current);
                }, 18);
            return btn;
        }

        /// <summary>模态根：全屏半透明遮罩 + 居中容器。返回 (遮罩, 容器)。</summary>
        public static (GameObject mask, GameObject root) CreateModal(string name, Color maskColor)
        {
            var canvas = CreateCanvas(name);
            var mask = new GameObject("Mask", typeof(RectTransform));
            mask.transform.SetParent(canvas.transform, false);
            var mrt = (RectTransform)mask.transform;
            mrt.anchorMin = Vector2.zero;
            mrt.anchorMax = Vector2.one;
            mrt.offsetMin = mrt.offsetMax = Vector2.zero;
            mask.AddComponent<Image>().color = maskColor;
            return (canvas.gameObject, mask);
        }

        /// <summary>就地销毁自身及其子物体。</summary>
        public static void Destroy(GameObject go)
        {
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }
}
