using System;
using StarPixel.Enemy;
using UnityEngine;

namespace StarPixel.Combat
{
    /// <summary>
    /// 统一索敌/范围枚举入口：同时覆盖单机（EnemyManager 列表）与联机（NetEnemyRegistry）两条路径，
    /// 使开火、AoE、环绕、Boss 索敌等逻辑在两种模式下完全复用同一份代码（对应 TDD 4.2）。
    /// </summary>
    public static class CombatTargets
    {
        /// <summary>
        /// M17 实测修复：遍历快照缓冲。
        /// EnemyManager.ActiveEnemies 是"活列表"——敌人死亡时会被 RemoveEnemy 即时移出。
        /// 而 ForEachWithin 的回调会造成击杀，等于在遍历过程中修改集合，
        /// 直接 foreach 原列表会抛 InvalidOperationException: Collection was modified
        /// （M17 真机实测捕获到 42 次，触发路径为 FireNova → ForEachWithin）。
        /// 故统一先快照到本缓冲再遍历；复用同一实例避免每帧分配。
        /// 类型名完全限定：Enemy 与命名空间 StarPixel.Enemy 同名，非限定写法会 CS0118。
        /// </summary>
        private static readonly System.Collections.Generic.List<StarPixel.Enemy.Enemy> Buffer =
            new System.Collections.Generic.List<StarPixel.Enemy.Enemy>();

        /// <summary>范围内最近的敌人/Boss Transform；无目标返回 null。</summary>
        public static Transform Nearest(Vector2 origin, float range)
        {
            Transform best = null;
            float bestD = range * range;

            var em = EnemyManager.Instance;
            if (em != null)
            {
                var list = em.ActiveEnemies;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e == null) continue;
                    float d = ((Vector2)e.transform.position - origin).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = e.transform; }
                }
                var boss = em.ActiveBoss;
                if (boss != null && !boss.damageable.isDead)
                {
                    float d = ((Vector2)boss.transform.position - origin).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = boss.transform; }
                }
            }

#if FISHNET_ENABLED
            var ne = StarPixel.Net.NetEnemyRegistry.Nearest(origin, Mathf.Sqrt(bestD));
            if (ne != null) best = ne.transform;
#endif
            return best;
        }

        /// <summary>
        /// M18：最近的存活敌人（不限距离）。
        /// 为什么单独开一个入口：联机下的敌人是 NetEnemy，不会进 EnemyManager.ActiveEnemies
        /// （那是单机 Enemy 的列表）。调用方若直接读那份列表，在联机下会得到空结果——
        /// 冒烟实测的自动驾驶就因此全程站桩不动（150s 只打出 10 杀、卡在第 1 波）。
        /// 不限距离用于"自动驾驶朝目标靠近"，与开火用的有限射程 Nearest 区分开。
        /// </summary>
        public static Transform NearestAny(Vector2 origin) => Nearest(origin, float.MaxValue);

        /// <summary>对范围内所有可受伤目标执行回调（Nova / 环绕刃用）。</summary>
        public static void ForEachWithin(Vector2 center, float radius, Action<Damageable> act)
        {
            if (act == null) return;
            float r2 = radius * radius;

            var em = EnemyManager.Instance;
            if (em != null)
            {
                // 先快照：回调里的击杀会即时把敌人移出 ActiveEnemies，
                // 若直接遍历原列表会抛 InvalidOperationException（见 Buffer 注释）。
                var list = em.ActiveEnemies;
                Buffer.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e != null) Buffer.Add(e);
                }

                for (int i = 0; i < Buffer.Count; i++)
                {
                    var e = Buffer[i];
                    if (e == null || e.damageable == null || e.damageable.isDead) continue;
                    if (((Vector2)e.transform.position - center).sqrMagnitude <= r2)
                        act(e.damageable);
                }
                var boss = em.ActiveBoss;
                if (boss != null && !boss.damageable.isDead &&
                    ((Vector2)boss.transform.position - center).sqrMagnitude <= r2)
                    act(boss.damageable);
            }

#if FISHNET_ENABLED
            StarPixel.Net.NetEnemyRegistry.ForEachWithin(center, radius, act);
#endif
        }

        /// <summary>当前存活敌人数量（波次清场判定；联机时统计网络敌人）。</summary>
        public static int AliveEnemyCount
        {
            get
            {
                int n = 0;
                var em = EnemyManager.Instance;
                if (em != null)
                {
                    var list = em.ActiveEnemies;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e != null && e.damageable != null && !e.damageable.isDead) n++;
                }
                    if (em.ActiveBoss != null && !em.ActiveBoss.damageable.isDead) n++;
                }
#if FISHNET_ENABLED
                // M18：用 AliveCount 而非 Count——后者含已死亡但未反生成的对象，
                // 会让"是否清场"的判定永远为否、波次卡死（详见 NetEnemyRegistry.AliveCount 注释）。
                n += StarPixel.Net.NetEnemyRegistry.AliveCount;
#endif
                return n;
            }
        }
    }
}
