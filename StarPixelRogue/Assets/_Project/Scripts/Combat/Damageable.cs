using UnityEngine;

namespace StarPixel.Combat
{
    /// <summary>
    /// 可受伤实体基类，敌人/玩家共用（对应 TDD 4.1 统一伤害管线）。
    /// </summary>
    public class Damageable : MonoBehaviour
    {
        public float maxHp;
        public float currentHp;
        public bool isDead;
        public bool isInitialized;
        public bool showNumbers = true; // 是否显示跳字（玩家设为 false）

        public System.Action<float> OnHpChanged; // 血量比例 0..1
        public System.Action OnDeath;

        public void Init(float hp)
        {
            maxHp = hp;
            currentHp = hp;
            isDead = false;
            isInitialized = true;
        }

        public void TakeDamage(float amount)
        {
            if (isDead) return;
            currentHp -= amount;
            OnHpChanged?.Invoke(Mathf.Clamp01(currentHp / maxHp));
            if (showNumbers) Core.EventBus.RaiseDamaged(amount, transform.position);
            if (currentHp <= 0f)
            {
                isDead = true;
                OnDeath?.Invoke();
            }
        }

        /// <summary>M11 断线重连：按比例直接设置血量（不触发跳字），并使死亡状态复位以便重连后继续作战。</summary>
        public void SetHpRatio(float ratio)
        {
            if (maxHp <= 0f) return;
            currentHp = Mathf.Clamp01(ratio) * maxHp;
            if (currentHp > 0f) isDead = false;
            OnHpChanged?.Invoke(Mathf.Clamp01(currentHp / maxHp));
        }
    }
}
