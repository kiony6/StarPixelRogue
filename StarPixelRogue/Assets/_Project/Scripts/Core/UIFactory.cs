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

        /// <summary>
        /// M53：界面调色板。
        ///
        /// 此前每个界面各自写一遍颜色字面量（同为"面板底"，主菜单是 (0.08,0.09,0.14)，
        /// 结算是另一个数），于是同一款游戏里出了好几种深蓝，改一处不会跟着改第二处。
        /// 这里给出一份共用色阶；界面代码只写"这是面板底 / 这是强调色"，不再自造数值。
        /// </summary>
        public static class Palette
        {
            /// <summary>深空底色：所有面板/按钮的基底。</summary>
            public static readonly Color Deep = new Color(0.06f, 0.07f, 0.12f, 0.94f);
            /// <summary>抬高一档的底色：卡片、分组框。</summary>
            public static readonly Color Raised = new Color(0.13f, 0.15f, 0.24f, 0.96f);
            /// <summary>再抬一档：悬停态/激活态。</summary>
            public static readonly Color Highlight = new Color(0.22f, 0.26f, 0.40f, 0.98f);
            /// <summary>主强调色（星能青）：标题、链接、进行中的进度。</summary>
            public static readonly Color Accent = new Color(0.35f, 0.85f, 1.00f, 1f);
            /// <summary>次强调色（紫罗兰）：次级标题、经验/成长。</summary>
            public static readonly Color AccentAlt = new Color(0.66f, 0.52f, 1.00f, 1f);
            /// <summary>战利金：稀有度、组合技、爆发。</summary>
            public static readonly Color Gold = new Color(1.00f, 0.82f, 0.32f, 1f);
            /// <summary>危险红：血量、受伤。</summary>
            public static readonly Color Danger = new Color(1.00f, 0.28f, 0.34f, 1f);
            /// <summary>生机绿：生命回复、可达状态。</summary>
            public static readonly Color Vital = new Color(0.35f, 0.92f, 0.55f, 1f);
            /// <summary>正文白（略偏冷，衬深色底）。</summary>
            public static readonly Color Text = new Color(0.92f, 0.95f, 1.00f, 1f);
            /// <summary>次要文字：说明、描述。</summary>
            public static readonly Color TextDim = new Color(0.66f, 0.72f, 0.86f, 1f);
            /// <summary>描边色：贴在文字下方拉开对比，保证浅色背景下也读得清。</summary>
            public static readonly Color Outline = new Color(0.02f, 0.03f, 0.06f, 0.85f);
        }

        /// <summary>在指定色上叠加亮度/透明度（不改动原色值本身）。</summary>
        public static Color Tint(Color c, float bright, float alpha = 1f)
        {
            return new Color(Mathf.Clamp01(c.r * bright), Mathf.Clamp01(c.g * bright), Mathf.Clamp01(c.b * bright),
                Mathf.Clamp01(c.a * alpha));
        }

        /// <summary>
        /// M51：结算面板必须压在所有面板之上，否则它的按钮会被别处的全屏遮罩吃掉点击。
        ///
        /// 教训来自联机「离开对局」实测：死亡那一刻如果选卡面板还开着，LevelUpCanvas 里那张
        /// 与屏幕等大的 BG（<see cref="Image"/>，alpha 0 但 raycastTarget 默认开）会盖住
        /// GameOverCanvas 上的按钮。两个 Overlay Canvas 的 sortingOrder 都是 0，
        /// GraphicRaycaster 按 order 排序时二者平级，谁先挡住 Metro 全看 Hierarchy 顺序——
        /// 于是同一份代码在不同机器上"有时点得到、有时点不到"。
        /// 结局与手感双输（点了没反应），所以结算这一层直接钉一个高 order。
        /// </summary>
        public const int TopSortingOrder = 9000;

        /// <summary>
        /// M51：叠在主菜单 / HUD 之上的那一层（设置 / 图鉴 / 选角 / 选卡）。
        ///
        /// 与 <see cref="TopSortingOrder"/> 分层而不是共用，是为了让两件事分开：
        /// 模态只要压得住它底下的那一层（1000），而"玩家的出口"必须压得住一切
        /// （9000）——真到了"设置压在结算之上"那天，需要的正是这两个数字差出来的一次比较。
        /// </summary>
        public const int ModalSortingOrder = 1000;

        /// <summary>
        /// M51：时耗提示遮罩所在的层。
        ///
        /// 它必须压得住选卡面板——预警弹出那一刻，屏幕上常常正停着一张 3 选 1，
        /// 压不住的话这条提示是发给了空气（M49 的 CARD 场景就是问这件事）。
        /// 又必须低于结算：结算上的「离开对局」是玩家唯一的出口，
        /// 任何"挂在屏幕上忘了收"的东西压住它，都等于把人锁死。
        /// </summary>
        public const int HintSortingOrder = 5000;

        /// <summary>主菜单 / HUD 所在层。基准层不抬，别的层才有参照。</summary>
        public const int MenuSortingOrder = 0;

        /// <summary>全屏 Overlay Canvas（自动挂 Scaler + Raycaster）。</summary>
        /// <param name="sortingOrder">覆盖层级；越大越靠前（渲染与射线命中同序）。</param>
        public static Canvas CreateCanvas(string name, Transform parent = null, int sortingOrder = 0)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
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

        /// <summary>按钮（圆角底 + 左侧强调条 + 高光渐变 + 描边文字），返回 Button。</summary>
        public static Button CreateButton(Transform parent, string name, string label, Vector2 pos, Vector2 size,
            Action onClick, int fontSize = 20, Color? bg = null, Color? accent = null)
        {
            Color base_ = bg ?? new Color(0.16f, 0.18f, 0.26f, 0.95f);
            // M53：面板改用九宫格圆角图 —— 此前是一块纯色方块，边缘硬、和背景糊在一起；
            // 圆角 + 顶部高光之后，按钮在深色背景上才"浮"得起来。
            var bgImg = CreateRoundPanel(parent, name, pos, size, base_, gloss: true);
            var go = bgImg.gameObject;

            var btn = go.AddComponent<Button>();
            // M53：状态色改为**乘数**而不是绝对值。原写法给出的是一个绝对色值
            // （0.30,0.34,0.48），它会与本就偏暗的底色相乘，结果是"悬停更暗"——
            // 鼠标放上去按钮反而往背景里缩，与"可点"的直觉正好相反。
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.35f, 1.38f, 1.55f, 1f);
            colors.pressedColor = new Color(0.72f, 0.74f, 0.86f, 1f);
            colors.selectedColor = new Color(1.35f, 1.38f, 1.55f, 1f);
            colors.fadeDuration = 0.08f;
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            // 左侧强调条：一眼看出"这是一颗可点的东西"
            var bar = Round(AddChild(go.transform, "AccentBar", new Vector2(6, size.y * 0.52f)),
                accent ?? Tint(AccentOf(base_), 1.15f));
            bar.rectTransform.anchorMin = bar.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            bar.rectTransform.pivot = new Vector2(0f, 0.5f);
            bar.rectTransform.anchoredPosition = new Vector2(4f, 0f);
            bar.raycastTarget = false;          // 装饰不许参与命中判定

            var text = CreateText(go.transform, "Label", label, Vector2.zero, fontSize);
            text.color = Palette.Text;
            text.raycastTarget = false;
            ApplyOutline(text, Palette.Outline, 2f);
            return btn;
        }

        /// <summary>从底色推出一条同色系的强调色（越亮越偏白，保证暗底上也压得住）。</summary>
        private static Color AccentOf(Color c)
        {
            float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (maxc <= 0.001f) return Palette.Accent;
            return new Color(Mathf.Clamp01(c.r / maxc * 1.25f), Mathf.Clamp01(c.g / maxc * 1.25f),
                Mathf.Clamp01(c.b / maxc * 1.25f), 1f);
        }

        /// <summary>在父物体下建一个占满父物体的子 RectTransform（用于叠渐变/高光层）。</summary>
        private static Transform AddChild(Transform parent, string name, Vector2 size)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            var rt = (RectTransform)child.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            return child.transform;
        }

        /// <summary>给一个 Image 换上圆角九宫格图。<see cref="SpriteFactory.MakeRoundPanel"/> 自带 border。</summary>
        private static Image Round(Transform t, Color color)
        {
            var img = t.gameObject.GetComponent<Image>();
            if (img == null) img = t.gameObject.AddComponent<Image>();
            img.sprite = SpriteFactory.MakeRoundPanel();
            img.type = Image.Type.Sliced;
            img.color = color;
            return img;
        }

        /// <summary>
        /// 圆角面板（可选高光渐变层）。用于卡片 / 分组框 / 按钮底 —— 一切"有边界的小块"。
        /// 全屏遮罩不该用它：那种地方要的是铺满，圆角反而会在屏幕四角留下可疑的缺口。
        /// </summary>
        public static Image CreateRoundPanel(Transform parent, string name, Vector2 pos, Vector2 size,
            Color bg, bool gloss = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = Round(go.transform, bg);
            if (gloss) AddGloss(go.transform, size, Tint(AccentOf(bg), 0.55f));
            return img;
        }

        /// <summary>
        /// 叠一层顶部高光。它的颜色**由调用方的底色派生**，所以面板无论什么颜色都自带层次，
        /// 不需要为每个界面手写一遍"亮一点的那个颜色"。
        /// </summary>
        private static Image AddGloss(Transform parent, Vector2 size, Color tint)
        {
            var child = AddChild(parent, "Gloss", size);
            var img = child.gameObject.AddComponent<Image>();
            img.sprite = SpriteFactory.MakeGloss();
            img.type = Image.Type.Simple;
            img.color = tint;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>给文字加描边与投影。深色底上的浅字看不清时，靠的是这一层对比。</summary>
        public static void ApplyOutline(Text t, Color color, float shadowDistance = 2f)
        {
            if (t == null) return;
            var outline = t.gameObject.GetComponent<Outline>();
            if (outline == null) outline = t.gameObject.AddComponent<Outline>();
            outline.effectColor = color;
            outline.effectDistance = new Vector2(shadowDistance, -shadowDistance);
        }

        /// <summary>标题文字（居中、保留溢出、自带描边）。</summary>
        public static Text CreateTitle(Transform parent, string name, string content, Vector2 pos, int size,
            Vector2? sizeDelta = null, Color? color = null)
        {
            var t = CreateText(parent, name, content, pos, size, TextAnchor.MiddleCenter, sizeDelta);
            t.color = color ?? Palette.Text;
            ApplyOutline(t, Palette.Outline, 2f);
            return t;
        }

        /// <summary>
        /// 把一段文字逐字染成渐变色（生成 rich text 的 &lt;color&gt; 标签）。
        ///
        /// 为什么用 rich text 而不是 shader：本工程不接任何自定义材质（见第 9 条缓存约束），
        /// 而标题那种"从左边的青渐变到右边的紫"如果只给单一 Text.color 是做不出来的。
        /// 逐字染色的代价是每个字多出两个标签，对几个字的标题可以忽略。
        /// </summary>
        public static string GradientRich(string content, Color from, Color to)
        {
            if (string.IsNullOrEmpty(content)) return content;
            var sb = new System.Text.StringBuilder();
            int n = content.Length;
            for (int i = 0; i < n; i++)
            {
                Color c = Color.Lerp(from, to, n > 1 ? i / (float)(n - 1) : 0f);
                sb.Append("<color=").Append(Hex(c)).Append('>').Append(content[i]).Append("</color>");
            }
            return sb.ToString();
        }

        /// <summary>颜色的 #RRGGBB 表示（uGUI 的 rich text 只认这种写法）。</summary>
        public static string Hex(Color c)
        {
            return "#" + ((int)(Mathf.Clamp01(c.r) * 255f)).ToString("X2")
                       + ((int)(Mathf.Clamp01(c.g) * 255f)).ToString("X2")
                       + ((int)(Mathf.Clamp01(c.b) * 255f)).ToString("X2");
        }

        /// <summary>分隔线（分组标题下的那道细线）。</summary>
        public static Image CreateDivider(Transform parent, string name, Vector2 pos, float width, Color color)        {
            var child = AddChild(parent, name, new Vector2(width, 2f));
            ((RectTransform)child).anchoredPosition = pos;
            var img = child.gameObject.AddComponent<Image>();
            img.sprite = SpriteFactory.MakeRoundPanel();
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>滑条（圆角底槽 + 圆角填充），返回 Slider；fillColor 可随后改。</summary>
        public static Slider CreateSlider(Transform parent, string name, Vector2 pos, Vector2 size,
            float value, Color fillColor, Color bgColor)
        {
            var bg = CreateRoundPanel(parent, name, pos, size, bgColor, gloss: false);
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
            // M53：填充条也换成圆角图，端头不再是生硬的直角——这是进度类控件最显眼的一处锯齿
            fillImg.sprite = SpriteFactory.MakeRoundPanel();
            fillImg.type = Image.Type.Sliced;
            fillImg.color = fillColor;
            fillImg.raycastTarget = false;

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

        /// <summary>
        /// M53：进度条（圆角底槽 + 圆角填充 + 外发光），返回 Slider。
        ///
        /// 与 <see cref="CreateSlider"/> 的区别：它不是给玩家拖的——长度由数值驱动、
        /// 不接收输入，故 interactable=false 且各层都不接射线：HUD 上的血条不该吃掉
        /// 玩家点在自己角色上的那一下操作。子节点名沿用旧实现（Fill Area / Fill）。
        /// </summary>
        public static Slider CreateBar(Transform parent, string name, Vector2 pos, Vector2 size,
            Color bgColor, Color fillColor, float value, bool visible, bool glow = true)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            if (glow)
            {
                // 外发光：垫一层按比例放大的柔光。深色背景上这层 halo 让条像"自己在发光"，
                // 比单纯把填充色调亮更通透。
                var haloObj = AddChild(root.transform, "Halo", new Vector2(size.x + 34f, size.y + 30f));
                var halo = haloObj.gameObject.AddComponent<Image>();
                halo.sprite = SpriteFactory.MakeGlow();
                halo.color = new Color(fillColor.r, fillColor.g, fillColor.b, 0.22f);
                halo.raycastTarget = false;
            }

            var bg = Round(root.transform, bgColor);
            bg.raycastTarget = false;

            var slider = root.AddComponent<Slider>();

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            var faRt = (RectTransform)fillArea.transform;
            faRt.anchorMin = new Vector2(0, 0);
            faRt.anchorMax = new Vector2(1, 1);
            faRt.offsetMin = new Vector2(3, 3);
            faRt.offsetMax = new Vector2(-3, -3);

            var fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = new Vector2(0, 0);
            fillRt.anchorMax = new Vector2(1, 1);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            var fillImg = fill.AddComponent<Image>();
            fillImg.sprite = SpriteFactory.MakeRoundPanel();
            fillImg.type = Image.Type.Sliced;
            fillImg.color = fillColor;
            fillImg.raycastTarget = false;

            slider.fillRect = fillRt;
            slider.targetGraphic = bg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = Mathf.Clamp01(value);
            slider.interactable = false;
            slider.transition = Selectable.Transition.None;

            root.SetActive(visible);
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
            // M51：模态遮罩一律站在**提示层**上。它唯一要做的事就是"盖住眼前的一切"，
            // 所以它不能与底下那些层同处 —— 否则"到底盖住了没有"取决于 Unity 的内部排序。
            var canvas = CreateCanvas(name, null, HintSortingOrder);
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
