using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 泛型单例基类（对应 TDD 3.2 的管理器框架）。
    /// 继承后通过 T.Instance 访问，全程唯一且跨场景保留。
    /// M5：实现 <see cref="ISingletonInstance"/>——GameBuilder.TeardownWorld 拆世界时
    /// 同步清理 Instance，避免「Destroy 已延迟到帧末、Instance 仍非空」导致重建被守卫拦住的时序问题。
    /// </summary>
    public interface ISingletonInstance
    {
        void ClearInstance();
    }

    public abstract class MonoSingleton<T> : MonoBehaviour, ISingletonInstance where T : MonoSingleton<T>
    {
        public static T Instance { get; private set; }

        protected virtual void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = (T)this;
        }

        protected virtual void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>拆世界时同步调用：立即解除 Instance 引用（不等帧末 OnDestroy）。</summary>
        public void ClearInstance()
        {
            if (Instance == this) Instance = null;
        }
    }
}
