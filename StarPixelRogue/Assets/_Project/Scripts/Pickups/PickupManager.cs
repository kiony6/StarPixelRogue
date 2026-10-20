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

        /// <summary>
        /// M41：场上掉落物上限（两类各自计）。
        ///
        /// 为什么必须有：账本此前**只进不出**——只有玩家走到身上（CollectDist 内）才会被移除，
        /// 既没有寿命也没有上限。短局里玩家跑得过来（45s 掉 30 个、捡走 28 个），看不出异常；
        /// 但波次推高后每波上百只怪、玩家根本捡不完：实测 30 分钟游戏时间堆到
        /// 887 个宝石 + 148 个回血球，而 <see cref="Update"/> 每帧都要把整张表遍历一遍算距离，
        /// 于是**越玩越卡**——所有短局判据却依旧全绿，因为 45 秒里它根本来不及堆。
        ///
        /// 为什么回收"离玩家最远"的而不是"最早的"：玩家够得着的要留住，
        /// 够不着的那个本来也捡不到，牺牲它对实际收益影响最小。
        /// </summary>
        private const int MaxGems = 240;
        private const int MaxHealth = 80;

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

        /// <summary>M41：超出上限时回收离玩家最远的一个（详见 <see cref="MaxGems"/> 注释）。</summary>
        private void TrimGems()
        {
            if (_gems.Count <= MaxGems) return;
            int worst = Farthest(_gems);
            if (worst < 0) return;
            var g = _gems[worst];
            _gems.RemoveAt(worst);
            if (g != null) g.Collect();          // 归还对象池：不加音效、不发经验
        }

        /// <summary>M41：超出上限时回收离玩家最远的一个。</summary>
        private void TrimHealth()
        {
            if (_healths.Count <= MaxHealth) return;
            int worst = Farthest(_healths);
            if (worst < 0) return;
            var h = _healths[worst];
            _healths.RemoveAt(worst);
            if (h != null) h.Collect();
        }

        /// <summary>账本里离玩家最远的那一项；全为空时返回 -1（空项由 Update 自行清理）。</summary>
        private int Farthest<T>(List<T> list) where T : Component
        {
            Vector2 p = _player != null ? (Vector2)_player.transform.position : Vector2.zero;
            int worst = -1;
            float worstD = -1f;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                float d = Vector2.Distance((Vector2)c.transform.position, p);
                if (d > worstD) { worstD = d; worst = i; }
            }
            return worst;
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
