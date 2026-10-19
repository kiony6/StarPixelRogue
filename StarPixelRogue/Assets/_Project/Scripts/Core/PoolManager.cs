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

        /// <summary>
        /// 本池**创建过的全部实例**（含仍在场上和已归还到队列里的）。
        /// 为什么需要：这些克隆体挂在场景根节点，既不在 GameBuilder 的 Track 列表里，
        /// 也不是任何管理器的子物体。拆世界时只销毁 PoolManager 自身的话，
        /// 它们会全部残留到下一局（M24 实测：宝石/回血球/敌人整批漏是新世界）。
        /// </summary>
        private readonly List<GameObject> _owned = new List<GameObject>();

        /// <summary>从池中取一个实例（无则克隆模板）。</summary>
        public GameObject Get(GameObject template)
        {
            int key = template.GetInstanceID();
            if (!_pools.TryGetValue(key, out var queue))
            {
                queue = new Queue<GameObject>();
                _pools[key] = queue;
            }
            GameObject go = queue.Count > 0 ? queue.Dequeue() : Track(Object.Instantiate(template));
            go.SetActive(true);
            return go;
        }

        private GameObject Track(GameObject go)
        {
            _owned.Add(go);
            return go;
        }

        public void Release(GameObject template, GameObject instance)
        {
            int key = template.GetInstanceID();
            if (!_pools.ContainsKey(key)) _pools[key] = new Queue<GameObject>();
            instance.SetActive(false);
            _pools[key].Enqueue(instance);
        }

        /// <summary>
        /// 拆世界时调用：销毁本池创建过的**每一个**实例并清空所有队列。
        /// 只 Destroy(PoolManager) 是不够的——克隆体自身才是占地方的那部分。
        /// </summary>
        public void DestroyAll()
        {
            foreach (var go in _owned)
                if (go != null) Object.Destroy(go);
            _owned.Clear();
            _pools.Clear();
        }
    }
}
