using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 联机预制体配置（挂在 NetBootstrap 场景中的普通物体上，非网络对象）。
    /// 由编辑器菜单「StarPixel/Setup FishNet」一键生成并接线；
    /// 单机模式下该配置不存在，<see cref="NetWorld"/> 全部走本地对象池路径。
    /// </summary>
    public class NetConfig : MonoBehaviour
    {
        public static NetConfig Instance;

        [Header("玩家")]
        public GameObject playerPrefab;          // 含 NetworkObject + NetPlayer + PlayerController

        [Header("世界实体（房主权威生成）")]
        public GameObject enemyPrefab;           // 含 NetworkObject + NetEnemy + Damageable
        public GameObject gemPrefab;             // 含 NetworkObject + NetPickup(经验)
        public GameObject healthPrefab;          // 含 NetworkObject + NetPickup(回血)
        public GameObject playerProjectilePrefab;// 含 NetworkObject + NetProjectile(玩家弹)
        public GameObject enemyProjectilePrefab; // 含 NetworkObject + NetProjectile(敌方弹)
        public GameObject worldStatePrefab;      // 含 NetworkObject + NetWorldState

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
