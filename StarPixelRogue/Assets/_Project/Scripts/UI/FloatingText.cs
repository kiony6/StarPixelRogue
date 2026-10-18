using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 跳字伤害：屏幕空间上浮 + 淡出（对应 TDD M2 打击感）。
    /// </summary>
    public class FloatingText : MonoBehaviour
    {
        private Vector3 _start;
        private float _life = 0.6f;
        private readonly float _maxLife = 0.6f;

        public void Init(Vector3 screenPos)
        {
            _start = screenPos;
            var rt = GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.anchoredPosition = screenPos;
        }

        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Destroy(gameObject); return; }
            float t = 1f - _life / _maxLife;
            var rt = GetComponent<RectTransform>();
            rt.anchoredPosition = _start + new Vector3(0f, t * 60f, 0f);
            var t2 = GetComponent<Text>();
            Color c = t2.color;
            c.a = 1f - t;
            t2.color = c;
        }
    }
}
