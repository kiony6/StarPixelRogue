using System;
using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>目录里的一条房间记录。</summary>
    public sealed class RoomEntry
    {
        /// <summary>
        /// 组队码（已规范化，含校验位）。
        /// <para>字段名不叫 Code：<c>StarPixel.Core</c> 里已有一个 <c>Code</c> 类型
        /// （账号错误码枚举）。同名的字段会让静态检查把它认成类型引用，
        /// 更实际的风险是将来在这个文件里写 <c>Code.Ok</c> 时指的是谁会变得含糊。</para>
        /// </summary>
        public string RoomKey;

        /// <summary>可连接的主机地址（IP 或平台中继的房间标识，取决于 <see cref="JoinTarget"/> 的实现）。</summary>
        public string Address;

        public int Port;

        /// <summary>房主显示名（UI 上用于"你要进的是谁的房间"）。</summary>
        public string HostName;

        /// <summary>当前人数（展示用，不参与匹配）。</summary>
        public int Players;

        /// <summary>最后一次见到它的时间（<see cref="Time.unscaledTime"/>）。</summary>
        public float LastSeen;

        /// <summary>是否是本机开的房。本机那条不参与过期清理——它自己知道还活着。</summary>
        public bool IsLocal;

        public RoomEntry(string code, string address, int port, string hostName, int players, float now, bool isLocal)
        {
            RoomKey = code;
            Address = address;
            Port = port;
            HostName = hostName;
            Players = players;
            LastSeen = now;
            IsLocal = isLocal;
        }
    }

    /// <summary>
    /// 房间目录：组队码 → 房间记录。
    /// <para>
    /// 它同时承担两个身份，别搞混：
    /// ① 房主侧——自己开的房登记进来，供别人发现；
    /// ② 客户端侧——发现到的远端房间缓存，供加入时查。
    /// </para>
    /// <para>
    /// 目录**不是**权威：它只是"最近听说过的房间"。真正的可用性由连接结果说话。
    /// 所以每条记录都带心跳时间，超时就当作房主已经关了游戏扔掉——
    /// 否则玩家会看到一个永远连不上的幽灵房间，而那比"搜不到"更让人困惑。
    /// </para>
    /// </summary>
    public static class RoomDirectory
    {
        /// <summary>默认存活时长（秒）。超过这么久没再收到心跳就移除。</summary>
        public const float DefaultTtl = 30f;

        private static readonly Dictionary<string, RoomEntry> _rooms =
            new Dictionary<string, RoomEntry>(StringComparer.Ordinal);

        /// <summary>本机开的房的码（未开房为 null）。</summary>
        public static string LocalCode { get; private set; }

        /// <summary>目录里的房间数（探针用）。</summary>
        public static int Count => _rooms.Count;

        /// <summary>当前时间源。用 unscaledTime 是因为结算时 timeScale 会归零，而网络不关心那个。</summary>
        public static float Now => Time.unscaledTime;

        /// <summary>
        /// 登记/刷新一条房间记录。已存在则更新地址与心跳（房主可能换了网卡或重启了游戏）。
        /// </summary>
        public static RoomEntry Put(string code, string address, int port, string hostName, int players, bool isLocal)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var e = new RoomEntry(code, address, port, hostName, players, Now, isLocal);
            _rooms[code] = e;
            if (isLocal) LocalCode = code;
            return e;
        }

        /// <summary>刷新心跳（房主每隔几秒调一次，或客户端再次发现同一房间时）。</summary>
        public static bool Touch(string code, int players = -1)
        {
            RoomEntry e;
            if (string.IsNullOrEmpty(code) || !_rooms.TryGetValue(code, out e)) return false;
            e.LastSeen = Now;
            if (players >= 0) e.Players = players;
            return true;
        }

        /// <summary>按码查找（未规范化，调用方应先用 <see cref="RoomCode.Normalize"/>）。</summary>
        public static RoomEntry Find(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            RoomEntry e;
            return _rooms.TryGetValue(code, out e) ? e : null;
        }

        /// <summary>移除一条（房主关房、或客户端确认连不上）。</summary>
        public static bool Forget(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (!_rooms.Remove(code)) return false;
            if (LocalCode == code) LocalCode = null;
            return true;
        }

        /// <summary>
        /// 清理过期房间，返回被清掉的数量。
        /// <para>本机那条永远不清理——它自己知道还活着，靠 TTL 判会把自己误杀。</para>
        /// </summary>
        public static int Prune(float ttl = DefaultTtl)
        {
            float now = Now;
            var dead = new List<string>();
            foreach (var kv in _rooms)
            {
                if (kv.Value.IsLocal) continue;
                if (now - kv.Value.LastSeen > ttl) dead.Add(kv.Key);
            }
            foreach (var c in dead) _rooms.Remove(c);
            return dead.Count;
        }

        /// <summary>清空目录（拆世界/回主菜单时调用，避免上一局的房间残留成幽灵）。</summary>
        public static void Reset()
        {
            _rooms.Clear();
            LocalCode = null;
        }

        /// <summary>
        /// 生成一个**当前目录里没被占用**的码。
        /// <para>为什么需要：码的空间是有限的（32^5 ≈ 3355 万），两个房撞码虽然罕见但一旦发生，
        /// 玩家会进错房间——这种偶发问题最难查，所以在生成时就避开。</para>
        /// </summary>
        public static string NewUnusedCode(int maxTries = 32)
        {
            for (int i = 0; i < maxTries; i++)
            {
                var c = RoomCode.Generate();
                if (Find(c) == null) return c;
            }
            return RoomCode.Generate();     // 极端情况下兜底：宁可撞也不卡住开房流程
        }
    }
}
