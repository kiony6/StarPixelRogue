using System;
using UnityEngine;

namespace StarPixel.Combat
{
    /// <summary>M39：一次升级选择的四种收场。</summary>
    public enum UpgradeNoticeReason
    {
        /// <summary>房主确认——强化已生效。</summary>
        Applied = 0,
        /// <summary>房主拒绝（批次已结算 / 越界 / 重放）。</summary>
        Rejected = 1,
        /// <summary>房主淘汰了该批次——卡片被撤。</summary>
        Invalidated = 2,
        /// <summary>等回执超时。</summary>
        TimedOut = 3,
    }

    /// <summary>
    /// M39：升级选择的**收场通知**——四种收场都必须让玩家看得见。
    ///
    /// 起因（M38 修完之后暴露的那一层）：
    ///   M38 把本地镜像改成"房主点头才许应用"，四种收场（确认 / 拒绝 / 作废 / 超时）
    ///   **都有出口、都有计数**了——但那四个出口的终点是 `Debug.Log` 和 `静态计数`，
    ///   玩家一个字也看不到。于是从玩家视角看是：
    ///       · 被拒：点了卡，面板关了，**什么都没发生**，也不知道为什么；
    ///       · 作废：面板**自己消失了**，没有任何解释；
    ///       · 超时：连日志都没有，彻底静默；
    ///       · 成功：面板关了，拿到了什么**也没说**（移速/生命上限变了，但玩家未必察觉）。
    ///
    ///   这不是"体验优化"，是**缺陷**：M37 说"宁可少发一次强化"、M38 说"宁可等确认"，
    ///   两条都是在**主动牺牲成功率**换正确性。牺牲得对，但代价必须让玩家知道——
    ///   否则玩家只会认为游戏吞了他的升级，而且**无从报告**（他连发生了什么都不知道）。
    ///   **静默的收场等于没发生。**
    ///
    /// 同样是**不依赖网络的纯静态类**（理由同 <see cref="UpgradeOfferBatches"/> / <see cref="UpgradeApplyGate"/>）：
    ///   NetPlayer 是 NetworkBehaviour 且整文件在 <c>#if FISHNET_ENABLED</c> 里，
    ///   单机实例化不出来。抽出来之后，"收场不出声"在 NOTICE 场景里秒级就能钉死。
    ///
    /// 守恒式（探针据此判定，任一条不成立都说明有收场被吞了）：
    ///   <c>Total == CountOf(四种) 之和 == ShownCount + SuppressedCount</c>，且 <c>SuppressedCount == 0</c>。
    ///   切语言重画（<see cref="Repaint"/>）会先撤销上一次的记账再重画，保证守恒不被破坏。
    /// </summary>
    public static class UpgradeNotice
    {
        public const int ReasonCount = 4;

        /// <summary>
        /// 上屏出口：由 UI（HUDController）注册，传入**已经本地化**的文案，返回是否真的显示出来。
        /// 没注册 / 返回 false → 记 <see cref="SuppressedCount"/>（有通知但没有出口，玩家仍然看不见）。
        /// </summary>
        public static Func<string, bool> Deliver;

        private static readonly int[] _counts = new int[ReasonCount];
        private static int _shown;
        private static int _suppressed;

        private static bool _hasLast;
        private static bool _lastWasShown;
        private static string _lastKey = "";
        private static string _lastArg = "";

        /// <summary>发生过几次收场（四种之和）。</summary>
        public static int Total
        {
            get
            {
                int s = 0;
                for (int i = 0; i < ReasonCount; i++) s += _counts[i];
                return s;
            }
        }

        /// <summary>真的上屏了几条。</summary>
        public static int ShownCount { get { return _shown; } }

        /// <summary>有通知却没上屏的条数（UI 不在 / 出口未注册）——**必须为 0**。</summary>
        public static int SuppressedCount { get { return _suppressed; } }

        /// <summary>
        /// 当前挂着的那条文案（切语言后按新语言重画，供探针/UI 读取）。
        /// **无论有没有上屏都记录**——"说了什么"与"有没有送达"是两个维度，
        /// 后者由 <see cref="LastShown"/> 与 <see cref="SuppressedCount"/> 表达。
        /// （最初写成没上屏就置空，结果出口一旦缺失，连文案本身都查不到了。）
        /// </summary>
        public static string LastText { get; private set; }

        /// <summary>当前挂着的那条**有没有真的上屏**。</summary>
        public static bool LastShown { get; private set; }

        /// <summary>当前挂着的那条属于哪种收场（UI 据此决定颜色：成功暖金 / 失败警示色）。</summary>
        public static UpgradeNoticeReason LastReason { get; private set; }

        public static int CountOf(UpgradeNoticeReason r)
        {
            int i = (int)r;
            return (i >= 0 && i < ReasonCount) ? _counts[i] : 0;
        }

        /// <summary>该收场对应的本地化键（中英两张表必须同时有）。</summary>
        public static string LocKeyOf(UpgradeNoticeReason r)
        {
            switch (r)
            {
                case UpgradeNoticeReason.Applied: return "upgrade.notice.applied";
                case UpgradeNoticeReason.Rejected: return "upgrade.notice.rejected";
                case UpgradeNoticeReason.Invalidated: return "upgrade.notice.invalidated";
                default: return "upgrade.notice.timedout";
            }
        }

        /// <summary>
        /// 记一次收场并推给玩家。<paramref name="arg0"/> 用于填进文案的占位符（如强化名）。
        /// 返回是否真的上屏。
        /// </summary>
        public static bool Raise(UpgradeNoticeReason r, string arg0 = null)
        {
            int i = (int)r;
            if (i < 0 || i >= ReasonCount) return false;
            _counts[i]++;
            _hasLast = true;
            LastReason = r;
            _lastKey = LocKeyOf(r);
            _lastArg = arg0 ?? "";
            return Paint(true);
        }

        /// <summary>
        /// 按**当前语言**重画上一条（切语言时用）。不新增收场计数，
        /// 但会把上一次的上屏/被吞记账撤销后重记——保证守恒式不被破坏。
        /// </summary>
        public static bool Repaint()
        {
            if (!_hasLast) return false;
            if (_lastWasShown) _shown--;
            else _suppressed--;
            return Paint(false);
        }

        private static bool Paint(bool debugLog)
        {
            string text = string.IsNullOrEmpty(_lastArg)
                ? Core.Localization.T(_lastKey)
                : Core.Localization.T(_lastKey, _lastArg);
            bool ok = Deliver != null && Deliver(text);
            if (ok) _shown++; else _suppressed++;
            _lastWasShown = ok;
            LastText = text;
            LastShown = ok;
            if (debugLog)
                Debug.Log("[M39] 收场通知 " + _lastKey + " → " + (ok ? "已上屏：" + text
                          : "**没有出口**（玩家看不到这次收场）"));
            return ok;
        }

        /// <summary>探针用：清零全部计数。</summary>
        public static void Reset()
        {
            for (int i = 0; i < ReasonCount; i++) _counts[i] = 0;
            _shown = 0;
            _suppressed = 0;
            _hasLast = false;
            _lastWasShown = false;
            _lastKey = "";
            _lastArg = "";
            LastText = "";
            LastShown = false;
            LastReason = UpgradeNoticeReason.Applied;
        }
    }
}
