using System.Collections.Generic;

namespace StarPixel.Combat
{
    /// <summary>
    /// M37：升级供选的**批次表**——把「第几次推卡」和「这次的三张卡」绑成一个 id。
    ///
    /// 为什么需要它（这是本轮两处缺陷的共同根因）：
    ///   房主原先只维护一份 <c>_cachedOffers</c>，每次推卡 <c>Clear()</c> 再填新的一组；
    ///   客户端回传的只有一个 <c>index</c>（第几张）。于是——
    ///     · 客户端还没选完就又升了一级 → 房主那份表已被换成新一组，
    ///       而客户端面板上显示的还是旧一组：玩家点第 1 张「攻速」，房主按 index=1
    ///       应用的是**新组**的第 1 张（可能是「移速」）。选的和得的不是同一张。
    ///     · 这类错配不会报错、不会崩，只有把两次推卡的标题并排看才看得出来。
    ///   改成批次后：一次推卡 = 一个 id，回传必须带 id，房主按 id 取回自己那一组，
    ///   取不到就拒绝（宁可少一次强化，也不能发错一张）。
    ///
    /// 为什么抽成独立类而不是塞进 NetPlayer：
    ///   NetPlayer 是 NetworkBehaviour（且整文件裹在 <c>#if FISHNET_ENABLED</c> 里），
    ///   单机无头环境根本实例化不出来，这条逻辑就永远只能"靠联机双开顺带覆盖"。
    ///   抽成不依赖网络的纯类之后，CardProbe 可以在单机里直接把「连推三批、乱序取回、
    ///   取回不存在的 id、超上限淘汰」全部钉死——和可测性有关，和功能无关。
    /// </summary>
    public class UpgradeOfferBatches
    {
        /// <summary>未回应批次的上限。超过后淘汰**最旧**的一批（它已经过期，玩家早错过了）。</summary>
        public const int DefaultCapacity = 16;

        private readonly Dictionary<int, List<UpgradeOffer>> _batches =
            new Dictionary<int, List<UpgradeOffer>>();

        /// <summary>插入顺序（Dictionary 不保证顺序，淘汰最旧需要它）。</summary>
        private readonly List<int> _order = new List<int>();

        private int _nextId = 1;

        public UpgradeOfferBatches(int capacity = DefaultCapacity)
        {
            Capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        public int Capacity { get; private set; }

        /// <summary>当前尚未被取走（玩家还没选）的批次数。</summary>
        public int PendingCount { get { return _batches.Count; } }

        /// <summary>因超过上限被淘汰的批次数（>0 说明玩家长时间不选卡，或者是回传链路断了）。</summary>
        public int EvictedCount { get; private set; }

        /// <summary>已分配出去的批次总数（= 推卡次数）。</summary>
        public int IssuedCount { get; private set; }

        /// <summary>
        /// M38：某批被上限淘汰时触发（参数是批次 id）。
        ///
        /// M37 把淘汰只做成了**计数**（EvictedCount），没有出口——房主知道这批没了，
        /// 客户端不知道，玩家眼前还摆着那张卡，点了之后回传被拒、什么也没发生。
        /// 有这个回调，房主才能在淘汰的同一时刻通知对端把那张卡撤掉（M38 实测缺陷）。
        /// </summary>
        public event System.Action<int> OnEvicted;

        /// <summary>
        /// 登记一批候选并分配批次 id。
        /// </summary>
        /// <returns>批次 id（>=1）；候选为空时返回 0（调用方应据此跳过推卡，别发空面板）。</returns>
        public int Add(List<UpgradeOffer> offers)
        {
            if (offers == null || offers.Count == 0) return 0;

            int id = _nextId++;
            IssuedCount++;

            // 拷贝一份：调用方普遍复用同一个临时列表，不拷的话前一批会被后一批覆盖，
            // 等于把本轮要修的缺陷换个地方再犯一次。
            _batches[id] = new List<UpgradeOffer>(offers);
            _order.Add(id);

            while (_order.Count > Capacity)
            {
                int oldest = _order[0];
                _order.RemoveAt(0);
                if (_batches.Remove(oldest))
                {
                    EvictedCount++;
                    var h = OnEvicted;                 // M38：淘汰必须能被对端感知（详见事件注释）
                    if (h != null) h.Invoke(oldest);
                }
            }
            return id;
        }

        /// <summary>该批次是否还在（已被取走或已被淘汰则为 false）。</summary>
        public bool Contains(int batchId)
        {
            return batchId > 0 && _batches.ContainsKey(batchId);
        }

        /// <summary>
        /// 取走该批次的第 <paramref name="index"/> 张，取到即移除整批（一批只允许选一次）。
        /// </summary>
        /// <returns>取到返回 true。批次不存在 / 已被取走 / 越界都返回 false——
        /// 宁可少发一次强化，也绝不能拿别的批次的卡来凑。</returns>
        public bool TryTake(int batchId, int index, out UpgradeOffer offer)
        {
            offer = default(UpgradeOffer);
            if (batchId <= 0) return false;

            List<UpgradeOffer> list;
            if (!_batches.TryGetValue(batchId, out list) || list == null) return false;
            if (index < 0 || index >= list.Count) return false;

            offer = list[index];
            _batches.Remove(batchId);
            _order.Remove(batchId);
            return true;
        }

        /// <summary>取走该批次的第 index 张并直接执行它的 apply（房主权威应用的便捷入口）。</summary>
        public bool TryApply(int batchId, int index)
        {
            UpgradeOffer o;
            return TryApply(batchId, index, out o);
        }

        /// <summary>
        /// 同上，并把**房主认定的那一张**带回来（M38）。
        ///
        /// 客户端本地镜像要应用的是**房主认定的这张**，不是"客户端自己记的那一组第 index 张"——
        /// 后者正是先斩后奏的根源：客户端按自己记得的组去应用，房主按自己的表去应用，
        /// 两边一旦对不上，客户端就是自说自话。由房主回传 kind，本地镜像才没有第二种可能。
        /// </summary>
        public bool TryApply(int batchId, int index, out UpgradeOffer offer)
        {
            offer = default(UpgradeOffer);
            if (!TryTake(batchId, index, out offer)) return false;
            if (offer.apply != null) offer.apply.Invoke();
            return true;
        }

        public void Clear()
        {
            _batches.Clear();
            _order.Clear();
        }
    }
}
