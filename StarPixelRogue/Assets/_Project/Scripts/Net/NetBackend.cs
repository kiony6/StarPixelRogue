using System;
using UnityEngine;   // GameObject/Vector3/Quaternion（M17 实测修复：此前缺失导致 NetBackend 无法编译）
#if FISHNET_ENABLED
using FishNet;
#endif

namespace StarPixel.Net
{
    /// <summary>
    /// 联机后端抽象。未安装 Fish-Net 时 <see cref="Instance"/> 为 null，
    /// 游戏保持纯单机（与 M1–M3.6 行为完全一致）；安装并定义 FISHNET_ENABLED 后自动切换到 Fish-Net 实现。
    /// </summary>
    public interface INetBackend
    {
        /// <summary>后端是否可用。</summary>
        bool Available { get; }

        /// <summary>以房主身份启动（同时起服务端与本地客户端）。</summary>
        void StartHost();

        /// <summary>以客户端身份连接目标地址。</summary>
        void StartClient(string address);

        /// <summary>停止当前会话。</summary>
        void Stop();

        /// <summary>本地客户端连接就绪（房主启动成功或客户端连上）——触发世界构建。</summary>
        event Action Ready;

        /// <summary>M11 断线重连：在不重启服务端的前提下重连本地客户端（房主本地客户端掉线、或客户端掉线后重试）。</summary>
        void RetryLastConnection();
    }

    /// <summary>后端解析器：编译期隔离 Fish-Net 依赖，运行期二次确认程序集真实存在。</summary>
    public static class NetBackend
    {
        public static INetBackend Instance { get; } = Create();

        private static INetBackend Create()
        {
#if FISHNET_ENABLED
            // 定义了符号但包未安装（或未解析）时降级，保证工程仍可编译运行单机
            if (!FishNetTypeAvailable()) return null;
            return new FishNetBackend();
#else
            return null;
#endif
        }

        /// <summary>
        /// M18 双开实测修复（重要）：Fish-Net 4.x 的运行时程序集名为 **FishNet.Runtime**，
        /// 而旧写法写死的是 `Type.GetType("FishNet.Object.NetworkObject, FishNet")`——恒为 null。
        /// 后果：即使定义了 FISHNET_ENABLED，<see cref="Instance"/> 也永远是空的，
        /// 联机入口形同虚设、游戏一直走纯单机（M4.5 起就存在，直到双开实测才暴露）。
        /// 改为按「类型全名」在已加载程序集中查找，不再依赖写死的程序集名，兼容后续版本改名。
        /// </summary>
        private static bool FishNetTypeAvailable()
        {
            const string TypeName = "FishNet.Object.NetworkObject";
            try
            {
                if (Type.GetType(TypeName) != null) return true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    if (asm.GetType(TypeName, false) != null) return true;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// 网络对象生成/回收的唯一收口。
    /// Fish-Net 各版本的 Spawn 签名略有差异（V4 起统一为
    /// <c>ServerManager.Spawn(GameObject, NetworkConnection owner, Scene)</c>），
    /// 如升级插件后遇编译错误只需改这一个文件。
    /// </summary>
    public static class NetSpawner
    {
        /// <summary>
        /// 生成网络对象。
        /// - <paramref name="beforeSpawn"/>：Instantiate 之后、网络 Spawn 之前调用。
        ///   **只能写非 SyncVar 的本地字段**——此时 NetworkObject 尚未完成网络初始化。
        /// - <paramref name="afterSpawn"/>：网络 Spawn 之后调用，**写 SyncVar 必须放这里**。
        ///
        /// M18 双开实测修复（重要）：此前把权威初始值（NetEnemy.ServerInit 等）放在 beforeSpawn，
        /// 意图是"让初始 SyncVar 随生成包一起送达"。但 Fish-Net 4.x 下此时
        /// SyncBase.IsNetworkInitialized 为 false，写 SyncVar 会抛 NullReferenceException——
        /// 实测 60 秒刷了 5.7 万条异常，敌人一个都生成不出来。
        /// 改为 afterSpawn 后：正确性恢复（客户端经 OnChange 修正表现，代价是可能有一帧默认外观）。
        /// </summary>
        public static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation,
            Action<GameObject> beforeSpawn = null, Action<GameObject> afterSpawn = null)
        {
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
            beforeSpawn?.Invoke(go);
#if FISHNET_ENABLED
            var no = go.GetComponent<FishNet.Object.NetworkObject>();
            if (no != null)
            {
                var server = InstanceFinder.ServerManager;
                if (server != null)
                {
                    server.Spawn(go, null);

                    /* M18 双开实测修复（防御层）：网络生成可能静默失败——
                     * 例如 SpawnablePrefabs 为空时 PrefabId 恒为 65535，
                     * NetworkObject 既拿不到 ObjectId 也不会初始化，
                     * 于是 NetworkBehaviour 内部的 NetworkObject 引用始终为 null，
                     * 之后每帧访问 IsServerInitialized / 写 SyncVar 都抛 NullReferenceException。
                     * 实测单个进程 60 秒刷出 213MB / 5.7 万条异常，磁盘直接写满。
                     *
                     * 生成失败时这里直接销毁对象、只告警一次：宁可这批实体不出现，
                     * 也不能让 Update 每帧刷异常把日志撑爆、掩盖真正的根因。 */
                    if (!no.IsSpawned)
                    {
                        if (!_spawnFailedWarned)
                        {
                            _spawnFailedWarned = true;
                            Debug.LogError("[Net] 网络生成失败（" + prefab.name + " 未获得 ObjectId）。" +
                                           "最常见原因是 NetworkManager 的 SpawnablePrefabs 未包含该预制体——" +
                                           "请执行菜单 StarPixel/Setup FishNet 重新装配，再重新构建。");
                        }
                        UnityEngine.Object.Destroy(go);
                        return null;
                    }
                }
                else Debug.LogWarning("[Net] ServerManager 不可用，跳过网络生成（对象仅在本地存在）");
            }
#endif
            afterSpawn?.Invoke(go);   // 网络 Spawn 成功之后：此刻写 SyncVar 才安全
            return go;
        }

        /// <summary>生成失败只告警一次，避免刷屏。</summary>
        private static bool _spawnFailedWarned;

        public static void Despawn(GameObject go)
        {
            if (go == null) return;
#if FISHNET_ENABLED
            var no = go.GetComponent<FishNet.Object.NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
#endif
        }
    }
}
