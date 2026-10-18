using UnityEngine;
using StarPixel.Enemy;

namespace StarPixel.Combat.Skill
{
    /// <summary>
    /// 弹道实体，由对象池管理（对应 TDD 4.1 / 4.4）。
    /// 命中检测采用手动圆形重叠，原型规模足够；后续可换 Physics2D。
    /// </summary>
    public class Projectile : MonoBehaviour
    {
        public float damage;
        public float speed;
        public float pierce;
        public float lifeTime = 2f;
        public bool applySlow;          // 组合技：命中施加减速
        public float slowFactor = 0.5f;

        private float _elapsed;
        private GameObject _template;
        private float _hitRadius = 0.35f;

        public void Launch(Vector2 origin, Vector2 dir, float dmg, float spd, float pierceCount, GameObject template)
        {
            _template = template;
            transform.position = origin;
            damage = dmg;
            speed = spd;
            pierce = pierceCount;
            applySlow = false;
            _elapsed = 0f;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0, 0, angle);
            gameObject.SetActive(true);
        }

        void Update()
        {
            _elapsed += Time.deltaTime;
            transform.Translate(Vector2.right * speed * Time.deltaTime, Space.Self);

            if (_elapsed >= lifeTime) { Die(); return; }

            var enemies = EnemyManager.Instance?.ActiveEnemies;
            if (enemies == null) return;

            // 从后向前遍历，命中后敌人可能入池（从列表移除），向下遍历安全
            for (int i = enemies.Count - 1; i >= 0; i--)
            {
                var e = enemies[i];
                if (e == null || e.damageable.isDead) continue;
                if (Vector2.Distance(transform.position, e.transform.position) <= _hitRadius + 0.4f)
                {
                    e.damageable.TakeDamage(damage);
                    if (applySlow) e.ApplySlow(slowFactor, 1.5f);
                    if (pierce > 0f) pierce -= 1f;
                    else { Die(); return; }
                }
            }

            // Boss 体积更大，单独判定
            var boss = EnemyManager.Instance?.ActiveBoss;
            if (boss != null && !boss.damageable.isDead &&
                Vector2.Distance(transform.position, boss.transform.position) <= _hitRadius + 1.0f)
            {
                boss.damageable.TakeDamage(damage);
                if (pierce > 0f) pierce -= 1f;
                else { Die(); return; }
            }
        }

        void Die()
        {
            if (_template != null) Core.PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
