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

        public static Sprite MakeSolidSprite() => MakeSolidSprite(Color.white);

        /// <summary>用指定颜色生成 8x8 方块（PPU=8 → 1 世界单位）。</summary>
        public static Sprite MakeSolidSprite(Color tint)
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
