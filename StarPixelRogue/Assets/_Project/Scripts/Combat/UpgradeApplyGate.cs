using System.Collections.Generic;

namespace StarPixel.Combat
{
    /// <summary>
    /// M38：客户端「待确认选择」表——一次选择必须先记账、等房主点头，然后才许生效。
    ///
    /// 为什么需要它（M37 修完之后才暴露出来的那半边）：
    ///   M37 把房主侧改成了批次表，取不到就拒绝，当时的说法是
    ///   「宁可少发一次强化，也绝不能发错一张」。这话只对了一半——
    ///   客户端那半边是**先斩后奏**的：面板回调里 `RequestChooseUpgradeServerRpc(...)`
    ///   发出去（可能被拒），紧接着**无条件**执行 `UpgradeCatalog.ApplyKind(...)`
    ///   把本地权威数据（移速 / 磁吸范围 / 生命上限）先加上了。
    ///   于是房主越"严格"，双端越不一致：房主拒绝了，客户端已经加成——
    ///   客户端跑得比房主快、血比房主厚，而且**没有任何日志、任何计数**。
    ///
    ///   也就是说：M37 那张安全网（拒绝）在没有这层确认之前，
    ///   每拦下一次就制造一次分叉。安全设计被另一半的写法抵消掉了。
    ///
    /// 改法：选择先 <see cref="Submit"/> 记一笔账（拿到 ackId），
    /// 只有房主回 <see cref="Confirm"/> 才允许本地应用，且**只允许一次**
    /// （ackId 一次性，重复回执/伪造回执一律无效——防重放）；
    /// 被拒走 <see cref="Reject"/>；批次被房主淘汰走 <see cref="Invalidate(int)"/>；
    /// 房主压根不回走 <see cref="PurgeExpired"/>。四种收场都有出口、都有计数。
    ///
    /// 同样是不依赖网络的纯类（理由同 <see cref="UpgradeOfferBatches"/>）：
    ///   NetPlayer 是 NetworkBehaviour 且整文件在 <c>#if FISHNET_ENABLED</c> 里，
    ///   单机实例化不出来。抽出来之后，"先斩后奏"在 GATE 场景里秒级就能钉死，
    ///   不必等 60 秒双开、也不必靠"感觉客户端跑得快了一点点"去猜。
    /// </summary>
    public class UpgradeApplyGate
    {
        /// <summary>等回执的默认超时（秒）。超时按"被拒"处理——不无限悬着。</summary>
        public const float DefaultTimeout = 15f;

        private struct Pending
        {
            public int batchId;
            public int index;
            public float at;
        }

        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private readonly List<int> _order = new List<int>();
        private int _nextAck = 1;

        /// <summary>发起过几次选择（等于面板被点了几次）。</summary>
        public int SubmittedCount { get; private set; }
        /// <summary>房主确认过几次（这才是**真正**生效的强化数）。</summary>
        public int ConfirmedCount { get; private set; }
        /// <summary>被房主拒绝过几次（批次过期/越界/重复）。</summary>
        public int RejectedCount { get; private set; }
        /// <summary>因所属批次被房主淘汰而作废的几次。</summary>
        public int InvalidatedCount { get; private set; }
        /// <summary>等回执等到超时的几次（房主不回，或回执丢了）。</summary>
        public int TimedOutCount { get; private set; }

        /// <summary>还悬着没落地的几次。收尾时应为 0——不为 0 就是有选择没人管。</summary>
        public int PendingCount { get { return _pending.Count; } }

        /// <summary>
        /// 记一笔待确认的选择，返回一次性 ackId（&lt;=0 表示参数非法，调用方应跳过发 RPC）。
        /// </summary>
        public int Submit(int batchId, int index)
        {
            if (batchId <= 0 || index < 0) return 0;
            int ack = _nextAck++;
            _pending[ack] = new Pending { batchId = batchId, index = index, at = -1f };
            _order.Add(ack);
            SubmittedCount++;
            return ack;
        }

        /// <summary>
        /// 记一笔带时间戳的待确认选择（<paramref name="now"/> 用 Time.unscaledTime，
        /// 不受暂停/顿帧影响——选卡期间 timeScale 可能被改成 0）。
        /// </summary>
        public int Submit(int batchId, int index, float now)
        {
            int ack = Submit(batchId, index);
            if (ack > 0)
            {
                var p = _pending[ack];
                p.at = now;
                _pending[ack] = p;
            }
            return ack;
        }

        /// <summary>
        /// 房主确认：只有**这一笔还在、且回执与请求对得上**才返回 true，并把它取走。
        /// 重复确认 / 未知 ackId / 已作废的一律 false——回执不是"加成就生效"的凭据，
        /// 它是"这一笔被结掉"的凭据，一笔只能结一次。
        /// </summary>
        public bool Confirm(int ackId, int batchId, int index)
        {
            Pending p;
            if (ackId <= 0 || !_pending.TryGetValue(ackId, out p)) return false;
            if (p.batchId != batchId || p.index != index) return false;
            _pending.Remove(ackId);
            _order.Remove(ackId);
            ConfirmedCount++;
            return true;
        }

        /// <summary>房主拒绝（或本地判定作废）：同样一笔只算一次。</summary>
        public bool Reject(int ackId)
        {
            if (ackId <= 0 || !_pending.Remove(ackId)) return false;
            _order.Remove(ackId);
            RejectedCount++;
            return true;
        }

        /// <summary>
        /// 房主淘汰了某一批（玩家长时间不选 / 回传链路断了）→ 这一批下面悬着的选择全部作废。
        /// 不作废的话，玩家点了那张卡，客户端会拿着一个永远不会来的回执一直等。
        /// </summary>
        /// <returns>作废了几笔。</returns>
        public int Invalidate(int batchId)
        {
            if (batchId <= 0) return 0;
            var hit = new List<int>();
            foreach (var kv in _pending)
                if (kv.Value.batchId == batchId) hit.Add(kv.Key);
            foreach (int ack in hit)
            {
                _pending.Remove(ack);
                _order.Remove(ack);
            }
            InvalidatedCount += hit.Count;
            return hit.Count;
        }

        /// <summary>
        /// 超时清理：房主不回、或回执在链路上丢了。悬着的选择不能永远悬着——
        /// 按"被拒"处理（宁可少一次强化，也不能让客户端自己说了算）。
        /// </summary>
        /// <returns>清理了几笔。</returns>
        public int PurgeExpired(float now, float timeout = DefaultTimeout)
        {
            var dead = new List<int>();
            foreach (var kv in _pending)
            {
                if (kv.Value.at < 0f) continue;               // 没记时间戳的不参与超时判定
                if (now - kv.Value.at > timeout) dead.Add(kv.Key);
            }
            foreach (int ack in dead)
            {
                _pending.Remove(ack);
                _order.Remove(ack);
            }
            TimedOutCount += dead.Count;
            return dead.Count;
        }

        public void Clear()
        {
            _pending.Clear();
            _order.Clear();
        }
    }
}
