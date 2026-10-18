#if FISHNET_ENABLED
using FishNet.Object;
using StarPixel.Combat;
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 网络弹丸（联机模式，对应 TDD 4.2 弹道系统）。
    /// 位置由 NetworkTransform 同步；飞行与命中判定只在房主运行：
    /// - 玩家弹（hostile=false）：命中 <see cref="NetEnemy"/>；
    /// - 敌方弹（hostile=true，Boss 径向弹幕）：命中 <see cref="NetPlayer"/>。
    ///
    /// M6.1：新增弹道行为（behavior 参数，房主权威飞行，客户端仅同步位置）：
    /// - 0 = 直射（默认，与单机 Projectile 一致）；
    /// - 1 = 回旋镖（飞出最远距离后返回，来回各对同一敌人伤一次）；
    /// - 2 = 追踪弹（周期性重锁最近敌人，按转向速率平滑修正）。
    /// </summary>
    public class NetProjectile : NetworkBehaviour
    {
        private Vector2 _dir = Vector2.right;
        private float _damage;
        private float _speed;
        private float _pierce;
        private bool _applySlow;
        private bool _hostile;
        private float _life = 2.5f;
        private float _hitRadius = 0.75f;   // 与单机 Projectile 判定一致（0.35 + 0.40）

        // —— M6.1 弹道行为状态 ——
        private int _behavior;              // 0 直射 / 1 回旋镖 / 2 追踪
        private float _range;               // 回旋镖最远距离
        private Vector2 _origin;
        private float _traveled;
        private bool _returning;
        private float _retargetTimer;
        private Transform _target;
        private readonly System.Collections.Generic.HashSet<int> _hitThisPass =
            new System.Collections.Generic.HashSet<int>();

        public void ServerInit(Vector2 dir, float dmg, float speed, float pierce, bool applySlow, bool hostile,
            int behavior = 0, float range = 0f)
        {
            _dir = dir.normalized;
            _damage = dmg;
            _speed = speed;
            _pierce = pierce;
            _applySlow = applySlow;
            _hostile = hostile;
            _behavior = Mathf.Clamp(behavior, 0, 2);
            _range = Mathf.Max(0.5f, range);
            _origin = transform.position;
            _traveled = 0f;
            _returning = false;
            _retargetTimer = 0f;
            _target = null;
            _hitThisPass.Clear();
            _life = _behavior == 1 ? _range / Mathf.Max(0.1f, speed) * 2f + 1.5f : 2.5f;
        }

        void Update()
        {
            if (!IsServerInitialized) return;   // 客户端只跟随 NetworkTransform 的位置同步

            _life -= Time.deltaTime;
            if (_life <= 0f) { NetSpawner.Despawn(gameObject); return; }

            switch (_behavior)
            {
                case 1: MoveBoomerang(); break;
                case 2: MoveHoming(); break;
                default:
                    transform.Translate(_dir * _speed * Time.deltaTime, Space.World);
                    break;
            }

            if (_hostile) HitPlayers();
            else HitEnemies();
        }

        // —— 行为 1：回旋镖（去程/回程各命中一次）——
        private void MoveBoomerang()
        {
            Vector2 pos = transform.position;
            float step = _speed * Time.deltaTime;

            if (!_returning)
            {
                pos += _dir * step;
                _traveled += step;
                if (_traveled >= _range)
                {
                    _returning = true;
                    _hitThisPass.Clear();     // 折返：重置本趟命中记录
                }
            }
            else
            {
                Vector2 toOrigin = _origin - pos;
                float remain = toOrigin.magnitude;
                if (remain <= step)
                {
                    NetSpawner.Despawn(gameObject);
                    return;
                }
                pos += toOrigin.normalized * step;
            }

            transform.position = pos;
            transform.Rotate(0f, 0f, 720f * Time.deltaTime);
        }

        // —— 行为 2：追踪弹 ——
        private void MoveHoming()
        {
            _retargetTimer -= Time.deltaTime;
            if (_retargetTimer <= 0f)
            {
                _retargetTimer = 0.15f;
                _target = NetEnemyRegistry.Nearest(transform.position, 12f)?.transform;
            }

            if (_target != null)
            {
                Vector2 toTarget = (Vector2)_target.position - (Vector2)transform.position;
                if (toTarget.sqrMagnitude > 0.0001f)
                {
                    Vector2 want = toTarget.normalized;
                    float maxRad = 8f * Time.deltaTime;
                    float angle = Vector2.SignedAngle(_dir, want) * Mathf.Deg2Rad;
                    _dir = Rotate(_dir, Mathf.Clamp(angle, -maxRad, maxRad)).normalized;
                }
            }

            transform.position = (Vector2)transform.position + _dir * _speed * Time.deltaTime;
            transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(_dir.y, _dir.x) * Mathf.Rad2Deg);
        }

        private static Vector2 Rotate(Vector2 v, float rad)
        {
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private void HitPlayers()
        {
            bool consumed = false;
            NetPlayerRegistry.ForEachAlive(p =>
            {
                if (consumed) return;
                if (Vector2.Distance(transform.position, p.transform.position) <= _hitRadius)
                {
                    p.Damageable.TakeDamage(_damage);
                    p.ServerRecordHit(_damage);
                    consumed = true;
                }
            });
            if (consumed) NetSpawner.Despawn(gameObject);
        }

        private void HitEnemies()
        {
            if (_behavior == 1)
            {
                HitEnemiesBoomerang();
                return;
            }

            var hit = NetEnemyRegistry.Nearest(transform.position, _hitRadius);
            if (hit == null) return;
            hit.ServerTakeDamage(_damage, _applySlow);
            if (_pierce > 0f) _pierce -= 1f;
            else NetSpawner.Despawn(gameObject);
        }

        /// <summary>回旋镖命中：同趟内每个敌人只受一次伤害（实例 id 去重）。</summary>
        private void HitEnemiesBoomerang()
        {
            var hit = NetEnemyRegistry.Nearest(transform.position, _hitRadius);
            if (hit == null) return;
            int id = hit.Damageable != null ? hit.Damageable.GetInstanceID() : hit.GetInstanceID();
            if (_hitThisPass.Contains(id)) return;
            _hitThisPass.Add(id);
            hit.ServerTakeDamage(_damage, _applySlow);
        }
    }
}
#endif
