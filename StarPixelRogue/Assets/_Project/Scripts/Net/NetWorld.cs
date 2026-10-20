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
            TrimPlayers.Clear();
            NetPlayerRegistry.ForEachAlive(p => { if (p != null) TrimPlayers.Add(p.transform.position); });
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
            TrimPlayers.Clear();
            NetPlayerRegistry.ForEachAlive(p => { if (p != null) TrimPlayers.Add(p.transform.position); });
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
