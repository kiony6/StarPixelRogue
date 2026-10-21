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

        // M53：拖尾。弹丸的速度是全项目最快的（14 单位/秒 ≈ 每帧四分之一屏），
        // 没有拖尾时它在玩家眼里只是"每隔几帧换个位置"，读不出飞向哪儿。
        private float _trailTimer;
        private SpriteRenderer _sr;
        private const float TrailInterval = 0.035f;

        public void Launch(Vector2 origin, Vector2 dir, float dmg, float spd, float pierceCount, GameObject template)
        {
            _template = template;
            transform.position = origin;
            damage = dmg;
            speed = spd;
            pierce = pierceCount;
            applySlow = false;
            _elapsed = 0f;
            _trailTimer = 0f;
            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0, 0, angle);
            gameObject.SetActive(true);
        }

        void Update()
        {
            _elapsed += Time.deltaTime;
            transform.Translate(Vector2.right * speed * Time.deltaTime, Space.Self);

            // 拖尾按时长而不是按帧：帧率一高就每帧留一颗，等于把上限直接吃满，
            // 而玩家看到的密度是一样的（视觉由留存时间决定，不由帧数决定）。
            _trailTimer -= Time.deltaTime;
            if (_trailTimer <= 0f)
            {
                _trailTimer = TrailInterval;
                if (_sr == null) _sr = GetComponent<SpriteRenderer>();
                StarPixel.Effects.Fx.Trail(transform.position,
                    _sr != null ? _sr.color : Color.yellow, 0.55f, 0.22f, 3);
            }

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
            // M53：出膛要有交代。此前弹丸飞到生命尽头就凭空消失，
            // 玩家分不清"打空了"还是"打到人了"；留一小团余晖表示该做的事做完了。
            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            StarPixel.Effects.Fx.Flash(transform.position,
                _sr != null ? _sr.color : Color.yellow, 0.5f, 0.14f, 3);
            if (_template != null) Core.PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
