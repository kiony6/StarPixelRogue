using System;
using System.Collections;

namespace StarPixel.Core.CloudSave
{
    /// <summary>
    /// 云存档后端抽象（M13）：把「存档怎么传」与「存档怎么裁决」解耦。
    /// 替换后端（自建 REST / Supabase / 云托管等）只需实现本接口，业务代码零改动。
    /// 约定：
    /// - 以协程驱动，回调在协程结束后于主线程触发；
    /// - 失败不抛异常，只回传原因字符串，由 <see cref="CloudSaveService"/> 降级为纯本地存档；
    /// - <see cref="Configured"/> 为 false 时云存档整体禁用，游戏行为与单机完全一致。
    /// </summary>
    public interface ICloudSaveProvider
    {
        /// <summary>是否已配置可用（未配置即为禁用）。</summary>
        bool Configured { get; }

        /// <summary>
        /// 拉取存档。onDone(是否成功, 成功时为 JSON 文本；云端无存档时成功且返回空串；失败时为错误原因)。
        /// </summary>
        IEnumerator Download(string slotId, Action<bool, string> onDone);

        /// <summary>上传存档（整体覆盖）。onDone(是否成功, 失败时为错误原因)。</summary>
        IEnumerator Upload(string slotId, string json, Action<bool, string> onDone);
    }
}
