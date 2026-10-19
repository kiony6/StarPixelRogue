using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 跳字伤害：屏幕空间上浮 + 淡出（对应 TDD M2 打击感）。
    ///
    /// M27：改为对象池复用。此前每次伤害都 new GameObject + Text，
    /// 重战况下每秒可达数百次分配，与项目「对象池抗 GC」的原则相悖；
    /// 现在由 <see cref="DamageNumberManager"/> 从模板克隆、到期归还。
    /// </summary>
    public class FloatingText : MonoBehaviour
    {
        private Vector3 _start;
        private float _life;
        private readonly float _maxLife = 0.6f;
        private Text _text;
        private Core.PoolManager _pool;
        private GameObject _template;

        /// <summary>
        /// 每次取出（含复用）都必须调用。
        ///
        /// **必须在 Init 里重置生命周期与透明度**——字段初始化器只在 AddComponent 时跑一次，
        /// 复用时不会重跑。漏掉重置的话，第二次被复用的实例 _life 仍是 0，
        /// 会在 Update 里立刻归还，玩家看到的就是「伤害数字只出现前几次，之后全消失」。
        /// </summary>
        public void Init(Vector3 screenPos, Core.PoolManager pool, GameObject template)
        {
            _start = screenPos;
            _pool = pool;
            _template = template;
            _life = _maxLife;

            if (_text == null) _text = GetComponent<Text>();
            if (_text != null) _text.color = new Color(1f, 0.85f, 0.2f, 1f);

            var rt = GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.anchoredPosition = screenPos;
        }

        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Expire(); return; }

            float t = 1f - _life / _maxLife;
            var rt = GetComponent<RectTransform>();
            rt.anchoredPosition = _start + new Vector3(0f, t * 60f, 0f);

            if (_text == null) _text = GetComponent<Text>();
            if (_text == null) return;
            Color c = _text.color;
            c.a = 1f - t;
            _text.color = c;
        }

        /// <summary>到期：有池就归还，没有池（或被单独 new 出来）才真的销毁。</summary>
        private void Expire()
        {
            if (_pool != null && _template != null)
            {
                _pool.Release(_template, gameObject);
                return;
            }
            Destroy(gameObject);
        }
    }
}
