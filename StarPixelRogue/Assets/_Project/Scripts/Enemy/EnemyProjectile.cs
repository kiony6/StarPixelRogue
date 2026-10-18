using UnityEngine;
using StarPixel.Combat;

namespace StarPixel.Enemy
{
    /// <summary>
    /// 敌方弹幕（Boss 技能）：直线飞行，命中玩家造成伤害，由对象池复用。
    /// </summary>
    public class EnemyProjectile : MonoBehaviour
    {
        private Vector2 _dir;
        private float _damage;
        private float _speed;
        private float _life;
        private Damageable _target;
        private GameObject _template;

        public void Launch(Vector2 origin, Vector2 dir, float damage, float speed, Damageable target, GameObject template)
        {
            transform.position = origin;
            _dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.down;
            _damage = damage;
            _speed = speed;
            _life = 5f;
            _target = target;
            _template = template;
            gameObject.SetActive(true);
        }

        void Update()
        {
            _life -= Time.deltaTime;
            transform.Translate(_dir * _speed * Time.deltaTime, Space.World);

            if (_life <= 0f) { Die(); return; }
            if (_target == null || _target.isDead) { if (_life < 4.5f) Die(); return; }

            if (Vector2.Distance(transform.position, _target.transform.position) < 0.45f)
            {
                _target.TakeDamage(_damage);
                Die();
            }
        }

        private void Die()
        {
            if (_template != null) Core.PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
