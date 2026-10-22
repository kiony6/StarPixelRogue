using System;

namespace StarPixel.Net
{
    /// <summary>
    /// 联机传输层种类。**这个枚举就是"换传输层"的全部开关**——改 <see cref="NetTransport.Default"/>
    /// 一处，装配、目标设置、端口、能力判断全部跟着变。
    /// </summary>
    public enum TransportKind
    {
        /// <summary>认不出来。默认值取它而不是 Tugboat：宁可显式 Unknown，也不要默默当成某种已知实现。</summary>
        Unknown = 0,

        /// <summary>Fish-Net 自带的裸 UDP 直连。局域网的快速路径。</summary>
        Tugboat = 1,

        /// <summary>Epic Online Services 中继（FishyEOS）。跨公网走这条。</summary>
        Eos = 2,

        /// <summary>Steam 中继（FishySteamworks）。备选路线，尚未接入。</summary>
        Steam = 3,
    }

    /// <summary>
    /// 一种传输层的"说明书"：类型名、能不能跨公网、要不要先登录、地址里有没有端口概念。
    /// <para>
    /// 为什么要有这张表：这些事实此前散在三处硬编码里（编辑器装配脚本的类型候选、
    /// 后端兜底的 <c>AddComponent&lt;Tugboat&gt;()</c>、以及"端口是 7777"这个约定）。
    /// 散着的时候，换传输层要同时改三处，漏一处就表现成"连不上"而不是报错。
    /// </para>
    /// </summary>
    public sealed class TransportSpec
    {
        public TransportKind Kind;

        /// <summary>传输层组件的完整类型名（含命名空间）。装配时按它去找类型。</summary>
        public string TypeName;

        /// <summary>能不能把两个处在**不同网络**里的玩家连起来。false = 只在同一局域网可用。</summary>
        public bool CanCrossInternet;

        /// <summary>联机前是否必须先在平台上登录（拿一个身份）。</summary>
        public bool NeedsLogin;

        /// <summary>地址里是否还包含"端口"这个概念。UDP 直连有，平台中继没有。</summary>
        public bool UsesPort;

        /// <summary>
        /// "告诉它连谁"要设的成员名（属性/字段）。
        /// <para>
        /// 为 null 表示该传输层**还没接入**，不要猜。
        /// Tugboat 也是 null——但它走的是 <c>SetClientAddress</c>，见 <see cref="NetTransportSetup.ApplyTarget"/>。
        /// </para>
        /// </summary>
        public string TargetMember;

        /// <summary>Tugboat 用 <c>SetClientAddress</c>；其余用 TargetMember。</summary>
        public bool UsesClientAddress;
    }

    /// <summary>
    /// 传输层的选择与说明书。
    ///
    /// <para>
    /// **这一层存在的全部理由**：换传输层不是"换个组件"那么简单，有三个地方会静默失效：
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>告诉它连谁的方式不一样。</b>
    /// FishyEOS 的来源里写着
    /// <c>public override void SetClientAddress(string address) { _ = address; }</c>——
    /// 参数被**直接扔掉**。项目此前正是靠 <c>SetClientAddress</c> 告诉传输层目标的，
    /// 换过去之后地址静默丢失：不报错，只是永远连不上。EOS 要设的是它组件上的
    /// <c>RemoteProductUserId</c>。
    /// </item>
    /// <item>
    /// <b>端口的有无不一样。</b>
    /// FishyEOS 同样把 <c>SetPort</c> 覆写成空实现——中继没有端口这个概念。
    /// 而局域网发现应答里公布的端口，只有 Tugboat 语义下才有意义。
    /// </item>
    /// <item>
    /// <b>认的是哪个组件也要显式说。</b>
    /// FishNet 的 <c>TransportManager.TryAddDefaultTransport</c> 在 <c>Transport == null</c> 时
    /// 用 <c>TryGetComponent</c> 抓"场景里任意一个"传输层，抓不到就自己塞一个 Tugboat。
    /// 只有一个传输层时这是便利；一旦同时装了 EOS 与 Tugboat（局域网兜底 + 公网主路），
    /// 选中哪个就不确定了。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// **本文件不碰任何 FishNet 类型**，因此在没装插件的构建里照样编译。
    /// 需要 <c>Transport</c> / <c>TransportManager</c> 的装配动作在 <c>Net/NetTransportSetup.cs</c>，
    /// 那个文件整个被 <c>FISHNET_ENABLED</c> 包着。
    /// 为什么拆成两个文件而不是一个文件里加条件编译：静态审计第 5 项会检查
    /// "整文件被保护的类型有没有被无保护代码引用"，一个类型一半在里一半在外，
    /// 这个检查就没法给出确定的答案——拆开之后，"在不在"是一眼可见的。
    /// </para>
    /// </summary>
    public static class NetTransport
    {
        /// <summary>
        /// 当前构建选用的传输层。**换传输层就改这一行。**
        /// <para>
        /// 默认 Tugboat：它随 Fish-Net 一起分发，不需要任何账号与凭据，
        /// 局域网内立刻可用。EOS 凭据到位后改成 <see cref="TransportKind.Eos"/> 即可，
        /// 其余代码一行不动。
        /// </para>
        /// </summary>
        public static TransportKind Default = TransportKind.Tugboat;

        /// <summary>
        /// 某种传输层的说明书。未知种类返回 <c>null</c>——
        /// 宁可让调用方明确处理"不认识"，也不要编一份出来的。
        /// </summary>
        public static TransportSpec Describe(TransportKind kind)
        {
            switch (kind)
            {
                case TransportKind.Tugboat:
                    return new TransportSpec
                    {
                        Kind = TransportKind.Tugboat,
                        TypeName = "FishNet.Transporting.Tugboat.Tugboat",
                        CanCrossInternet = false,      // 裸 UDP：没有打洞、没有中继
                        NeedsLogin = false,            // 不需要任何账号
                        UsesPort = true,
                        UsesClientAddress = true,
                        TargetMember = null,
                    };

                case TransportKind.Eos:
                    return new TransportSpec
                    {
                        Kind = TransportKind.Eos,
                        TypeName = "FishNet.Transporting.FishyEOSPlugin.FishyEOS",
                        CanCrossInternet = true,       // EOS Connect 自带打洞 + 中继兜底
                        NeedsLogin = true,             // 必须先 EOS Connect 登录拿 ProductUserId
                        UsesPort = false,              // SetPort 被覆写成空实现
                        UsesClientAddress = false,     // SetClientAddress 被覆写成空实现
                        TargetMember = "RemoteProductUserId",
                    };

                case TransportKind.Steam:
                    return new TransportSpec
                    {
                        Kind = TransportKind.Steam,
                        TypeName = "FishNet.Transporting.FishySteamworks.FishySteamworks",
                        CanCrossInternet = true,
                        NeedsLogin = true,
                        UsesPort = false,
                        UsesClientAddress = false,
                        TargetMember = null,           // 未接入：不要猜成员名，猜错会静默失效
                    };

                // Unknown 与将来任何新增但没进这张表的种类：一律"不认识"。
                case TransportKind.Unknown:
                default:
                    return null;
            }
        }

        /// <summary>当前构建的说明书（即 <see cref="Describe"/> 作用于 <see cref="Default"/>）。</summary>
        public static TransportSpec Current { get { return Describe(Default); } }

        /// <summary>当前构建能否跨公网。UI 用它决定要不要提示"仅限同一网络"。</summary>
        public static bool CanCrossInternet
        {
            get { var s = Current; return s != null && s.CanCrossInternet; }
        }
    }
}
