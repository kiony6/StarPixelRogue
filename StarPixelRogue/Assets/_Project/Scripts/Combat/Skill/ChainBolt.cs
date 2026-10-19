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

        private static void DrawSegment(Vector2 a, Vector2 b, Color color)
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
            go.transform.localScale = new Vector3(len, 0.09f, 1f);   // 细长条 = 电弧

            var e = go.AddComponent<ChainBolt>();
            e._life = Life;
        }

        private static void Flash(Vector2 pos, Color color)
        {
            var go = new GameObject("BoltFlash");   // @owner: registry(_alive)
            _alive.Add(go);
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
                transform.localScale = Vector3.one * Mathf.Lerp(0.9f, 0.2f, 1f - _life / Life);
        }
    }
}
