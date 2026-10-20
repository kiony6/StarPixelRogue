#if FISHNET_ENABLED
using System.Collections.Generic;
using StarPixel.Combat;   // Damageable（M18 联机实测修复：此前缺失，仅在 FISHNET_ENABLED 下才暴露）
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 网络敌人注册表。由 <see cref="NetEnemy"/> 在 OnStartClient 注册、OnStopClient 反注册，
    /// 使索敌/清场判定在房主与客户端走同一套代码路径（对应 TDD 4.2 AOI 思路）。
    /// </summary>
    public static class NetEnemyRegistry
    {
        private static readonly List<NetEnemy> All = new List<NetEnemy>();

        public static int Count
        {
            get { All.RemoveAll(e => e == null); return All.Count; }
        }

        /// <summary>
        /// M18：存活（未死亡）的网络敌人数。
        /// 与 <see cref="Count"/> 的区别：Count 统计所有已注册对象、不过滤死亡状态，
        /// 而波次推进要问的是"还有没有活口"——只要残留一具未反生成的尸体，
        /// 用 Count 就会让 AliveEnemyCount 永不归零、波次永久卡死。故单独提供过滤版。
        /// </summary>
        public static int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < All.Count; i++)
                {
                    var e = All[i];
                    if (e == null || e.IsDead) continue;
                    n++;
                }
                return n;
            }
        }

        public static void Register(NetEnemy e) { if (e != null && !All.Contains(e)) All.Add(e); }

        public static void Unregister(NetEnemy e) { if (e != null) All.Remove(e); }

        /// <summary>最近目标（供 CombatTargets 使用，无每帧分配）。</summary>
        public static NetEnemy Nearest(Vector2 origin, float range)
        {
            NetEnemy best = null;
            float bestD = range * range;
            for (int i = 0; i < All.Count; i++)
            {
                var e = All[i];
                if (e == null || e.IsDead) continue;
                float d = ((Vector2)e.transform.position - origin).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>
        /// M43：全部反生成（拆世界 / 重开一局用），只在房主侧调用。
        ///
        /// 为什么必须先拷一份再遍历：反生成会触发 <see cref="NetEnemy"/> 的 OnStopClient →
        /// 反注册，边遍历边删原表会漏掉一半敌人——正是"重开后还有一批旧敌人留在地上"的成因。
        /// 拷贝只在拆世界时发生一次，不影响战斗路径的零分配。
        /// </summary>
        public static void DespawnAll()
        {
            if (All.Count == 0) return;
            var copy = new List<NetEnemy>(All);
            All.Clear();
            for (int i = 0; i < copy.Count; i++)
            {
                var e = copy[i];
                if (e != null) NetSpawner.Despawn(e.gameObject);
            }
        }

        /// <summary>范围内的敌人依次回调（AoE 伤害用）。</summary>
        public static void ForEachWithin(Vector2 center, float radius, System.Action<Damageable> act)
        {
            float r2 = radius * radius;
            for (int i = 0; i < All.Count; i++)
            {
                var e = All[i];
                if (e == null || e.IsDead) continue;
                if (((Vector2)e.transform.position - center).sqrMagnitude <= r2)
                    act?.Invoke(e.Damageable);
            }
        }
    }

    /// <summary>网络玩家注册表（房主侧用于敌人 AI 索敌与接触伤害）。</summary>
    public static class NetPlayerRegistry
    {
        private static readonly List<NetPlayer> All = new List<NetPlayer>();
        private static NetPlayer _local;

        /// <summary>本机本地玩家（房主视角为房主自己的角色，客户端视角为自己的角色）。</summary>
        public static NetPlayer Local => _local;

        public static int Count
        {
            get { All.RemoveAll(p => p == null); return All.Count; }
        }

        public static void Register(NetPlayer p)
        {
            if (p == null) return;
            if (!All.Contains(p)) All.Add(p);
            if (p.IsOwner) _local = p;   // 注册时机在 OnStartClient，Owner 已分配
        }

        public static void Unregister(NetPlayer p)
        {
            if (p == null) return;
            All.Remove(p);
            if (_local == p) _local = null;
        }

        /// <summary>距离 position 最近的存活玩家（敌人 AI / 掉落磁吸用）。</summary>
        public static NetPlayer NearestAlive(Vector2 position)
        {
            NetPlayer best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < All.Count; i++)
            {
                var p = All[i];
                if (p == null || p.Damageable == null || p.Damageable.isDead) continue;
                float d = ((Vector2)p.transform.position - position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /// <summary>遍历所有存活玩家（零分配；回调内不得增删注册表）。</summary>
        public static void ForEachAlive(System.Action<NetPlayer> act)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var p = All[i];
                if (p == null || p.Damageable == null || p.Damageable.isDead) continue;
                act(p);
            }
        }
    }
}
#endif
