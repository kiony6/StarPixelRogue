using System.Collections.Generic;
using UnityEngine;
using StarPixel.Player;
using StarPixel.Net;

namespace StarPixel.Pickups
{
    /// <summary>
    /// 拾取管理器：经验宝石与回血球的掉落、磁吸与拾取（对应 TDD M2 升级循环 / M3.5 生存循环）。
    /// 两类拾取物都用对象池复用；磁吸半径取自玩家实时属性，随升级扩大。
    /// </summary>
    public class PickupManager : Core.MonoSingleton<PickupManager>
    {
        private readonly List<XpGem> _gems = new List<XpGem>();
        private readonly List<HealthPickup> _healths = new List<HealthPickup>();

        private GameObject _gemTemplate;
        private GameObject _healthTemplate;
        private PlayerController _player;

        private const float CollectDist = 0.4f;
        private const float MagnetSpeed = 12f;

        // M42：上限值与"回收谁"的判据统一下沉到 <see cref="PickupTrim"/>，
        // 与联机路径（NetWorld）共用同一套常量与算法——原先两份各自实现，
        // 联机那份压根没写（见 NetWorld.SpawnGem 的注释）。
        private const int MaxGems = PickupTrim.MaxGems;
        private const int MaxHealth = PickupTrim.MaxHealth;

        /// <summary>M42：传给 <see cref="PickupTrim"/> 的玩家位置表（复用同一实例，避免每次回收都分配）。</summary>
        private readonly List<Vector2> _trimPlayers = new List<Vector2>(1);

        public void Configure(GameObject gemTemplate, GameObject healthTemplate, PlayerController player)
        {
            _gemTemplate = gemTemplate;
            _healthTemplate = healthTemplate;
            _player = player;
        }

        public void DropGem(Vector3 pos, float xp)
        {
            // 联机模式：掉落网络宝石（房主权威生成，客户端磁吸+请求拾取）
            if (NetWorld.Active) { NetWorld.SpawnGem(pos, xp); return; }
            if (_gemTemplate == null) return;
            var go = Core.PoolManager.Instance.Get(_gemTemplate);
            var gem = go.GetComponent<XpGem>();
            gem.Init(pos, xp, _gemTemplate);
            _gems.Add(gem);
            TrimGems();
        }

        /// <summary>M41：超出上限时回收离玩家最远的一个（判据详见 <see cref="PickupTrim"/>）。</summary>
        private void TrimGems()
        {
            if (!PickupTrim.Over(_gems.Count, MaxGems)) return;
            int worst = Farthest(_gems);
            if (worst < 0) return;
            var g = _gems[worst];
            _gems.RemoveAt(worst);
            if (g != null) g.Collect();          // 归还对象池：不加音效、不发经验
        }

        /// <summary>M41：超出上限时回收离玩家最远的一个。</summary>
        private void TrimHealth()
        {
            if (!PickupTrim.Over(_healths.Count, MaxHealth)) return;
            int worst = Farthest(_healths);
            if (worst < 0) return;
            var h = _healths[worst];
            _healths.RemoveAt(worst);
            if (h != null) h.Collect();
        }

        /// <summary>M42：账本里离玩家最远的那一项（-1 = 全为空项；空项由 Update 自行清理）。</summary>
        private int Farthest<T>(List<T> list) where T : Component
        {
            _trimPlayers.Clear();
            if (_player != null) _trimPlayers.Add(_player.transform.position);
            return PickupTrim.FarthestIndex(list, _trimPlayers);
        }

        public void DropHealth(Vector3 pos, float amount)
        {
            if (NetWorld.Active) { NetWorld.SpawnHealth(pos, amount); return; }
            if (_healthTemplate == null) return;
            var go = Core.PoolManager.Instance.Get(_healthTemplate);
            var h = go.GetComponent<HealthPickup>();
            h.Init(pos, amount, _healthTemplate);
            _healths.Add(h);
            TrimHealth();
        }

        void Update()
        {
            if (_player == null || _player.damageable.isDead) return;
            float radius = _player.stats.pickupRadius;
            Vector2 ppos = _player.transform.position;

            for (int i = _gems.Count - 1; i >= 0; i--)
            {
                var gem = _gems[i];
                if (gem == null) { _gems.RemoveAt(i); continue; }
                float d = Vector2.Distance(gem.transform.position, ppos);
                if (d <= CollectDist)
                {
                    Core.SfxManager.Instance?.Play("pickup", 0.4f);
                    _player.AddXp(gem.xp);
                    gem.Collect();
                    _gems.RemoveAt(i);
                }
                else if (d <= radius)
                {
                    Vector2 dir = (ppos - (Vector2)gem.transform.position).normalized;
                    gem.transform.Translate(dir * MagnetSpeed * Time.deltaTime, Space.World);
                }
            }

            for (int i = _healths.Count - 1; i >= 0; i--)
            {
                var h = _healths[i];
                if (h == null) { _healths.RemoveAt(i); continue; }
                float d = Vector2.Distance(h.transform.position, ppos);
                if (d <= CollectDist)
                {
                    Core.SfxManager.Instance?.Play("pickup", 0.5f);
                    _player.Heal(h.healAmount);
                    h.Collect();
                    _healths.RemoveAt(i);
                }
                else if (d <= radius)
                {
                    Vector2 dir = (ppos - (Vector2)h.transform.position).normalized;
                    h.transform.Translate(dir * MagnetSpeed * Time.deltaTime, Space.World);
                }
            }
        }

        /// <summary>
        /// 拆世界 / 重开一局时调用：销毁场上还活着的全部掉落物并清空账本。
        ///
        /// M24：旧实现只 SetActive(false)，对象会变成场景里看不见、却也永远不会回收的孤儿——
        /// 拆完世界它们仍在，新世界的 PickupManager 又不认它们（账本已空），
        /// 于是玩家看到一堆"看得见却捡不起来"的宝石。
        /// 配合 PoolManager.DestroyAll() 把已经归还到池里的那批也一并清掉。
        /// </summary>
        public void ResetAll()
        {
            foreach (var g in _gems) if (g != null) Object.Destroy(g.gameObject);
            foreach (var h in _healths) if (h != null) Object.Destroy(h.gameObject);
            _gems.Clear();
            _healths.Clear();
        }
    }
}
