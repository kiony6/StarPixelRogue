using UnityEngine;

namespace StarPixel.Effects
{
    /// <summary>
    /// 临时视觉特效（无美术资源，纯色缩放+淡出）：
    /// - SpawnRing：Nova 爆发扩散环；
    /// - SpawnTelegraph：Boss 攻击前摇的脉冲警示圈。
    /// </summary>
    public class TempEffect : MonoBehaviour
    {
        public static void SpawnRing(Vector3 pos, float radius, Color color)
        {
            var go = new GameObject("Ring");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.MakeSolidSprite();
            sr.color = color;
            sr.sortingOrder = 3;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 0.2f;
            var e = go.AddComponent<TempEffect>();
            e._targetScale = radius * 2f;
            e._maxLife = 0.35f;
            e._life = 0.35f;
        }

        /// <summary>攻击前摇警示：在指定位置绘制一个会急促闪烁的圈，持续 duration 秒。</summary>
        public static void SpawnTelegraph(Vector3 pos, float radius, Color color, float duration)
        {
            var go = new GameObject("Telegraph");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.MakeSolidSprite();
            sr.color = new Color(color.r, color.g, color.b, 0.2f);
            sr.sortingOrder = 1;   // 位于实体下方，避免遮挡
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (radius * 2f);
            var e = go.AddComponent<TempEffect>();
            e._isTelegraph = true;
            e._teleRadius = radius;
            e._teleMax = Mathf.Max(0.05f, duration);
            e._teleLeft = e._teleMax;
            e._teleColor = color;
        }

        private float _life;
        private float _maxLife;
        private float _targetScale;

        private bool _isTelegraph;
        private float _teleLeft;
        private float _teleMax;
        private float _teleRadius;
        private Color _teleColor;

        void Update()
        {
            if (_isTelegraph)
            {
                UpdateTelegraph();
                return;
            }

            _life -= Time.deltaTime;
            if (_life <= 0f) { Destroy(gameObject); return; }
            float t = 1f - _life / _maxLife;
            transform.localScale = Vector3.one * Mathf.Lerp(0.2f, _targetScale, t);
            var sr = GetComponent<SpriteRenderer>();
            Color c = sr.color;
            c.a = 1f - t;
            sr.color = c;
        }

        private void UpdateTelegraph()
        {
            _teleLeft -= Time.deltaTime;
            if (_teleLeft <= 0f) { Destroy(gameObject); return; }

            float p = 1f - _teleLeft / _teleMax;
            // 越接近释放，闪烁越急促
            float blink = 0.16f + 0.4f * Mathf.Abs(Mathf.Sin(p * Mathf.PI * (3f + p * 8f)));
            var sr = GetComponent<SpriteRenderer>();
            var c = _teleColor;
            c.a = blink;
            sr.color = c;

            // 轻微收缩，表现“蓄力”
            transform.localScale = Vector3.one * (_teleRadius * 2f) * Mathf.Lerp(1f, 0.82f, p);
        }
    }
}
