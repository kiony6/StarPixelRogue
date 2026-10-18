#if FISHNET_ENABLED
using FishNet.Object;
using FishNet.Object.Synchronizing;
using StarPixel.Core;
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 世界状态同步对象（联机模式）：波次 / 击杀 / 区域，由房主写入、客户端读取后转 EventBus，
    /// 使 HUD 在两种模式下完全复用同一套代码。Boss 血量走 <see cref="NetEnemy"/> 自身 SyncVar。
    /// </summary>
    public class NetWorldState : NetworkBehaviour
    {
        private readonly SyncVar<int> _wave = new SyncVar<int>(1);
        private readonly SyncVar<int> _kills = new SyncVar<int>(0);
        private readonly SyncVar<int> _zone = new SyncVar<int>(1);
        // M10：环绕刃/光环旋转相位（房主写入、客户端读取），用于消除联机下远程玩家刃/光环角度偏差
        private readonly SyncVar<float> _orbitPhase = new SyncVar<float>(0f);

        private int _lastKills;

        public static NetWorldState Instance;

        /// <summary>M10：环绕刃/光环旋转的同步时间源（房主 Time.time 经网络广播，
        /// 各客户端据此旋转，角度与房主一致）。单机或未联网时此对象不存在，调用方需回退本机时间。</summary>
        public float OrbitPhase => _orbitPhase.Value;

        /// <summary>M18 联机双开实测：只读快照。房主与客户端读的是同一批 SyncVar，
        /// 双开脚本据此交叉核对两端状态是否一致（即房主权威同步链路是否真的在工作）。</summary>
        public int Wave => _wave.Value;
        public int Kills => _kills.Value;
        public int Zone => _zone.Value;

        public override void OnStartClient()
        {
            base.OnStartClient();
            Instance = this;
            // 守卫说明：房主会同时收到 asServer=true / asServer=false 两次回调；
            // 房主侧的波次/击杀已由 EnemyManager/NetEnemy 的本地路径驱动 HUD，
            // 这里只放行纯客户端，否则房主击杀数/波次提示会双份触发。
            _wave.OnChange += (prev, next, asServer) => { if (!asServer && !IsServerInitialized) EventBus.RaiseWaveChanged(next); };
            _zone.OnChange += (prev, next, asServer) => { if (!asServer && !IsServerInitialized) EventBus.RaiseZoneChanged(next); };
            _kills.OnChange += OnKillsChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            // 房主侧累计击杀并广播（联机专用；单机无此对象）
            Core.EventBus.OnEnemyKilled += OnLocalEnemyKilled;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            Core.EventBus.OnEnemyKilled -= OnLocalEnemyKilled;
        }

        private void OnLocalEnemyKilled(int amount) => ServerAddKills(amount);

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (Instance == this) Instance = null;
        }

        // M10：房主每帧把本地时间写入同步相位，客户端据此旋转环绕刃/光环，消除跨机器角度偏差
        void Update()
        {
            if (IsServerInitialized) _orbitPhase.Value = Time.time;
        }

        /// <summary>击杀数以增量广播（HUD 按 +1 累计，与服务端语义保持一致）。</summary>
        private void OnKillsChanged(int prev, int next, bool asServer)
        {
            if (asServer || IsServerInitialized) return;   // 房主侧已本地 +1，防双计
            int delta = Mathf.Max(0, next - prev);
            for (int i = 0; i < delta; i++) EventBus.RaiseEnemyKilled(1);
        }

        // —— 房主侧写入 ——
        [Server]
        public void ServerSetWave(int wave) { if (IsServerInitialized) _wave.Value = wave; }

        [Server]
        public void ServerSetZone(int zone) { if (IsServerInitialized) _zone.Value = zone; }

        /// <summary>房主累计击杀（订阅本地 EventBus 后转发）。</summary>
        [Server]
        public void ServerAddKills(int amount)
        {
            if (!IsServerInitialized) return;
            _kills.Value = Mathf.Max(0, _kills.Value + amount);
        }

        void Awake()
        {
            if (Instance == null) Instance = this;
        }
    }
}
#endif
