using System.Collections.Generic;
using UnityEngine;
using StarPixel.Combat;
using StarPixel.Net;

namespace StarPixel.Enemy
{
    /// <summary>
    /// 敌人/波次管理器（对应 TDD 4.2 / 4.4 / M3）。
    /// - 对象池驱动刷怪、波次递增难度；
    /// - 每 5 波进入 Boss 波，改为单 Boss 战；
    /// - 每 5 波切换一次「区域」，改变背景色与 HUD 区域提示。
    /// </summary>
    public class EnemyManager : Core.MonoSingleton<EnemyManager>
    {
        public List<Enemy> ActiveEnemies { get; private set; } = new List<Enemy>();
        public Boss ActiveBoss { get; private set; }
        public int CurrentWave => _wave;

        private GameObject _enemyTemplate;
        private GameObject _bossTemplate;
        private GameObject _enemyProjectileTemplate;
        private Transform _playerTransform;
        private Damageable _playerDamageable;

        private int _wave;
        private int _toSpawn;
        private float _spawnTimer;
        private float _waveCooldown = 3f;
        private float _difficultyScale = 1f;
        private bool _bossActive;
        private int _zone;

        private static readonly Color[] ZoneColors =
        {
            new Color(0.05f, 0.05f, 0.08f),
            new Color(0.09f, 0.04f, 0.11f),
            new Color(0.03f, 0.08f, 0.10f),
            new Color(0.11f, 0.07f, 0.04f),
            new Color(0.04f, 0.10f, 0.06f),
        };

        public void Configure(GameObject enemyTemplate, Transform playerTransform, Damageable playerDamageable)
        {
            _enemyTemplate = enemyTemplate;
            _playerTransform = playerTransform;
            _playerDamageable = playerDamageable;
        }

        public void ConfigureBoss(GameObject bossTemplate, GameObject enemyProjectileTemplate)
        {
            _bossTemplate = bossTemplate;
            _enemyProjectileTemplate = enemyProjectileTemplate;
        }

        public void StartWaves() => NextWave();

        /// <summary>
        /// M23 无头实测：暂停波次推进（默认 false，正常游戏永不置位）。
        /// 为什么需要：实测要自己摆靶子并精确数伤害，而正常波次会持续往 ActiveEnemies 里塞敌人——
        /// 它们既会抢走"最近目标"让武器打偏，也会顺带把玩家撞掉血、把数值断言搅乱。
        /// 只冻结刷怪与波次推进，不动已存在的敌人与 Boss 流程。
        /// </summary>
        public bool FreezeWaves { get; set; }

        void Update()
        {
            if (FreezeWaves) return;

            // 联机模式下波次由房主驱动，玩家死亡不阻断（权威侧统一结算）
            if (!NetSession.IsNetworked && (_playerDamageable == null || _playerDamageable.isDead)) return;

            // Boss 战期间暂停常规刷怪，等待 Boss 被击败
            if (_bossActive) return;

            if (_toSpawn > 0)
            {
                _spawnTimer -= Time.deltaTime;
                if (_spawnTimer <= 0f)
                {
                    SpawnOne();
                    _toSpawn--;
                    // M3.6 平衡：刷怪间隔随波次加快（0.15s → 下限 0.07s），制造人口压力与"被围住"的真实威胁
                    _spawnTimer = Mathf.Max(0.07f, 0.15f - _wave * 0.0025f);
                }
            }
            else if (CombatTargets.AliveEnemyCount == 0)   // 联机时统计网络敌人（CombatTargets 统一入口）
            {
                _waveCooldown -= Time.deltaTime;
                if (_waveCooldown <= 0f) NextWave();
            }
        }

        private void NextWave()
        {
            _wave++;
            // M8 方案 B：Wave 30 前 0.10/波，之后 0.18/波——XP 约 Wave 62 停滞、build 停止成长，
            // 后段加压让难度重新追上（模拟验证：游侠 P90 波 61→59，守卫死亡减少主要来自角色 Buff）
            _difficultyScale = _wave <= 30
                ? 1f + (_wave - 1) * 0.10f
                : 1f + 29f * 0.10f + (_wave - 30) * 0.18f;
            _waveCooldown = 3f;
            _spawnTimer = 0f;

            Core.EventBus.RaiseWaveChanged(_wave);
            UpdateZone();

#if FISHNET_ENABLED
            if (NetSession.IsNetworked) NetWorldState.Instance?.ServerSetWave(_wave);
#endif

            if (_wave % 5 == 0)
            {
                SpawnBoss();
            }
            else
            {
                _toSpawn = 8 + _wave * 4;
            }
        }

        private void UpdateZone()
        {
            int zone = (_wave - 1) / 5 + 1;
            if (zone == _zone) return;
            _zone = zone;
            Core.EventBus.RaiseZoneChanged(zone);

#if FISHNET_ENABLED
            if (NetSession.IsNetworked) NetWorldState.Instance?.ServerSetZone(zone);
#endif

            var cam = Camera.main;
            if (cam != null) cam.backgroundColor = ZoneColors[(zone - 1) % ZoneColors.Length];
        }

        private void SpawnBoss()
        {
            // M4.5：Boss 血量与单机版一致（400+90*wave，无额外难度缩放，见 Boss.Init）
            float bossHp = 400f + 90f * Mathf.Max(1, _wave);

            // 联机模式：Boss 作为网络敌人生成（房主权威，客户端同步位置/血量）
            if (NetWorld.Active)
            {
                Vector2 pos = RandomEdgePosition();
                NetWorld.SpawnEnemy(pos, EnemyKind.Tank, bossHp, 1.1f, false, true, _wave);
                // 与单机一致：Boss 战期间暂停常规刷怪，等 Boss 被击败再进下一波
                // （NetEnemy.ServerDie → EnemyManager.OnBossDefeated 会解除）
                _bossActive = true;
                Core.EventBus.RaiseBossSpawned(bossHp, bossHp);
                return;
            }

            if (_bossTemplate == null) { _toSpawn = 8 + _wave * 4; return; }
            _bossActive = true;

            // M9/M12：Boss 变体随机（0=守望者 / 1=风暴领主 / 2=虚空编织者）。仅单机路径应用——
            // 联机 Boss 走 NetWorld 简化镜像（无 BossBrain 模式），变体模式不生效。
            int variant = Random.Range(0, 3);

            var go = Instantiate(_bossTemplate);
            go.SetActive(true);
            go.transform.localScale = Vector3.one * 1.6f;
            var boss = go.GetComponent<Boss>();
            boss.Init(_playerTransform, _playerDamageable, _enemyProjectileTemplate, this, _wave, variant);
            ActiveBoss = boss;
        }

        /// <summary>Boss 死亡回调：结束 Boss 战，短暂间隔后进入下一波。</summary>
        public void OnBossDefeated()
        {
            ActiveBoss = null;
            _bossActive = false;
            _waveCooldown = 3f;
        }

        /// <summary>Boss 召唤小怪入口（复用敌人对象池，纳入常规清场统计）。</summary>
        public void SpawnAdd(Vector2 pos, EnemyKind kind, float hp, float speed)
        {
            DiscoverEnemyKind(kind, false);   // M6.4 图鉴
            if (NetWorld.Active)
            {
                NetWorld.SpawnEnemy(pos, kind, hp, speed, false, false, _wave);
                return;
            }
            if (_enemyTemplate == null) return;
            var go = Core.PoolManager.Instance.Get(_enemyTemplate);
            var e = go.GetComponent<Enemy>();
            e.scoreValue = 1;
            e.contactDamage = KindContactDamage(kind);
            e.Spawn(pos, hp, speed, kind, _enemyTemplate, _playerTransform, _playerDamageable);
            ActiveEnemies.Add(e);
        }

        /// <summary>M3.6 平衡：按敌人类型区分接触伤害（追击 6 / 疾行 9 / 重甲 11）。</summary>
        private static float KindContactDamage(EnemyKind kind)
        {
            switch (kind)
            {
                case EnemyKind.Runner: return 9f;
                case EnemyKind.Tank:   return 11f;
                default:               return 6f;
            }
        }

        /// <summary>M6.4：敌种发现记录（图鉴）。精英作为独立条目（编码 3）。</summary>
        private static void DiscoverEnemyKind(EnemyKind kind, bool elite)
        {
            Core.SaveManager.Instance?.DiscoverEnemy(elite ? 3 : (int)kind);
        }

        private void SpawnOne()
        {
            Vector2 pos = RandomEdgePosition();
            var kind = PickKind(_wave);
            float hp, speed;
            int score;
            switch (kind)
            {
                case EnemyKind.Runner:
                    // M8 方案 B：速度封顶 5.2（原公式 Wave 23 起超玩家基础移速 5.6，无移速强化时无法风筝）
                    hp = 12f * _difficultyScale; speed = Mathf.Min(3.8f + _wave * 0.08f, 5.2f); score = 1; break;
                case EnemyKind.Tank:
                    hp = 60f * _difficultyScale; speed = 1.4f + _wave * 0.05f; score = 3; break;
                default: // Chaser
                    hp = 20f * _difficultyScale; speed = 2.2f + _wave * 0.06f; score = 1; break;
            }

            // 精英：第 3 波起按概率出现，随波次缓慢提升，上限约 26%
            bool elite = _wave >= 3 && Random.value < Mathf.Min(0.06f + _wave * 0.01f, 0.26f);

            DiscoverEnemyKind(kind, elite);   // M6.4 图鉴

            // 联机模式：生成网络敌人（房主权威）
            if (NetWorld.Active)
            {
                NetWorld.SpawnEnemy(pos, kind, hp, speed, elite, false, _wave);
                return;
            }

            var go = Core.PoolManager.Instance.Get(_enemyTemplate);
            var e = go.GetComponent<Enemy>();
            e.scoreValue = elite ? score * 5 : score;
            e.contactDamage = KindContactDamage(kind) * (elite ? 1.6f : 1f);   // 精英接触伤害更高
            e.Spawn(pos, hp, speed, kind, _enemyTemplate, _playerTransform, _playerDamageable, elite);
            ActiveEnemies.Add(e);
        }

        private static EnemyKind PickKind(int wave)
        {
            if (wave < 2) return EnemyKind.Chaser;
            float r = Random.value;
            if (wave < 4) return r < 0.7f ? EnemyKind.Chaser : EnemyKind.Runner;
            if (r < 0.5f) return EnemyKind.Chaser;
            return r < 0.85f ? EnemyKind.Runner : EnemyKind.Tank;
        }

        public void RemoveEnemy(Enemy e) => ActiveEnemies.Remove(e);

        private Vector2 RandomEdgePosition()
        {
            Vector2 center = Vector2.zero;
#if FISHNET_ENABLED
            if (NetSession.IsNetworked)
            {
                // 联机：围绕最近玩家刷怪，保证压力始终来自玩家身边
                var p = NetPlayerRegistry.NearestAlive(center);
                if (p != null) center = p.transform.position;
            }
            else
#endif
            {
                if (_playerTransform != null) center = _playerTransform.position;
            }
            float radius = 9f;
            float ang = Random.Range(0f, Mathf.PI * 2f);
            return center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius;
        }

        /// <summary>
        /// 拆世界 / 重开一局时调用：销毁场上还活着的全部敌人并重置波次状态。
        ///
        /// M24：旧实现只 SetActive(false)。敌人是 PoolManager 的克隆体，定义在上一局的
        /// _playerTransform（已被新建的世界换掉了），这些"幽灵敌人"会带着旧坐标继续刷新出来
        /// ——新世界的 EnemyManager 账本里没有它们，既打不到也不会被清。
        /// 配合 PoolManager.DestroyAll() 才算真的干净。
        /// </summary>
        public void ResetAll()
        {
            foreach (var e in ActiveEnemies) if (e != null) Object.Destroy(e.gameObject);
            ActiveEnemies.Clear();
            _wave = 0;
            _zone = 0;
            _bossActive = false;
        }
    }
}
