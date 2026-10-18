using UnityEngine;

namespace StarPixel.Pickups
{
    /// <summary>
    /// 回血球：敌人/精英/Boss 掉落，靠近玩家被磁吸拾取并回复生命（对应 TDD M3.5 生存手感）。
    /// 由 PickupManager 对象池管理。
    /// </summary>
    public class HealthPickup : MonoBehaviour
    {
        public float healAmount;
        private GameObject _template;

        public void Init(Vector3 pos, float amount, GameObject template)
        {
            _template = template;
            healAmount = amount;
            transform.position = pos;
            gameObject.SetActive(true);
        }

        public void Collect()
        {
            if (_template != null) Core.PoolManager.Instance.Release(_template, gameObject);
            else gameObject.SetActive(false);
        }
    }
}
