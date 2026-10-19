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
        }

        public void DropHealth(Vector3 pos, float amount)
        {
            if (NetWorld.Active) { NetWorld.SpawnHealth(pos, amount); return; }
            if (_healthTemplate == null) return;
            var go = Core.PoolManager.Instance.Get(_healthTemplate);
            var h = go.GetComponent<HealthPickup>();
            h.Init(pos, amount, _healthTemplate);
            _healths.Add(h);
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
