using System.Collections.Generic;
using UnityEngine;
using StarPixel.Core;
using StarPixel.Enemy;

namespace StarPixel.Combat.Skill
{
    /// <summary>
    /// 回旋镖（M6.1「月刃」）：从玩家飞出至最远距离后原路返回，来回各对同一敌人造成一次伤害。
    /// 由对象池管理；命中判定与 <see cref="Projectile"/> 一致（手动圆形重叠）。
    /// </summary>
    public class Boomerang : MonoBehaviour
    {
        public float damage;
        public float speed;
        public float maxRange = 6f;
        public bool applySlow;
        public float slowFactor = 0.5f;

        private Vector2 _origin;
        private Vector2 _dir;
        private float _traveled;
        private bool _returning;
        private GameObject _template;
        private float _hitRadius = 0.4f;
        private float _life = 4f;

        // 每趟（去/回）已命中的敌人实例 id：保证同一敌人单趟只受一次伤害
        private readonly HashSet<int> _hitThisPass = new HashSet<int>();

        public void Launch(Vector2 origin, Vector2 dir, float dmg, float spd, float range, GameObject template)
        {
            _template = template;
            _origin = origin;
            _dir = dir.normalized;
            damage = dmg;
            speed = spd;
            maxRange = Mathf.Max(1f, range);
            _traveled = 0f;
            _returning = false;
            _life = 4f;
            applySlow = false;
            _hitThisPass.Clear();
            transform.position = origin;
            gameObject.SetActive(true);
        }

        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Die(); return; }

            Vector2 pos = (Vector2)transform.position;
            float step = speed * Time.deltaTime;

            if (!_returning)
            {
                pos += _dir * step;
                _traveled += step;
                if (_traveled >= maxRange)
                {
                    // 到达最远点：折返，并重置本趟命中记录（回程可再次命中）
                    _returning = true;
                    _hitThisPass.Clear();
                }
            }
            else
            {
                Vector2 toOrigin = _origin - pos;
                float remain = toOrigin.magnitude;
                if (remain <= step)
                {
                    pos = _origin;
                    transform.position = pos;
                    Die();
                    return;
                }
                pos += toOrigin.normalized * step;
            }

            transform.position = pos;
            transform.Rotate(0f, 0f, 720f * Time.deltaTime);   // 自旋表现

            TryHit(pos);
        }

        private void TryHit(Vector2 pos)
        {
            var enemies = EnemyManager.Instance?.ActiveEnemies;
            if (enemies != null)
            {
                // 从后向前遍历：命中后敌人可能入池（从列表移除）
                for (int i = enemies.Count - 1; i >= 0; i--)
                {
                    var e = enemies[i];
                    if (e == null || e.damageable.isDead) continue;
                    if (Vector2.Distance(pos, e.transform.position) > _hitRadius + 0.4f) continue;
                    int id = e.damageable.GetInstanceID();
                    if (_hitThisPass.Contains(id)) continue;
                    _hitThisPass.Add(id);
                    e.damageable.TakeDamage(damage);
                    if (applySlow) e.ApplySlow(slowFactor, 1.5f);
                }
            }

            var boss = EnemyManager.Instance?.ActiveBoss;
            if (boss != null && !boss.damageable.isDead &&
                Vector2.Distance(pos, boss.transform.position) <= _hitRadius + 1.0f)
            {
                int id = boss.damageable.GetInstanceID();
                if (!_hitThisPass.Contains(id))
                {
                    _hitThisPass.Add(id);
                    boss.damageable.TakeDamage(damage);
                }
            }
        }

        void Die()
        {
            if (_template != null) PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
