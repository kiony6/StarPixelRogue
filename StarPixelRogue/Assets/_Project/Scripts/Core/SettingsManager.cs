using System;
using System.IO;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 设置管理器（M5 商业化准备）：音频/画质/语言/青少年模式/遥测开关等持久化。
    /// - 单文件 JSON（Application.persistentDataPath/settings.json），原子写（tmp + replace）防半截文件；
    /// - 加载后立即 Apply（音量/画质/全屏），任何修改经 <see cref="Save"/> 落盘并广播 <see cref="Changed"/>；
    /// - <see cref="Ensure"/> 幂等创建，供 GameBuilder/UI 在任何入口前调用。
    /// </summary>
    public class SettingsManager : MonoSingleton<SettingsManager>
    {
        [Serializable]
        public class Data
        {
            public float masterVolume = 1f;
            public float musicVolume = 0.75f;
            public float sfxVolume = 1f;
            public int quality = 2;               // 0=低 1=中 2=高（QualitySettings 档位索引）
            public bool fullscreen = true;
            public string language = "zh-CN";     // zh-CN / en-US
            public bool screenShake = true;
            public bool telemetry = true;
            // M13：云存档开关。默认关闭（隐私优先）；开启后仍需「已同意最新版隐私政策 + 后端已配置」才真正联网。
            public bool cloudSave = false;
            public bool minorMode = false;        // 青少年模式（未接入官方实名认证前的本地框架）
            public string agreedTermsVersion = ""; // 已同意的法律文本版本（合规门判断）
        }

        public Data Current = new Data();

        /// <summary>设置变更广播（音量/画质/语言切换后触发，SfxManager/HUD 订阅）。</summary>
        public static event Action Changed;

        private static readonly string FilePath =
            Path.Combine(Application.persistentDataPath, "settings.json");

        protected override void Awake()
        {
            base.Awake();
            Load();
            Apply();
        }

        /// <summary>幂等确保实例存在（可在任何流程前调用）。</summary>
        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("SettingsManager");
            go.AddComponent<SettingsManager>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoEnsure() => Ensure();

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) { Current = new Data(); return; }
                var json = File.ReadAllText(FilePath);
                var loaded = JsonUtility.FromJson<Data>(json);
                if (loaded != null) Current = loaded;
            }
            catch (Exception e)
            {
                // M22：读档失败时**先把坏文件挪走归档**，再回退默认值。
                // 旧实现只打一行 warning 就换成默认值，下一次 Save 会把默认值写回原路径——
                // 用户整套设置（音量/画质/语言/已同意协议版本）在没有任何提示的情况下清零，
                // 而且再也找不回来（可能只是掉电写坏了半个字段）。
                Debug.LogWarning("[Settings] 读取失败，使用默认值: " + e.Message);
                Quarantine();
                Current = new Data();
            }
            Sanitize();
        }

        /// <summary>
        /// 把读不动的设置文件另存为 settings.json.corrupt-yyyyMMdd-HHmmss（不覆盖，
        /// 连撞名都带序号），供用户/客服排查。归档失败不影响启动。
        /// </summary>
        private static void Quarantine()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var target = FilePath + ".corrupt-" + stamp;
                for (int i = 2; File.Exists(target); i++)
                    target = FilePath + ".corrupt-" + stamp + "-" + i;
                File.Move(FilePath, target);
                Debug.LogWarning("[Settings] 损坏的设置文件已归档: " + target);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Settings] 损坏文件归档失败（继续用默认值）: " + e.Message);
            }
        }

        /// <summary> clamp 非法值（手改文件/跨版本升级）。</summary>
        private void Sanitize()
        {
            Current.masterVolume = Mathf.Clamp01(Current.masterVolume);
            Current.musicVolume = Mathf.Clamp01(Current.musicVolume);
            Current.sfxVolume = Mathf.Clamp01(Current.sfxVolume);
            Current.quality = Mathf.Clamp(Current.quality, 0, 2);
            if (Current.language != "zh-CN" && Current.language != "en-US") Current.language = "zh-CN";
        }

        public void Save()
        {
            Sanitize();
            try
            {
                var json = JsonUtility.ToJson(Current, true);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);           // 原子替换，避免崩溃写坏存档
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Settings] 写入失败: " + e.Message);
            }
            Apply();
            Changed?.Invoke();
        }

        /// <summary>把设置应用到引擎（音量/画质/全屏/语言）。</summary>
        public void Apply()
        {
            AudioListener.volume = Current.masterVolume;
            try
            {
                if (QualitySettings.GetQualityLevel() != Current.quality &&
                    Current.quality < QualitySettings.names.Length)
                    QualitySettings.SetQualityLevel(Current.quality, true);
            }
            catch (Exception e) { Debug.LogWarning("[Settings] 画质应用失败: " + e.Message); }
            Screen.fullScreen = Current.fullscreen;
            Localization.SetLanguage(Current.language, silent: true);
        }

        // —— 便捷读写（统一走 Save 落盘） ——
        public void SetVolume(string channel, float v)
        {
            v = Mathf.Clamp01(v);
            if (channel == "master") Current.masterVolume = v;
            else if (channel == "music") Current.musicVolume = v;
            else if (channel == "sfx") Current.sfxVolume = v;
            Save();
        }

        public void SetQuality(int q) { Current.quality = Mathf.Clamp(q, 0, 2); Save(); }
        public void SetFullscreen(bool on) { Current.fullscreen = on; Save(); }
        public void SetScreenShake(bool on) { Current.screenShake = on; Save(); }
        public void SetTelemetry(bool on)
        {
            Current.telemetry = on;
            Save();
            // M20：关掉开关的同时丢弃尚未落盘的事件——否则开关前缓存的数据
            // 仍会在下一次 Flush 被写进文件，违背"关闭即停止采集"的承诺。
            if (!on) Telemetry.DiscardQueue();
        }

        /// <summary>
        /// M13：云存档开关（默认关闭）。仅置位开关——是否真正联网由
        /// <see cref="CloudSave.CloudSaveService.Enabled"/> 综合「已同意隐私政策 + 后端已配置」判定。
        /// </summary>
        public void SetCloudSave(bool on)
        {
            if (Current.cloudSave == on) return;
            Current.cloudSave = on;
            Save();
            Telemetry.Log(Telemetry.EvSettingsChanged, "key", "cloud_save", "value", on ? 1 : 0);
        }

        /// <summary>青少年模式切换：开启即重置当日计时（防止绕过限时）。</summary>
        public void SetMinorMode(bool on)
        {
            if (Current.minorMode == on) return;
            Current.minorMode = on;
            Save();
            if (on) PlaytimeGuard.ResetToday();
        }

        public void SetLanguage(string lang)
        {
            if (Current.language == lang) return;
            Current.language = lang;
            Save();                                  // Save 内 Apply 会同步语言
        }

        /// <summary>合规门调用：记录同意的法律文本版本。</summary>
        public void AgreeTerms(string termsVersion)
        {
            Current.agreedTermsVersion = termsVersion;
            Save();
        }

        /// <summary>当前是否已同意最新版法律文本。</summary>
        public bool TermsAgreed(string termsVersion)
        {
            return Current.agreedTermsVersion == termsVersion && !string.IsNullOrEmpty(termsVersion);
        }
    }
}
