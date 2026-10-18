using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Combat
{
    /// <summary>
    /// 组合技管理器（对应 TDD 4.1 组合技 / 差异化核心）。
    /// 当玩家同时持有某两个流派 tag 的武器时，激活对应加成。
    /// M4：加成计算改为纯函数 <see cref="Compute"/>——联机时房主持有多个玩家控制器，
    /// 全局单例状态会互相覆盖，纯函数保证每个玩家按自己的武器池结算（单机行为完全不变）。
    /// </summary>
    public class ComboManager : Core.MonoSingleton<ComboManager>
    {
        /// <summary>组合技加成（值类型，按玩家武器池即时计算）。</summary>
        public struct Bonus
        {
            public float damageMult;
            public int pierce;
            public bool slowOnHit;
        }

        /// <summary>按引用修改组合加成的委托（C# lambda 不支持 ref 参数，必须用匿名方法）。</summary>
        private delegate void BonusApply(ref Bonus b);

        private struct ComboRule
        {
            public string tagA;
            public string tagB;
            public BonusApply apply;
        }

        // 组合定义：需要同时拥有的两个 tag
        private static readonly ComboRule[] Combos =
        {
            new ComboRule { tagA = "fire", tagB = "storm", apply = delegate (ref Bonus b) { b.damageMult = 1.5f; } },
            new ComboRule { tagA = "ice",  tagB = "earth", apply = delegate (ref Bonus b) { b.slowOnHit = true; } },
            new ComboRule { tagA = "lightning", tagB = "fire", apply = delegate (ref Bonus b) { b.pierce = 2; } },
            // M6.1：圣光领域（holy）与火焰流派共鸣
            new ComboRule { tagA = "holy", tagB = "fire", apply = delegate (ref Bonus b) { b.damageMult = 1.4f; } },
        };

        /// <summary>按武器 tag 列表计算组合加成（纯函数，无状态，可安全用于多玩家）。</summary>
        public static Bonus Compute(List<string> ownedTags)
        {
            var b = new Bonus { damageMult = 1f, pierce = 0, slowOnHit = false };
            if (ownedTags == null || ownedTags.Count == 0) return b;
            var set = new HashSet<string>(ownedTags);
            foreach (var rule in Combos)
            {
                if (set.Contains(rule.tagA) && set.Contains(rule.tagB))
                    rule.apply(ref b);
            }
            return b;
        }

        /// <summary>M6.4：当前激活的组合键列表（图鉴发现记录用，如 "fire_storm"）。</summary>
        public static List<string> ActiveComboKeys(List<string> ownedTags)
        {
            var keys = new List<string>();
            if (ownedTags == null) return keys;
            var set = new HashSet<string>(ownedTags);
            foreach (var rule in Combos)
            {
                if (set.Contains(rule.tagA) && set.Contains(rule.tagB))
                    keys.Add(rule.tagA + "_" + rule.tagB);
            }
            return keys;
        }

        /// <summary>兼容入口：刷新单例上的当前加成（单机 HUD/调试用）。</summary>
        public void Recompute(List<string> ownedTags)
        {
            var b = Compute(ownedTags);
            bonusDamageMult = b.damageMult;
            bonusPierce = b.pierce;
            slowOnHit = b.slowOnHit;

            // M6.4：激活的组合技记入图鉴（本地存档，仅枚举键）
            foreach (var key in ActiveComboKeys(ownedTags))
                Core.SaveManager.Instance?.DiscoverCombo(key);
        }

        // 当前激活的加成（单机调试用；联机开火请用 Compute）
        public float bonusDamageMult = 1f;
        public int bonusPierce = 0;
        public bool slowOnHit = false;
    }
}
