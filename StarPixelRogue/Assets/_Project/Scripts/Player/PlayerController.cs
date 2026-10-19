using System.Collections.Generic;
using UnityEngine;
using StarPixel.Combat;
using StarPixel.Combat.Skill;
using StarPixel.Enemy;
using StarPixel.Core;
using StarPixel.Effects;
using StarPixel.Net;

namespace StarPixel.Player
{
    /// <summary>
    /// 玩家控制：WASD 移动 + 多武器自动开火 + 经验/升级（对应 TDD M1/M2 核心循环）。
    /// 武器为数据驱动：每个 WeaponSlot 持有一个 SkillDef，按自身冷却自动开火；
    /// 升级解锁新武器或强化属性，组合技由 ComboManager 实时计算并作用于开火。
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        public static PlayerController Instance;

        public PlayerStats stats = new PlayerStats();
        public Damageable damageable { get; private set; }

        /// <summary>是否为「本机本地玩家」。联机模式下房主持有所有玩家的控制器，
        /// 但只有本地玩家跑输入/事件；远程玩家仅作为权威数据容器（由 NetPlayer 置 false）。
        ///
        /// M18：默认改为 false。此前默认 true，导致联机下玩家对象刚生成、
        /// NetPlayer.OnStartClient 尚未完成接线（网络弹丸生成器为空、本地弹丸模板也未设置）时，
        /// Update 就按"本地玩家"自行开火，退化到本地对象池分支并抛 NullReferenceException
        /// （房主 60 秒实测抛出 13 条，客户端 0 条——客户端只发开火请求，不本地结算）。
        /// 单机路径由 BuildSinglePlayer 显式置 true，行为不变。</summary>
        public bool isLocal = false;

        private GameObject _projectileTemplate;
        private GameObject _boomerangTemplate;   // M6.1：回旋镖模板
        private GameObject _homingTemplate;      // M6.1：追踪弹模板
        private GameObject _mineTemplate;        // M9：脉冲地雷模板
        private Camera _cam;

        /// <summary>弹丸生成委托：联机模式下由 NetPlayer 注入（生成网络弹丸）；单机为空走本地对象池。</summary>
        private System.Action<Vector3, Vector2, float, float, float, bool, float> _projectileSpawner;

        /// <summary>
        /// M6.1：特殊弹丸生成委托（回旋镖/追踪弹），联机模式下由 NetPlayer 注入。
        /// 参数：起点、方向、伤害、速度、射程(回旋镖最远距离)、行为(1=回旋镖 2=追踪)、减速。
        /// </summary>
        private System.Action<Vector3, Vector2, float, float, float, int, bool> _specialSpawner;

        // 武器槽（默认持有「星能弹」）
        private readonly List<WeaponSlot> _weapons = new List<WeaponSlot>();

        // 等级与经验
        public int level = 1;
        private float _xp;
        private float _xpToNext = 5f;
        private float _prevHpRatio = 1f;

        /// <summary>M7：本局已激活的组合技键（去重广播，新激活时才提示）。</summary>
        private readonly HashSet<string> _activeCombos = new HashSet<string>();

        private void OnPlayerHpChanged(float ratio)
        {
            if (!isLocal) return;                 // 远程玩家血量由 NetPlayer 的 SyncVar 驱动其客户端 HUD
            EventBus.RaisePlayerHealthRatio(ratio);
            if (ratio < _prevHpRatio - 0.0001f)
            {
                EventBus.RaisePlayerHurt();
                EventBus.RaiseShake(0.14f, 0.14f);
            }
            _prevHpRatio = ratio;
        }

        void Awake()
        {
            if (Instance == null) Instance = this;   // 首个创建者胜出（联机下房主玩家先于远程玩家创建）
            damageable = GetComponent<Damageable>();
            damageable.showNumbers = false;
            damageable.OnHpChanged += OnPlayerHpChanged;
            damageable.OnDeath += OnPlayerDeath;
            _cam = Camera.main;

            // 默认武器
            _weapons.Add(new WeaponSlot { def = Combat.UpgradeCatalog.CreateBolt(), level = 1, timer = 0f });
        }

        /// <summary>注入联机弹丸生成器（NetPlayer 调用）。</summary>
        public void SetProjectileSpawner(System.Action<Vector3, Vector2, float, float, float, bool, float> spawner)
        {
            _projectileSpawner = spawner;
        }

        public void SetProjectileTemplate(GameObject t) => _projectileTemplate = t;

        /// <summary>注入 M6.1 特殊弹丸模板（回旋镖/追踪弹；单机由 GameBuilder 调用）。</summary>
        public void SetSpecialTemplates(GameObject boomerang, GameObject homing)
        {
            _boomerangTemplate = boomerang;
            _homingTemplate = homing;
        }

        /// <summary>注入 M9 脉冲地雷模板（单机由 GameBuilder 调用）。</summary>
        public void SetMineTemplate(GameObject t) => _mineTemplate = t;

        /// <summary>注入联机特殊弹丸生成器（NetPlayer 调用）。</summary>
        public void SetSpecialSpawner(System.Action<Vector3, Vector2, float, float, float, int, bool> spawner)
        {
            _specialSpawner = spawner;
        }

        private int _appliedCharacterId = -1;

        /// <summary>
        /// M23：角色乘区的基准值。乘区是**乘算**的，若直接在当前 stats 上再乘一次，
        /// 换角色就会叠乘而不是替换（守卫 1.35 倍切游侠再切回守卫 → 1.35×1.35 = 1.82 倍血量）。
        /// 故先记下"第一次应用角色前"的七项基准值，之后每次应用都从基准重算。
        /// </summary>
        private PlayerStats _characterBaseStats;
        private bool _hasCharacterBase;

        /// <summary>
        /// M6.2：应用角色定义——属性乘区 + 替换起始武器 + 生命上限同步。
        /// 幂等：同一角色重复调用不会重复乘算（联机下客户端预测与房主应用各调一次）。
        /// M23：换角色时从**基准值**重算乘区，避免叠乘（见 <see cref="_characterBaseStats"/>）。
        /// </summary>
        public void ApplyCharacter(CharacterDef def)
        {
            if (def == null) return;
            if (_appliedCharacterId == def.id) return;
            // 注意：这里**不能**因为 id 不同就跳过——同 id 但数值被改过（例如作弊/热更）仍应重新应用，
            // 只是幂等判定以 id 为准；基准快照保证无论应用多少次都不会越乘越大。
            bool switching = _appliedCharacterId >= 0;
            _appliedCharacterId = def.id;

            if (!_hasCharacterBase)
            {
                _characterBaseStats = stats.Clone();
                _hasCharacterBase = true;
            }

            // 从基准值重算（而不是在当前值上再乘）
            stats.maxHp = _characterBaseStats.maxHp * def.hpMult;
            stats.moveSpeed = _characterBaseStats.moveSpeed * def.speedMult;
            stats.attackDamageMult = _characterBaseStats.attackDamageMult * def.damageMult;
            stats.cooldownMult = _characterBaseStats.cooldownMult * def.cooldownMult;
            stats.pickupRadius = _characterBaseStats.pickupRadius * def.pickupMult;
            stats.areaMult = _characterBaseStats.areaMult * def.areaMult;
            stats.xpGainMult = _characterBaseStats.xpGainMult * def.xpMult;

            // 生命上限同步（GameBuilder 的 Damageable.Init 与 stats.maxHp 可能不一致，这里统一）
            if (damageable != null)
            {
                damageable.maxHp = stats.maxHp;
                damageable.currentHp = stats.maxHp;
                if (isLocal) EventBus.RaisePlayerHealthRatio(1f);
            }

            // 替换起始武器：清除默认武器，改为角色起始武器
            if (def.startingWeaponId > 0)
            {
                _weapons.Clear();
                var startDef = Combat.UpgradeCatalog.CreateById(def.startingWeaponId);
                if (startDef != null)
                {
                    _weapons.Add(new WeaponSlot { def = startDef, level = 1, timer = 0f });
                    Core.SaveManager.Instance?.DiscoverWeapon(startDef.id);   // M6.4 图鉴
                }
            }

            ComboManager.Instance?.Recompute(GetTags());
            CheckCombos();
            if (isLocal) EventBus.RaiseWeaponsChanged();
        }

        /// <summary>
        /// 房主侧远程玩家开火入口（客户端 ServerRpc 触发）。
        /// 在此做权威冷却校验，避免客户端伪造高频开火。
        /// </summary>
        public void ServerFire(int weaponId)
        {
            if (NetSession.IsClient) return;             // 客户端无权威
            var w = _weapons.Find(x => x.def.id == weaponId);
            if (w == null) return;
            // 光环为持续型武器，不走冷却计时（由 UpdateAura 驱动），忽略其开火请求
            if (w.def.weaponType == WeaponType.Aura) return;
            if (w.def.weaponType != WeaponType.Orbit && w.timer > 0f) return;
            w.timer = Mathf.Max(0.05f, w.def.cooldown * stats.cooldownMult);
            FireWeapon(w);
        }

        /// <summary>当前武器槽列表（升级选卡需要读取等级）。</summary>
        public List<WeaponSlot> Weapons => _weapons;

        /// <summary>提升指定 id 武器的等级（1..SkillDef.MaxLevel）。</summary>
        public void LevelUpWeapon(int id)
        {
            var w = _weapons.Find(x => x.def.id == id);
            if (w != null && w.level < SkillDef.MaxLevel) w.level++;
            ComboManager.Instance?.Recompute(GetTags());
            CheckCombos();
            if (isLocal) EventBus.RaiseWeaponsChanged();
        }

        /// <summary>M7：对比新旧激活组合集，新激活的组合向本地 HUD/音效广播（仅本地玩家，联机远程玩家不重复提示）。</summary>
        private void CheckCombos()
        {
            if (!isLocal) return;
            var keys = ComboManager.ActiveComboKeys(GetTags());
            foreach (var k in keys)
            {
                if (_activeCombos.Add(k))
                    EventBus.RaiseComboActivated(k);
            }
            _activeCombos.RemoveWhere(k => !keys.Contains(k));
        }

        void Update()
        {
            if (damageable.isDead) return;

            // 移动 + 相机跟随：仅本地玩家（联机下远程玩家位置由 NetworkTransform 同步）
            if (isLocal)
            {
                // M16 跨平台：统一经 PlayerInput 取移动向量（触屏走虚拟摇杆，桌面沿用 Input 轴）
                Vector2 dir = Core.PlayerInput.MoveVector().normalized;
                transform.Translate(dir * stats.moveSpeed * Time.deltaTime, Space.World);

                if (_cam != null)
                    _cam.transform.position = new Vector3(transform.position.x, transform.position.y, _cam.transform.position.z);
            }

            // 武器逻辑：计时器在所有侧维护（房主侧为远程玩家做权威冷却），
            // 开火仅本地玩家触发（联机客户端转为向房主发请求）
            for (int i = 0; i < _weapons.Count; i++)
            {
                var w = _weapons[i];
                if (w.def.weaponType == WeaponType.Orbit)
                {
                    UpdateOrbit(w);
                    continue;
                }
                if (w.def.weaponType == WeaponType.Aura)
                {
                    UpdateAura(w);       // M6.1：光环持续伤害（无需冷却计时）
                    continue;
                }
                if (w.timer > 0f)
                {
                    w.timer -= Time.deltaTime;
                    continue;
                }
                if (!isLocal) continue;      // 远程玩家：等待客户端开火请求（ServerFire 重置计时）
                FireWeapon(w);
                w.timer = Mathf.Max(0.05f, w.def.cooldown * stats.cooldownMult);
            }
        }

        // —— 开火分发 ——
        private void FireWeapon(WeaponSlot w)
        {
            // 联机客户端：不本地结算任何伤害，仅向房主请求开火（ADR-001 半权威）
            if (NetSession.IsClient)
            {
#if FISHNET_ENABLED
                // NetPlayer 是联机专属类型（整文件受 FISHNET_ENABLED 保护），
                // 未装插件时本引用必须一并编译掉，否则 PlayerController 直接编译失败。
                // （离线时 NetSession.IsClient 恒为 false，本分支不会进入，行为不变。）
                GetComponent<StarPixel.Net.NetPlayer>()?.RequestFire(w.def.id);
#endif
                // M6.1：即时特效（链闪电）做客户端本地预测——先播放电弧，伤害以房主结算为准
                if (w.def.weaponType == WeaponType.Chain)
                    FireChain(w, ComboManager.Compute(GetTags()), false);
                return;
            }

            SfxManager.Instance?.Play("shoot", 0.45f);
            var combo = ComboManager.Compute(GetTags());   // 按本玩家武器池即时计算（M4：多玩家安全）
            switch (w.def.weaponType)
            {
                case WeaponType.Projectile:
                case WeaponType.Scatter:
                    FireProjectiles(w, combo);
                    break;
                case WeaponType.Nova:
                    FireNova(w, combo);
                    break;
                case WeaponType.Chain:
                    FireChain(w, combo, true);
                    break;
                case WeaponType.Boomerang:
                    FireBoomerang(w, combo);
                    break;
                case WeaponType.Homing:
                    FireHoming(w, combo);
                    break;
                case WeaponType.Mine:
                    FireMine(w, combo);
                    break;
            }
        }

        /// <summary>
        /// M18：本地对象池分支是否可用。联机下若玩家尚未被 NetPlayer 接线完成，
        /// 既没有网络弹丸生成器、本地模板也为空（联机路径本就不设模板），
        /// 此时 PoolManager.Get(null) 会抛 NullReferenceException。
        /// 这里统一前置判断，不可用就跳过本次生成——少打一发好过整帧异常。
        /// </summary>
        private static bool CanSpawnLocal(GameObject template)
        {
            return template != null && Core.PoolManager.Instance != null;
        }

        private void FireProjectiles(WeaponSlot w, ComboManager.Bonus combo)
        {
            var target = CombatTargets.Nearest(transform.position, w.def.range > 0 ? w.def.range : stats.attackRange);
            if (target == null) return;
            Vector2 dir = ((Vector2)target.position - (Vector2)transform.position).normalized;
            // 等级 + 弹体强化共同决定弹数（M3.6 修复：直射武器此前写死 1，吃不到"弹体+1"）
            int count = w.def.EffectiveCount(w.level) + stats.projectileBonus;
            float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * combo.damageMult;
            float pierce = w.def.EffectivePierce(w.level) + combo.pierce;
            bool slow = combo.slowOnHit;

            for (int i = 0; i < count; i++)
            {
                float spread = (count > 1) ? (i - (count - 1) / 2f) * 0.18f : 0f;
                Vector2 d = Rotate(dir, spread);
                if (_projectileSpawner != null)
                {
                    // 联机：走网络弹丸（房主权威生成，客户端经 NetworkTransform 同步）
                    _projectileSpawner(transform.position, d, dmg, w.def.projectileSpeed, pierce, slow, 0.5f);
                }
                else
                {
                    if (!CanSpawnLocal(_projectileTemplate)) return;   // M18：见 CanSpawnLocal 注释
                    var go = PoolManager.Instance.Get(_projectileTemplate);
                    var proj = go.GetComponent<Projectile>();
                    proj.Launch(transform.position, d, dmg, w.def.projectileSpeed, pierce, _projectileTemplate);
                    proj.applySlow = slow;
                    proj.slowFactor = 0.5f;
                }
            }
        }

        private void FireNova(WeaponSlot w, ComboManager.Bonus combo)
        {
            float r = w.def.EffectiveAoeRadius(w.level) * stats.areaMult;
            float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * combo.damageMult;
            // 统一索敌入口：同时覆盖单机敌人列表与联机网络敌人（TDD 4.2）
            CombatTargets.ForEachWithin(transform.position, r, dmgable =>
            {
                if (dmgable != null && !dmgable.isDead) dmgable.TakeDamage(dmg);
            });
            Effects.TempEffect.SpawnRing(transform.position, r, w.def.tags != null && System.Array.IndexOf(w.def.tags, "ice") >= 0 ? Color.cyan : Color.yellow);
        }

        // —— M6.1 链闪电：在敌人之间跳跃，每跳伤害衰减 ——
        private void FireChain(WeaponSlot w, ComboManager.Bonus combo, bool applyDamage)
        {
            float range = w.def.range > 0 ? w.def.range : stats.attackRange;
            float dmgBase = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * combo.damageMult;
            int jumps = Mathf.Max(1, w.def.EffectiveCount(w.level));

            var points = new List<Vector2>();
            var hit = new HashSet<int>();
            Vector2 from = transform.position;
            float searchRange = range;

            for (int i = 0; i < jumps; i++)
            {
                var target = FindNearestDamageable(from, searchRange, hit);
                if (target == null) break;
                hit.Add(target.GetInstanceID());
                float dmg = dmgBase * Mathf.Pow(Mathf.Max(0.1f, w.def.damageFalloff), i);
                if (applyDamage) target.TakeDamage(dmg);
                points.Add(target.transform.position);
                from = target.transform.position;
                searchRange = w.def.chainRange;   // 首跳用索敌范围，之后用跳电半径
            }

            if (points.Count > 0)
                ChainBolt.Spawn(transform.position, points, new Color(0.65f, 0.82f, 1f));
        }

        /// <summary>范围内最近的可受伤目标（排除已命中集合；联机下自动覆盖网络敌人）。</summary>
        private Damageable FindNearestDamageable(Vector2 origin, float range, HashSet<int> exclude)
        {
            if (range <= 0f) return null;
            Damageable best = null;
            float bestD = range * range;
            CombatTargets.ForEachWithin(origin, range, d =>
            {
                if (d == null || d.isDead) return;
                if (exclude != null && exclude.Contains(d.GetInstanceID())) return;
                float dist = ((Vector2)d.transform.position - origin).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = d; }
            });
            return best;
        }

        // —— M6.1 回旋镖：飞出后返回，来回各伤一次 ——
        private void FireBoomerang(WeaponSlot w, ComboManager.Bonus combo)
        {
            var target = CombatTargets.Nearest(transform.position, w.def.range > 0 ? w.def.range : stats.attackRange);
            if (target == null) return;
            Vector2 dir = ((Vector2)target.position - (Vector2)transform.position).normalized;
            int count = Mathf.Max(1, w.def.EffectiveCount(w.level));
            float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * combo.damageMult;

            for (int i = 0; i < count; i++)
            {
                float spread = (count > 1) ? (i - (count - 1) / 2f) * 0.35f : 0f;
                Vector2 d = Rotate(dir, spread);
                if (_specialSpawner != null)
                {
                    // 联机：网络弹丸（行为=回旋镖，房主权威飞行与命中）
                    _specialSpawner(transform.position, d, dmg, w.def.projectileSpeed, w.def.boomerangRange, 1, combo.slowOnHit);
                }
                else
                {
                    if (!CanSpawnLocal(_boomerangTemplate)) return;    // M18：见 CanSpawnLocal 注释
                    var go = PoolManager.Instance.Get(_boomerangTemplate);
                    var bm = go.GetComponent<Boomerang>();
                    bm.Launch(transform.position, d, dmg, w.def.projectileSpeed, w.def.boomerangRange, _boomerangTemplate);
                    bm.applySlow = combo.slowOnHit;
                    bm.slowFactor = 0.5f;
                }
            }
        }

        // —— M6.1 追踪弹：转向最近敌人 ——
        private void FireHoming(WeaponSlot w, ComboManager.Bonus combo)
        {
            var target = CombatTargets.Nearest(transform.position, w.def.range > 0 ? w.def.range : stats.attackRange);
            if (target == null) return;
            Vector2 dir = ((Vector2)target.position - (Vector2)transform.position).normalized;
            int count = Mathf.Max(1, w.def.EffectiveCount(w.level));
            float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * combo.damageMult;

            for (int i = 0; i < count; i++)
            {
                float spread = (count > 1) ? (i - (count - 1) / 2f) * 0.5f : 0f;
                Vector2 d = Rotate(dir, spread);
                if (_specialSpawner != null)
                {
                    // 联机：网络弹丸（行为=追踪，房主权威索敌与命中）
                    _specialSpawner(transform.position, d, dmg, w.def.projectileSpeed, w.def.homingTurn, 2, combo.slowOnHit);
                }
                else
                {
                    if (!CanSpawnLocal(_homingTemplate)) return;       // M18：见 CanSpawnLocal 注释
                    var go = PoolManager.Instance.Get(_homingTemplate);
                    var hm = go.GetComponent<HomingMissile>();
                    hm.Launch(transform.position, d, dmg, w.def.projectileSpeed, w.def.homingTurn, _homingTemplate);
                    hm.applySlow = combo.slowOnHit;
                    hm.slowFactor = 0.5f;
                }
            }
        }

        // —— M9 脉冲地雷：埋设于玩家位置，激活后区域封锁（联机下走本地对象池，伤害仅房主权威结算）——
        private void FireMine(WeaponSlot w, ComboManager.Bonus combo)
        {
            int count = Mathf.Max(1, w.def.EffectiveCount(w.level));
            float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * combo.damageMult;
            float radius = w.def.EffectiveAoeRadius(w.level) * stats.areaMult;

            for (int i = 0; i < count; i++)
            {
                Vector2 pos = (Vector2)transform.position;
                if (count > 1)   // 多枚时围绕玩家小幅散布
                {
                    float a = (i / (float)count) * Mathf.PI * 2f;
                    pos += new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.8f;
                }
                if (CanSpawnLocal(_mineTemplate))   // M18：见 CanSpawnLocal 注释
                {
                    var go = PoolManager.Instance.Get(_mineTemplate);
                    var m = go.GetComponent<Mine>();
                    m.Launch(pos, dmg, radius, w.def.mineArmTime, w.def.mineTrigger, w.def.mineLife, _mineTemplate);
                    m.applySlow = combo.slowOnHit;
                    m.slowFactor = 0.5f;
                }
            }
        }

        // —— M6.1 光环领域：持续伤害 + 常驻视觉环（结构同 UpdateOrbit）——
        private void UpdateAura(WeaponSlot w)
        {
            float radius = w.def.EffectiveAoeRadius(w.level) * stats.areaMult;

            // 视觉环：所有玩家均生成（联机下远程玩家也可见——纯表现，位置由 NetworkTransform 同步；
            // 伤害仍仅权威侧结算，见下方 IsAuthority 守卫）
            if (w.auraVisual == null)
            {
                var go = new GameObject("AuraRing");
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Core.SpriteFactory.MakeSolidSprite();
                var c = new Color(1f, 0.92f, 0.55f, 0.16f);   // 圣光：淡金半透明
                sr.color = c;
                sr.sortingOrder = 2;
                w.auraVisual = go;
            }
            if (w.auraVisual != null)
            {
                float pulse = 1f + Mathf.Sin(NetSession.SyncedOrbitTime() * 3f) * 0.04f;
                w.auraVisual.transform.position = transform.position;
                w.auraVisual.transform.localScale = Vector3.one * (radius * 2f * pulse);
            }

            // 伤害：仅权威侧（房主/单机）结算
            if (!NetSession.IsAuthority) return;
            w.hitTimer -= Time.deltaTime;
            if (w.hitTimer <= 0f)
            {
                w.hitTimer = Mathf.Max(0.1f, w.def.auraInterval);
                float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * ComboManager.Compute(GetTags()).damageMult;
                CombatTargets.ForEachWithin(transform.position, radius, dmgable =>
                {
                    if (dmgable == null || dmgable.isDead) return;
                    dmgable.TakeDamage(dmg);
                });
            }
        }

        private void UpdateOrbit(WeaponSlot w)
        {
            int blades = w.def.EffectiveCount(w.level);
            // 视觉刃：所有玩家均生成（联机下远程玩家也可见——纯表现；伤害仅权威侧，见下方守卫）
            if (w.orbitBlades == null || w.orbitBlades.Count != blades)
            {
                if (w.orbitBlades != null)
                    foreach (var b in w.orbitBlades) if (b != null) Destroy(b);
                w.orbitBlades = new List<GameObject>();
                for (int i = 0; i < blades; i++)
                {
                    var go = new GameObject("OrbitBlade");
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = SpriteFactory.MakeSolidSprite();
                    sr.color = Color.magenta;
                    go.transform.localScale = Vector3.one * 0.4f;
                    w.orbitBlades.Add(go);
                }
            }
            if (w.orbitBlades != null)
            {
                float angleStep = 360f / w.orbitBlades.Count;
                // M10：旋转相位改用同步时间源，消除联机下远程玩家刃角度偏差（纯视觉）
                float phase = NetSession.SyncedOrbitTime() * 180f;
                for (int i = 0; i < w.orbitBlades.Count; i++)
                {
                    float ang = (phase + i * angleStep) * Mathf.Deg2Rad;
                    Vector3 off = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0) * w.def.orbitRadius;
                    w.orbitBlades[i].transform.position = transform.position + off;
                }
            }

            // 伤害：仅权威侧（房主/单机）结算，客户端不做本地判定
            if (!NetSession.IsAuthority) return;
            w.hitTimer -= Time.deltaTime;
            if (w.hitTimer <= 0f)
            {
                w.hitTimer = 0.30f;   // M3.6 平衡：由 0.25 放缓，避免环绕刃 DPS 一家独大
                float dmg = CombatManager.Instance.CalcDamage(w.def.EffectiveDamage(w.level), stats.attackDamageMult) * ComboManager.Compute(GetTags()).damageMult;
                CombatTargets.ForEachWithin(transform.position, w.def.orbitRadius + 0.6f, dmgable =>
                {
                    if (dmgable == null || dmgable.isDead) return;
                    float ed = Vector2.Distance(transform.position, dmgable.transform.position);
                    if (Mathf.Abs(ed - w.def.orbitRadius) < 0.6f) dmgable.TakeDamage(dmg);
                });
            }
        }

        // —— 升级/经验 ——
        public void AddXp(float amount)
        {
            _xp += amount * stats.xpGainMult;
            while (_xp >= _xpToNext)
            {
                _xp -= _xpToNext;
                level++;
                // M3.6 平衡：由 1.35/+3 放缓为 1.25/+2（原曲线 Lv15 需 2466 XP，玩家根本达不到）
                _xpToNext = Mathf.Round(_xpToNext * 1.25f + 2f);
                if (isLocal) EventBus.RaiseLevelUp(level);   // 远程玩家升级由 NetPlayer 走 TargetRpc 推卡
            }
            if (isLocal) EventBus.RaiseXpChanged(_xp / _xpToNext);
        }

        public void AddSkill(SkillDef def)
        {
            var existing = _weapons.Find(w => w.def.id == def.id);
            if (existing != null)
            {
                // 已持有：提升武器等级（M3.6 取代旧的"改 cooldown/damage"粗暴叠加）
                if (existing.level < SkillDef.MaxLevel) existing.level++;
            }
            else
            {
                _weapons.Add(new WeaponSlot { def = def, level = 1, timer = 0f });
            }
            ComboManager.Instance?.Recompute(GetTags());
            EventBus.RaiseWeaponsChanged();
            Core.SaveManager.Instance?.DiscoverWeapon(def.id);   // M6.4 图鉴
        }

        public bool HasSkill(int id) => _weapons.Exists(w => w.def.id == id);

        public void UpgradeMaxHp(float amount)
        {
            stats.maxHp += amount;
            damageable.maxHp = stats.maxHp;
            damageable.currentHp = stats.maxHp;
            if (isLocal) EventBus.RaisePlayerHealthRatio(1f);
        }

        /// <summary>拾取回血球时回复生命（不超过上限）。</summary>
        public void Heal(float amount)
        {
            if (damageable.isDead || amount <= 0f) return;
            damageable.currentHp = Mathf.Min(damageable.maxHp, damageable.currentHp + amount);
            _prevHpRatio = damageable.maxHp > 0f ? damageable.currentHp / damageable.maxHp : 1f;
            if (isLocal) EventBus.RaisePlayerHealthRatio(_prevHpRatio);
        }

        public void AddProjectileToAll()
        {
            // M3.6：改为独立计数，直射/散射武器都能受益（旧实现只改 def.projectileCount，对直射无效）
            stats.projectileBonus += 1;
        }

        /// <summary>当前经验比例（0..1，HUD 与联机同步用）。</summary>
        public float XpRatio => _xpToNext > 0f ? Mathf.Clamp01(_xp / _xpToNext) : 0f;

        /// <summary>
        /// M11 断线重连：玩家进度快照。房主在远程玩家断连时捕获、重连时重放，
        /// 使玩家回到断线前的等级/武器/血量而不是从 1 级重新开始。
        /// 定义为非联机条件类型，便于 PlayerController（无 FISHNET 守卫）与 NetPlayer 共用。
        /// </summary>
        public struct PlayerReconnectState
        {
            public int level;
            public float xp;
            public float hpRatio;
            public int[] weaponIds;
            public int[] weaponLevels;
        }

        /// <summary>M11：捕获当前进度为快照（房主权威侧调用）。</summary>
        public PlayerReconnectState CaptureProgression()
        {
            var s = new PlayerReconnectState();
            s.level = level;
            s.xp = _xp;
            s.hpRatio = (damageable != null && damageable.maxHp > 0f)
                ? Mathf.Clamp01(damageable.currentHp / damageable.maxHp) : 1f;
            s.weaponIds = new int[_weapons.Count];
            s.weaponLevels = new int[_weapons.Count];
            for (int i = 0; i < _weapons.Count; i++)
            {
                s.weaponIds[i] = _weapons[i].def != null ? _weapons[i].def.id : 0;
                s.weaponLevels[i] = _weapons[i].level;
            }
            return s;
        }

        /// <summary>
        /// M11：重放进度快照（房主权威侧调用）。重建武器列表、等级、经验曲线与血量，
        /// 并刷新组合技与本地事件。组合技键/经验曲线按等级重新推导，避免与正常升级逻辑冲突。
        /// </summary>
        public void ReapplyProgression(PlayerReconnectState s)
        {
            if (s.weaponIds == null) return;

            // 武器：按快照精确重建（清旧→填入→补足等级）
            _weapons.Clear();
            for (int i = 0; i < s.weaponIds.Length; i++)
            {
                int id = s.weaponIds[i];
                int lv = (s.weaponLevels != null && i < s.weaponLevels.Length) ? s.weaponLevels[i] : 1;
                var def = Combat.UpgradeCatalog.CreateById(id);
                if (def == null) continue;
                var slot = new WeaponSlot { def = def, level = 1, timer = 0f };
                _weapons.Add(slot);
                while (slot.level < lv && slot.level < SkillDef.MaxLevel) slot.level++;
            }
            if (_weapons.Count == 0)
                _weapons.Add(new WeaponSlot { def = Combat.UpgradeCatalog.CreateBolt(), level = 1, timer = 0f });

            // 等级 + 经验 + 经验曲线（与 AddXp 同一公式重推，保证 _xpToNext 与等级自洽）
            level = Mathf.Max(1, s.level);
            _xp = Mathf.Max(0f, s.xp);
            _xpToNext = 5f;
            for (int i = 1; i < level; i++) _xpToNext = Mathf.Round(_xpToNext * 1.25f + 2f);

            // 血量：按比例恢复到（断线前的）生命值
            if (damageable != null) damageable.SetHpRatio(Mathf.Clamp(s.hpRatio, 0.05f, 1f));

            ComboManager.Instance?.Recompute(GetTags());
            CheckCombos();
            if (isLocal)
            {
                Core.EventBus.RaiseWeaponsChanged();
                Core.EventBus.RaiseLevelUp(level);
                Core.EventBus.RaiseXpChanged(XpRatio);
            }
        }

        private List<string> GetTags()
        {
            var tags = new List<string>();
            foreach (var w in _weapons)
                if (w.def.tags != null) tags.AddRange(w.def.tags);
            return tags;
        }

        private static Vector2 Rotate(Vector2 v, float rad)
        {
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private void OnPlayerDeath()
        {
            if (!isLocal) return;                  // 远程玩家死亡由房主同步血量，客户端经 SyncVar 处理
            EventBus.RaiseGameOver();
            if (!NetSession.IsNetworked) gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            // M4.5：断线拆世界后 Instance 可能悬空，阻碍重建后的世界正确引用本地玩家
            if (Instance == this) Instance = null;

            // M8：清理环绕刃/光环视觉对象（它们不是子物体，不会随玩家销毁而自动消失；
            // 联机下远程玩家反生成、拆世界重建时都必须显式销毁，否则残留漂浮视觉）
            foreach (var w in _weapons)
            {
                if (w == null) continue;
                if (w.orbitBlades != null)
                {
                    foreach (var b in w.orbitBlades) if (b != null) Destroy(b);
                    w.orbitBlades = null;
                }
                if (w.auraVisual != null) { Destroy(w.auraVisual); w.auraVisual = null; }
            }
        }

        // 武器运行时状态（非 Mono，挂在玩家更新循环里）
        public class WeaponSlot
        {
            public SkillDef def;
            public int level = 1;      // M3.6：武器等级 1..SkillDef.MaxLevel
            public float timer;
            public float hitTimer;
            public List<GameObject> orbitBlades;
            public GameObject auraVisual;   // M6.1：光环领域视觉环
        }
    }
}
