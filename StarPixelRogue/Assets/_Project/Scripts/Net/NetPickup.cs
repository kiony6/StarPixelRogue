#if FISHNET_ENABLED
using FishNet.Object;
using FishNet.Object.Synchronizing;
using StarPixel.Core;
using StarPixel.Player;
using UnityEngine;

namespace StarPixel.Net
{
    public enum NetPickupKind { Xp, Health }

    /// <summary>
    /// 网络掉落物（经验宝石 / 回血球，联机模式）。
    /// M4.5 修正：磁吸与收集判定全部在房主（服务端）执行——
    /// - 旧实现让客户端本地 Translate 磁吸，会与 NetworkTransform 的服务端权威同步互相拉扯（抖动）；
    /// - 且客户端本地判定 + ServerRpc 回传多一个往返，手感反而更差。
    /// 现在：房主统一模拟（磁吸/拾取/发奖），客户端只跟随 NetworkTransform 的位置同步，
    /// 拾取反馈（消失/音效）经反生成广播立即到达所有端。
    /// </summary>
    public class NetPickup : NetworkBehaviour
    {
        private readonly SyncVar<NetPickupKind> _kind = new SyncVar<NetPickupKind>(NetPickupKind.Xp);
        private readonly SyncVar<float> _value = new SyncVar<float>(0f);

        private const float CollectDist = 0.4f;
        private const float MagnetSpeed = 12f;

        public void ServerInit(NetPickupKind kind, float xp, float heal)
        {
            _kind.Value = kind;
            _value.Value = kind == NetPickupKind.Xp ? xp : heal;
        }

        /// <summary>
        /// M42：种类只读出口。<c>_kind</c> 是 private SyncVar，不给出口的话无头探针只能靠
        /// 预制体名猜"场上这些到底是宝石还是回血球"——猜错就会把回血球算进宝石的判据里。
        /// </summary>
        public NetPickupKind Kind => _kind.Value;

        /// <summary>
        /// M42：从账本反登记。拾取（Despawn）/ 超限回收（Despawn）/ 拆世界（Destroy）
        /// 三条销毁路径都必然经过这里，是账本"有出"的唯一保证——只进不出的账本正是
        /// M41 在单机侧修掉、联机侧本轮才补上的那个缺陷。
        /// </summary>
        void OnDestroy()
        {
            NetWorld.UnregisterPickup(this);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // 初始值不触发 OnChange（Fish-Net 行为），此处显式应用 + 订阅后续变化双保险
            _kind.OnChange += (_, __, ___) => ApplyVisual();
            ApplyVisual();
        }

        private void ApplyVisual()
        {
            var sr = GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.color = Color.white;
                sr.sprite = Core.SpriteFactory.GetSprite(
                    _kind.Value == NetPickupKind.Xp ? "gem" : "health", Color.white);
            }
        }

        void Update()
        {
            if (!IsServerInitialized) return;   // 纯客户端只跟随位置同步，不做本地判定

            // 磁吸目标：距离最近的存活玩家（合作模式下共享掉落，先到先得）
            var target = NetPlayerRegistry.NearestAlive(transform.position);
            if (target == null || target.Controller == null) return;

            float dist = Vector2.Distance(transform.position, target.transform.position);
            if (dist <= CollectDist)
            {
                Grant(target);
                NetSpawner.Despawn(gameObject);
                return;
            }
            if (dist <= target.Controller.stats.pickupRadius)
            {
                Vector2 dir = ((Vector2)target.transform.position - (Vector2)transform.position).normalized;
                transform.Translate(dir * MagnetSpeed * Time.deltaTime, Space.World);
            }
        }

        private void Grant(NetPlayer p)
        {
            if (_kind.Value == NetPickupKind.Xp)
            {
                p.ServerGrantXp(_value.Value);
                SfxManager.Instance?.Play("pickup", 0.4f);
            }
            else
            {
                p.ServerGrantHeal(_value.Value);
                SfxManager.Instance?.Play("pickup", 0.5f);
            }
        }
    }
}
#endif
