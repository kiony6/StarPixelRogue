using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Compliance
{
    /// <summary>
    /// 法律文本浏览窗（M5）：可滚动长文本模态，供合规门与设置面板复用。
    /// 纯代码构建（ScrollRect + RectMask2D + ContentSizeFitter），支持滚轮与拖拽。
    /// </summary>
    public static class LegalViewer
    {
        /// <summary>打开文本窗；返回模态根（调用方持以便销毁）。</summary>
        public static GameObject Open(string title, string body)
        {
            var (canvasGo, mask) = Core.UIFactory.CreateModal("LegalViewer", new Color(0.04f, 0.04f, 0.07f, 0.96f));

            var panel = Core.UIFactory.CreatePanel(mask.transform, "Panel", Vector2.zero, new Vector2(920, 600),
                new Color(0.10f, 0.11f, 0.17f, 1f));

            Core.UIFactory.CreateText(panel.transform, "Title", title, new Vector2(0, 260), 30);

            // —— 视口（裁切长文本） ——
            var viewport = Core.UIFactory.CreatePanel(panel.transform, "Viewport", new Vector2(0, -10),
                new Vector2(840, 440), new Color(0.07f, 0.07f, 0.11f, 1f));
            var viewportRt = (RectTransform)viewport.transform;
            viewport.gameObject.AddComponent<RectMask2D>();

            // —— 内容（自动高度） ——
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewportRt, false);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0.5f, 1f);
            crt.anchorMax = new Vector2(0.5f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(820, 400f);

            var bodyText = content.AddComponent<Text>();
            bodyText.text = body;
            bodyText.font = Core.UIFactory.UIFont;
            bodyText.color = new Color(0.88f, 0.90f, 0.95f);
            bodyText.alignment = TextAnchor.UpperLeft;
            bodyText.fontSize = 20;
            bodyText.horizontalOverflow = HorizontalWrapMode.Wrap;
            bodyText.verticalOverflow = VerticalWrapMode.Overflow;
            bodyText.lineSpacing = 1.25f;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // —— 滚动 ——
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = crt;
            scroll.viewport = viewportRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

            Core.UIFactory.CreateButton(panel.transform, "Close", Core.Localization.T("legal.close"),
                new Vector2(0, -258), new Vector2(220, 52), () => Core.UIFactory.Destroy(canvasGo));

            return canvasGo;
        }
    }
}
