#if FISHNET_ENABLED
using System;
using FishNet.Managing;
using FishNet.Managing.Object;      // M18：DefaultPrefabObjects（运行时补注册可生成预制体）
using FishNet.Object;               // M18：NetworkObject
using FishNet.Transporting;
using FishNet.Transporting.Tugboat; // M18：Tugboat（兜底传输层）
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// Fish-Net 后端实现（对应 TDD ADR-001：状态同步 + 半权威房主）。
    /// 仅在定义了脚本符号 FISHNET_ENABLED 且 Fish-Net 包可用时编译/生效。
    ///
    /// 权威模型：
    /// - 房主（Host）运行权威模拟：敌人/波次/Boss/掉落/伤害/经验全部在房主侧结算；
    /// - 客户端只发送输入（移动、开火请求、选卡、拾取请求），不做权威判定；
    /// - 客户端本地移动为「客户端权威 + 本地预测」（NetworkTransform 默认 _clientAuthoritative=true），
    ///   其余实体由房主状态广播驱动。
    ///
    /// 玩家对象由 Fish-Net 的 PlayerSpawner 组件在客户端加载完初始场景后自动生成
    /// （见 NetSetupEditor 装配脚本），无需手工 Spawn。
    /// </summary>
    public sealed class FishNetBackend : INetBackend
    {
        public bool Available => true;

        public event Action Ready;

        private NetworkManager _nm;
        private bool _intentHost;

        // —— M11 断线重连状态 ——
        private bool _intentionalStop;           // 主动离开（回大厅/退出）时不自动重连
        private const int MaxReconnectAttempts = 5;
        private const float ReconnectDelay = 1.5f;

        public void StartHost()
        {
            EnsureNetworkManager();
            if (_nm == null) return;
            _intentHost = true;
            _intentionalStop = false;
            NetSession.GenerateReconnectToken();
            NetReconnectStore.ClearAll();         // 房主：清空上一局残留的进度快照
            NetReconnect.Ensure()?.ResetState();

            // 房主 = 服务端 + 本地客户端同时启动
            _nm.ServerManager.StartConnection();
            _nm.ClientManager.StartConnection();
            // 模式在本地客户端连接就绪回调里置为 Host（见 OnClientConnectionState）
        }

        public void StartClient(string address)
        {
            EnsureNetworkManager();
            if (_nm == null) return;
            _intentHost = false;
            _intentionalStop = false;
            NetSession.GenerateReconnectToken();
            NetReconnect.Ensure()?.ResetState();

            var transport = _nm.TransportManager != null ? _nm.TransportManager.Transport : null;
            if (transport != null) transport.SetClientAddress(address);
            _nm.ClientManager.StartConnection();
            // 模式在连接就绪回调里置为 Client
        }

        /// <summary>M11 断线重连：在保留服务端的前提下重连本地客户端（房主=localhost，客户端=上次地址）。</summary>
        public void RetryLastConnection()
        {
            EnsureNetworkManager();
            if (_nm == null) return;
            var transport = _nm.TransportManager != null ? _nm.TransportManager.Transport : null;
            if (transport != null) transport.SetClientAddress(_intentHost ? "127.0.0.1" : NetSession.RemoteAddress);
            _nm.ClientManager.StartConnection();
            // 模式在连接就绪回调里按 _intentHost 重新置位
        }

        /// <summary>
        /// M18 实测专用钩子：模拟一次「意外掉线」——只停本地客户端连接，且**不**置
        /// <c>_intentionalStop</c>，从而走真实的 M11 自动重连路径
        /// （Stopped → 拆本地镜像 → NetReconnect → RetryLastConnection → Ready → 重建）。
        ///
        /// 为什么不能直接调 <see cref="Stop"/>：Stop 表示「主动离开」，会置 intentionalStop
        /// 并退订连接状态事件，重连分支根本不会进入——那样测的不是 M11，而是"退出房间"。
        /// 正常游戏流程不调用本方法。
        /// </summary>
        public void SimulateDrop()
        {
            EnsureNetworkManager();
            if (_nm == null) return;
            _intentionalStop = false;
            try
            {
                if (_nm.IsClientStarted) _nm.ClientManager.StopConnection();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Net] 模拟掉线失败: " + e.Message);
            }
        }

        public void Stop()
        {
            _intentionalStop = true;
            NetReconnect.Ensure()?.Cancel();
            if (_nm == null) return;
            try { _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState; } catch { }
            try { if (_nm.IsServerStarted) _nm.ServerManager.StopConnection(true); }
            catch (Exception e) { Debug.LogWarning("[Net] 停止服务端失败: " + e.Message); }
            try { if (_nm.IsClientStarted) _nm.ClientManager.StopConnection(); }
            catch (Exception e) { Debug.LogWarning("[Net] 停止客户端失败: " + e.Message); }
            // 清空引用：下次 Start 时重新查找 NetworkManager 并重新订阅，
            // 否则二次建房/加入将收不到连接就绪事件（Ready 永远不触发）。
            _nm = null;
        }

        /// <summary>确保场景中存在 NetworkManager（编辑器装配脚本会生成带完整配置的场景；此处为兜底）。</summary>
        private void EnsureNetworkManager()
        {
            if (_nm != null) return;
            _nm = UnityEngine.Object.FindObjectOfType<NetworkManager>();

            if (_nm == null)
            {
                /* M18 双开实测修复（重要）：旧写法直接 AddComponent<NetworkManager>()，
                 * 而 NetworkManager.Awake() 第一件事就是校验 SpawnablePrefabs——为 null 时
                 * **直接 return**，导致 ClientManager/ServerManager 等组件全都不创建。
                 * 结果兜底出来的 NM 是个"空壳"：_nm.ClientManager 为 null，
                 * 下一行订阅事件就 NullReferenceException，房主根本起不来。
                 *
                 * 修法：先在**未激活**的物体上加组件（Awake 不会执行），
                 * 把预制体集合与传输层配好后再激活，让 Awake 能顺利通过校验。
                 * 兜底场景仍属异常配置，故同时输出明确指引。 */
                var go = new GameObject("[NetworkManager]");   // @owner: app
                go.SetActive(false);
                _nm = go.AddComponent<NetworkManager>();
                EnsureSpawnablesPopulated(_nm);
                AddFallbackTransport(_nm);
                go.SetActive(true);
                Debug.LogWarning("[Net] 运行时兜底创建 NetworkManager（未配置 PlayerSpawner，请改用菜单 " +
                                 "StarPixel/Setup FishNet 生成 NetBootstrap 场景以获得完整联机能力）");
            }
            else
            {
                // 场景里自带的 NM 也要自检：预制体集合可能是空的（编辑器装配漏配时）
                EnsureSpawnablesPopulated(_nm);
            }

            if (_nm.ClientManager == null)
            {
                Debug.LogError("[Net] NetworkManager 初始化不完整（ClientManager 为空）——" +
                               "通常意味着 SpawnablePrefabs 未配置。请执行菜单 StarPixel/Setup FishNet 后重新进入。");
                _nm = null;
                return;
            }
            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        /// <summary>
        /// M18 双开实测修复（关键）：确保 SpawnablePrefabs 非空。
        ///
        /// 实测根因：编辑器装配脚本把 7 个预制体 AddObject 进 DefaultPrefabObjects 后**没有落盘**，
        /// 构建拿到的是 <c>_prefabs: []</c>。于是服务端 Spawn 时 PrefabId 恒为 65535（未分配），
        /// 客户端解析生成包报「PrefabId 65535 is out of range」，NetworkObject 也从未初始化，
        /// 进而 NetworkBehaviour 拿不到 NetworkObject、所有 SyncVar 读写 NullReferenceException。
        ///
        /// 这里在**运行时**用 NetConfig（场景里现成的预制体引用）兜底补注册，
        /// 使联机在"资产漏配"的情况下仍能跑起来，而不是刷几万条异常后静默失败。
        /// AddObject 在运行时会触发 InitializePrefabRange，预制体的 PrefabId 立即分配。
        /// </summary>
        private static void EnsureSpawnablesPopulated(NetworkManager nm)
        {
            if (nm == null) return;

            if (nm.SpawnablePrefabs == null)
            {
                nm.SpawnablePrefabs = ScriptableObject.CreateInstance<DefaultPrefabObjects>();
                Debug.LogWarning("[Net] SpawnablePrefabs 为空，已运行时创建 DefaultPrefabObjects 实例");
            }

            if (nm.SpawnablePrefabs.GetObjectCount() > 0) return;

            var cfg = NetConfig.Instance;
            if (cfg == null) return;

            var sources = new[]
            {
                cfg.playerPrefab, cfg.enemyPrefab, cfg.gemPrefab, cfg.healthPrefab,
                cfg.playerProjectilePrefab, cfg.enemyProjectilePrefab, cfg.worldStatePrefab
            };

            int added = 0;
            foreach (var prefab in sources)
            {
                if (prefab == null) continue;
                var nob = prefab.GetComponent<NetworkObject>();
                if (nob == null)
                {
                    Debug.LogWarning("[Net] 预制体缺少 NetworkObject，无法注册为可生成对象：" + prefab.name);
                    continue;
                }
                nm.SpawnablePrefabs.AddObject(nob, false, true);
                added++;
            }

            if (added > 0)
                Debug.LogWarning("[Net] SpawnablePrefabs 运行时补注册 " + added + " 个预制体" +
                                 "（说明资产未正确生成，建议重跑 StarPixel/Setup FishNet）");
        }

        /// <summary>兜底 NM 的传输层：没有 transport 时 StartConnection 会直接失败，联机无从谈起。</summary>
        private static void AddFallbackTransport(NetworkManager nm)
        {
            if (nm == null) return;
            if (nm.GetComponent<Tugboat>() != null) return;
            nm.gameObject.AddComponent<Tugboat>();
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            switch (args.ConnectionState)
            {
                case LocalConnectionState.Started:
                    // 本地客户端就绪：身份由启动方式（建房/加入）决定
                    NetSession.SetMode(_intentHost ? NetMode.Host : NetMode.Client);
                    Ready?.Invoke();
                    break;

                case LocalConnectionState.Stopped:
                    // 主动离开（回大厅/退出）：回到单机态并通知大厅重建 UI
                    if (_intentionalStop)
                    {
                        NetSession.ResetToOffline();
                        NetLobby.NotifyDisconnected();
                        break;
                    }

                    // M11 断线重连：意外掉线——进入自动重连，尽量保留当前对局。
                    // 房主本地客户端掉线但服务端仍在运行：仅重连本地客户端，权威世界（敌人/波次/Boss）保留，不拆；
                    // 纯客户端掉线：先拆本地镜像，重连成功后由 Ready 重新 Build 同一局。
                    var reconnect = NetReconnect.Ensure();
                    if (reconnect == null)
                    {
                        NetSession.ResetToOffline();
                        NetLobby.NotifyDisconnected();
                        break;
                    }
                    if (_intentHost)
                    {
                        reconnect.BeginReconnect(true, "127.0.0.1", MaxReconnectAttempts, ReconnectDelay);
                    }
                    else
                    {
                        GameBuilder.TeardownWorld();   // 拆本地镜像，重连后由 Ready 重建
                        reconnect.BeginReconnect(false, NetSession.RemoteAddress, MaxReconnectAttempts, ReconnectDelay);
                    }
                    break;
            }
        }
    }
}
#endif
