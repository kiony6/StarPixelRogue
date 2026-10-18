#if FISHNET_ENABLED
using System.Collections.Generic;
using StarPixel.Player;

namespace StarPixel.Net
{
    /// <summary>
    /// M11 断线重连：主机侧断线玩家进度快照表。
    /// 远程玩家掉线时由 <see cref="NetPlayer.OnStopServer"/> 按重连令牌暂存其进度，
    /// 该玩家重连时由 <see cref="NetPlayer"/> 的 ServerRpc 取出并重放。
    /// 仅房主进程持有；开新房时 <see cref="NetReconnectStore.ClearAll"/> 清空上一局残留。
    /// </summary>
    public static class NetReconnectStore
    {
        private static readonly Dictionary<string, PlayerController.PlayerReconnectState> _snaps =
            new Dictionary<string, PlayerController.PlayerReconnectState>();

        public static void Put(string token, PlayerController.PlayerReconnectState state)
        {
            if (string.IsNullOrEmpty(token)) return;
            _snaps[token] = state;
        }

        public static bool TryGet(string token, out PlayerController.PlayerReconnectState state)
        {
            if (!string.IsNullOrEmpty(token) && _snaps.TryGetValue(token, out state)) return true;
            state = default;
            return false;
        }

        public static void Consume(string token)
        {
            if (!string.IsNullOrEmpty(token)) _snaps.Remove(token);
        }

        public static void ClearAll() => _snaps.Clear();
    }
}
#endif
