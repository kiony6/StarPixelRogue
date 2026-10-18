using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace StarPixel.EditorTools
{
    /// <summary>
    /// 构建自动化（M5 上线准备）：
    /// - 版本号单一数据源：Assets/_Project/version.json（可入 git，便于 review）；
    /// - 构建前写入 StreamingAssets/BuildInfo.json（版本/构建号/时间/Unity 版本），运行时由 VersionInfo 读取；
    /// - 自动生成空的 Bootstrap 场景并登记进 Build Settings——游戏全部内容由
    ///   GameBuilder 的 [RuntimeInitializeOnLoadMethod] 运行时构建，因此场景只需"存在"；
    /// - PlayerSettings 公司/产品名/版本号同步注入。
    ///
    /// 菜单：StarPixel/构建 Windows (Mono 快速验证) | StarPixel/构建 Windows (IL2CPP 正式包) | StarPixel/递增版本号
    /// 联机包请在定义 FISHNET_ENABLED 后改用 NetBootstrap 场景（见 README M4）。
    /// </summary>
    public static class BuildScript
    {
        private const string CompanyName = "StarPixelStudio";
        private const string ProductName = "星界像素狂潮";
        private const string VersionFilePath = "Assets/_Project/version.json";
        private const string BootstrapScenePath = "Assets/Scenes/Bootstrap.unity";
        private const string OutputDir = "Build/Windows";

        [MenuItem("StarPixel/构建 Windows (Mono 快速验证)")]
        public static void BuildMono() => Build(ScriptingImplementation.Mono2x, "Mono");

        [MenuItem("StarPixel/构建 Windows (IL2CPP 正式包)")]
        public static void BuildIl2Cpp() => Build(ScriptingImplementation.IL2CPP, "IL2CPP");

        [MenuItem("StarPixel/构建 Android (IL2CPP, APK)")]
        public static void BuildAndroid() => BuildMobile(BuildTarget.Android, BuildTargetGroup.Android, "apk");

        [MenuItem("StarPixel/构建 iOS (Xcode 工程)")]
        public static void BuildIOS() => BuildMobile(BuildTarget.iOS, BuildTargetGroup.iOS, "");

        [MenuItem("StarPixel/启用联机代码 (定义 FISHNET_ENABLED)")]
        public static void EnableFishNetDefine() => SetFishNetDefine(true);

        [MenuItem("StarPixel/停用联机代码 (移除 FISHNET_ENABLED)")]
        public static void DisableFishNetDefine() => SetFishNetDefine(false);

        /// <summary>
        /// M18：切换 FISHNET_ENABLED 脚本定义符号。
        /// 联机相关代码整段用 #if FISHNET_ENABLED 保护，未定义时**完全不参与编译**——
        /// 这正是 M10/M11/M14 的联机代码此前从未被真正验证过的原因（M17 实测才发现这一点）。
        /// 本方法对 Standalone/Android/iOS 三个目标组统一追加或移除该符号。
        /// </summary>
        private static void SetFishNetDefine(bool enable)
        {
            const string Symbol = "FISHNET_ENABLED";
            var groups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android, BuildTargetGroup.iOS };
            foreach (var g in groups)
            {
                string cur = PlayerSettings.GetScriptingDefineSymbolsForGroup(g) ?? "";
                var list = new System.Collections.Generic.List<string>(
                    cur.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                bool has = list.Contains(Symbol);
                if (enable && !has) list.Add(Symbol);
                else if (!enable && has) list.Remove(Symbol);
                PlayerSettings.SetScriptingDefineSymbolsForGroup(g, string.Join(";", list.ToArray()));
            }
            Debug.Log("[Build] " + Symbol + " = " + enable + "（Standalone/Android/iOS）");
        }

        [MenuItem("StarPixel/递增版本号 (patch)")]
        public static void BumpVersion()
        {
            var v = ReadVersion();
            var parts = v.Split('.');
            if (parts.Length == 3 && int.TryParse(parts[2], out int patch))
            {
                string next = parts[0] + "." + parts[1] + "." + (patch + 1);
                File.WriteAllText(VersionFilePath, "{\n  \"version\": \"" + next + "\"\n}\n");
                Debug.Log("[Build] 版本号 " + v + " → " + next + "（下次构建生效）");
            }
            else Debug.LogWarning("[Build] version.json 格式无法解析: " + v);
        }

        private static string ReadVersion()
        {
            try
            {
                var text = File.ReadAllText(VersionFilePath);
                var i = text.IndexOf("\"version\"", StringComparison.Ordinal);
                if (i < 0) return "0.5.0";
                var q1 = text.IndexOf('"', i + 9);
                var q2 = text.IndexOf('"', q1 + 1);
                return text.Substring(q1 + 1, q2 - q1 - 1);
            }
            catch { return "0.5.0"; }
        }

        private static void Build(ScriptingImplementation backend, string backendLabel)
        {
            string version = ReadVersion();
            string buildNumber = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);

            // 1) 版本信息注入（运行时 VersionInfo 读取）
            WriteBuildInfo(version, buildNumber);

            // 2) PlayerSettings
            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = version;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                CompanyName + ".StarPixelRogue");

            // 3) Bootstrap 场景（空场景：全部内容运行时构建）
            EnsureBootstrapScene();

            // 4) 脚本后端
            try
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Build] 设置脚本后端失败（可能未安装对应模块）: " + e.Message);
            }

            // 5) 执行构建
            var scenes = new[] { BootstrapScenePath };
            Directory.CreateDirectory(OutputDir);
            // 可执行文件名用 ASCII（避免非 ASCII 路径在部分构建工具/CI 下出问题），
            // 窗口标题等玩家可见名称由 PlayerSettings.productName 控制。
            string exePath = Path.Combine(OutputDir, "StarPixelRogue.exe");

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = exePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            var summary = BuildPipeline.BuildPlayer(options);
            if (summary.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log(string.Format("[Build] 成功（{0}）：{1}  v{2} build {3}  耗时 {4:F1}s",
                    backendLabel, exePath, version, buildNumber, summary.summary.totalTime.TotalSeconds));
                EditorUtility.RevealInFinder(exePath);
            }
            else
            {
                Debug.LogError("[Build] 失败：" + summary.summary.result + " - " + summary.summary.ToString());
            }
        }

        /// <summary>版本信息注入（桌面与移动端共用）。</summary>
        private static void WriteBuildInfo(string version, string buildNumber)
        {
            try
            {
                Directory.CreateDirectory("Assets/StreamingAssets");
                var json = "{\n  \"version\": \"" + version + "\",\n" +
                           "  \"buildNumber\": \"" + buildNumber + "\",\n" +
                           "  \"buildDate\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\",\n" +
                           "  \"unityVersion\": \"" + Application.unityVersion + "\"\n}";
                File.WriteAllText("Assets/StreamingAssets/BuildInfo.json", json);
                AssetDatabase.ImportAsset("Assets/StreamingAssets/BuildInfo.json");
            }
            catch (Exception e) { Debug.LogWarning("[Build] BuildInfo.json 写入失败: " + e.Message); }
        }

        /// <summary>
        /// M16 跨平台：移动端构建（Android APK / iOS Xcode 工程）。
        /// 与桌面构建的关键差异：
        /// - 强制横屏：本作为横版弹幕/割草，竖屏会严重压缩横向视野；
        /// - Android 需要递增整数 bundleVersionCode（商店强制），iOS 需要字符串 buildNumber；
        /// - 移动端一律 IL2CPP + ARM64（Google Play 自 2019 起强制 64 位，不再支持纯 32 位）。
        /// 注意：iOS 在 Windows 上只能导出 Xcode 工程，仍需在 Mac 上继续编译与签名。
        /// </summary>
        private static void BuildMobile(BuildTarget target, BuildTargetGroup group, string ext)
        {
            string version = ReadVersion();
            string buildNumber = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);

            WriteBuildInfo(version, buildNumber);

            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = version;
            PlayerSettings.SetApplicationIdentifier(group, CompanyName + ".StarPixelRogue");
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

            if (target == BuildTarget.Android)
            {
                // 取时间戳后 8 位（MMddHHmm）作为递增整数版本号，避免完整时间戳溢出 int
                int code;
                int.TryParse(buildNumber.Substring(buildNumber.Length - 8), out code);
                PlayerSettings.Android.bundleVersionCode = code;
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            }
            else if (target == BuildTarget.iOS)
            {
                PlayerSettings.iOS.buildNumber = buildNumber;
                PlayerSettings.iOS.targetOSVersionString = "12.0";
            }

            EnsureBootstrapScene();

            try { PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP); }
            catch (Exception e)
            {
                Debug.LogWarning("[Build] 设置 IL2CPP 失败（可能未安装对应模块）: " + e.Message);
            }

            string dir = "Build/" + target;
            Directory.CreateDirectory(dir);
            string outPath = ext.Length > 0 ? Path.Combine(dir, "StarPixelRogue." + ext) : dir;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { BootstrapScenePath },
                locationPathName = outPath,
                target = target,
                targetGroup = group,
                options = BuildOptions.None
            };

            var summary = BuildPipeline.BuildPlayer(options);
            if (summary.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log(string.Format("[Build] 成功（{0}）：{1}  v{2} build {3}  耗时 {4:F1}s",
                    target, outPath, version, buildNumber, summary.summary.totalTime.TotalSeconds));
                EditorUtility.RevealInFinder(outPath);
            }
            else
            {
                Debug.LogError("[Build] 失败：" + summary.summary.result + " - " + summary.summary.ToString());
            }
        }

        /// <summary>
        /// M17 无头实测支持：准备 Bootstrap 场景并以 Mono 后端构建 Windows 玩家包，
        /// 供命令行 -executeMethod 调用（正常开发请使用上面的菜单项）。
        /// 与菜单构建的差异：不做 EditorUtility.RevealInFinder（无头环境无资源管理器可弹）。
        /// </summary>
        public static void HeadlessBuild()
        {
            string version = ReadVersion();
            string buildNumber = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            WriteBuildInfo(version, buildNumber);

            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = version;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                CompanyName + ".StarPixelRogue");

            EnsureBootstrapScene();

            string outDir = "Build/Smoke";
            Directory.CreateDirectory(outDir);
            string exe = Path.Combine(outDir, "StarPixelRogue.exe");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { BootstrapScenePath },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            var summary = BuildPipeline.BuildPlayer(options);
            Debug.Log("[HeadlessBuild] result=" + summary.summary.result + " path=" + exe);
        }

#if FISHNET_ENABLED
        /// <summary>联机场景（由 NetSetupEditor 装配：NetworkManager + PlayerSpawner + 网络预制体）。</summary>
        private const string NetBootstrapScenePath = "Assets/Scenes/NetBootstrap.unity";

        /// <summary>
        /// M18 联机双开实测支持：无头构建「联机版」玩家包。
        /// 与 HeadlessBuild 的关键差异——不再用空的 Bootstrap 场景，而是先确保 NetBootstrap
        /// 场景存在（缺失时自动调用 Fish-Net 装配脚本生成），否则场景里没有 NetworkManager，
        /// 运行时兜底创建会抛 NullReferenceException，房主根本起不来（M18 双开实测首次暴露）。
        /// </summary>
        public static void HeadlessNetBuild()
        {
            if (!File.Exists(NetBootstrapScenePath))
            {
                Debug.Log("[HeadlessNetBuild] NetBootstrap 场景缺失，先执行 Fish-Net 装配…");
                NetSetupEditor.Setup();
                AssetDatabase.SaveAssets();
            }
            if (!File.Exists(NetBootstrapScenePath))
            {
                Debug.LogError("[HeadlessNetBuild] 装配后仍找不到 " + NetBootstrapScenePath);
                return;
            }
            RegisterScene(NetBootstrapScenePath);

            string version = ReadVersion();
            string buildNumber = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            WriteBuildInfo(version, buildNumber);

            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = version;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
                CompanyName + ".StarPixelRogue");

            string outDir = "Build/SmokeNet";
            Directory.CreateDirectory(outDir);
            string exe = Path.Combine(outDir, "StarPixelRogue.exe");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { NetBootstrapScenePath },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            var summary = BuildPipeline.BuildPlayer(options);
            Debug.Log("[HeadlessNetBuild] result=" + summary.summary.result + " path=" + exe);
        }
#endif

        /// <summary>把场景登记为可构建（幂等）。</summary>
        private static void RegisterScene(string scenePath)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(
                EditorBuildSettings.scenes);
            bool registered = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].path == scenePath)
                {
                    if (!list[i].enabled) list[i] = new EditorBuildSettingsScene(scenePath, true);
                    registered = true;
                    break;
                }
            }
            if (!registered) list.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        /// <summary>确保存在空的 Bootstrap 场景并登记为可构建（运行时自动搭建全部内容）。</summary>
        private static void EnsureBootstrapScene()
        {
            bool exists = File.Exists(BootstrapScenePath);
            if (!exists)
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                Directory.CreateDirectory(Path.GetDirectoryName(BootstrapScenePath));
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, BootstrapScenePath);
                Debug.Log("[Build] 已创建空 Bootstrap 场景（内容由运行时自动构建）");
            }

            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(
                EditorBuildSettings.scenes);
            bool registered = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].path == BootstrapScenePath)
                {
                    if (!list[i].enabled) list[i] = new EditorBuildSettingsScene(BootstrapScenePath, true);
                    registered = true;
                    break;
                }
            }
            if (!registered) list.Add(new EditorBuildSettingsScene(BootstrapScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
