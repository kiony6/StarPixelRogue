using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace StarPixel.Core.CloudSave
{
    /// <summary>
    /// 通用 REST 云存档后端（M13）：GET 拉取 / PUT 上传，后端只需实现这两个端点（契约见 README「M13 后端契约」）。
    /// - baseUrl 为空时 <see cref="Configured"/> 为 false，云存档自动禁用（单机行为不变、零网络请求）；
    /// - 404 视为「云端还没有这份存档」——属首次同步的正常情况，按成功且空数据处理；
    /// - 每次请求都带独立超时，避免断网时协程长期挂起。
    /// 传输内容仅为战绩/图鉴数值（见隐私政策），不含任何个人身份信息。
    /// </summary>
    public sealed class HttpCloudSaveProvider : ICloudSaveProvider
    {
        private readonly string _baseUrl;
        private readonly string _token;
        private readonly int _timeoutSeconds;

        public HttpCloudSaveProvider(string baseUrl, string token = "", int timeoutSeconds = 8)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _token = token ?? "";
            _timeoutSeconds = Mathf.Max(1, timeoutSeconds);
        }

        /// <summary>
        /// 已配置且传输安全。刻意严格：非 HTTPS 一律视为未配置（云存档禁用），
        /// 使隐私政策中「仅通过 HTTPS 加密传输」的承诺在代码层面成立，而不只是文案。
        /// 例外：本机地址（localhost / 127.0.0.1）允许 http，便于本地自测后端。
        /// </summary>
        public bool Configured => IsSecure(_baseUrl);

        private static bool IsSecure(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
            Debug.LogWarning("[CloudSave] 后端地址必须为 HTTPS（本机测试除外），云存档已禁用: " + url);
            return false;
        }

        public IEnumerator Download(string slotId, Action<bool, string> onDone)
        {
            var url = _baseUrl + "/save/" + UnityWebRequest.EscapeURL(slotId);
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = _timeoutSeconds;
                ApplyAuth(req);
                yield return req.SendWebRequest();

                if (!Ok(req))
                {
                    // 404：云端尚无此存档（首次同步），不算失败
                    if (req.responseCode == 404) { onDone?.Invoke(true, ""); yield break; }
                    onDone?.Invoke(false, Describe(req));
                    yield break;
                }
                onDone?.Invoke(true, req.downloadHandler != null ? req.downloadHandler.text : "");
            }
        }

        public IEnumerator Upload(string slotId, string json, Action<bool, string> onDone)
        {
            var url = _baseUrl + "/save/" + UnityWebRequest.EscapeURL(slotId);
            var body = Encoding.UTF8.GetBytes(json ?? "{}");
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT))
            {
                req.timeout = _timeoutSeconds;
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                ApplyAuth(req);
                yield return req.SendWebRequest();

                bool ok = Ok(req);
                onDone?.Invoke(ok, ok ? "" : Describe(req));
            }
        }

        private void ApplyAuth(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(_token))
                req.SetRequestHeader("Authorization", "Bearer " + _token);
        }

        /// <summary>兼容 Unity 2019（isNetworkError/isHttpError）与 2020+（result）两套判定 API。</summary>
        private static bool Ok(UnityWebRequest req)
        {
#if UNITY_2020_1_OR_NEWER
            return req.result == UnityWebRequest.Result.Success;
#else
            return !req.isNetworkError && !req.isHttpError;
#endif
        }

        private static string Describe(UnityWebRequest req)
        {
            return "HTTP " + req.responseCode + " " + (req.error ?? "");
        }
    }
}
