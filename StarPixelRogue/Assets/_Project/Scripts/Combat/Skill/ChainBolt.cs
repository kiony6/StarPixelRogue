using System.Collections.Generic;
using UnityEngine;
using StarPixel.Core;

namespace StarPixel.Combat.Skill
{
    /// <summary>
    /// 链闪电特效（M6.1「雷霆之链」表现层）：沿给定折线绘制短暂电弧，命中点附带闪光。
    /// 纯表现，无伤害逻辑；伤害由 <see cref="PlayerController"/> 在权威侧结算。
    /// </summary>
    public class ChainBolt : MonoBehaviour
    {
        private const float Life = 0.16f;

        /// <summary>
        /// 绘制一条闪电：origin 为起点，points 为依次命中的世界坐标（至少 1 个）。
        /// </summary>
        public static void Spawn(Vector2 origin, IList<Vector2> points, Color color)
        {
            if (points == null || points.Count == 0) return;

            Vector2 prev = origin;
            for (int i = 0; i < points.Count; i++)
            {
                DrawSegment(prev, points[i], color);
                Flash(points[i], color);
                prev = points[i];
            }
        }

        private static void DrawSegment(Vector2 a, Vector2 b, Color color)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f) return;

            var go = new GameObject("BoltSeg");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteFactory.MakeSolidSprite();
            sr.color = color;
            sr.sortingOrder = 9;
            go.transform.position = (a + b) * 0.5f;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            go.transform.rotation = Quaternion.Euler(0f, 0f, angle);
            go.transform.localScale = new Vector3(len, 0.09f, 1f);   // 细长条 = 电弧

            var e = go.AddComponent<ChainBolt>();
            e._life = Life;
        }

        private static void Flash(Vector2 pos, Color color)
        {
            var go = new GameObject("BoltFlash");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteFactory.MakeSolidSprite();
            sr.color = color;
            sr.sortingOrder = 9;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 0.5f;

            var e = go.AddComponent<ChainBolt>();
            e._life = Life * 1.4f;
            e._shrink = true;
        }

        private float _life;
        private bool _shrink;

        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Destroy(gameObject); return; }
            var sr = GetComponent<SpriteRenderer>();
            if (sr == null) { Destroy(gameObject); return; }
            var c = sr.color;
            c.a = Mathf.Clamp01(_life / Life);
            sr.color = c;
            if (_shrink)
                transform.localScale = Vector3.one * Mathf.Lerp(0.9f, 0.2f, 1f - _life / Life);
        }
    }
}
