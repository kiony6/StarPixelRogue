using System;
using System.Collections;
using UnityEngine;

namespace StarPixel.Core.CloudSave
{
    /// <summary>
    /// 云存档服务（M13）：在既有本地 <see cref="SaveManager"/> 之上叠加「可选、可关闭」的云端同步。
    /// 设计原则（隐私优先 + 绝不砸单机体验）：
    /// - 三重开关，任一不满足即完全不联网，单机行为与未装本模块时完全一致：
    ///   ① 用户在设置里开启（<see cref="SettingsManager.Data.cloudSave"/>，默认关闭）
    ///   ② 已同意最新版隐私政策（<see cref="SettingsManager.TermsAgreed"/>）
    ///   ③ 后端已配置 baseUrl（<see cref="HttpCloudSaveProvider.Configured"/>）；
    /// - 冲突裁决取新：按 <see cref="SaveManager.Record.savedAt"/> 比较，新者胜、整体替换
    ///   （刻意不做累加合并——合并会让 totalKills 等累计项重复计数，取新更可预测）；
    /// - 绝不阻塞玩法：全程协程 + 失败静默降级为纯本地存档，断网/抖动只重试、不影响对局；
    /// - 节流上行：图鉴发现等高频存档合并为最短 15s 一次推送。
    /// 上传内容仅为战绩/图鉴数值，不含任何个人身份信息（见隐私政策第三条）。
    /// </summary>
    public class CloudSaveService : MonoSingleton<CloudSaveService>
    {
        /// <summary>可选配置（Assets/_Project/Resources/cloudsave.json，不存在则全部取默认值=禁用）。</summary>
        [Serializable]
        public class Config
        {
            public string baseUrl = "";
            public string token = "";
            public int timeoutSeconds = 8;
            public string slotId = "default";
        }

        /// <summary>存档槽 id（预留多槽扩展）。</summary>
        public string SlotId { get; private set; } = "default";

        /// <summary>最近一次同步结果（设置页展示用）。</summary>
        public string LastStatus { get; private set; } = "未启用";

        /// <summary>最近一次同步是否成功。</summary>
        public bool LastSyncOk { get; private set; }

        public ICloudSaveProvider Provider { get; private set; }

        private const float PushInterval = 15f;   // 上行最小间隔（秒）
        private const float RetryBase = 20f;      // 失败退避基数（秒）
        private const int MaxRetries = 3;
        private const string ConfigResource = "cloudsave";

        private float _pushTimer;
        private bool _pushPending;
        private float _retryTimer;
        private int _retries;
        private bool _busy;

        /// <summary>三重开关后的最终可用态（未启用时本服务不发任何网络请求）。</summary>
        public bool Enabled
        {
            get
            {
                if (Provider == null || !Provider.Configured) return false;
                var s = SettingsManager.Instance;
                if (s == null) return false;
                if (!s.Current.cloudSave) return false;                                 // ① 用户未开启（默认关）
                if (!s.TermsAgreed(VersionInfo.ComplianceVersion)) return false;        // ② 未同意最新版隐私政策
                return true;
            }
        }

        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("CloudSaveService");
            go.AddComponent<CloudSaveService>();
        }

        protected override void Awake()
        {
            base.Awake();
            var cfg = LoadConfig() ?? new Config();
            Provider = new HttpCloudSaveProvider(cfg.baseUrl, cfg.token, cfg.timeoutSeconds);
            SlotId = string.IsNullOrEmpty(cfg.slotId) ? "default" : cfg.slotId;
            SaveManager.Saved += OnLocalSaved;
        }

        void Start()
        {
            SaveManager.Ensure();   // 保证本地存档先就位，避免裁决时读到空数据
            if (!Enabled) { LastStatus = "未启用（可在设置中开启）"; return; }
            StartCoroutine(PullAndReconcile());
        }

        void OnDestroy()
        {
            SaveManager.Saved -= OnLocalSaved;
        }

        void Update()
        {
            if (!Enabled) return;

            if (_pushPending && !_busy)
            {
                _pushTimer -= Time.deltaTime;
                if (_pushTimer <= 0f)
                {
                    _pushPending = false;
                    _pushTimer = PushInterval;
                    StartCoroutine(Push());
                }
            }

            // 失败退避重试（后台进行，不阻塞玩法）
            if (_retries > 0 && _retries <= MaxRetries && !_pushPending && !_busy)
            {
                _retryTimer -= Time.deltaTime;
                if (_retryTimer <= 0f) StartCoroutine(Push());
            }
        }

        private void OnLocalSaved() => SchedulePush();

        /// <summary>请求上行同步（受节流约束；immediate=true 时跳过等待立即执行）。</summary>
        public void SchedulePush(bool immediate = false)
        {
            if (!Enabled) return;
            _pushPending = true;
            if (immediate) _pushTimer = 0f;
        }

        /// <summary>手动同步（设置页「立即同步」按钮）。</summary>
        public void SyncNow()
        {
            if (!Enabled || _busy) return;
            StartCoroutine(PullAndReconcile());
        }

        /// <summary>启动时拉取并与本地裁决（取新）。</summary>
        private IEnumerator PullAndReconcile()
        {
            if (_busy) yield break;
            _busy = true;

            bool ok = false;
            string payload = null;
            yield return Provider.Download(SlotId, (o, p) => { ok = o; payload = p; });

            if (!ok)
            {
                LastSyncOk = false;
                LastStatus = "拉取失败（继续使用本地存档）";
                Telemetry.Log(Telemetry.EvCloudSync, "op", "pull", "ok", false);
                _busy = false;
                yield break;
            }

            if (string.IsNullOrEmpty(payload))
            {
                // 云端为空（首次同步）：把本地推上去建档
                LastSyncOk = true;
                LastStatus = "云端为空，正在建档";
                _busy = false;
                SchedulePush(true);
                yield break;
            }

            var remote = Parse(payload);
            if (remote == null)
            {
                LastSyncOk = false;
                LastStatus = "云端数据无法解析，已保留本地存档";
                _busy = false;
                yield break;
            }

            if (remote.schemaVersion > SaveManager.SchemaVersion)
            {
                // 云端是更高版本的存档结构，本地无法安全解析 -> 保留本地，绝不覆盖
                LastStatus = "云端存档版本较新，已保留本地存档";
                _busy = false;
                yield break;
            }

            // 本地存档未就绪时不合并——绝不拿云端数据冒险覆盖（Start 已 Ensure，此处为防御性兜底）
            if (SaveManager.Instance == null)
            {
                LastStatus = "本地存档未就绪，跳过云端合并";
                _busy = false;
                yield break;
            }

            var local = SaveManager.Instance.Data;
            int cmp = SaveManager.CompareSavedAt(remote.savedAt, local != null ? local.savedAt : "");

            if (cmp > 0)
            {
                SaveManager.Instance.ReplaceFromCloud(remote);
                LastStatus = "已采用云端存档（云端较新）";
            }
            else
            {
                LastStatus = "本地较新，保持本地";
                SchedulePush(true);   // 顺手收敛两端
            }

            LastSyncOk = true;
            Telemetry.Log(Telemetry.EvCloudSync, "op", "pull", "ok", true);
            _busy = false;
        }

        private IEnumerator Push()
        {
            if (_busy) yield break;
            _busy = true;

            var save = SaveManager.Instance;
            if (save == null) { _busy = false; yield break; }

            var json = JsonUtility.ToJson(save.Data, true);
            bool ok = false;
            string err = "";
            yield return Provider.Upload(SlotId, json, (o, e) => { ok = o; err = e; });

            LastSyncOk = ok;
            if (ok)
            {
                LastStatus = "已上传至云端";
                _retries = 0;
            }
            else
            {
                _retries++;
                _retryTimer = RetryBase * _retries;
                LastStatus = _retries > MaxRetries
                    ? "上传失败，已停止重试（本地存档不受影响）"
                    : "上传失败，" + _retryTimer + "s 后重试：" + err;
            }
            Telemetry.Log(Telemetry.EvCloudSync, "op", "push", "ok", ok);
            _busy = false;
        }

        void OnApplicationPause(bool paused) { if (paused) SchedulePush(true); }
        void OnApplicationQuit() { SchedulePush(true); }

        private static SaveManager.Record Parse(string json)
        {
            try { return JsonUtility.FromJson<SaveManager.Record>(json); }
            catch (Exception e)
            {
                Debug.LogWarning("[CloudSave] 云端存档解析失败: " + e.Message);
                return null;
            }
        }

        private static Config LoadConfig()
        {
            try
            {
                var asset = Resources.Load<TextAsset>(ConfigResource);
                if (asset == null || string.IsNullOrEmpty(asset.text)) return null;
                return JsonUtility.FromJson<Config>(asset.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CloudSave] 配置读取失败: " + e.Message);
                return null;
            }
        }
    }
}
