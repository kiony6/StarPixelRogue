using System;
using UnityEngine;

namespace StarPixel.Player
{
    /// <summary>
    /// 玩家数值（原型用可序列化类；后续可由 ScriptableObject / 云端存档驱动）。
    /// </summary>
    [Serializable]
    public class PlayerStats
    {
        // —— M3.6 平衡（数值由 balance_sim.py 蒙特卡洛验证，中位存活约 15 分钟 / 第 14 波）——
        public float maxHp = 110f;
        public float moveSpeed = 5.6f;
        public float attackDamageMult = 1f;
        public float attackCooldown = 0.35f;  // 自动攻击间隔（秒）
        public float attackRange = 7f;       // 索敌范围
        public int projectileCount = 1;
        public float projectileSpeed = 9f;
        public float projectileDamage = 18f;
        public float projectilePierce = 0f;

        // M3.6：弹体强化改为独立计数（旧实现改 def.projectileCount，但直射武器开火写死 1，导致强化无效）
        public int projectileBonus = 0;

        // —— M2 新增：成长与手感倍率（由升级强化）——
        public float pickupRadius = 1.6f;    // 经验宝石磁吸半径
        public float xpGainMult = 1f;        // 经验获取倍率
        public float cooldownMult = 1f;      // 冷却倍率（越低越快）
        public float areaMult = 1f;          // 范围/体积倍率

        /// <summary>
        /// M23：浅拷贝。角色乘区是乘算的，换角色必须从**基准值**重算而不是在当前值上再乘，
        /// 否则重装守卫会把 1.35 倍再乘一次（实测 148.5 → 200.5 血）。
        /// 字段全部是值类型，浅拷贝即足够。
        /// </summary>
        public PlayerStats Clone()
        {
            return (PlayerStats)MemberwiseClone();
        }
    }
}
