using System;
using UnityEngine;
using StarPixel.Combat;   // Damageable（M17 实测修复：此前缺失导致 BossBrain 无法编译）
using StarPixel.Core;
using StarPixel.Effects;
using StarPixel.Net;

namespace StarPixel.Enemy
{
    /// <summary>
    /// Boss 大脑（M6.3）：三阶段战斗状态机，单机与联机共用同一份逻辑。
    /// - 阶段随血量递进：P1（100%–66%）/ P2（66%–33%）/ P3（&lt;33%），
    ///   移动速度、弹幕密度与数量、召唤频率、接触伤害逐级提升，阶段转换有演出（震屏/音效/警示圈）；
    /// - 权威守卫 <see cref="NetSession.IsAuthority"/>：单机（Offline）与联机房主运行，纯客户端不跑；
    /// - 攻击/召唤经 <see cref="Config"/> 委托注入：单机走对象池与 EnemyManager，
    ///   联机走 <see cref="NetWorld"/> 网络生成（客户端经 NetworkTransform 同步）。
    /// </summary>
    [RequireComponent(typeof(Damageable))]
    public class BossBrain : MonoBehaviour
    {
        /// <summary>宿主注入的行为委托（单机/联机各自接线）。</summary>
        public class Config
        {
            /// <summary>当前目标玩家（每帧求值；联机为最近存活玩家）。</summary>
            public Func<Transform> playerProvider;
            /// <summary>接触伤害：(目标 Transform, 伤害值)。</summary>
            public Action<Transform, float> contactHit;
            /// <summary>发射弹丸：(位置, 方向, 伤害, 速度, hostile)。</summary>
            public Action<Vector3, Vector2, float, float, bool> fireProjectile;
            /// <summary>召唤小怪：(位置, 种类, 血量, 速度)。</summary>
            public Action<Vector2, EnemyKind, float, float> summon;
            /// <summary>基础移动速度（P1）。</summary>
            public float baseMoveSpeed = 1.1f;
            /// <summary>M9/M12：Boss 变体。0=守望者（径向弹幕+召唤）；1=风暴领主（螺旋弹幕+突进，无召唤）；2=虚空编织者（瞄准式狙击散射+扩散环，不召唤不突进）。</summary>
            public int variant = 0;
        }

        public Damageable damageable { get; private set; }

        /// <summary>当前阶段（1..3）。</summary>
        public int Phase { get; private set; } = 1;

        /// <summary>阶段染色（Boss 外观随阶段变化；变体 1「风暴领主」走青蓝调，与守望者区分）。</summary>
        public Color PhaseTint =>
            _variant == 1
            ? (Phase >= 3 ? new Color(0.40f, 1f, 1f)
               : Phase == 2 ? new Color(0.50f, 0.80f, 1f)
               : new Color(0.60f, 0.95f, 1f))
            : _variant == 2
            ? (Phase >= 3 ? new Color(1f, 0.40f, 0.85f)
               : Phase == 2 ? new Color(0.75f, 0.40f, 1f)
               : new Color(0.85f, 0.60f, 1f))
            : (Phase >= 3 ? new Color(1f, 0.55f, 0.50f)
               : Phase == 2 ? new Color(1f, 0.78f, 1f)
               : Color.white);

        /// <summary>阶段切换事件（联机宿主据此写 SyncVar 广播给客户端）。</summary>
        public event Action<int> PhaseChanged;

        /// <summary>
        /// M8：前摇警示圈事件（半径类型 kind：0=弹幕前摇 / 1=召唤前摇 / 10+Phase=阶段转换演出）。
        /// 权威侧已本地生成 <see cref="TempEffect.SpawnTelegraph"/>，本事件仅供联机宿主
        /// 转发给客户端（单机无人订阅，行为不变）。
        /// </summary>
        public event Action<int> TelegraphRequested;

        private Config _cfg;
        private SpriteRenderer _sr;

        private float _burstTimer = 2.5f;
        private float _summonTimer = 6f;
        private float _contactTimer;
        private bool _burstWarned;
        private bool _summonWarned;

        private int _variant;          // M9：Boss 变体
        private float _spiralAngle;    // M9：风暴领主螺旋相位
        private float _dashTimer;      // M9：突进计时
        private bool _dashing;         // M9：是否正在突进
        private float _dashTime;       // M9：本次突进剩余时间
        private Vector2 _dashDir;      // M9：突进方向
        private bool _dashWarned;      // M9：突进前摇是否已预警

        private float _aimTimer;       // M12：虚空编织者瞄准射击计时
        private bool _aimWarned;       // M12：瞄准前摇是否已预警
        private const float AimLead = 0.4f;  // M12：瞄准前摇提前量

        private const float TelegraphLead = 0.55f;

        void Awake()
        {
            damageable = GetComponent<Damageable>();
            _sr = GetComponent<SpriteRenderer>();
        }

        public void Init(Config cfg)
        {
            _cfg = cfg;
            _variant = cfg.variant;
            Phase = 1;
            _burstTimer = 2.5f;
            _summonTimer = 6f;
            _burstWarned = false;
            _summonWarned = false;
            _contactTimer = 0f;
            _spiralAngle = 0f;
            _dashTimer = 4f;
            _dashing = false;
            _dashTime = 0f;
            _dashDir = Vector2.zero;
            _dashWarned = false;
            _aimTimer = 2f;
            _aimWarned = false;
        }

        void Update()
        {
            if (damageable == null || damageable.isDead) return;
            if (_cfg == null) return;
            if (!NetSession.IsAuthority) return;   // 单机=权威；联机仅房主持有本逻辑

            float ratio = damageable.maxHp > 0f ? damageable.currentHp / damageable.maxHp : 0f;
            UpdatePhase(ratio);

            Transform player = _cfg.playerProvider != null ? _cfg.playerProvider() : null;

            // M9：变体 1「风暴领主」走独立的螺旋弹幕+突进逻辑（无召唤）
            if (_variant == 1) { UpdateTempest(player); return; }
            // M12：变体 2「虚空编织者」走瞄准式狙击散射+扩散环（不召唤不突进）
            if (_variant == 2) { UpdateVoid(player); return; }

            // 移动（速度随阶段提升）
            if (player != null)
            {
                float speed = _cfg.baseMoveSpeed * (Phase >= 3 ? 1.9f : Phase == 2 ? 1.45f : 1f);
                Vector2 dir = ((Vector2)player.position - (Vector2)transform.position).normalized;
                transform.Translate(dir * speed * Time.deltaTime, Space.World);

                // 接触伤害（防止贴脸站桩）
                _contactTimer -= Time.deltaTime;
                if (_contactTimer <= 0f && Vector2.Distance(transform.position, player.position) < 1.3f)
                {
                    _cfg.contactHit?.Invoke(player, Phase >= 3 ? 20f : Phase == 2 ? 17f : 14f);
                    _contactTimer = 0.6f;
                }
            }

            // 径向弹幕（带前摇预警；密度随阶段提升）
            _burstTimer -= Time.deltaTime;
            if (!_burstWarned && _burstTimer <= TelegraphLead)
            {
                _burstWarned = true;
                TempEffect.SpawnTelegraph(transform.position, 1.3f, new Color(1f, 0.32f, 0.42f), TelegraphLead);
                TelegraphRequested?.Invoke(0);
            }
            if (_burstTimer <= 0f)
            {
                Burst();
                _burstWarned = false;
                _burstTimer = Phase >= 3 ? 1.0f : Phase == 2 ? 1.6f : 2.3f;
            }

            // 召唤小怪（带前摇预警；频率随阶段提升）
            _summonTimer -= Time.deltaTime;
            if (!_summonWarned && _summonTimer <= TelegraphLead)
            {
                _summonWarned = true;
                TempEffect.SpawnTelegraph(transform.position, 2.4f, new Color(1f, 0.75f, 0.25f), TelegraphLead);
                TelegraphRequested?.Invoke(1);
            }
            if (_summonTimer <= 0f)
            {
                Summon();
                _summonWarned = false;
                _summonTimer = Phase >= 3 ? 4.5f : Phase == 2 ? 6.5f : 9f;
            }
        }

        private void UpdatePhase(float ratio)
        {
            int next = ratio <= 0.33f ? 3 : ratio <= 0.66f ? 2 : 1;
            if (next <= Phase) return;
            Phase = next;
            PhaseChanged?.Invoke(Phase);

            // 阶段转换演出
            EventBus.RaiseShake(Phase >= 3 ? 0.5f : 0.35f, 0.3f);
            SfxManager.Instance?.Play("boss");
            TempEffect.SpawnTelegraph(transform.position, 3.2f, PhaseTint, 0.8f);
            TelegraphRequested?.Invoke(10 + Phase);   // M8：转发给联机客户端
            if (_sr != null) _sr.color = PhaseTint;
        }

        private void Burst()
        {
            int count = Phase >= 3 ? 18 : Phase == 2 ? 14 : 10;
            float dmg = Phase >= 3 ? 12f : Phase == 2 ? 10.5f : 9f;
            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                _cfg.fireProjectile?.Invoke(transform.position,
                    new Vector2(Mathf.Cos(a), Mathf.Sin(a)), dmg, 6.5f, false);
            }
            EventBus.RaiseShake(0.28f, 0.22f);
        }

        private void Summon()
        {
            int n = Phase >= 2 ? 3 : 2;
            for (int i = 0; i < n; i++)
            {
                // M17 实测修复：同时 using System 与 UnityEngine 时 Random 有歧义（CS0104），完全限定
                float a = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector2 pos = (Vector2)transform.position + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 2.5f;
                _cfg.summon?.Invoke(pos, EnemyKind.Runner, 16f, 3.6f);
            }
        }

        // —— M9 第二种 Boss 变体「风暴领主」——
        // 与守望者（径向弹幕+召唤）区分：以旋转螺旋弹幕制造走位压力，并周期性向玩家突进，
        // 不召唤小怪（更强调纯走位/输出节奏的考验）。
        private void UpdateTempest(Transform player)
        {
            if (player == null) return;

            // 突进优先推进（盖过常规漂移）
            if (_dashing)
            {
                _dashTime -= Time.deltaTime;
                transform.Translate(_dashDir * 7f * Time.deltaTime, Space.World);
                if (_dashTime <= 0f) _dashing = false;
            }
            else
            {
                float speed = _cfg.baseMoveSpeed * 0.7f * (Phase >= 3 ? 1.6f : Phase == 2 ? 1.3f : 1f);
                Vector2 dir = ((Vector2)player.position - (Vector2)transform.position).normalized;
                transform.Translate(dir * speed * Time.deltaTime, Space.World);

                // 突进（带前摇预警圈）
                _dashTimer -= Time.deltaTime;
                if (!_dashWarned && _dashTimer <= TelegraphLead)
                {
                    _dashWarned = true;
                    TempEffect.SpawnTelegraph(transform.position, 3.0f, new Color(0.4f, 0.9f, 1f), TelegraphLead);
                    TelegraphRequested?.Invoke(0);
                }
                if (_dashTimer <= 0f)
                {
                    _dashWarned = false;
                    _dashTimer = Phase >= 3 ? 3.0f : Phase == 2 ? 4.0f : 5.5f;
                    _dashing = true;
                    _dashTime = 0.35f;
                    _dashDir = ((Vector2)player.position - (Vector2)transform.position).normalized;
                    EventBus.RaiseShake(0.25f, 0.2f);
                }
            }

            // 接触伤害（防止贴脸站桩）
            _contactTimer -= Time.deltaTime;
            if (_contactTimer <= 0f && Vector2.Distance(transform.position, player.position) < 1.3f)
            {
                _cfg.contactHit?.Invoke(player, Phase >= 3 ? 20f : Phase == 2 ? 17f : 14f);
                _contactTimer = 0.6f;
            }

            // 螺旋弹幕（无召唤）
            _burstTimer -= Time.deltaTime;
            if (!_burstWarned && _burstTimer <= TelegraphLead)
            {
                _burstWarned = true;
                TempEffect.SpawnTelegraph(transform.position, 1.3f, new Color(0.4f, 0.9f, 1f), TelegraphLead);
                TelegraphRequested?.Invoke(0);
            }
            if (_burstTimer <= 0f)
            {
                SpiralBurst();
                _burstWarned = false;
                _burstTimer = Phase >= 3 ? 0.5f : Phase == 2 ? 0.8f : 1.2f;
            }
        }

        private void SpiralBurst()
        {
            int arms = Phase >= 3 ? 4 : Phase == 2 ? 3 : 2;
            float dmg = Phase >= 3 ? 10f : Phase == 2 ? 9f : 8f;
            for (int i = 0; i < arms; i++)
            {
                float a = _spiralAngle + i * Mathf.PI * 2f / arms;
                _cfg.fireProjectile?.Invoke(transform.position,
                    new Vector2(Mathf.Cos(a), Mathf.Sin(a)), dmg, 6.0f, false);
            }
            _spiralAngle += 0.4f;   // 每次爆发旋转固定相位 -> 螺旋
            EventBus.RaiseShake(0.18f, 0.15f);
        }

        // —— M12 第三种 Boss 变体「虚空编织者」——
        // 与守望者（径向弹幕+召唤）、风暴领主（螺旋弹幕+突进）区分：以「瞄准式狙击散射」为主压力，
        // 配合周期性扩散环做区域封锁；不召唤、不突进，惩罚站桩（逼迫玩家持续走位/闪避）。
        // 染色走紫罗兰调，视觉上与前两者（红白 / 青蓝）明确区分。
        private void UpdateVoid(Transform player)
        {
            if (player == null) return;

            // 缓慢漂移靠近（比风暴领主更慢，强调"瞄准压制"而非机动战）
            float speed = _cfg.baseMoveSpeed * 0.8f * (Phase >= 3 ? 1.7f : Phase == 2 ? 1.35f : 1f);
            Vector2 dir = ((Vector2)player.position - (Vector2)transform.position).normalized;
            transform.Translate(dir * speed * Time.deltaTime, Space.World);

            // 接触伤害（防止贴脸站桩）
            _contactTimer -= Time.deltaTime;
            if (_contactTimer <= 0f && Vector2.Distance(transform.position, player.position) < 1.3f)
            {
                _cfg.contactHit?.Invoke(player, Phase >= 3 ? 20f : Phase == 2 ? 17f : 14f);
                _contactTimer = 0.6f;
            }

            // 瞄准式狙击散射（带前摇预警圈）：直接朝玩家当前位置打一束，逼迫走位
            _aimTimer -= Time.deltaTime;
            if (!_aimWarned && _aimTimer <= AimLead)
            {
                _aimWarned = true;
                TempEffect.SpawnTelegraph(transform.position, 1.0f, new Color(0.85f, 0.5f, 1f), AimLead);
                TelegraphRequested?.Invoke(0);
            }
            if (_aimTimer <= 0f)
            {
                AimedVolley(player);
                _aimWarned = false;
                _aimTimer = Phase >= 3 ? 1.1f : Phase == 2 ? 1.5f : 2.0f;
            }

            // 扩散环（区域封锁，频率低于守望者的径向爆发）
            _burstTimer -= Time.deltaTime;
            if (!_burstWarned && _burstTimer <= TelegraphLead)
            {
                _burstWarned = true;
                TempEffect.SpawnTelegraph(transform.position, 1.4f, new Color(0.85f, 0.5f, 1f), TelegraphLead);
                TelegraphRequested?.Invoke(0);
            }
            if (_burstTimer <= 0f)
            {
                RingBurst();
                _burstWarned = false;
                _burstTimer = Phase >= 3 ? 3.5f : Phase == 2 ? 5f : 6.5f;
            }
        }

        private void AimedVolley(Transform player)
        {
            if (player == null) return;
            Vector2 baseDir = ((Vector2)player.position - (Vector2)transform.position).normalized;
            float baseAng = Mathf.Atan2(baseDir.y, baseDir.x);
            int pellets = Phase >= 3 ? 7 : Phase == 2 ? 5 : 3;
            float spread = 0.34f;   // 锥形总半角（弧度）
            float dmg = Phase >= 3 ? 11f : Phase == 2 ? 10f : 9f;
            for (int i = 0; i < pellets; i++)
            {
                float t = pellets == 1 ? 0f : (i / (float)(pellets - 1) - 0.5f);
                float ang = baseAng + t * spread;
                _cfg.fireProjectile?.Invoke(transform.position,
                    new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)), dmg, 8.0f, false);
            }
            EventBus.RaiseShake(0.2f, 0.15f);
        }

        private void RingBurst()
        {
            int count = Phase >= 3 ? 22 : Phase == 2 ? 16 : 12;
            float dmg = Phase >= 3 ? 10f : Phase == 2 ? 9f : 8f;
            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                _cfg.fireProjectile?.Invoke(transform.position,
                    new Vector2(Mathf.Cos(a), Mathf.Sin(a)), dmg, 5.5f, false);
            }
            EventBus.RaiseShake(0.3f, 0.22f);
        }
    }
}
