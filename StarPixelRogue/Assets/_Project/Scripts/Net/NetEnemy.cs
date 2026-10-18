#if FISHNET_ENABLED
using FishNet.Object;
using FishNet.Object.Synchronizing;
using StarPixel.Combat;
using StarPixel.Core;
using StarPixel.Enemy;
using StarPixel.Effects;
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 网络敌人（联机模式，对应 TDD 4.2 / ADR-001 房主权威）。
    /// - 位置由 NetworkTransform 同步；AI/伤害/掉落只在房主（IsServer）运行；
    /// - 客户端只做表现：按 kind 选精灵、受击跳字、Boss 血条（经 SyncVar 回调转 EventBus）。
    /// </summary>
    [RequireComponent(typeof(Damageable))]
    public class NetEnemy : NetworkBehaviour
    {
        private Damageable _dmg;
        private SpriteRenderer _sr;
        private float _speed;
        private float _contactDamage;
        private float _baseScale = 0.45f;
        private float _flashTimer;
        private float _slowTimer;
        private float _slowMult = 1f;
        private float _contactTimer;
        private int _score;
        private bool _elite;
        private bool _boss;
        private bool _bossBarShown;
        private bool _died;   // M18 联机实测：死亡结算只跑一次（任何伤害路径）
        private BossBrain _brain;

        // —— 同步状态（Fish-Net SyncVar；版本 API 差异仅影响本类）——
        private readonly SyncVar<int> _kind = new SyncVar<int>(0);
        private readonly SyncVar<bool> _eliteFlag = new SyncVar<bool>(false);
        private readonly SyncVar<float> _hpRatio = new SyncVar<float>(1f);
        private readonly SyncVar<float> _maxHp = new SyncVar<float>(1f);
        private readonly SyncVar<bool> _isBoss = new SyncVar<bool>(false);
        private readonly SyncVar<float> _lastHit = new SyncVar<float>(0f);
        private readonly SyncVar<int> _hitSeq = new SyncVar<int>(0);
        private readonly SyncVar<int> _phase = new SyncVar<int>(1);   // M6.3：Boss 阶段（客户端染色）
        // M8：前摇警示圈广播——权威侧本地已生成，这里转发给客户端（序号递增确保 OnChange 必触发）
        private readonly SyncVar<int> _telegraphSeq = new SyncVar<int>(0);
        private readonly SyncVar<int> _telegraphKind = new SyncVar<int>(0);   // 0=弹幕 / 1=召唤 / 10+Phase=阶段演出

        private static readonly string[] KindSpriteNames = { "enemy_chaser", "enemy_runner", "enemy_tank" };

        public bool IsDead => _dmg != null && _dmg.isDead;
        public Damageable Damageable => _dmg;

        /// <summary>房主生成时调用（服务器权威配置，经 SyncVar 广播给客户端）。</summary>
        public void ServerInit(EnemyKind kind, float hp, float speed, bool elite, bool boss, int wave)
        {
            _speed = speed;
            _elite = elite;
            _boss = boss;
            _score = boss ? 10 : (kind == EnemyKind.Tank ? 3 : 1) * (elite ? 5 : 1);

            // M4.5：Boss 血量改由调用方按波次传入（400+90*wave，与单机 Boss.Init 一致），
            // 此前这里写死 ×12（6600 血且不随波次成长），与单机版失衡。
            float finalHp = hp * (elite ? 3.2f : 1f);
            _contactDamage = boss ? 14f
                : (kind == EnemyKind.Runner ? 9f : kind == EnemyKind.Tank ? 11f : 6f) * (elite ? 1.6f : 1f);
            _baseScale = boss ? 1.6f
                : (kind == EnemyKind.Tank ? 0.7f : kind == EnemyKind.Runner ? 0.35f : 0.45f) * (elite ? 1.5f : 1f);

            _kind.Value = (int)kind;
            _eliteFlag.Value = elite;
            _maxHp.Value = finalHp;
            _isBoss.Value = boss;
            _hpRatio.Value = 1f;
            _phase.Value = 1;

            // M6.3：Boss 战斗行为（三阶段/弹幕/召唤）由 BossBrain 驱动，
            // 与单机共用同一状态机；弹幕与召唤经 NetWorld 网络生成，客户端可见。
            if (boss)
            {
                _brain = GetComponent<BossBrain>() ?? gameObject.AddComponent<BossBrain>();
                _brain.Init(new BossBrain.Config
                {
                    playerProvider = () => NetPlayerRegistry.NearestAlive(transform.position)?.transform,
                    contactHit = (t, d) =>
                    {
                        var np = t != null ? t.GetComponent<NetPlayer>() : null;
                        if (np == null) return;
                        np.Damageable?.TakeDamage(d);
                        np.ServerRecordHit(d);
                    },
                    fireProjectile = (pos, dir, dmg, speed, hostile) =>
                        NetWorld.SpawnProjectile(pos, dir, dmg, speed, 0f, false, true),
                    summon = (pos, addKind, addHp, addSpeed) =>
                        NetWorld.SpawnEnemy(pos, addKind, addHp, addSpeed, false, false, 0),
                    baseMoveSpeed = 1.1f,
                });
                _brain.PhaseChanged += p => { if (IsServerInitialized) _phase.Value = p; };
                // M8：前摇警示圈广播（权威侧 BossBrain 已本地生成，这里只转发客户端）
                _brain.TelegraphRequested += k =>
                {
                    if (!IsServerInitialized) return;
                    _telegraphKind.Value = k;
                    _telegraphSeq.Value = _telegraphSeq.Value + 1;
                };
            }

            // 房主本地立即应用外观（客户端经 SyncVar 回调/OnStartClient 应用）
            ApplyVisual();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            _dmg = GetComponent<Damageable>();
            _sr = GetComponent<SpriteRenderer>();
            NetEnemyRegistry.Register(this);

            // M18 联机实测修复：死亡管道改挂 Damageable.OnDeath。
            // 此前只有 ServerTakeDamage（弹丸命中）会调用 ServerDie，而 Nova / 光环 / 环绕刃
            // 走的是 CombatTargets.ForEachWithin → 直接 dmgable.TakeDamage(...)，完全绕过它：
            // 敌人被打成 isDead 却既不计击杀、不掉宝石、也不网络反生成，
            // 尸体永久留在 NetEnemyRegistry 里 → AliveEnemyCount 不归零 → 波次卡死
            // （150s 实测卡在第 3 波、场上恒剩 1 个）。挂到 OnDeath 后任何伤害来源都能正确结算。
            if (_dmg != null) _dmg.OnDeath += OnDamageableDeath;

            _kind.OnChange += (_, __, ___) => ApplyVisual();
            _eliteFlag.OnChange += (_, __, ___) => ApplyVisual();
            _hpRatio.OnChange += OnHpRatioChanged;
            _hitSeq.OnChange += OnHitSeqChanged;
            _isBoss.OnChange += OnBossFlagChanged;
            _phase.OnChange += (_, __, ___) => ApplyVisual();   // M6.3：阶段切换刷新染色
            _telegraphSeq.OnChange += OnTelegraphSeqChanged;    // M8：客户端前摇警示圈

            ApplyVisual();

            // 初始 SyncVar 值不触发 OnChange（Fish-Net 行为）：
            // Boss 血条若等 _hpRatio 变化才显示，而满血开战第一次掉血前客户端将没有血条。
            // 这里对纯客户端显式初始化 Boss 血条（房主侧由 EnemyManager.SpawnBoss 直接 Raise）。
            if (!IsServerInitialized && _isBoss.Value) ShowBossBar(_hpRatio.Value);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (_dmg != null) _dmg.OnDeath -= OnDamageableDeath;
            NetEnemyRegistry.Unregister(this);
            if (!IsServerInitialized && _isBoss.Value) EventBus.RaiseBossDefeated();
        }

        // —— 房主权威模拟（联机模式下 AI/伤害/掉落只在此侧运行）——
        void Update()
        {
            if (!IsServerInitialized) return;      // 纯客户端不跑权威逻辑
            if (_dmg == null || _dmg.isDead) return;

            // 受击缩放冲击 + 精英呼吸脉冲（保留像素细节；Boss 与普通敌人共用）
            if (_flashTimer > 0f) _flashTimer -= Time.deltaTime;
            float punch = _flashTimer > 0f ? 1.12f : 1f;
            float breathe = _elite ? 1f + Mathf.Sin(Time.time * 6f) * 0.04f : 1f;
            transform.localScale = Vector3.one * _baseScale * punch * breathe;

            if (_boss)
            {
                // M6.3：Boss 的移动/弹幕/召唤/阶段全部由 BossBrain 驱动
                // （与单机共用同一状态机；接触伤害经 brain 的 contactHit 委托结算）。
                float bossRatio = _dmg.maxHp > 0f ? Mathf.Clamp01(_dmg.currentHp / _dmg.maxHp) : 0f;
                if (Mathf.Abs(bossRatio - _hpRatio.Value) > 0.001f) _hpRatio.Value = bossRatio;
                return;
            }

            var target = NetPlayerRegistry.NearestAlive(transform.position);
            if (target == null) return;

            Vector2 dir = ((Vector2)target.transform.position - (Vector2)transform.position).normalized;
            float spd = _speed * (_slowTimer > 0f ? _slowMult : 1f);
            transform.Translate(dir * spd * Time.deltaTime, Space.World);

            // 接触伤害
            _contactTimer -= Time.deltaTime;
            if (_contactTimer <= 0f &&
                Vector2.Distance(transform.position, target.transform.position) < 0.7f)
            {
                target.Damageable?.TakeDamage(_contactDamage);
                target.ServerRecordHit(_contactDamage);
                _contactTimer = 0.5f;
            }

            float ratio = _dmg.maxHp > 0f ? Mathf.Clamp01(_dmg.currentHp / _dmg.maxHp) : 0f;
            if (Mathf.Abs(ratio - _hpRatio.Value) > 0.001f) _hpRatio.Value = ratio;
        }

        /// <summary>房主侧伤害入口（由 NetProjectile 命中时调用）。</summary>
        public void ServerTakeDamage(float amount, bool applySlow)
        {
            if (!IsServerInitialized || _dmg == null || _dmg.isDead) return;
            if (applySlow) { _slowMult = Mathf.Min(_slowMult, 0.5f); _slowTimer = Mathf.Max(_slowTimer, 1.5f); }

            // 先记命中同步、再扣血：扣血可能触发 OnDeath → ServerDie → 网络反生成，
            // 反生成后再写 SyncVar 等于往已回收的对象上写，会丢掉最后一次受击表现。
            _hitSeq.Value++;
            _lastHit.Value = amount;
            _flashTimer = 0.08f;

            _dmg.TakeDamage(amount);   // 死亡统一由 OnDamageableDeath → ServerDie 结算
        }

        /// <summary>
        /// M18：死亡统一入口（房主侧）。挂 Damageable.OnDeath 而非只挂在 ServerTakeDamage 上，
        /// 这样 Nova / 光环 / 环绕刃等直接调 TakeDamage 的范围伤害也能正确结算击杀与掉落
        /// ——详见 OnStartClient 中的注释。
        /// </summary>
        private void OnDamageableDeath()
        {
            if (!IsServerInitialized) return;     // 伤害本就只在房主侧结算
            if (_died) return;                    // 防止任何路径下的重复结算
            _died = true;
            ServerDie();
        }

        private void ServerDie()
        {
            // 房主结算：击杀事件 + 掉落 + 网络反生成
            EventBus.RaiseEnemyKilled(1);   // 与单机 HUD 语义一致：1 个敌人 = 1 次击杀（经验值另算，见掉落）

            Vector2 pos = transform.position;
            int gems = _elite ? 3 : (_boss ? 14 : 1);
            for (int i = 0; i < gems; i++)
                NetWorld.SpawnGem(pos + Random.insideUnitCircle * 0.4f, _score);

            if (_elite || _boss)
            {
                EventBus.RaiseEliteKilled(pos);
                EventBus.RaiseShake(_boss ? 0.35f : 0.20f, 0.2f);
                NetWorld.SpawnHealth(pos, _boss ? 40f : 30f);
            }
            else if (Random.value < 0.05f)
            {
                NetWorld.SpawnHealth(pos, 15f);
            }

            if (_boss)
            {
                EnemyManager.Instance?.OnBossDefeated();
                EventBus.RaiseBossDefeated();
                SaveManager.Instance?.DiscoverBossDefeat();   // M6.4 图鉴
            }

            NetSpawner.Despawn(gameObject);
        }

        // —— 客户端表现 ——
        private void OnHpRatioChanged(float prev, float next, bool asServer)
        {
            if (asServer) return;
            // 注意：不能加 IsServerInitialized 守卫——联机模式下房主的 Boss 血条
            // 唯一数据源就是本回调的 asServer=false 分支（房主侧无单机 Boss 组件）。
            if (_isBoss.Value) ShowBossBar(next);
        }

        private void OnHitSeqChanged(int prev, int next, bool asServer)
        {
            // 房主侧跳字已由本地 Damageable.showNumbers 路径触发，避免双份跳字
            if (asServer || IsServerInitialized || next <= prev) return;
            EventBus.RaiseDamaged(_lastHit.Value, transform.position);
        }

        private void OnBossFlagChanged(bool prev, bool next, bool asServer)
        {
            if (asServer || !next) return;
            ApplyVisual();
            if (!IsServerInitialized) ShowBossBar(_hpRatio.Value);
        }

        /// <summary>M8：客户端前摇警示圈（权威侧已由 BossBrain 本地生成，此处只补客户端表现）。</summary>
        private void OnTelegraphSeqChanged(int prev, int next, bool asServer)
        {
            if (asServer || next <= prev) return;
            SpawnTelegraphVisual(_telegraphKind.Value);
        }

        private void SpawnTelegraphVisual(int kind)
        {
            // 与 BossBrain 的三个前摇点一一对应（半径/颜色/时长）
            switch (kind)
            {
                case 0:
                    TempEffect.SpawnTelegraph(transform.position, 1.3f, new Color(1f, 0.32f, 0.42f), 0.55f);
                    break;
                case 1:
                    TempEffect.SpawnTelegraph(transform.position, 2.4f, new Color(1f, 0.75f, 0.25f), 0.55f);
                    break;
                case 12:   // P2 转换
                    TempEffect.SpawnTelegraph(transform.position, 3.2f, new Color(1f, 0.78f, 1f), 0.8f);
                    break;
                case 13:   // P3 转换
                    TempEffect.SpawnTelegraph(transform.position, 3.2f, new Color(1f, 0.55f, 0.50f), 0.8f);
                    break;
            }
        }

        /// <summary>Boss 血条：首次出现时 RaiseBossSpawned，之后转发血量比例。</summary>
        private void ShowBossBar(float ratio)
        {
            if (!_bossBarShown)
            {
                _bossBarShown = true;
                EventBus.RaiseBossSpawned(ratio * _maxHp.Value, _maxHp.Value);
            }
            EventBus.RaiseBossHp(ratio);
        }

        private void ApplyVisual()
        {
            if (_sr == null) return;
            if (_eliteFlag.Value || _isBoss.Value)
            {
                _sr.sprite = Core.SpriteFactory.GetSprite(_isBoss.Value ? "boss" : "enemy_elite", Color.white);
            }
            else
            {
                int k = Mathf.Clamp(_kind.Value, 0, KindSpriteNames.Length - 1);
                _sr.sprite = Core.SpriteFactory.GetSprite(KindSpriteNames[k], Color.white);
            }

            // M6.3：Boss 阶段染色（与单机 BossBrain.PhaseTint 一致）
            if (_isBoss.Value)
            {
                _sr.color = _phase.Value >= 3 ? new Color(1f, 0.55f, 0.50f)
                    : _phase.Value == 2 ? new Color(1f, 0.78f, 1f)
                    : Color.white;
            }
            else
            {
                _sr.color = Color.white;
            }
        }
    }
}
#endif
