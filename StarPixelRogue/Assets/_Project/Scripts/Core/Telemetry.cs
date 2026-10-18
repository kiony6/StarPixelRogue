using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 匿名数据埋点骨架（M5）：事件常量 + 本地队列 + JSONL 落盘。
    /// 合规边界（隐私政策第 4 条）：
    /// - 仅采集匿名玩法统计（波次/时长/选项分布），**无设备标识、无个人身份信息**；
    /// - <see cref="SettingsManager"/> 中关闭「匿名数据统计」后本类不再记录新事件；
    /// - 网络上报为 TODO 接入点：上线前需完成隐私政策公示与用户单独同意，
    ///   同意链路复用 ComplianceGate 的协议版本机制。
    /// </summary>
    public static class Telemetry
    {
        // —— 事件名常量（避免散落字符串；排序即分类） ——
        public const string EvAppStart = "app_start";
        public const string EvAgreeTerms = "agree_terms";
        public const string EvRunStart = "run_start";
        public const string EvRunEnd = "run_end";            // wave,time,kills,level,reason
        public const string EvWaveReached = "wave_reached";  // wave
        public const string EvBossDefeated = "boss_defeated";
        public const string EvUpgradeChosen = "upgrade_chosen"; // kind
        public const string EvSettingsChanged = "settings_changed"; // key,value
        public const string EvLanguageChanged = "language_changed";
        public const string EvNetHostStart = "net_host_start";
        public const string EvNetClientJoin = "net_client_join";
        public const string EvDisconnected = "disconnected";
        public const string EvCloudSync = "cloud_sync";      // M13: op(pull/push), ok

        private const int FlushThreshold = 50;
        private const float FlushInterval = 20f;

        private static readonly List<string> Queue = new List<string>();
        private static float _sinceFlush;
        private static bool _quitting;

        private static string LogDir =>
            Path.Combine(Application.persistentDataPath, "Logs");

        private static string LogFile =>
            Path.Combine(LogDir, "telemetry-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");

        /// <summary>
        /// 记录一条事件。kv 为交替的 key/value（如 Log(EvRunEnd, "wave", 3, "time", 61.2f)）。
        /// </summary>
        public static void Log(string ev, params object[] kv)
        {
            if (!Enabled()) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"t\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\",");
                sb.Append("\"e\":\"").Append(ev).Append("\"");
                for (int i = 0; i + 1 < kv.Length; i += 2)
                {
                    sb.Append(",\"").Append(Convert.ToString(kv[i])).Append("\":");
                    var v = kv[i + 1];
                    if (v is string s) sb.Append("\"").Append(s.Replace("\"", "'")).Append("\"");
                    else if (v is bool b) sb.Append(b ? "true" : "false");
                    else sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append("}");
                lock (Queue) Queue.Add(sb.ToString());
                if (Queue.Count >= FlushThreshold) Flush();
            }
            catch (Exception e) { Debug.LogWarning("[Telemetry] 记录失败: " + e.Message); }
        }

        /// <summary>埋点开关（默认开；用户在设置中关闭后停止采集）。</summary>
        public static bool Enabled()
        {
            return SettingsManager.Instance == null || SettingsManager.Instance.Current.telemetry;
        }

        /// <summary>主循环驱动（GameBuilder 每帧调用，或经 MonoSingleton 挂载）。</summary>
        public static void Tick(float deltaTime)
        {
            _sinceFlush += deltaTime;
            if (_sinceFlush >= FlushInterval)
            {
                _sinceFlush = 0f;
                Flush();
            }
        }

        /// <summary>队列落盘（追加写 JSONL，单行一条事件）。</summary>
        public static void Flush()
        {
            if (Queue.Count == 0) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                List<string> batch;
                lock (Queue)
                {
                    batch = new List<string>(Queue);
                    Queue.Clear();
                }
                File.AppendAllLines(LogFile, batch);
            }
            catch (Exception e) { Debug.LogWarning("[Telemetry] 落盘失败: " + e.Message); }
        }

        /// <summary>应用退出前冲刷（防丢最后 20 秒）。</summary>
        public static void FlushOnQuit()
        {
            _quitting = true;
            Flush();
        }

        /// <summary>诊断日志目录（设置面板「导出诊断日志」展示用）。</summary>
        public static string ExportPath => LogDir;

        /// <summary>
        /// TODO(M5.1)：网络上报接入点。上线并取得用户单独同意后，
        /// 在此将 Flush 的批次 POST 到自有分析后端；未同意前禁止上传（合规红线）。
        /// </summary>
        public static void Upload() { /* 占位：见隐私政策第 4 条 */ }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallQuitHook()
        {
            Application.quitting += FlushOnQuit;
        }
    }
}
