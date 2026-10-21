using UnityEngine;
using StarPixel.Combat;
using StarPixel.Core;
using StarPixel.Enemy;

namespace StarPixel.Combat.Skill
{
    /// <summary>
    /// 追踪弹（M6.1「蜂群导弹」）：周期性重新锁定最近敌人，按转向速率平滑修正航向。
    /// 索敌走 <see cref="CombatTargets"/>，因此单机与联机（房主权威）行为一致。
    /// </summary>
    public class HomingMissile : MonoBehaviour
    {
        public float damage;
        public float speed;
        public float turnRate = 8f;      // 每秒最大转向弧度
        public float lifeTime = 3f;
        public bool applySlow;
        public float slowFactor = 0.5f;

        private Vector2 _dir = Vector2.right;
        private Transform _target;
        private float _retargetTimer;
        private float _elapsed;
        private GameObject _template;
        private float _hitRadius = 0.35f;

        // M53：尾焰。追踪弹会频繁改向，喷口那一串火星正是"它在追"的证据；
        // 没有尾迹时它看着像一枚会拐弯的方块，读不出意图。
        private float _trailTimer;
        private float _sparkTimer;
        private const float TrailInterval = 0.03f;
        private const float SparkInterval = 0.07f;

        public void Launch(Vector2 origin, Vector2 dir, float dmg, float spd, float turn, GameObject template)
        {
            _template = template;
            transform.position = origin;
            _dir = dir.normalized;
            damage = dmg;
            speed = spd;
            turnRate = Mathf.Max(1f, turn);
            _target = null;
            _retargetTimer = 0f;
            _elapsed = 0f;
            applySlow = false;
            _trailTimer = 0f;
            _sparkTimer = 0f;
            gameObject.SetActive(true);
        }

        void Update()
        {
            _elapsed += Time.deltaTime;
            if (_elapsed >= lifeTime) { Die(); return; }

            // 周期性重新锁定（避免每帧全场索敌的性能浪费）
            _retargetTimer -= Time.deltaTime;
            if (_retargetTimer <= 0f)
            {
                _retargetTimer = 0.15f;
                _target = CombatTargets.Nearest(transform.position, 12f);
            }

            if (_target != null)
            {
                Vector2 toTarget = ((Vector2)_target.position - (Vector2)transform.position);
                if (toTarget.sqrMagnitude > 0.0001f)
                {
                    Vector2 want = toTarget.normalized;
                    float maxRad = turnRate * Time.deltaTime;
                    // 限制单帧转向角，实现平滑追踪而非瞬转
                    float angle = Vector2.SignedAngle(_dir, want) * Mathf.Deg2Rad;
                    float clamped = Mathf.Clamp(angle, -maxRad, maxRad);
                    _dir = Rotate(_dir, clamped).normalized;
                }
            }

            transform.position = (Vector2)transform.position + _dir * speed * Time.deltaTime;
            float zAngle = Mathf.Atan2(_dir.y, _dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, zAngle);

            // 尾焰 + 零星火星：两者用不同间隔，彼此错开，
            // 免得两条序列同步闪动、暴露出它们是由同一个计时器驱动的
            _trailTimer -= Time.deltaTime;
            if (_trailTimer <= 0f)
            {
                _trailTimer = TrailInterval;
                StarPixel.Effects.Fx.Trail(transform.position, new Color(1f, 0.62f, 0.22f), 0.42f, 0.26f, 3);
            }
            _sparkTimer -= Time.deltaTime;
            if (_sparkTimer <= 0f)
            {
                _sparkTimer = SparkInterval;
                StarPixel.Effects.Fx.Spray(transform.position, -_dir, new Color(1f, 0.78f, 0.35f),
                    1, 2.4f, 0.22f, 0.24f, 0.5f, 3);
            }

            TryHit();
        }

        private void TryHit()
        {
            var enemies = EnemyManager.Instance?.ActiveEnemies;
            if (enemies != null)
            {
                for (int i = enemies.Count - 1; i >= 0; i--)
                {
                    var e = enemies[i];
                    if (e == null || e.damageable.isDead) continue;
                    if (Vector2.Distance(transform.position, e.transform.position) <= _hitRadius + 0.4f)
                    {
                        e.damageable.TakeDamage(damage);
                        if (applySlow) e.ApplySlow(slowFactor, 1.5f);
                        Die();
                        return;
                    }
                }
            }

            var boss = EnemyManager.Instance?.ActiveBoss;
            if (boss != null && !boss.damageable.isDead &&
                Vector2.Distance(transform.position, boss.transform.position) <= _hitRadius + 1.0f)
            {
                boss.damageable.TakeDamage(damage);
                Die();
            }
        }

        private static Vector2 Rotate(Vector2 v, float rad)
        {
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        void Die()
        {
            StarPixel.Effects.Fx.Flash(transform.position, new Color(1f, 0.7f, 0.3f), 0.6f, 0.16f, 3);
            StarPixel.Effects.Fx.Burst(transform.position, new Color(1f, 0.55f, 0.25f), 3, 3.2f, 0.22f, 0.22f, 3);
            if (_template != null) PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
