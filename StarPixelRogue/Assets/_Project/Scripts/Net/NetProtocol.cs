using System;

namespace StarPixel.Net
{
    /// <summary>会话模式：单机 / 房主(服务端+客户端) / 客户端。</summary>
    public enum NetMode
    {
        Offline = 0,
        Host = 1,
        Client = 2,
    }

    /// <summary>
    /// 联机协议常量（对应 TDD ADR-001：状态同步 + 半权威房主）。
    /// 所有联机相关数值集中在此，便于与《平衡模拟器》和服务器配置对齐。
    /// </summary>
    public static class NetProtocol
    {
        /// <summary>本地逻辑模拟频率（Unity FixedUpdate，Hz）。</summary>
        public const int SimulationTickRate = 60;

        /// <summary>网络状态广播频率（Hz）。房主以此频率向观战者推送世界状态。</summary>
        public const int NetworkTickRate = 30;

        /// <summary>状态广播间隔（秒）。</summary>
        public const float StateSendInterval = 1f / NetworkTickRate;

        /// <summary>
        /// AOI 兴趣管理半径（世界单位）。超出半径的敌人降频同步，避免千怪同屏时带宽爆炸。
        /// </summary>
        public const float InterestRadius = 18f;

        /// <summary>远处敌人的降频同步间隔（秒，约 10Hz）。</summary>
        public const float FarEntitySendInterval = 0.1f;

        /// <summary>默认联机端口。</summary>
        public const int DefaultPort = 7777;

        /// <summary>客户端移动预测的平滑系数（本地预测 → 权威位置校正）。</summary>
        public const float PredictionBlend = 0.25f;

        /// <summary>客户端开火请求的本地表现延迟（秒）：请求已上达房主但弹幕尚未同步回来时的兜底动画。</summary>
        public const float LocalFireCueTimeout = 0.15f;
    }

    /// <summary>
    /// 会话状态机：记录当前处于单机还是联机、是房主还是客户端。
    /// 纯 C# 静态类，不依赖 Fish-Net —— 未安装联机插件时本类型依然编译通过，单机路径零影响。
    /// </summary>
    public static class NetSession
    {
        /// <summary>当前会话模式。默认 Offline（保持 M1–M3.6 单机行为不变）。</summary>
        public static NetMode Mode { get; private set; } = NetMode.Offline;

        /// <summary>是否处于联机会话。</summary>
        public static bool IsNetworked => Mode != NetMode.Offline;

        /// <summary>是否房主（房主既是服务端也是客户端，拥有权威模拟）。</summary>
        public static bool IsHost => Mode == NetMode.Host;

        /// <summary>是否纯客户端（无权威，只发送输入与请求）。</summary>
        public static bool IsClient => Mode == NetMode.Client;

        /// <summary>
        /// M14 观战：本客户端是否以观战身份加入（不生成自己的角色，只观察对局）。
        /// 单机与房主恒为 false；仅「以观战身份加入」入口置 true，
        /// 由 <c>NetPlayer</c> 在生成时据此请求房主回收玩家对象。
        /// </summary>
        public static bool Spectator { get; private set; }

        /// <summary>设置观战身份（必须在发起连接前调用，否则玩家对象已生成）。</summary>
        public static void SetSpectator(bool on) => Spectator = on;

        /// <summary>是否运行权威模拟（房主）。敌人/伤害/经验/掉落均由权威方结算。</summary>
        public static bool IsAuthority => Mode != NetMode.Client;

        /// <summary>联机后端是否可用（安装了 Fish-Net 且定义了 FISHNET_ENABLED）。</summary>
        public static bool BackendAvailable => NetBackend.Instance != null;

        /// <summary>房间地址（房主=本机，客户端=输入的目标 IP）。</summary>
        public static string RemoteAddress = "127.0.0.1";

        public static string PlayerName = "Player";

        /// <summary>
        /// M11 断线重连：重连令牌。由客户端在每次开新局时生成并持久化于本静态类，
        /// 跨「掉线→重连」保持同一值——房主据此把断线前的进度快照匹配回同一玩家。
        /// 该令牌仅为区分同房间内不同玩家的重连请求，不含任何个人信息。
        /// </summary>
        public static string ReconnectToken { get; private set; }

        /// <summary>生成本局的重连令牌（开新房/加入房间时调用，确保与上一局残留快照解耦）。</summary>
        public static void GenerateReconnectToken()
        {
            ReconnectToken = System.Guid.NewGuid().ToString("N");
        }

        public static event Action<NetMode> ModeChanged;

        /// <summary>切换会话模式（由大厅 UI 或连接回调调用）。</summary>
        public static void SetMode(NetMode mode)
        {
            if (Mode == mode) return;
            Mode = mode;
            ModeChanged?.Invoke(mode);
        }

        /// <summary>回到单机模式（断线重连失败/退出房间时使用）。</summary>
        public static void ResetToOffline()
        {
            Mode = NetMode.Offline;
            Spectator = false;   // M14：回单机/断开时清掉观战身份，避免下次加入仍是观战
            ModeChanged?.Invoke(Mode);
        }

        /// <summary>格式化模式名（HUD 显示用）。</summary>
        public static string ModeLabel()
        {
            if (Spectator) return "观战";   // M14
            switch (Mode)
            {
                case NetMode.Host: return "房主";
                case NetMode.Client: return "客户端";
                default: return "单机";
            }
        }

        /// <summary>
        /// 环绕刃/光环旋转的同步时间源（M10 角度同步修复）。
        /// 联机时改用房主经 <c>NetWorldState</c> 广播的同步相位，使各客户端看到的远程玩家刃/光环角度与房主一致；
        /// 单机、未联网或回退路径使用本机 <c>Time.time</c>（与历史行为一致）。
        /// 此前环绕刃用各自机器的 <c>Time.time</c> 旋转，位置虽已同步但角度按本机时钟，
        /// 导致远程玩家刃/光环在房主与不同客户端间出现角度偏差（纯视觉）。
        /// </summary>
        public static float SyncedOrbitTime()
        {
#if FISHNET_ENABLED
            if (IsNetworked && NetWorldState.Instance != null) return NetWorldState.Instance.OrbitPhase;
#endif
            return UnityEngine.Time.time;
        }
    }
}
