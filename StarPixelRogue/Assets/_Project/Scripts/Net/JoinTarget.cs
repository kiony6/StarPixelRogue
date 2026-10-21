using System;
using System.Net;

namespace StarPixel.Net
{
    /// <summary>一个组队码最终解析出来的连接目标。</summary>
    public sealed class JoinResult
    {
        public bool Ok;

        /// <summary>失败原因（是**本地化键**，不是现成文案——UI 层再去查表，便于切换语言）。</summary>
        public string ReasonKey;

        public string RoomKey;
        public string Address;
        public int Port;
        public string HostName;
        public int Players;

        public static JoinResult Fail(string key) => new JoinResult { Ok = false, ReasonKey = key };

        public static JoinResult FromEntry(RoomEntry e) => new JoinResult
        {
            Ok = true,
            RoomKey = e.RoomKey,
            Address = e.Address,
            Port = e.Port,
            HostName = e.HostName,
            Players = e.Players,
        };
    }

    /// <summary>
    /// 把"玩家输入的组队码"解析成"可连接的地址"。
    /// <para>
    /// **这一层存在的全部理由**：寻址方式将来一定会换。
    /// 现在是「局域网广播发现 → 直连 IP」；要跨公网就得换成「平台中继 → 房间 ID」。
    /// 如果解析逻辑散在 <c>NetLobby</c> 里，换一次就得把 UI 和流程一起改一遍；
    /// 收在这里之后，将来只替换 <see cref="ResolveOverLan"/> 内部的实现，UI 与流程一行不动。
    /// </para>
    /// <para>
    /// 解析分两级，别混着用：
    /// <see cref="ResolveCached"/> 只查本地目录、**不碰网络、不阻塞**，主线程可直接调；
    /// <see cref="ResolveOverLan"/> 会广播并最长阻塞 <see cref="RoomDiscovery.QueryTimeout"/> 秒，
    /// 必须在协程里调，否则界面会冻住。
    /// </para>
    /// </summary>
    public static class JoinTarget
    {
        /// <summary>
        /// 解析（只查目录）：合法码 → 目录命中 → 目标。
        /// <para>查不到**不报错也广播**——调用方可以先查缓存走快速路径，
        /// 没命中再决定要不要花两秒去广播。</para>
        /// </summary>
        public static JoinResult ResolveCached(string rawInput)
        {
            string code;
            if (!RoomCode.TryParse(rawInput, out code))
                return JoinResult.Fail("net.err_bad_code");

            var e = RoomDirectory.Find(code);
            if (e == null) return JoinResult.Fail("net.err_no_room");
            return JoinResult.FromEntry(e);
        }

        /// <summary>
        /// 解析（含局域网广播）：先在目录里找，找不到再广播问一次。
        /// <para>广播结果会**顺手登记进目录**，下次同样这个码就不用再等两秒。</para>
        /// </summary>
        public static JoinResult ResolveOverLan(string rawInput)
        {
            string code;
            if (!RoomCode.TryParse(rawInput, out code))
                return JoinResult.Fail("net.err_bad_code");

            // 已经知道它在哪：直接走，不必再广播
            var cached = RoomDirectory.Find(code);
            if (cached != null) return JoinResult.FromEntry(cached);

            var replies = RoomDiscovery.Query(code);
            if (replies.Count == 0)
                return JoinResult.Fail(string.IsNullOrEmpty(RoomDiscovery.LastError)
                    ? "net.err_no_room"
                    : "net.err_discover_failed");

            var best = replies[0];
            var entry = RoomDirectory.Put(best.RoomKey, best.Address, best.Port, best.HostName, best.Players, false);
            return JoinResult.FromEntry(entry);
        }

        /// <summary>
        /// 输入是否是一个可直接连接的地址（<c>192.168.1.5</c> 或 <c>192.168.1.5:7777</c>）。
        /// <para>
        /// 判断靠"能不能解析成 IP"，而不是"看起来像不像"——
        /// 组队码的字母表里没有点号，所以含点的一定是地址，这个区分是确定的、不会误判。
        /// </para>
        /// </summary>
        public static bool TryParseDirectAddress(string raw, out string address, out int port)
        {
            address = "";
            port = NetProtocol.DefaultPort;
            if (string.IsNullOrEmpty(raw)) return false;

            string s = raw.Trim();
            int colon = s.IndexOf(':');
            string ipPart = colon >= 0 ? s.Substring(0, colon) : s;
            string portPart = colon >= 0 ? s.Substring(colon + 1) : "";

            IPAddress ip;
            if (!IPAddress.TryParse(ipPart, out ip)) return false;

            if (colon >= 0)
            {
                int p;
                if (!int.TryParse(portPart, out p) || p <= 0 || p > 65535) return false;
                port = p;
            }
            address = ipPart;
            return true;
        }

        /// <summary>
        /// 房主建房：生成码、登记进目录、开始广播应答。
        /// <para>返回生成的码（失败返回 null，失败原因见 <see cref="RoomDiscovery.LastError"/>）。
        /// 广播失败**不算建房失败**——房间照样能开，只是别人得手动填地址。</para>
        /// </summary>
        /// <summary>
        /// 本机开的房的码。
        /// <para>
        /// 为什么不读 <see cref="RoomDirectory.LocalCode"/>：那是目录里的**标记**，
        /// 而关房要清的是"我开过的那个码"。两者一旦不同步（目录被 Reset、
        /// 登记时漏了本机标记、开了第二个房），按标记清就会漏——
        /// 漏掉的那条永远留在目录里，正是目录注释里说的那种幽灵房间。
        /// 自己记住自己开了什么，比回头去问目录可靠。
        /// </para>
        /// </summary>
        private static string _hostedCode = "";

        public static string Host(string hostName, int port)
        {
            string code = RoomDirectory.NewUnusedCode();
            if (string.IsNullOrEmpty(code)) return null;

            string ip = "";
            var addrs = RoomDiscovery.LocalAddresses();
            if (addrs.Count > 0) ip = addrs[0].ToString();

            RoomDirectory.Put(code, ip, port, hostName, 1, true);
            _hostedCode = code;
            RoomDiscovery.StartListening();
            return code;
        }

        /// <summary>关房：清掉本机开的那个码，并停止广播应答。重复调用无副作用。</summary>
        public static void Unhost()
        {
            if (!string.IsNullOrEmpty(_hostedCode)) RoomDirectory.Forget(_hostedCode);
            _hostedCode = "";
            RoomDiscovery.StopListening();
        }
    }
}
