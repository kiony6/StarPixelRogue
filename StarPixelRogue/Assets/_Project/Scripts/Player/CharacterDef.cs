using UnityEngine;

namespace StarPixel.Player
{
    /// <summary>
    /// 角色定义（M6.2）：属性乘区 + 起始武器 + 特性文案。
    /// 原型阶段用代码构造（同 SkillDef 的运行时实例思路），无需资产文件。
    /// </summary>
    [System.Serializable]
    public class CharacterDef
    {
        public int id;
        public string nameKey;            // Localization 键
        public string descKey;
        public string traitKey;
        public int startingWeaponId;      // 起始武器（UpgradeCatalog id）

        // 属性乘区（作用于 PlayerStats 默认值；1 = 无修正）
        public float hpMult = 1f;
        public float speedMult = 1f;
        public float damageMult = 1f;
        public float cooldownMult = 1f;
        public float pickupMult = 1f;
        public float areaMult = 1f;
        public float xpMult = 1f;

        /// <summary>创建星尘游侠（均衡型，默认角色）。</summary>
        public static CharacterDef CreateRanger()
        {
            return new CharacterDef
            {
                id = 0,
                nameKey = "char.ranger",
                descKey = "char.ranger_desc",
                traitKey = "char.ranger_trait",
                startingWeaponId = 1,      // 星能弹
                xpMult = 1.15f,            // 特性：经验获取 +15%
            };
        }

        /// <summary>创建重装守卫（坦克型：更慢更硬，范围与伤害更高）。</summary>
        public static CharacterDef CreateBulwark()
        {
            return new CharacterDef
            {
                id = 1,
                nameKey = "char.bulwark",
                descKey = "char.bulwark_desc",
                traitKey = "char.bulwark_trait",
                startingWeaponId = 4,      // 裂地波（土系爆发）
                hpMult = 1.35f,
                speedMult = 0.90f,
                damageMult = 1.10f,
                cooldownMult = 0.88f,   // M8 方案 B（G2）：0.92→0.88，模拟中唯一把守卫撞限率 82%→100% 的杠杆
                pickupMult = 0.85f,
                areaMult = 1.20f,
                xpMult = 1f,
            };
        }
    }
}
