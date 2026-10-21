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

        // M28：存活登记表。此前 BoltSeg / BoltFlash 与 M26 修过的警示圈是**同一个缺陷形态**——
        // new GameObject 建在场景根节点上、无人登记、只靠 Update 里的计时自毁。
        // 而自毁依赖时间推进：选卡/结算面板的 timeScale=0 会把它们冻住，
        // 重开一局后它们整批出现在新世界的开局画面里（电弧横在屏幕上一动不动）。
        private static readonly List<GameObject> _alive = new List<GameObject>();

        /// <summary>拆世界时清掉全部在飞的链闪电（由 GameBuilder.TeardownWorld 调用）。</summary>
        public static void DestroyAll()
        {
            foreach (var go in _alive)
                if (go != null) Object.Destroy(go);
            _alive.Clear();
        }

        /// <summary>
        /// 画一段电弧。
        ///
        /// M53：直线长条改成**带抖动的折线**。此前每跳是一根笔直的细长方块，
        /// 连起来像几根牙签；把每一跳拆成若干带随机横向偏移的小段之后，
        /// 才读得出"一道电流扯了过去"。
        /// </summary>
        private static void DrawSegment(Vector2 a, Vector2 b, Color color)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f) return;

            // 段数随长度走但封顶：短跳用 2 段就够，长跳最多 4 段 —— 超过之后
            // 视觉收益递减而对象数还在涨，且 OrphanProbe 的 CHAIN 场景按名字数对象。
            const int maxSubs = 4;
            int subs = Mathf.Clamp(Mathf.RoundToInt(len / 0.55f), 2, maxSubs);
            Vector2 prev = a;
            for (int i = 1; i <= subs; i++)
            {
                Vector2 to = (i == subs) ? b : Vector2.Lerp(a, b, i / (float)subs);
                if (i < subs)
                {
                    // 中点横向抖动：偏移量随段长缩放，短跳不至于抖成一个锯齿团
                    Vector2 perp = Perp(to - prev).normalized;
                    to += perp * Random.Range(-0.16f, 0.16f) * Mathf.Min(1f, len);
                }
                DrawSub(prev, to, color, i == subs ? 0.075f : 0.065f);
                prev = to;
            }
        }

        private static Vector2 Perp(Vector2 v) => new Vector2(-v.y, v.x);

        private static void DrawSub(Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.01f) return;

            var go = new GameObject("BoltSeg");   // @owner: registry(_alive)
            _alive.Add(go);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteFactory.MakeSolidSprite();
            sr.color = color;
            sr.sortingOrder = 9;
            go.transform.position = (a + b) * 0.5f;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            go.transform.rotation = Quaternion.Euler(0f, 0f, angle);
            go.transform.localScale = new Vector3(len, thickness, 1f);   // 细长条 = 电弧

            var e = go.AddComponent<ChainBolt>();
            e._life = Life;
        }

        /// <summary>
        /// 命中点闪光。M53：精灵由实心方块换成四芒星，
        /// 于是"跳到下一个人身上"这件事有了一个明确的落点。
        /// </summary>
        private static void Flash(Vector2 pos, Color color)
        {
            var go = new GameObject("BoltFlash");   // @owner: registry(_alive)
            _alive.Add(go);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteFactory.MakeStar();
            sr.color = color;
            sr.sortingOrder = 9;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 0.5f;

            var e = go.AddComponent<ChainBolt>();
            e._life = Life * 1.4f;
            e._shrink = true;
            e._star = true;

            // 在每一个被电到的目标脚下垫一团余晖（走池化通道，不计入本类的登记表）
            StarPixel.Effects.Fx.Glow(pos, color, 1.3f, 0.24f, 2);
            StarPixel.Effects.Fx.Spray(pos, Vector2.up, color, 3, 2.6f, 0.2f, 0.22f, 1.2f, 6);
        }

        private float _life;
        private bool _shrink;
        private bool _star;      // M53：星芒命中使用另一条缩放曲线（缩得更快、留痕更短）

        // 自毁时也要出表，否则 _alive 会一直攒着已销毁的引用（生命周期内命中越多、表越长）。
        void OnDestroy()
        {
            _alive.Remove(gameObject);
        }

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
                transform.localScale = Vector3.one *
                    (_star ? Mathf.Lerp(1.5f, 0.15f, 1f - _life / Life)
                           : Mathf.Lerp(0.9f, 0.2f, 1f - _life / Life));
        }
    }
}
