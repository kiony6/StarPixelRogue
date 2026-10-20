using System.Collections.Generic;
using UnityEngine;
using StarPixel.Combat.Skill;
using StarPixel.Player;

namespace StarPixel.Combat
{
    /// <summary>
    /// 升级目录（对应 TDD M2 升级选卡）：武器解锁 + 属性强化。
    /// 武器以运行时 ScriptableObject 实例定义，无需资产文件；
    /// 正式版可改为读取 Resources/Config 下的 .asset。
    /// </summary>
    public struct UpgradeOffer
    {
        public string title;
        public string desc;
        public System.Action apply;
        /// <summary>
        /// M4.5：供选类型编码，用于联机时把「选卡序号」的含义同步给客户端做本地镜像：
        /// 0..99 = 属性强化序号；100+武器id = 解锁；200+武器id = 等级强化。
        /// </summary>
        public int kind;
    }

    public static class UpgradeCatalog
    {
        // M33：本文件是纯数据定义。skillName 里保留中文是有意为之——它同时承担
        // 「开发期可读标识」和「locKey 缺失时的兜底」（见 SkillDef.DisplayName），
        // 玩家看到的武器名一律走 locKey。这里整段豁免静态检查的中文文案项。
        // @zhonly-begin: 开发期可读标识，界面显示走 locKey（SkillDef.DisplayName 已保证）
        // —— 武器定义（id=1 为默认持有）。数值经 balance_sim.py 蒙特卡洛验证（M3.6）——
        public static SkillDef CreateBolt()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 1; s.skillName = "星能弹"; s.locKey = "weapon.name.1"; s.weaponType = WeaponType.Projectile;
            s.tags = new[] { "fire" };
            s.cooldown = 0.35f; s.damage = 18f; s.projectileSpeed = 9f; s.pierce = 0; s.range = 7f;
            return s;
        }

        public static SkillDef CreateScatter()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 2; s.skillName = "散射枪"; s.locKey = "weapon.name.2"; s.weaponType = WeaponType.Scatter;
            s.tags = new[] { "storm" };
            s.cooldown = 0.55f; s.damage = 10f; s.projectileCount = 3; s.projectileSpeed = 9f; s.range = 7f;
            return s;
        }

        public static SkillDef CreateNova()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 3; s.skillName = "寒霜新星"; s.locKey = "weapon.name.3"; s.weaponType = WeaponType.Nova;
            s.tags = new[] { "ice" };
            s.cooldown = 1.8f; s.damage = 22f; s.aoeRadius = 3f;
            return s;
        }

        public static SkillDef CreateQuake()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 4; s.skillName = "裂地波"; s.locKey = "weapon.name.4"; s.weaponType = WeaponType.Nova;
            s.tags = new[] { "earth" };
            s.cooldown = 2.6f; s.damage = 32f; s.aoeRadius = 4.5f;
            return s;
        }

        public static SkillDef CreateOrbit()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 5; s.skillName = "雷环刃"; s.locKey = "weapon.name.5"; s.weaponType = WeaponType.Orbit;
            s.tags = new[] { "lightning" };
            s.cooldown = 0f; s.damage = 12f; s.orbitRadius = 2f; s.orbitCount = 1;
            return s;
        }

        // —— M6.1 新武器族 ——

        public static SkillDef CreateChain()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 6; s.skillName = "雷霆之链"; s.locKey = "weapon.name.6"; s.weaponType = WeaponType.Chain;
            s.tags = new[] { "lightning" };
            s.cooldown = 1.6f; s.damage = 26f; s.range = 7f;
            s.chainCount = 3; s.chainRange = 4f; s.damageFalloff = 0.8f;
            return s;
        }

        public static SkillDef CreateBoomerang()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 7; s.skillName = "月刃"; s.locKey = "weapon.name.7"; s.weaponType = WeaponType.Boomerang;
            s.tags = new[] { "storm" };
            s.cooldown = 1.4f; s.damage = 20f; s.projectileCount = 1;
            s.projectileSpeed = 10f; s.range = 7f; s.boomerangRange = 6f;
            return s;
        }

        public static SkillDef CreateHoming()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 8; s.skillName = "蜂群导弹"; s.locKey = "weapon.name.8"; s.weaponType = WeaponType.Homing;
            s.tags = new[] { "storm" };
            s.cooldown = 1.8f; s.damage = 12f; s.projectileCount = 2;
            s.projectileSpeed = 7f; s.range = 9f; s.homingTurn = 8f;
            return s;
        }

        public static SkillDef CreateAura()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 9; s.skillName = "圣光领域"; s.locKey = "weapon.name.9"; s.weaponType = WeaponType.Aura;
            s.tags = new[] { "holy" };
            s.cooldown = 0f; s.damage = 10f; s.aoeRadius = 3.2f; s.auraInterval = 0.5f;
            return s;
        }

        // —— M9 第10把武器：脉冲地雷（区域封锁；earth 标签与寒霜新星构成 ice+earth 减速组合）——
        public static SkillDef CreateMine()
        {
            var s = ScriptableObject.CreateInstance<SkillDef>();
            s.id = 10; s.skillName = "脉冲地雷"; s.locKey = "weapon.name.10"; s.weaponType = WeaponType.Mine;
            s.tags = new[] { "earth" };
            s.cooldown = 2.0f; s.damage = 45f; s.projectileCount = 1;
            s.aoeRadius = 2.6f; s.mineArmTime = 0.35f; s.mineTrigger = 1.6f; s.mineLife = 8f;
            return s;
        }

        // @zhonly-end

        private static readonly int[] UnlockableWeaponIds = { 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        /// <summary>构建 count 个不重复的升级供选：武器解锁 + 武器等级强化 + 属性强化。</summary>
        public static List<UpgradeOffer> BuildOffers(int count, PlayerController p)
        {
            var pool = new List<UpgradeOffer>();

            // 可解锁武器（未持有的）
            foreach (int id in UnlockableWeaponIds)
            {
                if (p.HasSkill(id)) continue;
                var def = CreateById(id);
                pool.Add(new UpgradeOffer
                {
                    title = Core.Localization.T("upgrade.unlock", def.DisplayName),
                    desc = WeaponDesc(def),
                    apply = () => p.AddSkill(def),
                    kind = 100 + id
                });
            }

            // 已持有武器的等级强化（M3.6：加深 build，避免中后期无卡可选）
            if (p.Weapons != null)
            {
                foreach (var w in p.Weapons)
                {
                    if (w == null || w.def == null) continue;
                    if (w.level >= SkillDef.MaxLevel) continue;
                    int next = w.level + 1;
                    int id = w.def.id;
                    pool.Add(new UpgradeOffer
                    {
                        title = Core.Localization.T("upgrade.level", w.def.DisplayName, next),
                        desc = w.def.NextLevelDesc(next),
                        apply = () => p.LevelUpWeapon(id),
                        kind = 200 + id
                    });
                }
            }

            // 属性强化（可重复）
            for (int i = 0; i < StatOfferCount; i++)
                pool.Add(StatOffer(i, p));

            // 洗牌后取前 count 个
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp;
            }
            var result = new List<UpgradeOffer>();
            for (int i = 0; i < Mathf.Min(count, pool.Count); i++) result.Add(pool[i]);
            return result;
        }

        /// <summary>属性强化数量（与 <see cref="StatOffer"/> / <see cref="ApplyKind"/> 必须保持一致）。</summary>
        public const int StatOfferCount = 7;

        /// <summary>
        /// M38：本机**真正**应用过几次强化（ApplyKind / ApplyStat 全算，含闭包路径）。
        ///
        /// 为什么非要这个数：光看"确认了几次"抓不到先斩后奏——那种写法是在**确认之外**
        /// 又加了一次，确认计数照样对得上。只有把"实际应用的次数"和"确认的次数"分开统计，
        /// 多出来的那几次才无处可藏（判据：两者必须相等，多一次都是自己给自己发的）。
        /// </summary>
        public static int ApplyCount { get; private set; }

        /// <summary>
        /// 第 index 个属性强化供选（单一数据源：BuildOffers 与联机 ApplyKind 共用）。
        /// apply 闭包统一转调 <see cref="ApplyStat"/>，保证单机与联机客户端镜像效果一致。
        /// </summary>
        public static UpgradeOffer StatOffer(int index, PlayerController p)
        {
            switch (index)
            {
                case 0: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.0"), desc = Core.Localization.T("upgrade.stat.desc.0"), kind = 0, apply = () => ApplyStat(0, p) };
                case 1: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.1"), desc = "", kind = 1, apply = () => ApplyStat(1, p) };
                case 2: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.2"), desc = "", kind = 2, apply = () => ApplyStat(2, p) };
                case 3: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.3"), desc = "", kind = 3, apply = () => ApplyStat(3, p) };
                case 4: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.4"), desc = Core.Localization.T("upgrade.stat.desc.4"), kind = 4, apply = () => ApplyStat(4, p) };
                case 5: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.5"), desc = "", kind = 5, apply = () => ApplyStat(5, p) };
                case 6: return new UpgradeOffer { title = Core.Localization.T("upgrade.stat.6"), desc = "", kind = 6, apply = () => ApplyStat(6, p) };
                default: return default;
            }
        }

        /// <summary>
        /// 按类型编码应用强化（联机客户端镜像用；房主侧请直接用 offer.apply 闭包）。
        /// 客户端表现与预测均为本地非权威数据，权威结算以房主为准。
        /// </summary>
        public static void ApplyKind(int kind, PlayerController p)
        {
            if (p == null) return;
            ApplyCount++;                     // M38：真实应用次数（详见 ApplyCount 注释）
            if (kind >= 200) p.LevelUpWeapon(kind - 200);
            else if (kind >= 100) p.AddSkill(CreateById(kind - 100));
            else ApplyStatCore(kind, p);      // 走内核，避免同一笔被计两次
        }

        /// <summary>
        /// M39：按 kind **现算**这张卡的本地化标题（给收场提示用）。
        ///
        /// 为什么不让房主把 title 一起发过来：房主生成的 title 是**房主的语言**。
        /// 在"房主中文 + 客户端英文"的房间里直接传，玩家会看到一条中文提示——
        /// 房间里的两个人语言不同是很常见的。所以回执只带 kind，标题按**客户端自己**的语言现算。
        ///
        /// 纯逻辑（不依赖网络），探针可直接断言三档分流都对。
        /// </summary>
        public static string TitleOfKind(int kind, PlayerController p = null)
        {
            if (kind >= 200)
            {
                int id = kind - 200;
                // 拿得到玩家手上的这把武器 → 与 BuildOffers 完全一致的写法（含下一级）
                if (p != null && p.Weapons != null)
                {
                    foreach (var w in p.Weapons)
                    {
                        if (w == null || w.def == null || w.def.id != id) continue;
                        int next = Mathf.Min(w.level + 1, SkillDef.MaxLevel);
                        return Core.Localization.T("upgrade.level", w.def.DisplayName, next);
                    }
                }
                var d = CreateById(id);
                if (d != null) return Core.Localization.T("upgrade.unlock", d.DisplayName);
                return Core.Localization.T("upgrade.generic");
            }
            if (kind >= 100)
            {
                var def = CreateById(kind - 100);
                if (def != null) return Core.Localization.T("upgrade.unlock", def.DisplayName);
                return Core.Localization.T("upgrade.generic");
            }
            if (kind >= 0 && kind < StatOfferCount)
                return Core.Localization.T("upgrade.stat." + kind);
            return Core.Localization.T("upgrade.generic");
        }

        /// <summary>按序号应用属性强化（与 <see cref="StatOffer"/> 一一对应）。</summary>
        public static void ApplyStat(int index, PlayerController p)
        {
            ApplyCount++;                     // M38：闭包路径（房主 offer.apply）也要计进去
            ApplyStatCore(index, p);
        }

        /// <summary>属性强化的实际落地（两条公开入口共用，保证 ApplyCount 一笔只加一次）。</summary>
        private static void ApplyStatCore(int index, PlayerController p)
        {
            switch (index)
            {
                case 0: p.UpgradeMaxHp(20); break;
                case 1: p.stats.moveSpeed *= 1.08f; break;
                case 2: p.stats.attackDamageMult *= 1.15f; break;
                case 3: p.stats.cooldownMult *= 0.88f; break;
                case 4: p.AddProjectileToAll(); break;
                case 5: p.stats.pickupRadius *= 1.3f; break;
                case 6: p.stats.areaMult *= 1.15f; break;
            }
        }

        /// <summary>全部武器 id（图鉴/调试用；与 <see cref="CreateById"/> 一一对应）。</summary>
        public static readonly int[] AllWeaponIds = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        /// <summary>按武器 id 创建定义（联机客户端重建本地武器表现用）。</summary>
        public static SkillDef CreateById(int id)
        {
            switch (id)
            {
                case 2: return CreateScatter();
                case 3: return CreateNova();
                case 4: return CreateQuake();
                case 5: return CreateOrbit();
                case 6: return CreateChain();
                case 7: return CreateBoomerang();
                case 8: return CreateHoming();
                case 9: return CreateAura();
                case 10: return CreateMine();
                default: return CreateBolt();
            }
        }

        /// <summary>武器类型的一句话描述（图鉴/选卡共用）。</summary>
        public static string WeaponDesc(SkillDef d)
        {
            switch (d.weaponType)
            {
                case WeaponType.Scatter: return Core.Localization.T("weapon.desc.scatter");
                case WeaponType.Nova: return Core.Localization.T("weapon.desc.nova");
                case WeaponType.Orbit: return Core.Localization.T("weapon.desc.orbit");
                case WeaponType.Chain: return Core.Localization.T("weapon.desc.chain");
                case WeaponType.Boomerang: return Core.Localization.T("weapon.desc.boomerang");
                case WeaponType.Homing: return Core.Localization.T("weapon.desc.homing");
                case WeaponType.Aura: return Core.Localization.T("weapon.desc.aura");
                case WeaponType.Mine: return Core.Localization.T("weapon.desc.mine");
                default: return Core.Localization.T("weapon.desc.projectile");
            }
        }
    }
}
