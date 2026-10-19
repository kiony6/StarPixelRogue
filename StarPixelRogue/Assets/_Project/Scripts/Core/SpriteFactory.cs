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
