using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StarPixel.Net
{
    /// <summary>局域网里一台被发现的房间（应答报文解开后的结果）。</summary>
    public sealed class RoomReply
    {
        /// <summary>同 <see cref="RoomEntry.RoomKey"/>：不叫 Code 是为了避开 Core 里的同名类型。</summary>
        public string RoomKey;
        public string Address;
        public int Port;
        public string HostName;
        public int Players;
    }

    /// <summary>
    /// 局域网房间发现：客户端拿着组队码在子网里问一句"谁是 XXX"，房主自己举手。
    /// <para>
    /// **为什么需要这一层**：组队码只解决了"要找哪个房间"，没解决"那个房间在哪台机器上"。
    /// 家用网络里房主没有固定 IP、也不该让玩家去查自己的内网地址，
    /// 广播问一句是唯一让"输个码就能连上"成立的办法。
    /// </para>
    /// <para>
    /// **它的边界**：广播只在同一个二层网段内有效，跨公网不成立。
    /// 跨公网要走平台中继（EOS / Steam），那时替换的是 <see cref="JoinTarget"/> 的解析实现，
    /// 本类的报文编解码依然用于局域网直连这条快速路径。
    /// </para>
    /// <para>
    /// 报文编解码是**纯函数**，与 socket 完全分离——这样它能在无头探针里被全量断言，
    /// 而真实收发（依赖网卡、依赖另一台机器）不进入判据。
    /// </para>
    /// </summary>
    public static class RoomDiscovery
    {
        /// <summary>发现端口（与游戏端口 7777 分开：一个用于找房间，一个用于跑游戏）。</summary>
        public const int Port = 47777;

        /// <summary>协议前缀 + 版本。版本留着是为了将来改报文格式时能拒绝旧版，而不是解出个错的。</summary>
        public const string Magic = "SPD1";

        /// <summary>广播地址。</summary>
        public static readonly string BroadcastAddress = "255.255.255.255";

        /// <summary>等待应答的时长（秒）。</summary>
        public const float QueryTimeout = 2.0f;

        private static UdpClient _socket;
        private static bool _listening;

        /// <summary>最近一次 socket 故障的原因（诊断用；为空即一切正常）。</summary>
        public static string LastError { get; private set; } = "";

        // ============================ 报文编解码（纯逻辑） ============================

        /// <summary>打包询问报文。</summary>
        public static string BuildQuery(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            return Magic + "|Q|" + code;
        }

        /// <summary>解询问报文。前缀不对或字段数不对一律拒绝——宁可漏掉一个房间，也不要解出个错码。</summary>
        public static bool TryParseQuery(string msg, out string code)
        {
            code = "";
            if (string.IsNullOrEmpty(msg)) return false;
            var p = msg.Split('|');
            if (p.Length != 3) return false;
            if (p[0] != Magic || p[1] != "Q") return false;
            if (!RoomCode.IsValid(p[2])) return false;
            code = p[2];
            return true;
        }

        /// <summary>
        /// 打包应答报文。
        /// <para>主机名里的分隔符会被换掉：它是唯一一个用户可控、又参与到报文结构里的字段，
        /// 不处理的话一个带 <c>|</c> 的名字就能把整条报文切歪。</para>
        /// </summary>
        public static string BuildReply(string code, string address, int port, string hostName, int players)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(address)) return "";
            string name = (hostName ?? "").Replace('|', ' ').Trim();
            if (name.Length > 24) name = name.Substring(0, 24);
            return string.Format("{0}|A|{1}|{2}|{3}|{4}|{5}", Magic, code, address, port, name, players);
        }

        /// <summary>解应答报文。</summary>
        public static bool TryParseReply(string msg, out RoomReply reply)
        {
            reply = null;
            if (string.IsNullOrEmpty(msg)) return false;
            var p = msg.Split('|');
            if (p.Length != 7) return false;
            if (p[0] != Magic || p[1] != "A") return false;
            if (!RoomCode.IsValid(p[2])) return false;

            IPAddress ip;
            if (!IPAddress.TryParse(p[3], out ip)) return false;
            int port;
            if (!int.TryParse(p[4], out port) || port <= 0 || port > 65535) return false;
            int players;
            if (!int.TryParse(p[6], out players)) players = 0;

            reply = new RoomReply { RoomKey = p[2], Address = p[3], Port = port, HostName = p[5], Players = players };
            return true;
        }

        // ============================ 真实收发 ============================

        /// <summary>
        /// 房主开始监听广播（建房成功后调用）。失败不抛异常——
        /// 发现不了只是"别人得手动填 IP"，不该让整个开房流程挂掉。
        /// </summary>
        public static bool StartListening()
        {
            if (_listening) return true;
            try
            {
                _socket = new UdpClient();
                _socket.EnableBroadcast = true;
                _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                BeginReceive();
                _listening = true;
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseSocket();
                return false;
            }
        }

        /// <summary>停止监听并释放端口（拆世界/关房时必须调，否则端口会被一直占着）。</summary>
        public static void StopListening()
        {
            _listening = false;
            CloseSocket();
        }

        /// <summary>是否已处于监听状态（探针用）。</summary>
        public static bool IsListening => _listening;

        private static void BeginReceive()
        {
            try
            {
                _socket.BeginReceive(OnReceive, null);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private static void OnReceive(IAsyncResult ar)
        {
            IPEndPoint from = null;
            byte[] buf = null;
            try
            {
                if (_socket == null) return;
                buf = _socket.EndReceive(ar, ref from);
            }
            catch
            {
                return;                       //  socket 已关闭：正常退出路径
            }
            finally
            {
                if (_listening) BeginReceive();   // 无论本次成败都续上下一次，否则听一次就聋了
            }

            if (buf == null || buf.Length == 0 || from == null) return;
            string code;
            if (!TryParseQuery(Encoding.UTF8.GetString(buf), out code)) return;

            // 只应答"问的是我"的那一句——否则局域网里每个房主都会对每条询问回一次，
            // 客户端会同时收到一堆不相干的房间。
            var mine = RoomDirectory.Find(code);
            if (mine == null || !mine.IsLocal) return;

            string ip = PickLocalAddress(from.Address);
            if (string.IsNullOrEmpty(ip)) return;

            string msg = BuildReply(code, ip, NetProtocol.DefaultPort, mine.HostName, mine.Players);
            var data = Encoding.UTF8.GetBytes(msg);
            try
            {
                _socket.Send(data, data.Length, new IPEndPoint(from.Address, Port));
            }
            catch { }
        }

        /// <summary>
        /// 客户端广播一句"谁是 <paramref name="code"/>"，同步等待并收集应答。
        /// <para>这是**阻塞调用**（最长 <see cref="QueryTimeout"/> 秒），
        /// 调用方必须放在协程里或接受卡顿——放在主线程直接调会让界面冻住两秒。</para>
        /// </summary>
        public static List<RoomReply> Query(string code)
        {
            var found = new List<RoomReply>();
            if (string.IsNullOrEmpty(code)) return found;

            UdpClient cli = null;
            try
            {
                cli = new UdpClient();
                cli.EnableBroadcast = true;
                cli.Client.ReceiveTimeout = (int)(QueryTimeout * 1000);

                var data = Encoding.UTF8.GetBytes(BuildQuery(code));
                try
                {
                    cli.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(BroadcastAddress), Port));
                }
                catch
                {
                    // 某些环境不允许 255.255.255.255（例如无广播权限的容器）。
                    // 退回子网广播由调用方决定——这里如实记录，不假装成功。
                    // @zhonly: 只进日志并被判空，不呈现给玩家（界面文案走 net.err_discover_failed 键）
                    LastError = "广播发送被拒绝";
                    return found;
                }

                var deadline = DateTime.UtcNow.AddSeconds(QueryTimeout);
                while (DateTime.UtcNow < deadline)
                {
                    IPEndPoint from = null;
                    byte[] buf;
                    try
                    {
                        buf = cli.Receive(ref from);
                    }
                    catch (SocketException)
                    {
                        break;                    // 超时：没有人应答，正常结果
                    }
                    if (buf == null || buf.Length == 0) continue;

                    RoomReply r;
                    if (TryParseReply(Encoding.UTF8.GetString(buf), out r) && r.RoomKey == code)
                        found.Add(r);
                }
                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                if (cli != null)
                {
                    try { cli.Close(); } catch { }
                }
            }
            return found;
        }

        /// <summary>
        /// 挑一个"和对方同一网段"的本地地址作为应答里的 IP。
        /// <para>一台机器常常有多个网卡（有线 / 无线 / 虚拟机 / VPN），
        /// 随便回一个可能回的是对方根本连不上的那个（比如 VirtualBox 的 192.168.56.x）。
        /// 按"和询问方同子网"挑，命中率最高。</para>
        /// </summary>
        public static string PickLocalAddress(IPAddress peer)
        {
            if (peer == null) return "";
            try
            {
                var peerBytes = peer.GetAddressBytes();
                if (peerBytes.Length != 4) return "";
                foreach (var ip in LocalAddresses())
                {
                    var b = ip.GetAddressBytes();
                    if (b.Length != 4) continue;
                    // 同网段判定：这里只比前三位（/24），家用网络绝大多数是这个掩码
                    if (b[0] == peerBytes[0] && b[1] == peerBytes[1] && b[2] == peerBytes[2])
                        return ip.ToString();
                }
            }
            catch { }
            return "";
        }

        /// <summary>枚举本机 IPv4 地址（排除回环）。无网卡时返回空列表，不抛异常。</summary>
        public static List<IPAddress> LocalAddresses()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip == null) continue;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip)) continue;
                    list.Add(ip);
                }
            }
            catch { }
            return list;
        }

        private static void CloseSocket()
        {
            if (_socket == null) return;
            try { _socket.Close(); } catch { }
            _socket = null;
        }

        /// <summary>彻底复位（拆世界时调用，确保不留端口与残留状态）。</summary>
        public static void Reset()
        {
            StopListening();
            LastError = "";
        }
    }
}
