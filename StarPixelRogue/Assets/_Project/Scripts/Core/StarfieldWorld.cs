using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 对局场景的视差星空（M53）。
    ///
    /// 为什么要有它：此前的背景就是相机的纯色 clear color——玩家在"一片什么都没有的
    /// 深蓝里"移动，分不清是自己走得慢还是世界根本没动。加上一层随距离分档减速的星点
    /// 之后，速度感才有了参照物，这也是像素弹幕游戏里最常见的做法。
    ///
    /// 实现要点有两个：
    ///   1. **无限铺陈**：星点按一块远大于视口的"地砖"图案排布，每帧把每颗星相对相机
    ///      取模绕回，于是一百来颗星就够铺满任何尺寸的画面，且永远不会走空；
    ///   2. **视差**：屏幕位置 = 图案坐标 - 相机位移 ×(1-k)，k 越大越"远"（走得越慢）。
    ///      k=1 时星星被钉在相机上——那是无限远，看着像贴片，故统一取 0.5~0.85 的中距离。
    ///
    /// 由 GameBuilder 创建并 Track，随世界一起拆。
    /// </summary>
    public class StarfieldWorld : MonoBehaviour
    {
        /// <summary>地砖尺寸。必须显著大于视口（正交 8 → 高 16），否则会看见图案重复。</summary>
        private const float TileW = 64f;
        private const float TileH = 48f;

        private class Star
        {
            public Transform tr;
            public SpriteRenderer sr;
            public Vector2 pattern;     // 地砖内的固定坐标
            public float k;             // 视差系数：越大越远
            public float baseAlpha;
            public float twinkleRate;
            public float phase;
        }

        private readonly List<Star> _stars = new List<Star>();

        /// <summary>
        /// 生成星场。
        /// </summary>
        public void Build(int count, int nebulaCount)
        {
            for (int i = 0; i < count; i++)
            {
                float depth = Random.value;
                float size = depth > 0.92f ? Random.Range(0.13f, 0.20f)
                           : depth > 0.6f ? Random.Range(0.07f, 0.12f)
                                          : Random.Range(0.04f, 0.07f);
                float k = Mathf.Lerp(0.55f, 0.9f, depth);
                Color c = depth > 0.92f ? new Color(1f, 0.95f, 0.85f)
                        : depth > 0.6f ? new Color(0.8f, 0.9f, 1f)
                                       : new Color(0.6f, 0.72f, 1f);

                var go = new GameObject("Star");
                go.transform.SetParent(transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = SpriteFactory.MakeRoundPanel();
                sr.color = new Color(c.r, c.g, c.b, Random.Range(0.35f, 0.9f));
                sr.sortingOrder = -100;          // 压在所有实体之下
                go.transform.localScale = new Vector3(size, size, 1f);

                _stars.Add(new Star
                {
                    tr = go.transform,
                    sr = sr,
                    pattern = new Vector2(Random.Range(0f, TileW), Random.Range(0f, TileH)),
                    k = k,
                    baseAlpha = sr.color.a,
                    twinkleRate = Random.Range(0.4f, 1.8f),
                    phase = Random.Range(0f, Mathf.PI * 2f),
                });
            }

            // 几团星云：给纯黑的底加一点色彩层次。数量刻意很少，
            // 它们的成本随面积走，铺多了等同于在每一帧给 GPU 多糊几张大透明面片。
            for (int i = 0; i < nebulaCount; i++)
            {
                var go = new GameObject("Nebula");
                go.transform.SetParent(transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = SpriteFactory.MakeGlow();
                Color tint = i % 3 == 0 ? new Color(0.35f, 0.55f, 1f)
                           : i % 3 == 1 ? new Color(0.75f, 0.4f, 1f)
                                        : new Color(0.25f, 0.85f, 1f);
                sr.color = new Color(tint.r, tint.g, tint.b, 0.10f);
                sr.sortingOrder = -110;
                float size = Random.Range(14f, 26f);
                go.transform.localScale = new Vector3(size, size, 1f);

                _stars.Add(new Star
                {
                    tr = go.transform,
                    sr = sr,
                    pattern = new Vector2(Random.Range(0f, TileW), Random.Range(0f, TileH)),
                    k = 0.85f,                   // 比星点更远：动得最慢
                    baseAlpha = sr.color.a,
                    twinkleRate = 0f,            // 星云不闪，闪了就像坏掉的灯泡
                    phase = 0f,
                });
            }
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector2 c = cam.transform.position;
            float t = Time.unscaledTime;

            for (int i = 0; i < _stars.Count; i++)
            {
                var s = _stars[i];
                if (s.tr == null) continue;

                // 屏幕坐标 = 图案坐标 - 相机位移 ×(1-k)，再按地砖尺寸绕回相机周围
                Vector2 screen = s.pattern - c * (1f - s.k);
                screen.x = Repeat(screen.x + TileW * 0.5f, TileW) - TileW * 0.5f;
                screen.y = Repeat(screen.y + TileH * 0.5f, TileH) - TileH * 0.5f;
                s.tr.position = new Vector3(c.x + screen.x, c.y + screen.y, 0f);

                if (s.sr != null && s.twinkleRate > 0f)
                {
                    float tw = 0.7f + 0.3f * Mathf.Sin(t * s.twinkleRate + s.phase);
                    var col = s.sr.color;
                    col.a = Mathf.Clamp01(s.baseAlpha * tw);
                    s.sr.color = col;
                }
            }
        }

        private static float Repeat(float v, float len)
        {
            float r = v % len;
            return r < 0f ? r + len : r;
        }
    }
}
