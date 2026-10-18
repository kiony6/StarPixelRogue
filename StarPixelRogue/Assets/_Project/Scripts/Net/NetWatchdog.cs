#if FISHNET_ENABLED
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 联机生成看门狗（M4.5 实机排错用）：
    /// 进入联机世界后若 <see cref="NetPlayer.Local"/> 迟迟未出现，说明玩家预制体没有被
    /// PlayerSpawner 生成（忘配预制体 / 场景不对 / 插件未装）——此时在 Console 输出
    /// 带排查步骤的明确错误，避免「联机进了房间但什么都没有」的静默失败。
    /// </summary>
    public class NetWatchdog : MonoBehaviour
    {
        private float _elapsed;
        private bool _reported;

        void Update()
        {
            if (_reported) return;
            _elapsed += Time.deltaTime;
            if (NetPlayer.Local != null)
            {
                _reported = true;
                Destroy(this);
                return;
            }
            if (_elapsed < 6f) return;

            _reported = true;
            var cfg = NetConfig.Instance;
            Debug.LogError(
                "[StarPixel] 联机世界已构建但 6 秒内未生成本地玩家。请检查：\n" +
                "1) 是否打开的是 Assets/Scenes/NetBootstrap.unity（含 NetworkManager + NetConfig + PlayerSpawner）；\n" +
                "2) NetworkManager 上的 PlayerSpawner 是否指定了 NetPlayer 预制体；\n" +
                "3) NetConfig.playerPrefab 是否为 null：" + (cfg == null ? "NetConfig 缺失" : (cfg.playerPrefab == null ? "是" : "否")) + "；\n" +
                "4) 控制台是否有 Fish-Net 的连接/认证错误。\n" +
                "修复后重新 Play，或先用菜单 StarPixel/Setup FishNet 重新装配。");
        }
    }
}
#endif
