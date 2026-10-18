using UnityEngine;

namespace StarPixel.Combat
{
    /// <summary>
    /// 统一伤害结算管线（对应 TDD 4.1: CombatManager.CalcDamage）。
    /// 原型阶段为线性公式，后续接入 Buff / 词条 / 暴击。
    /// </summary>
    public class CombatManager : Core.MonoSingleton<CombatManager>
    {
        public float CalcDamage(float baseDamage, float attackerMult)
        {
            return baseDamage * attackerMult;
        }
    }
}
