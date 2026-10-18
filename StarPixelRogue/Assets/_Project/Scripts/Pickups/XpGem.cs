using UnityEngine;

namespace StarPixel.Pickups
{
    /// <summary>
    /// 经验宝石：敌人死亡时掉落，靠近玩家被磁吸拾取（对应 TDD M2 升级循环）。
    /// 由 PickupManager 对象池管理。
    /// </summary>
    public class XpGem : MonoBehaviour
    {
        public float xp;
        private GameObject _template;

        public void Init(Vector3 pos, float amount, GameObject template)
        {
            _template = template;
            xp = amount;
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
