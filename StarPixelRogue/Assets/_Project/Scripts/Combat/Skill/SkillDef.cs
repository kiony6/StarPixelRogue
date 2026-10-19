using UnityEngine;

namespace StarPixel.Combat.Skill
{
    /// <summary>
    /// 武器/技能行为类型，决定 PlayerController 的开火逻辑（对应 TDD 4.1 数据驱动）。
    /// M6.1 扩充四个新武器族：Chain 链闪电 / Boomerang 回旋镖 / Homing 追踪弹 / Aura 光环领域。
    /// </summary>
    public enum WeaponType
    {
        Projectile,  // 直射弹道
        Scatter,     // 散射（多弹）
        Nova,        // 以玩家为中心的爆发 AoE
        Orbit,       // 环绕玩家的近战刃
        Chain,       // M6.1：瞬时链闪电（最近目标之间跳跃）
        Boomerang,   // M6.1：回旋镖（飞出后返回，来回各伤一次）
        Homing,      // M6.1：追踪弹（转向最近敌人）
        Aura,        // M6.1：光环领域（以自身为中心持续伤害）
        Mine         // M9：脉冲地雷（埋设后自动引爆的区域封锁）
    }

    /// <summary>
    /// 技能数据定义（数据驱动，对应 TDD 4.1 SkillDef 表）。
    /// 策划可在 Unity 中右键 Create > StarPixel > Skill Def 创建资产并填表；
    /// 原型阶段升级目录通过代码运行时 CreateInstance 生成，无需资产文件。
    /// </summary>
    [CreateAssetMenu(fileName = "NewSkill", menuName = "StarPixel/Skill Def")]
    public class SkillDef : ScriptableObject
    {
        /// <summary>武器最大等级（M3.6 平衡：加深 build 深度，避免成长断崖）。</summary>
        public const int MaxLevel = 4;

        public int id;
        public string skillName;

        /// <summary>
        /// M18 续四：武器名的本地化键（<see cref="DisplayName"/> 用它取中英双语文案）。
        /// 为什么不直接把 <see cref="skillName"/> 做成双语：它还承担开发期可读标识
        /// （日志、资产名、配置对齐），混用会导致"改文案"和"改标识"两件事纠缠。
        /// 留空时 <see cref="DisplayName"/> 回退到 skillName，老数据不会因此显示空串。
        /// </summary>
        public string locKey = "";

        /// <summary>按当前语言渲染的展示名（升级卡 / HUD / 图鉴统一走这里）。</summary>
        public string DisplayName =>
            !string.IsNullOrEmpty(locKey) ? Core.Localization.T(locKey) : skillName;

        public WeaponType weaponType = WeaponType.Projectile;
        public string[] tags;             // 元素/流派标签（组合技用）

        public float cooldown = 0.4f;     // 冷却（秒）
        public float damage = 12f;        // 基础伤害
        public int projectileCount = 1;   // 弹体数量（Scatter/Homing/Boomerang 用）
        public float projectileSpeed = 9f;
        public float pierce = 0f;         // 穿透数
        public float areaScale = 1f;      // 范围缩放
        public float range = 7f;          // 索敌范围

        public float aoeRadius = 3f;      // Nova 爆发半径 / Aura 光环半径
        public float orbitRadius = 2f;    // Orbit 环绕半径
        public int orbitCount = 1;        // Orbit 刃数量

        // —— M6.1 新武器族参数 ——
        public int chainCount = 3;        // Chain：跳电目标数（含首个）
        public float chainRange = 4f;     // Chain：每次跳电的搜索半径
        public float damageFalloff = 0.8f;// Chain：每跳伤害衰减系数
        public float boomerangRange = 6f; // Boomerang：最远飞出距离
        public float homingTurn = 8f;     // Homing：每秒最大转向弧度
        public float auraInterval = 0.5f; // Aura：伤害 tick 间隔（秒）

        // —— M9 第10把武器：脉冲地雷 ——
        public float mineArmTime = 0.35f;  // 布雷后激活延迟（秒）
        public float mineTrigger = 1.6f;   // 触发半径（敌人进入即引爆）
        public float mineLife = 8f;        // 最长存活时间（到点自动引爆，秒）

        // —— M3.6 等级成长公式（与 balance_sim.py 的 weapon_stats 一一对应）——

        /// <summary>指定等级下的有效伤害。</summary>
        public float EffectiveDamage(int level)
        {
            int lv = Mathf.Clamp(level, 1, MaxLevel);
            switch (weaponType)
            {
                case WeaponType.Projectile: return damage * Mathf.Pow(1.25f, lv - 1);   // 直射：+25%/级
                case WeaponType.Scatter:    return damage * Mathf.Pow(1.10f, lv - 1);   // 散射：+10%/级
                case WeaponType.Nova:       return damage * Mathf.Pow(1.20f, lv - 1);   // 爆发：+20%/级
                case WeaponType.Orbit:      return damage * Mathf.Pow(1.10f, lv - 1);   // 环绕：+10%/级
                case WeaponType.Chain:      return damage * Mathf.Pow(1.18f, lv - 1);   // 链电：+18%/级
                case WeaponType.Boomerang:  return damage * Mathf.Pow(1.22f, lv - 1);   // 回旋镖：+22%/级
                case WeaponType.Homing:     return damage * Mathf.Pow(1.20f, lv - 1);   // 追踪：+20%/级
                case WeaponType.Aura:       return damage * Mathf.Pow(1.15f, lv - 1);   // 光环：+15%/级
                case WeaponType.Mine:       return damage * Mathf.Pow(1.20f, lv - 1);   // 地雷：+20%/级
            }
            return damage;
        }

        /// <summary>指定等级下的有效弹体 / 刃 / 跳电目标数量。</summary>
        public int EffectiveCount(int level)
        {
            int lv = Mathf.Clamp(level, 1, MaxLevel);
            switch (weaponType)
            {
                case WeaponType.Projectile:
                    if (lv >= 4) return projectileCount + 2;   // Lv3 +1、Lv4 再 +1
                    if (lv >= 3) return projectileCount + 1;
                    return projectileCount;
                case WeaponType.Scatter: return projectileCount + (lv - 1);  // 每级 +1 弹体
                case WeaponType.Orbit:   return lv;                          // 每级 +1 刃
                case WeaponType.Chain:   return chainCount + (lv - 1);       // 每级 +1 跳电目标
                case WeaponType.Boomerang:
                    return projectileCount + (lv >= 3 ? 1 : 0) + (lv >= 4 ? 1 : 0); // Lv3/Lv4 各 +1
                case WeaponType.Homing:  return projectileCount + (lv - 1);  // 每级 +1 枚
                case WeaponType.Mine:   return projectileCount + (lv >= 3 ? 1 : 0) + (lv >= 4 ? 1 : 0); // Lv3/Lv4 各 +1 枚
            }
            return projectileCount;
        }

        /// <summary>指定等级下的有效穿透数。</summary>
        public float EffectivePierce(int level)
        {
            int lv = Mathf.Clamp(level, 1, MaxLevel);
            if (weaponType == WeaponType.Projectile && lv >= 4) return pierce + 1f;
            return pierce;
        }

        /// <summary>指定等级下的有效 AoE 半径。</summary>
        public float EffectiveAoeRadius(int level)
        {
            int lv = Mathf.Clamp(level, 1, MaxLevel);
            if (weaponType == WeaponType.Nova) return aoeRadius * Mathf.Pow(1.15f, lv - 1);
            if (weaponType == WeaponType.Aura) return aoeRadius * Mathf.Pow(1.12f, lv - 1);
            if (weaponType == WeaponType.Mine) return aoeRadius * Mathf.Pow(1.12f, lv - 1);
            return aoeRadius;
        }

        /// <summary>
        /// 下一级效果描述对应的**本地化键**（升级选卡用）。
        /// M18 续四：原先这里直接返回成品中文，英文界面下升级卡会冒出中文。
        /// 改返回键后由调用方 <c>Localization.T(...)</c> 渲染，与武器名/状态文案同一套做法。
        /// </summary>
        public string NextLevelDescKey(int nextLevel)
        {
            switch (weaponType)
            {
                case WeaponType.Projectile:
                    if (nextLevel == 3) return "weapon.next.projectile.3";
                    if (nextLevel == 4) return "weapon.next.projectile.4";
                    return "weapon.next.projectile";
                case WeaponType.Scatter: return "weapon.next.scatter";
                case WeaponType.Nova:    return "weapon.next.nova";
                case WeaponType.Orbit:   return "weapon.next.orbit";
                case WeaponType.Chain:   return "weapon.next.chain";
                case WeaponType.Boomerang:
                    return nextLevel >= 3 ? "weapon.next.boomerang.3" : "weapon.next.boomerang";
                case WeaponType.Homing:  return "weapon.next.homing";
                case WeaponType.Aura:    return "weapon.next.aura";
                case WeaponType.Mine:    return nextLevel >= 3 ? "weapon.next.mine.3" : "weapon.next.mine";
            }
            return "";
        }

        /// <summary>下一级效果描述（已按当前语言渲染，直接可展示）。</summary>
        public string NextLevelDesc(int nextLevel)
        {
            var key = NextLevelDescKey(nextLevel);
            return string.IsNullOrEmpty(key) ? "" : Core.Localization.T(key);
        }
    }
}
