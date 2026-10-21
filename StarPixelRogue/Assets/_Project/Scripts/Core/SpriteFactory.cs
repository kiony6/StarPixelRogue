using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 精灵工厂（对应 TDD M3 美术接入）。
    /// 优先从 Resources/Sprites/&lt;name&gt; 加载真实像素贴图并以固定 PPU=32 创建 Sprite；
    /// 资源缺失时回退为纯色方块，保证工程在任何情况下都能直接 Play。
    /// PPU=32 使 32px 贴图 = 1 世界单位，与旧纯色方块（8px/PPU8=1 单位）尺寸一致，
    /// 因此所有已有的 localScale 无需改动。
    /// </summary>
    public static class SpriteFactory
    {
        private const float PPU = 32f;
        private static Texture2D _whiteTex;
        private static Sprite _solid;   // M30：共享纯白方块（见 MakeSolidSprite 注释）
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        public static Texture2D WhiteTexture
        {
            get
            {
                if (_whiteTex == null)
                {
                    _whiteTex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                    var px = new Color32[64];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                    _whiteTex.SetPixels32(px);
                    _whiteTex.Apply();
                }
                return _whiteTex;
            }
        }

        public static Sprite MakeSolidSprite() => Solid();

        /// <summary>
        /// 用指定颜色生成 8x8 方块（PPU=8 → 1 世界单位）。
        ///
        /// M30：本方法此前**每次调用都 new 一张纹理**，而它挂在最热的路径上——
        /// 爆发环、Boss 警示圈、链闪电的每段电弧每次生成都要调一次。
        /// 运行时 new 出来的 Texture2D 不挂场景、不进登记表、也没人持有引用去 Destroy：
        /// GC 回收得了托管包装，回收不了 Unity 原生侧那张纹理。
        /// 实测连放 24 轮特效纹理从 29 涨到 173，重开三局涨 18，只涨不降。
        /// 而本类里本来就有一张共享白纹理——MakeSolidSprite 却完全没用它。
        ///
        /// 所有调用点都是 `sr.sprite = ...; sr.color = ...`（颜色由 SpriteRenderer 决定），
        /// 因此纯白这一路直接返回共享实例即可；带染色的按 8bit 量化进缓存，
        /// 调用点的颜色集合有限，不会无界增长。
        /// </summary>
        public static Sprite MakeSolidSprite(Color tint)
        {
            if (tint == Color.white) return Solid();

            var c32 = (Color32)tint;
            string key = "solid:" + c32.r + "_" + c32.g + "_" + c32.b + "_" + c32.a;
            if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;

            var sp = CreateSolid(tint);
            _cache[key] = sp;
            return sp;
        }

        /// <summary>共享的纯白方块：唯一一张，全工程复用。</summary>
        private static Sprite Solid()
        {
            if (_solid != null) return _solid;
            _solid = CreateSolid(Color.white);
            return _solid;
        }

        private static Sprite CreateSolid(Color tint)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var px = new Color32[64];
            var c32 = (Color32)tint;
            for (int i = 0; i < px.Length; i++) px[i] = c32;
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 8f);
        }

        /// <summary>
        /// M53：一次性生成**全部**程序化形状。
        ///
        /// 为什么必须有个提前备好的时机：<see cref="Dev.GpuProbe"/> 的 BURST / RESTART 场景
        /// 要求 24 轮连放特效之后纹理总数**增量为 0**。若这些形状等到"第一次放拖尾时"才建，
        /// 而它的预热路径只放爆发环/警示圈/链闪电，拖尾那一次构造就会被算进**增量**——
        /// 于是正经的一次性构造会被判成"每轮都在泄漏"。形状的脸谱是固定的（key 不随颜色/半径变化），
        /// 放在 <c>GameBuilder.PrepareStage</c> 里建一次，它们就都落进基线之前了。
        /// </summary>
        public static void PrecacheShapes()
        {
            MakeRoundPanel();
            MakeGloss();
            MakeGlow();
            MakeRing();
            MakeStar();
            MakeSparkPixel();
        }

        /// <summary>形状缓存：全部只进不出——数量被 <see cref="PrecacheShapes"/> 的脸谱钉死。</summary>
        private static readonly Dictionary<string, Sprite> _shape = new Dictionary<string, Sprite>();

        private static Sprite Shape(string key, System.Func<Sprite> build)
        {
            if (_shape.TryGetValue(key, out var cached) && cached != null) return cached;
            var sp = build();
            _shape[key] = sp;
            return sp;
        }

        /// <summary>圆角矩形面板（白底+圆角 alpha），带 9 宫格 border，供 uGUI 的 Sliced 图缩放不变形。</summary>
        public static Sprite MakeRoundPanel() => Shape("round-panel", BuildRoundPanel);

        /// <summary>竖向高光渐变（顶部亮、底部透明），叠在面板/按钮上做出"打了一束光"的层次。</summary>
        public static Sprite MakeGloss() => Shape("gloss", BuildGloss);

        /// <summary>径向柔光（中心实、边缘软），所有"会发光的地方"都用它充当光晕。</summary>
        public static Sprite MakeGlow() => Shape("glow", BuildGlow);

        /// <summary>空心冲击环：外缘软、内缘硬，替代此前用实心方块缩放冒充的"扩散环"。</summary>
        public static Sprite MakeRing() => Shape("ring", BuildRing);

        /// <summary>四芒星闪：命中点/爆炸核心用，比单纯的方块放大更像"炸了一下"。</summary>
        public static Sprite MakeStar() => Shape("star", BuildStar);

        /// <summary>像素火花：中间实心、外圈半透的小颗粒，崩解散片用它，比纯方块有质感。</summary>
        public static Sprite MakeSparkPixel() => Shape("spark-pixel", BuildSparkPixel);

        private const int RoundSize = 48;
        private const int RoundCorner = 12;

        private static Sprite BuildRoundPanel()
        {
            int n = RoundSize;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;      // 面板边缘用插值，避免圆角出现台阶
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // 到"圆角盒子"的最近距离 → 覆盖率（0.5 像素软边）
                    float cx = Mathf.Clamp(x + 0.5f, RoundCorner, n - RoundCorner);
                    float cy = Mathf.Clamp(y + 0.5f, RoundCorner, n - RoundCorner);
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float a = Mathf.Clamp01(RoundCorner - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * a));
                }
            tex.SetPixels32(px);
            tex.Apply();
            // border = 圆角半径：九宫格把这圈锁住，拉伸任意尺寸时圆角都不会被拉成椭圆
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 8f,
                0, SpriteMeshType.FullRect, new Vector4(RoundCorner, RoundCorner, RoundCorner, RoundCorner));
        }

        private static Sprite BuildGloss()
        {
            int w = 8, h = 48;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                // 顶半段由透明抬到 0.55，后半段回落：模拟一条斜切的高光带
                float t = (h - 1 - y) / (float)(h - 1);          // 1 = 顶
                float a = t > 0.5f ? (t - 0.5f) * 2f * 0.55f : t * 0.18f;
                var c = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(a)));
                for (int x = 0; x < w; x++) px[y * w + x] = c;
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 8f);
        }

        private static Sprite BuildGlow()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;      // 光晕必须软，点采样会糊成一格格的方块
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * a));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 8f);
        }

        private static Sprite BuildRing()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f;
            // 峰值落在 0.82 半径上：环本身留出厚度，外侧还有一点余晖
            const float at = 0.82f, width = 0.16f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Clamp01(1f - Mathf.Abs(d - at) / width);
                    a *= a;                              // 平方让边缘更利，像冲击波而不是光斑
                    if (d > 1f) a = 0f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * a));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 8f);
        }

        private static Sprite BuildStar()
        {
            const int n = 48;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                    // 横竖两道收得很窄的光条 + 一小团核心
                    float h = Mathf.Exp(-Mathf.Abs(dx) * 34f) * Mathf.Exp(-Mathf.Abs(dy) * 3.4f);
                    float v = Mathf.Exp(-Mathf.Abs(dy) * 34f) * Mathf.Exp(-Mathf.Abs(dx) * 3.4f);
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float core = Mathf.Exp(-r * 13f) * 0.9f;
                    float a = Mathf.Clamp01(h + v + core);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * (a * a)));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 8f);
        }

        private static Sprite BuildSparkPixel()
        {
            const int n = 16;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;          // 保持像素颗粒，这是全 project的美术基座
            // 中间 8x8 实心、外圈一圈半透明：小尺度下看着就是一颗带一点边的火星
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool inner = x >= 4 && x < 12 && y >= 4 && y < 12;
                    bool mid = x >= 2 && x < 14 && y >= 2 && y < 14;
                    byte a = (byte)(inner ? 255 : mid ? 120 : 0);
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 8f);
        }

        /// <summary>
        /// 获取具名精灵：优先加载 Resources/Sprites/&lt;name&gt;，缺失则回退纯色方块。
        /// </summary>
        public static Sprite GetSprite(string name, Color fallbackColor)
        {
            if (_cache.TryGetValue(name, out var cached) && cached != null) return cached;

            var tex = Resources.Load<Texture2D>("Sprites/" + name);
            Sprite sp;
            if (tex != null)
            {
                tex.filterMode = FilterMode.Point;   // 像素风：关闭插值
                sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), PPU);
            }
            else
            {
                sp = MakeSolidSprite(fallbackColor);
            }
            _cache[name] = sp;
            return sp;
        }
    }
}
