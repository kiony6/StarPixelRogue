using UnityEngine;

namespace StarPixel.Effects
{
    /// <summary>
    /// 一颗池化粒子（M53）。生命周期由 <see cref="Fx"/> 掌管，本类只负责"把自己演完"。
    ///
    /// 为什么不直接 new / Destroy：<see cref="TempEffect"/> 就是那么做的——每次都要新建
    /// 一个 GameObject、再等 Update 里的倒计时把它销毁，实测到的是「每秒几十个对象在
    /// 创建与回收」。改成池之后，这两个动作都退化成一次 SetActive。
    ///
    /// 为什么每颗粒子自带 Update 而不是由管理器统一遍历：管理器遍历确实更快，
    /// 代价是那张表必须由人记得去维护——漏一次退场，表就带着已经销毁的引用一路攒到
    /// 下一局（M29 的静态委托链正是这个形态）。自带 Update 的实例交给 Unity 的对象
    /// 生命周期兜住：实例销毁即退场，不需要任何人记得去摘它。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class FxSprite : MonoBehaviour
    {
        private SpriteRenderer _sr;
        private Vector2 _vel;
        private float _drag;
        private float _life;
        private float _lifeMax;
        private float _scaleFrom;
        private float _scaleTo;
        private Color _colFrom;
        private Color _colTo;
        private float _spin;
        private bool _running;
        private GameObject _template;

        /// <summary>这一颗是否正在演出（未演出时不该被任何人再动）。</summary>
        public bool IsRunning { get { return _running; } }

        void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
        }

        /// <summary>
        /// 起演。
        ///
        /// 刻意把**全部**表现参数一次性收进来：<see cref="Fx"/> 的每个发射器只是一句 Play，
        /// 不存在"设了这个忘了那个"的半初始化状态——复用实例时字段初始化器不会重跑，
        /// 漏赋一个就会看到上一颗粒子留下的姿态（M27 修的就是这一类）。
        /// </summary>
        public void Play(Vector2 pos, Vector2 vel, float scaleFrom, float scaleTo,
            Color colFrom, Color colTo, float life, float spin, float drag, Sprite sprite, int order)
        {
            _template = Fx.Template;
            transform.position = new Vector3(pos.x, pos.y, 0f);
            _vel = vel;
            _drag = drag;
            _life = _lifeMax = Mathf.Max(0.02f, life);
            _scaleFrom = scaleFrom;
            _scaleTo = scaleTo;
            _colFrom = colFrom;
            _colTo = colTo;
            _spin = spin;
            _running = true;

            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            if (_sr != null)
            {
                _sr.sprite = sprite;
                _sr.sortingOrder = order;
                _sr.flipX = false;
                _sr.flipY = false;
            }
            // 出场朝向随机：整片拖尾 / 火星若朝同一角度，一眼看得出是同一张图复制出来的
            transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            Apply(0f);
        }

        void Update()
        {
            if (!_running) return;
            _life -= Time.deltaTime;
            if (_life <= 0f) { ReturnNow(); return; }

            if (_drag > 0f) _vel *= Mathf.Clamp01(1f - _drag * Time.deltaTime);
            transform.position += (Vector3)(_vel * Time.deltaTime);

            float t = 1f - _life / _lifeMax;
            Apply(t);
            if (_spin != 0f) transform.Rotate(0f, 0f, _spin * Time.deltaTime);
        }

        private void Apply(float t)
        {
            float s = Mathf.Lerp(_scaleFrom, _scaleTo, t);
            transform.localScale = new Vector3(s, s, 1f);
            if (_sr != null) _sr.color = Color.Lerp(_colFrom, _colTo, t);
        }

        /// <summary>
        /// 立即收场并归还给池。
        ///
        /// 只有两条路会走到这里：① 演完（Update 里到点）；② 同屏粒子超过
        /// <see cref="Fx.MaxActive"/>，被 <see cref="Fx"/> 挤掉。后者同样计入回收，
        /// 因此"累计取用 - 累计回收 == 在场数"这条恒等式始终成立，
        /// 探针据此判断有没有粒子卡在半路既不演也不还。
        /// </summary>
        public void ReturnNow()
        {
            if (!_running) return;
            _running = false;
            Fx.Unregister(this);
            if (_template != null && Core.PoolManager.Instance != null)
                Core.PoolManager.Instance.Release(_template, gameObject);
            else
                gameObject.SetActive(false);   // 池已不在（正在拆世界）：先失活，随后被统一销毁
        }

        void OnDisable()
        {
            // 被外力停用（拆世界 / 手动隐藏）时也要出账，否则静态表会一路攒着尸体
            if (_running)
            {
                _running = false;
                Fx.Unregister(this);
            }
        }
    }
}
