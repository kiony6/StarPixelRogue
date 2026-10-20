using System.Collections.Generic;
using StarPixel.Enemy;
using StarPixel.Pickups;
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 世界对象生成接缝（对应 TDD 4.2/4.4 的实体管理）。
    /// - 单机（NetSession.Mode == Offline）：调用方直接走原有对象池，不使用本类；
    /// - 联机：所有敌人/掉落/弹丸/世界状态统一经此生成网络对象，房主权威、客户端只读同步。
    /// 这样做的好处：单机路径零改动零风险，联机路径集中在一处可调。
    ///
    /// M18 双开实测修复：权威初始值（各 ServerInit）原本经 beforeSpawn 在"网络 Spawn 之前"注入，
    /// 意图是让初始 SyncVar 随生成包一起送达。但 Fish-Net 4.x 下那时对象尚未完成网络初始化，
    /// 写 SyncVar 会抛 NullReferenceException（实测 60s 刷 5.7 万条异常、敌人完全生成不出来）。
    /// 现改走 NetSpawner 的 **afterSpawn**（Spawn 之后）注入；客户端改由 OnChange 修正表现，
    /// 代价是最多一帧的默认外观，正确性优先。
    /// </summary>
    public static class NetWorld
    {
        /// <summary>是否应走网络生成路径。</summary>
        public static bool Active => NetSession.IsNetworked && NetConfig.Instance != null;

        // —— 敌人（房主权威生成）——
        public static GameObject SpawnEnemy(Vector3 pos, EnemyKind kind, float hp, float speed,
            bool elite, bool boss, int wave)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.enemyPrefab == null) return null;
            return NetSpawner.Spawn(cfg.enemyPrefab, pos, Quaternion.identity, null, go =>
            {
#if FISHNET_ENABLED
                var ne = go.GetComponent<NetEnemy>();
                if (ne != null) ne.ServerInit(kind, hp, speed, elite, boss, wave);
#endif
            });
        }

        // —— 掉落物账本（M42）——
        //
        // M41 修的是单机侧（PickupManager）：两个 List 只进不出，30 分钟游戏时间堆到
        // 887 个宝石 + 148 个回血球，每帧还要全表算距离 → 越玩越卡。
        // **联机侧是同一个缺陷，而且更贵**：
        //   · NetEnemy.ServerDie() 直接调 SpawnGem/SpawnHealth，整条路绕过 PickupManager，
        //     从来没有任何账本、没有任何上限；
        //   · 每个 NetPickup 都是 NetworkBehaviour：2 个 SyncVar + NetworkTransform，
        //     磁吸时位置每帧在动 → 每个都在持续占用同步带宽；
        //   · NetPickup.Update() 在房主侧对**每一个**掉落物调一次 NetPlayerRegistry.NearestAlive()
        //     （全注册表遍历）——N 个掉落物 = N × 玩家数次距离计算/帧。
        // 双开时两个玩家打得更快，堆到的数量只会比单机更多。
        //
        // 整块受 FISHNET_ENABLED 保护：NetPickup 是联机专属类型，
        // 未装插件时必须整块编译掉（本文件一贯的隔离方式）。
#if FISHNET_ENABLED
        private static readonly List<NetPickup> Gems = new List<NetPickup>();
        private static readonly List<NetPickup> Healths = new List<NetPickup>();
        private static readonly List<Vector2> TrimPlayers = new List<Vector2>(2);
        private static int _gemsTrimmed;
        private static int _healthsTrimmed;

        // —— 存活玩家快照（M43）——
        //
        // 为什么要有：M42 修的是"账本只进不出"，但每次生成/回收掉落物都要调一次
        // NetPlayerRegistry.ForEachAlive()（全注册表遍历），而 NetPickup.Update() 更是
        // **每个**掉落物每帧遍历一次——房主侧的成本是 N × 玩家数 次距离计算，
        // 且 N 条 Update 各自独立跑，谁也不知道别人刚刚算过同一件事。
        //
        // 改成"每帧只重建一次、所有掉落物共用"：每帧 1 次注册表遍历，N 次距离计算仍然保留
        // （玩家只有 1～2 个，那是必要的最近目标判定，不是浪费），但遍历次数从 N+回收次数
        // 降到 1。抽样计数器 <see cref="PlayerSnapshotBuilds"/> 供探针判"没有退回去"。
        private static readonly List<NetPlayer> LivePlayers = new List<NetPlayer>(4);
        private static int _snapshotFrame = -1;
        private static int _snapshotBuilds;
        private static int _despawnedOnReset;

        /// <summary>M43：累计重建玩家快照的次数（每帧最多 1 次）。判据"玩家遍历不随掉落物增长"读它。</summary>
        public static int PlayerSnapshotBuilds => _snapshotBuilds;

        /// <summary>M43：上一次拆世界反生成了多少个对象（诊断用，不作判据）。</summary>
        public static int DespawnedOnReset => _despawnedOnReset;

        /// <summary>M43：把存活玩家重建成每帧一份的快照；同一帧内重复调用直接返回。</summary>
        private static void RefreshPlayers()
        {
            int frame = Time.frameCount;
            if (_snapshotFrame == frame) return;
            _snapshotFrame = frame;
            _snapshotBuilds++;
            LivePlayers.Clear();
            NetPlayerRegistry.ForEachAlive(p => { if (p != null) LivePlayers.Add(p); });
        }

        /// <summary>
        /// M43：离当前位置最近的存活玩家 + 真实距离。替代逐掉落物调用
        /// <see cref="NetPlayerRegistry.NearestAlive"/>（每次都要遍历整个注册表）。
        /// </summary>
        public static NetPlayer NearestPlayer(Vector2 pos, out float dist)
        {
            RefreshPlayers();
            NetPlayer best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < LivePlayers.Count; i++)
            {
                var p = LivePlayers[i];
                if (p == null) continue;
                float d = ((Vector2)p.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            dist = best == null ? float.MaxValue : Mathf.Sqrt(bestSq);
            return best;
        }

        /// <summary>M43：把快照里的玩家位置填进回收用的列表（与 NearestPlayer 共用同一次遍历）。</summary>
        private static void FillTrimPlayers()
        {
            RefreshPlayers();
            TrimPlayers.Clear();
            for (int i = 0; i < LivePlayers.Count; i++)
                if (LivePlayers[i] != null) TrimPlayers.Add(LivePlayers[i].transform.position);
        }

        /// <summary>M42：场上网络宝石数（账本口径，供探针诊断）。判据走场景扫描，不读这个。</summary>
        public static int GemCount { get { Purge(Gems); return Gems.Count; } }

        /// <summary>M42：场上网络回血球数。</summary>
        public static int HealthCount { get { Purge(Healths); return Healths.Count; } }

        /// <summary>M42：累计因超上限回收的宝石数。判据"上限真的生效"读它——世界没跑起来时它是 0。</summary>
        public static int GemTrimmed => _gemsTrimmed;

        /// <summary>M42：累计因超上限回收的回血球数。</summary>
        public static int HealthTrimmed => _healthsTrimmed;

        /// <summary>M42：拆世界/重开一局时清空账本与计数（账本是静态的，不清会跨局残留悬空引用）。</summary>
        public static void ResetPickups()
        {
            Gems.Clear();
            Healths.Clear();
            _gemsTrimmed = 0;
            _healthsTrimmed = 0;
            LivePlayers.Clear();
            _snapshotFrame = -1;
        }

        /// <summary>
        /// M43：拆世界时把所有**网络生成**的实体交还房主权威——反生成会广播给每个客户端。
        ///
        /// 为什么必须有这一步：<c>GameBuilder.TeardownWorld()</c> 此前只清单机那套
        /// （PoolManager 池实例 / EnemyManager.ActiveEnemies / PickupManager 账本），
        /// 而联机实体根本不在那些容器里：
        ///   · 敌人注册在 <see cref="NetEnemyRegistry"/>，不在 ActiveEnemies；
        ///   · 掉落物注册在本类的 Gems/Healths，不在 PickupManager；
        ///   · 世界状态对象由 <see cref="SpawnWorldState"/> 生成，谁都没有持有它；
        ///   · 在飞的弹丸（<see cref="NetProjectile"/>）连注册表都没有（M44 补上）。
        /// 结果：重开一局后，上一局的宝石原封不动躺在脚下（而且捡不起来——账本已经清零），
        /// 旧敌人继续追着新玩家跑，旧的 NetWorldState 还活着并和新生成的那个抢 Instance
        /// （波次在两份之间跳）。客户端更糟：房主若本地 Object.Destroy 而不反生成，
        /// 客户端副本会变成僵尸，永远留在场上。
        ///
        /// 只在房主侧执行：客户端不主动反生成任何东西，它等广播。
        /// </summary>
        public static void DespawnAllWorldObjects()
        {
            // IsAuthority 在 Offline 下也为真（Mode != Client），故必须同时要求联网，
            // 否则单机拆世界会走进一条本不存在的联机清理路径。
            if (!NetSession.IsNetworked || !NetSession.IsAuthority) return;

            // 按**类型扫场**，而不是只信注册表 / 账本。
            //
            // 为什么不能只清"登记过的"：敌人是在 NetEnemy.OnStartClient 里注册的，
            // 而 Fish-Net 对"当帧刚生成、客户端回调还没走完"的对象不会立刻登记。
            // 实测：拆完隔一帧仍有 4 个 NetEnemy 活着，而 NetEnemyRegistry 已经是 0。
            // 拆世界的语义是"场上一个不留"——必须以场景为准，
            // 注册表与账本只是索引，不是事实。
            int n = 0;
            n += SweepDespawn<NetPickup>();
            n += SweepDespawn<NetEnemy>();
            n += SweepDespawn<NetWorldState>();
            // M44：弹丸也必须扫。它是 M43 唯一漏掉的一类网络实体——
            // 没有账本、没有注册表，谁都不持有它，只能靠"按类型扫场"才找得到。
            // 漏掉它的后果：重开一局后，上一局仍在飞的弹丸会留在新世界里继续结算伤害
            // （最长 2.5s 自毁，Boss 的径向弹幕更长），客户端那份还会变成僵尸。
            n += SweepDespawn<NetProjectile>();

            NetEnemyRegistry.DespawnAll();   // 清掉注册表里已失效 / 未激活的引用
            Gems.Clear();
            Healths.Clear();
            // 反生成不一定同步回调 OnStopClient（房主侧尤其），这里显式清掉：
            // 不清的话下一次 Build 会撞上"Instance != null"的旧引用，新世界状态永远不被采纳。
            NetWorldState.Instance = null;

            _despawnedOnReset = n;
        }

        /// <summary>
        /// 把场上所有该类型的联机实体交还房主权威（反生成会广播给每个客户端）。
        /// 跳过预制体资产（场景无效）与已回收的停用对象（Fish-Net 的 Despawn 默认回池，
        /// 回收后对象仍在场景里、只是被停用）。
        /// </summary>
        private static int SweepDespawn<T>() where T : Component
        {
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c == null) continue;
                var go = c.gameObject;
                if (!go.scene.IsValid()) continue;
                if (!go.activeInHierarchy) continue;
                NetSpawner.Despawn(go);
                n++;
            }
            return n;
        }

        /// <summary>M42：由 NetPickup.OnDestroy 调用——拾取/回收/拆世界三条销毁路径都归一到这里。</summary>
        public static void UnregisterPickup(NetPickup p)
        {
            if (p == null) return;
            Gems.Remove(p);
            Healths.Remove(p);
        }

        private static void Purge(List<NetPickup> list) => list.RemoveAll(g => g == null);

        /// <summary>
        /// M42：超出上限时回收"离最近玩家最远"的一个，走**房主权威反生成**
        /// （<see cref="NetSpawner.Despawn"/> → NetworkObject.Despawn 会广播给所有端）。
        ///
        /// 为什么不能本地 Object.Destroy：客户端对此一无所知，它的那份副本会变成僵尸——
        /// 位置不再更新却永远留在场上，双端计数从此分叉。这正是区分力注入 R4 要抓的面。
        /// </summary>
        private static void TrimGems()
        {
            Purge(Gems);
            if (!PickupTrim.Over(Gems.Count, PickupTrim.MaxGems)) return;
            FillTrimPlayers();   // M43：与磁吸共用"每帧一次"的玩家快照，不再每次生成都遍历注册表
            int worst = PickupTrim.FarthestIndex(Gems, TrimPlayers);
            if (worst < 0) return;
            var g = Gems[worst];
            Gems.RemoveAt(worst);
            _gemsTrimmed++;
            if (g != null) NetSpawner.Despawn(g.gameObject);
        }

        /// <summary>M42：回血球同上（两类共用"只进不出"的写法，只修一半是最容易发生的漏修）。</summary>
        private static void TrimHealth()
        {
            Purge(Healths);
            if (!PickupTrim.Over(Healths.Count, PickupTrim.MaxHealth)) return;
            FillTrimPlayers();   // M43：与磁吸共用"每帧一次"的玩家快照，不再每次生成都遍历注册表
            int worst = PickupTrim.FarthestIndex(Healths, TrimPlayers);
            if (worst < 0) return;
            var h = Healths[worst];
            Healths.RemoveAt(worst);
            _healthsTrimmed++;
            if (h != null) NetSpawner.Despawn(h.gameObject);
        }

        // —— 掉落物 ——
        public static GameObject SpawnGem(Vector3 pos, float xp)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.gemPrefab == null) return null;
            var go = NetSpawner.Spawn(cfg.gemPrefab, pos, Quaternion.identity, null, g =>
            {
#if FISHNET_ENABLED
                var p = g.GetComponent<NetPickup>();
                if (p != null) p.ServerInit(NetPickupKind.Xp, xp, 0f);
#endif
            });
#if FISHNET_ENABLED
            Register(go, Gems);
            TrimGems();
#endif
            return go;
        }

        public static GameObject SpawnHealth(Vector3 pos, float heal)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.healthPrefab == null) return null;
            var go = NetSpawner.Spawn(cfg.healthPrefab, pos, Quaternion.identity, null, g =>
            {
#if FISHNET_ENABLED
                var p = g.GetComponent<NetPickup>();
                if (p != null) p.ServerInit(NetPickupKind.Health, 0f, heal);
#endif
            });
#if FISHNET_ENABLED
            Register(go, Healths);
            TrimHealth();
#endif
            return go;
        }

        /// <summary>M42：生成成功后登记进账本（生成失败返回 null 时不登记，避免账本里出现死项）。</summary>
        private static void Register(GameObject go, List<NetPickup> ledger)
        {
            if (go == null) return;
            var p = go.GetComponent<NetPickup>();
            if (p != null) ledger.Add(p);
        }
#endif

        // —— 弹丸（hostile = 敌方弹幕）——
        public static GameObject SpawnProjectile(Vector3 pos, Vector2 dir, float dmg, float speed,
            float pierce, bool applySlow, bool hostile)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null) return null;
            var prefab = hostile ? cfg.enemyProjectilePrefab : cfg.playerProjectilePrefab;
            if (prefab == null) return null;
            return NetSpawner.Spawn(prefab, pos, Quaternion.identity, null, go =>
            {
#if FISHNET_ENABLED
                var pr = go.GetComponent<NetProjectile>();
                if (pr != null) pr.ServerInit(dir, dmg, speed, pierce, applySlow, hostile);
#endif
            });
        }

        /// <summary>M6.1：带弹道行为的玩家弹丸（1=回旋镖 2=追踪）。</summary>
        public static GameObject SpawnProjectileEx(Vector3 pos, Vector2 dir, float dmg, float speed,
            float range, int behavior, bool applySlow)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.playerProjectilePrefab == null) return null;
            return NetSpawner.Spawn(cfg.playerProjectilePrefab, pos, Quaternion.identity, null, go =>
            {
#if FISHNET_ENABLED
                var pr = go.GetComponent<NetProjectile>();
                if (pr != null) pr.ServerInit(dir, dmg, speed, 0f, applySlow, false, behavior, range);
#endif
            });
        }

        // —— 世界状态同步对象（波次/击杀/区域/Boss 血量）——
        public static GameObject SpawnWorldState()
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.worldStatePrefab == null) return null;
            return NetSpawner.Spawn(cfg.worldStatePrefab, Vector3.zero, Quaternion.identity);
        }
    }
}
