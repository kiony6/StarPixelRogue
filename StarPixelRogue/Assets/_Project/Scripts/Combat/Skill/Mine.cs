using UnityEngine;
using StarPixel.Combat;
using StarPixel.Core;
using StarPixel.Enemy;

namespace StarPixel.Combat.Skill
{
    /// <summary>
    /// 脉冲地雷（M9「脉冲地雷」）：埋设于玩家当前位置，激活延迟后进入待引爆状态；
    /// 任一敌人进入触发半径即引爆，造成范围伤害；超过最长存活时间也会自动引爆。
    /// 区域封锁型武器——与现有全部「即时/持续」武器形成差异（主动布雷、延迟清场）。
    /// 由对象池管理；伤害仅权威侧结算（联机下房主权威，见 PlayerController.FireMine）。
    /// </summary>
    public class Mine : MonoBehaviour
    {
        public float damage;
        public float radius = 2.6f;
        public float armTime = 0.35f;
        public float triggerRadius = 1.6f;
        public float life = 8f;
        public bool applySlow;
        public float slowFactor = 0.5f;

        private float _elapsed;
        private bool _armed;
        private bool _exploded;
        private GameObject _template;
        private SpriteRenderer _sr;
        private float _chargeTimer;      // M53：待引爆状态的脉冲/蓄力采样节奏

        public void Launch(Vector2 origin, float dmg, float radius, float arm, float trigger, float life, GameObject template)
        {
            _template = template;
            transform.position = origin;
            damage = dmg;
            this.radius = Mathf.Max(0.5f, radius);
            armTime = Mathf.Max(0.05f, arm);
            triggerRadius = Mathf.Max(0.3f, trigger);
            this.life = Mathf.Max(0.5f, life);
            _elapsed = 0f;
            _armed = false;
            _exploded = false;
            _chargeTimer = 0f;
            applySlow = false;
            gameObject.SetActive(true);

            _sr = GetComponent<SpriteRenderer>();
            if (_sr != null)
            {
                _sr.color = new Color(1f, 0.4f, 0.4f, 0.5f);    // 未激活：暗红
                transform.localScale = Vector3.one * 0.5f;
            }
            transform.rotation = Quaternion.identity;
        }

        void Update()
        {
            if (_exploded) return;
            _elapsed += Time.deltaTime;

            if (!_armed)
            {
                if (_elapsed >= armTime)
                {
                    _armed = true;
                    if (_sr != null) _sr.color = new Color(1f, 0.85f, 0.2f, 0.9f);  // 激活：亮黄
                }
                return;
            }

            // 视觉脉冲（激活后轻微呼吸，提示已布防）
            if (_sr != null)
            {
                float pulse = 1f + Mathf.Sin(Time.time * 8f) * 0.12f;
                transform.localScale = Vector3.one * 0.5f * pulse;
            }

            // M53：每隔一小段就在脚底垫一圈微光——"这颗雷醒着"。
            // 与上面的呼吸错位分布比起只用缩放更易察觉，因为它覆盖了地雷周围那片地面。
            _chargeTimer -= Time.deltaTime;
            if (_chargeTimer <= 0f)
            {
                _chargeTimer = 0.32f;
                StarPixel.Effects.Fx.Glow(transform.position, new Color(1f, 0.72f, 0.25f), 1.1f, 0.3f, 1);
            }

            // 超时自动引爆
            if (_elapsed >= life) { Explode(); return; }

            // 触发半径内有敌人即引爆
            bool near = false;
            CombatTargets.ForEachWithin(transform.position, triggerRadius, d =>
            {
                if (d != null && !d.isDead) near = true;
            });
            if (near) Explode();
        }

        private void Explode()
        {
            if (_exploded) return;
            _exploded = true;

            // 范围伤害（ForEachWithin 同时覆盖敌人与 Boss）
            CombatTargets.ForEachWithin(transform.position, radius, d =>
            {
                if (d == null || d.isDead) return;
                d.TakeDamage(damage);
                // M17 实测修复：ApplySlow 定义在 Enemy 上而非 Damageable，
                // 而 ForEachWithin 回调给的是 Damageable——需取 Enemy 组件；Boss 没有该组件，安全跳过。
                if (applySlow)
                {
                    var en = d.GetComponent<StarPixel.Enemy.Enemy>();
                    if (en != null) en.ApplySlow(slowFactor, 1.5f);
                }
            });

            // M53：爆炸由三层组成——外推的冲击环、中心的白热闪光、四散的火星。
            // 此前只有一个实心方块缩放，扩到一半就变成一块雾，"炸"的力度全丢在里头。
            var hot = new Color(1f, 0.72f, 0.28f);
            var ember = new Color(1f, 0.45f, 0.15f);
            float waveR = radius * 1.15f;
            StarPixel.Effects.Fx.Shockwave(transform.position, radius * 0.35f, waveR, hot, 0.32f, 4);
            StarPixel.Effects.Fx.Shockwave(transform.position, radius * 0.2f, waveR * 0.7f, Color.white, 0.22f, 5);
            StarPixel.Effects.Fx.Flash(transform.position, new Color(1f, 0.9f, 0.6f), radius * 1.4f, 0.22f, 5);
            StarPixel.Effects.Fx.Burst(transform.position, ember, 12, waveR * 2.2f, 0.3f, 0.4f, 5);
            StarPixel.Effects.Fx.Glow(transform.position, hot, waveR * 1.5f, 0.3f, 1);
            Core.EventBus.RaiseShake(0.18f, 0.15f);   // M8 手感：轻微震屏反馈
            Die();
        }

        void Die()
        {
            if (_template != null) PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
