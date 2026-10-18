using StarPixel.Enemy;
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

        // —— 掉落物 ——
        public static GameObject SpawnGem(Vector3 pos, float xp)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.gemPrefab == null) return null;
            return NetSpawner.Spawn(cfg.gemPrefab, pos, Quaternion.identity, null, go =>
            {
#if FISHNET_ENABLED
                var p = go.GetComponent<NetPickup>();
                if (p != null) p.ServerInit(NetPickupKind.Xp, xp, 0f);
#endif
            });
        }

        public static GameObject SpawnHealth(Vector3 pos, float heal)
        {
            var cfg = NetConfig.Instance;
            if (cfg == null || cfg.healthPrefab == null) return null;
            return NetSpawner.Spawn(cfg.healthPrefab, pos, Quaternion.identity, null, go =>
            {
#if FISHNET_ENABLED
                var p = go.GetComponent<NetPickup>();
                if (p != null) p.ServerInit(NetPickupKind.Health, 0f, heal);
#endif
            });
        }

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
