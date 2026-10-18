using UnityEngine;
using StarPixel.Combat;
using StarPixel.Pickups;

namespace StarPixel.Enemy
{
    /// <summary>敌人种类：追击 / 疾行 / 重甲（对应 TDD M2 敌人多样化）。</summary>
    public enum EnemyKind { Chaser, Runner, Tank }

    /// <summary>
    /// 敌人实体：追击 AI + 接触伤害 + 受击反馈 + 减速 + 死亡掉落（对应 TDD 4.2 / M2 / M3.5）。
    /// M3.5 起支持「精英怪」：更大体型、约 3 倍血量、金色专属外观、死亡掉多颗宝石并且必掉回血球。
    /// 由对象池复用。
    /// </summary>
    [RequireComponent(typeof(Damageable))]
    public class Enemy : MonoBehaviour
    {
        public Damageable damageable { get; private set; }
        public float moveSpeed = 2.5f;
        public float contactDamage = 8f;
        public float contactCooldown = 0.5f;
        public int scoreValue = 1;     // 同时作为经验值
        public bool isElite { get; private set; }

        private float _contactTimer;
        private GameObject _template;
        private Transform _playerTransform;
        private Damageable _playerDamageable;

        private SpriteRenderer _sr;
        private Color _baseColor;
        private EnemyKind _kind;
        private float _flashTimer;
        private float _slowTimer;
        private float _slowMult = 1f;
        private float _baseScale = 0.45f;

        private static readonly Color[] KindColors = { Color.red, Color.yellow, new Color(0.3f, 0.9f, 0.3f) };
        private static readonly float[] KindScales = { 0.45f, 0.35f, 0.7f };
        private static readonly string[] KindSpriteNames = { "enemy_chaser", "enemy_runner", "enemy_tank" };

        void Awake()
        {
            damageable = GetComponent<Damageable>();
            damageable.OnDeath += OnDeath;
            damageable.OnHpChanged += _ => _flashTimer = 0.08f;
            _sr = GetComponent<SpriteRenderer>();
        }

        public void Spawn(Vector2 pos, float hp, float speed, EnemyKind kind, GameObject template,
            Transform playerTransform, Damageable playerDamageable, bool elite = false)
        {
            _template = template;
            _playerTransform = playerTransform;
            _playerDamageable = playerDamageable;
            transform.position = pos;
            moveSpeed = speed;
            _contactTimer = 0f;
            _slowTimer = 0f;
            _slowMult = 1f;
            _flashTimer = 0f;
            _kind = kind;
            isElite = elite;
            _baseColor = KindColors[(int)kind];

            if (elite)
            {
                // 精英：金色装甲外观 + 更大体型 + 明显更高血量
                _sr.sprite = Core.SpriteFactory.GetSprite("enemy_elite", new Color(1f, 0.8f, 0.2f));
                _baseScale = KindScales[(int)kind] * 1.5f;
                damageable.Init(hp * 3.2f);
            }
            else
            {
                _sr.sprite = Core.SpriteFactory.GetSprite(KindSpriteNames[(int)kind], _baseColor);
                _baseScale = KindScales[(int)kind];
                damageable.Init(hp);
            }

            _sr.color = Color.white;   // 保留精灵原始配色（缺失时 GetSprite 已用基色方块回退）
            transform.localScale = Vector3.one * _baseScale;
            gameObject.SetActive(true);
        }

        void Update()
        {
            if (damageable.isDead) return;

            // 减速计时
            if (_slowTimer > 0f)
            {
                _slowTimer -= Time.deltaTime;
                if (_slowTimer <= 0f) _slowMult = 1f;
            }

            // 受击反馈：缩放冲击（保留精灵细节，避免纯色 tint 吃掉像素画）
            if (_flashTimer > 0f) _flashTimer -= Time.deltaTime;
            float punch = _flashTimer > 0f ? 1.12f : 1f;
            // 精英：常驻轻微呼吸脉冲，视觉上更“有威胁”
            float breathe = isElite ? 1f + Mathf.Sin(Time.time * 6f) * 0.04f : 1f;
            transform.localScale = Vector3.one * _baseScale * punch * breathe;

            _contactTimer -= Time.deltaTime;
            if (_playerTransform == null) return;

            Vector2 dir = ((Vector2)_playerTransform.position - (Vector2)transform.position).normalized;
            transform.Translate(dir * moveSpeed * _slowMult * Time.deltaTime, Space.World);

            if (_contactTimer <= 0f && Vector2.Distance(transform.position, _playerTransform.position) < 0.7f)
            {
                _playerDamageable?.TakeDamage(contactDamage);
                _contactTimer = contactCooldown;
            }
        }

        /// <summary>组合技/技能施加减速。</summary>
        public void ApplySlow(float factor, float duration)
        {
            _slowMult = Mathf.Min(_slowMult, factor);
            _slowTimer = Mathf.Max(_slowTimer, duration);
        }

        private void OnDeath()
        {
            EnemyManager.Instance.RemoveEnemy(this);
            Core.EventBus.RaiseEnemyKilled(scoreValue);

            Vector2 pos = transform.position;
            int gems = isElite ? 3 : 1;
            for (int i = 0; i < gems; i++)
                PickupManager.Instance?.DropGem((Vector2)pos + Random.insideUnitCircle * 0.4f, scoreValue);

            if (isElite)
            {
                // 精英死亡：震屏 + 专属事件 + 必掉回血球
                Core.EventBus.RaiseEliteKilled(pos);
                Core.EventBus.RaiseShake(0.20f, 0.16f);
                PickupManager.Instance?.DropHealth(pos, 30f);
            }
            else if (Random.value < 0.05f)
            {
                // 普通怪小概率掉回血
                PickupManager.Instance?.DropHealth(pos, 15f);
            }

            if (_template != null) Core.PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
