using System;
using System.IO;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 版本信息（M5）：来源优先级
    /// 1) StreamingAssets/BuildInfo.json（由 Editor/BuildScript 在构建前写入，含真实构建号）；
    /// 2) 代码内 <see cref="FallbackVersion"/>（编辑器直开时的开发版本号）。
    /// 合规字段 <see cref="ComplianceVersion"/> 为法律文本版本号，
    /// 与 <see cref="SettingsManager.Data.agreedTermsVersion"/> 对应——
    /// 文本更新时递增此号，已同意旧版的用户下次启动会重新看到合规门。
    /// </summary>
    public static class VersionInfo
    {
        /// <summary>语义化版本（开发期直开时展示）。</summary>
        public const string FallbackVersion = "0.5.0-dev";

        /// <summary>法律文本版本（yyyy.MM）。更新协议/隐私政策时必须递增此号。</summary>
        // M13：隐私政策增补「云存档（可选、默认关闭）」条款——涉及数据是否上传的重大变更，
        // 递增此号使已同意旧版的用户下次启动时重新看到合规门并再次确认。
        private const string ConstComplianceVersion = "2026.10.1";

        /// <summary>
        /// M21 实测专用：覆盖法律文本版本，用来构造「用户已同意旧版本」这一场景
        /// ——协议更新后必须重新征得同意，是本模块最关键的合规行为，不进实测就只能靠读代码推断。
        /// 正常启动永远返回 <see cref="ConstComplianceVersion"/>。
        /// </summary>
        public static string ComplianceVersionOverrideForTest;

        /// <summary>当前法律文本版本（合规门判定与同意记录都以此为准）。</summary>
        public static string ComplianceVersion =>
            string.IsNullOrEmpty(ComplianceVersionOverrideForTest)
                ? ConstComplianceVersion
                : ComplianceVersionOverrideForTest;

        public static string Version { get; private set; } = FallbackVersion;
        public static string BuildNumber { get; private set; } = "dev";
        public static string BuildDate { get; private set; } = "";
        public static string UnityVersion { get; private set; } = Application.unityVersion;

        private static bool _loaded;

        /// <summary>展示用完整版本串（主菜单/关于/设置页脚）。</summary>
        public static string Display
        {
            get
            {
                EnsureLoaded();
                return "v" + Version + (BuildNumber == "dev" ? "" : " build " + BuildNumber);
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "BuildInfo.json");
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                var info = JsonUtility.FromJson<BuildInfo>(json);
                if (info == null) return;
                if (!string.IsNullOrEmpty(info.version)) Version = info.version;
                if (!string.IsNullOrEmpty(info.buildNumber)) BuildNumber = info.buildNumber;
                if (!string.IsNullOrEmpty(info.buildDate)) BuildDate = info.buildDate;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Version] BuildInfo.json 读取失败: " + e.Message);
            }
        }

        [Serializable]
        private class BuildInfo
        {
            public string version;
            public string buildNumber;
            public string buildDate;
            public string unityVersion;
        }
    }
}
