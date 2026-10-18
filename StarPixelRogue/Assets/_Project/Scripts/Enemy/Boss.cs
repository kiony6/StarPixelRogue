using UnityEngine;
using StarPixel.Combat;
using StarPixel.Core;
using StarPixel.Pickups;

namespace StarPixel.Enemy
{
    /// <summary>
    /// Boss 实体（对应 TDD M3 Boss 战 / M3.5 前摇预警 / M6.3 三阶段）。
    /// M6.3 起战斗行为（移动/弹幕/召唤/阶段）全部由 <see cref="BossBrain"/> 驱动——
    /// 单机与联机共用同一份状态机；本类只负责单机接线（对象池/EnemyManager）、
    /// 血量事件与死亡掉落。
    /// </summary>
    [RequireComponent(typeof(Damageable))]
    public class Boss : MonoBehaviour
    {
        public Damageable damageable { get; private set; }
        public BossBrain Brain { get; private set; }

        private Transform _player;
        private Damageable _playerDmg;
        private GameObject _projTemplate;
        private EnemyManager _manager;
        private SpriteRenderer _sr;
        private float _flash;
        private float _hpEventTimer;

        void Awake()
        {
            damageable = GetComponent<Damageable>();
            damageable.showNumbers = true;
            damageable.OnDeath += OnDeath;
            damageable.OnHpChanged += _ => _flash = 0.1f;
            _sr = GetComponent<SpriteRenderer>();
        }

        public void Init(Transform player, Damageable playerDmg, GameObject projTemplate, EnemyManager manager, int wave, int variant = 0)
        {
            _player = player;
            _playerDmg = playerDmg;
            _projTemplate = projTemplate;
            _manager = manager;

            // M3.6 平衡：由 550+130/wave 下调（原 Wave10 Boss 1860 血过于肉）
            float hp = 400f + 90f * Mathf.Max(1, wave);
            damageable.Init(hp);

            transform.position = player != null
                ? (Vector2)player.position + new Vector2(0f, 6f)
                : new Vector2(0f, 6f);

            Brain = GetComponent<BossBrain>() ?? gameObject.AddComponent<BossBrain>();
            Brain.Init(new BossBrain.Config
            {
                playerProvider = () => _player,
                contactHit = (_, d) => _playerDmg?.TakeDamage(d),
                fireProjectile = (pos, dir, dmg, speed, _) =>
                {
                    if (_projTemplate == null) return;
                    var go = Core.PoolManager.Instance.Get(_projTemplate);
                    go.GetComponent<EnemyProjectile>().Launch(pos, dir, dmg, speed, _playerDmg, _projTemplate);
                },
                summon = (pos, kind, addHp, addSpeed) => _manager?.SpawnAdd(pos, kind, addHp, addSpeed),
                baseMoveSpeed = 1.1f,
                variant = variant,   // M9：变体选择
            });

            Core.EventBus.RaiseBossSpawned(hp, hp);
            Core.EventBus.RaiseBossHp(1f);
            Core.EventBus.RaiseShake(0.45f, 0.35f);
            // M8 手感：出场瞬间镜头推近再回弹（配合上面的震屏，强化"Boss 登场"仪式感）
            Core.CameraShake.Instance?.ZoomPulse(0.72f, 0.55f);
            Core.SfxManager.Instance?.Play("boss");
        }

        void Update()
        {
            if (damageable.isDead) return;

            // 受击反馈：短暂泛红（阶段染色由 BossBrain.PhaseTint 决定）
            if (_flash > 0f)
            {
                _flash -= Time.deltaTime;
                if (_sr != null) _sr.color = new Color(1f, 0.55f, 0.55f);
            }
            else if (_sr != null)
            {
                _sr.color = Brain != null ? Brain.PhaseTint : Color.white;
            }

            // 血条刷新（限频）
            _hpEventTimer -= Time.deltaTime;
            if (_hpEventTimer <= 0f)
            {
                _hpEventTimer = 0.1f;
                float ratio = damageable.maxHp > 0f ? damageable.currentHp / damageable.maxHp : 0f;
                Core.EventBus.RaiseBossHp(ratio);
            }
        }

        private void OnDeath()
        {
            Core.EventBus.RaiseBossDefeated();
            Core.EventBus.RaiseShake(0.6f, 0.5f);
            Core.SaveManager.Instance?.DiscoverBossDefeat();   // M6.4 图鉴

            Vector2 pos = transform.position;
            // 大爆奖：大量经验宝石 + 回血球
            for (int i = 0; i < 14; i++)
                PickupManager.Instance?.DropGem((Vector2)pos + Random.insideUnitCircle * 1.6f, 4f);
            PickupManager.Instance?.DropHealth(pos, 40f);

            _manager?.OnBossDefeated();
            Destroy(gameObject);
        }
    }
}
