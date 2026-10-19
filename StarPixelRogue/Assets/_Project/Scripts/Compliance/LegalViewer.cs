using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Compliance
{
    /// <summary>
    /// 法律文本浏览窗（M5）：可滚动长文本模态，供合规门与设置面板复用。
    /// 纯代码构建（ScrollRect + RectMask2D + ContentSizeFitter），支持滚轮与拖拽。
    ///
    /// M35 新增 <see cref="OpenLocalized"/>：传**键**而不是成品字符串。
    /// 此前合规门传的是 <c>Localization.T(...)</c> 与 <c>LegalTexts.Get(...)</c> 的结果，
    /// 窗口一旦打开就是死的——切语言时标题与正文都停在旧语言（关掉重开才会变）。
    /// 传键之后窗自己订阅 <see cref="Core.Localization.LanguageChanged"/> 就地刷新。
    /// </summary>
    public static class LegalViewer
    {
        /// <summary>打开文本窗（成品字符串版，供无键场景使用）；返回模态根（调用方持以便销毁）。</summary>
        public static GameObject Open(string title, string body)
        {
            var (root, _t, _b, _c) = Build(title, body);
            return root;
        }

        /// <summary>
        /// 打开文本窗（本地化版）：标题走本地化键、正文走法务文档键，
        /// 已打开的窗在切语言时就地刷新（M35）。返回模态根（调用方持以便销毁）。
        /// </summary>
        public static GameObject OpenLocalized(string titleKey, string legalKey)
        {
            var (root, titleText, bodyText, closeText) =
                Build(Core.Localization.T(titleKey), LegalTexts.Get(legalKey));

            var refresher = root.AddComponent<LegalTextRefresher>();
            refresher.TitleText = titleText;
            refresher.BodyText = bodyText;
            refresher.CloseText = closeText;
            refresher.TitleKey = titleKey;
            refresher.LegalKey = legalKey;
            return root;
        }

        /// <summary>构建整棵 UI；返回（模态根 / 标题 Text / 正文 Text / 关闭按钮 Label）。</summary>
        private static (GameObject, Text, Text, Text) Build(string title, string body)
        {
            // CreateModal 返回 (mask, root)：mask 是画布根，root 是遮罩下的挂载点
            var (canvasGo, mask) = Core.UIFactory.CreateModal("LegalViewer", new Color(0.04f, 0.04f, 0.07f, 0.96f));

            var panel = Core.UIFactory.CreatePanel(mask.transform, "Panel", Vector2.zero, new Vector2(920, 600),
                new Color(0.10f, 0.11f, 0.17f, 1f));

            var titleText = Core.UIFactory.CreateText(panel.transform, "Title", title, new Vector2(0, 260), 30);

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

            // 「关闭」按钮的 Label 同样是 Build 时一次性写进去的成品字符串——
            // M35 实测：法务窗正开着时切语言，标题与正文都换了，只有它停在旧语言。
            var closeBtn = Core.UIFactory.CreateButton(panel.transform, "Close",
                Core.Localization.T("legal.close"),
                new Vector2(0, -258), new Vector2(220, 52), () => Core.UIFactory.Destroy(canvasGo));
            var closeText = closeBtn != null ? closeBtn.GetComponentInChildren<Text>() : null;

            return (canvasGo, titleText, bodyText, closeText);
        }
    }

    /// <summary>
    /// 挂在法务窗根上的刷新器：切语言时就地重设标题与正文。
    /// 静态事件必须 OnDestroy 退订（M29 教训），否则窗销毁后回调仍指向已死对象。
    /// </summary>
    internal class LegalTextRefresher : MonoBehaviour
    {
        public Text TitleText;
        public Text BodyText;
        public Text CloseText;
        public string TitleKey;
        public string LegalKey;

        void OnEnable()
        {
            Core.Localization.LanguageChanged += Refresh;
        }

        void OnDestroy()
        {
            Core.Localization.LanguageChanged -= Refresh;
        }

        private void Refresh()
        {
            if (TitleText != null) TitleText.text = Core.Localization.T(TitleKey);
            // 正文按**键**重新取——切语言时 LegalTexts 会走另一条资源路径
            if (BodyText != null) BodyText.text = LegalTexts.Get(LegalKey);
            // 关闭按钮：它也是一次性写进去的成品字符串，漏掉它窗上就会留一句旧语言
            if (CloseText != null) CloseText.text = Core.Localization.T("legal.close");
        }
    }
}
