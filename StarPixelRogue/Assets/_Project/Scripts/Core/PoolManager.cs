using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 通用 GameObject 对象池（对应 TDD 4.2 / 7 的性能要求）。
    /// 以模板 GameObject 的 instanceID 为键，避免运行时频繁 Instantiate/Destroy 造成 GC 抖动。
    /// </summary>
    public class PoolManager : MonoSingleton<PoolManager>
    {
        private readonly Dictionary<int, Queue<GameObject>> _pools = new Dictionary<int, Queue<GameObject>>();

        /// <summary>从池中取一个实例（无则克隆模板）。</summary>
        public GameObject Get(GameObject template)
        {
            int key = template.GetInstanceID();
            if (!_pools.TryGetValue(key, out var queue))
            {
                queue = new Queue<GameObject>();
                _pools[key] = queue;
            }
            GameObject go = queue.Count > 0 ? queue.Dequeue() : Object.Instantiate(template);
            go.SetActive(true);
            return go;
        }

        /// <summary>归还实例到对应模板的池。</summary>
        public void Release(GameObject template, GameObject instance)
        {
            int key = template.GetInstanceID();
            if (!_pools.ContainsKey(key)) _pools[key] = new Queue<GameObject>();
            instance.SetActive(false);
            _pools[key].Enqueue(instance);
        }
    }
}
