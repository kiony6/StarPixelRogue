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
    /// - <see cref="SettingsManager"/> 中关闭「匿名数据统计」后本类不再记录新事件，
    ///   且关闭瞬间**丢弃队列中尚未落盘的事件**（M20：用户关掉开关的意图是
    ///   "别再留下我的数据"，把开关前缓存的事件继续写进文件违背这一意图）；
    /// - 网络上报为 TODO 接入点：上线前需完成隐私政策公示与用户单独同意，
    ///   同意链路复用 ComplianceGate 的协议版本机制。
    ///
    /// M20 修掉两处会让埋点数据**静默作废**的缺陷：
    /// ① 字符串值未做完整 JSON 转义——旧实现只把双引号换成单引号，
    ///   值里一旦出现反斜杠（如 Windows 路径）、换行或制表符，产出的就是非法 JSON，
    ///   整个 JSONL 文件从此无法被解析，采集了等于白采集；
    /// ② <see cref="Flush"/> 先清空队列再写文件——写失败（磁盘满/权限/占用）时
    ///   批次已被移除，事件永久丢失且无任何重试。
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

        /// <summary>队列上限：落盘持续失败时重试批次不再无限堆积，超过即丢最旧的。</summary>
        private const int MaxQueue = 5000;

        private static readonly List<string> Queue = new List<string>();
        private static float _sinceFlush;
        private static bool _quitting;

        /// <summary>M20 实测：把日志目录指向别处（如与现有文件同名的非法路径）以制造落盘失败。null = 真实目录。</summary>
        public static string LogDirOverrideForTest;

        private static string LogDir => LogDirOverrideForTest
            ?? Path.Combine(Application.persistentDataPath, "Logs");

        private static string LogFile =>
            Path.Combine(LogDir, "telemetry-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl");

        /// <summary>M20 实测：当前未落盘的事件数（验证"关开关即清队列 / 失败批次回到队列"）。</summary>
        public static int QueueCount { get { lock (Queue) { return Queue.Count; } } }

        /// <summary>M20 实测：落盘失败次数（验证"失败可观测、批次未丢"）。</summary>
        public static int FlushFailCount { get; private set; }

        /// <summary>
        /// 记录一条事件。kv 为交替的 key/value（如 Log(EvRunEnd, "wave", 3, "time", 61.2f)）。
        /// </summary>
        public static void Log(string ev, params object[] kv)
        {
            if (!Enabled()) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"t\":\"").Append(Escape(DateTime.Now.ToString("HH:mm:ss"))).Append("\",");
                sb.Append("\"e\":\"").Append(Escape(ev)).Append("\"");
                for (int i = 0; i + 1 < kv.Length; i += 2)
                {
                    sb.Append(",\"").Append(Escape(Convert.ToString(kv[i]))).Append("\":");
                    var v = kv[i + 1];
                    if (v is string s) sb.Append("\"").Append(Escape(s)).Append("\"");
                    else if (v is bool b) sb.Append(b ? "true" : "false");
                    else sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append("}");
                lock (Queue) Queue.Add(sb.ToString());
                if (Queue.Count >= FlushThreshold) Flush();
            }
            catch (Exception e) { Debug.LogWarning("[Telemetry] 记录失败: " + e.Message); }
        }

        /// <summary>
        /// M20：JSON 字符串转义。旧实现只把双引号换成单引号——值里出现反斜杠/换行/制表符
        /// 时会产出非法 JSON，整个 JSONL 文件从此无法解析。事件名/键/值统一走这里。
        /// </summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOfAny(EscapeNeeded) < 0) return s;      // 快路径：绝大多数事件不含特殊字符
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static readonly char[] EscapeNeeded = { '"', '\\', '\n', '\r', '\t', '\b', '\f' };

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
            if (QueueCount == 0) return;
            List<string> batch;
            lock (Queue)
            {
                batch = new List<string>(Queue);
                Queue.Clear();
            }
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllLines(LogFile, batch);
            }
            catch (Exception e)
            {
                // M20：写失败时批次放回队首，下次 Tick/退出时重试——
                // 旧实现在这里把批次静默丢掉（队列已清、文件没写上）。
                FlushFailCount++;
                Debug.LogWarning("[Telemetry] 落盘失败: " + e.Message);
                lock (Queue)
                {
                    Queue.InsertRange(0, batch);
                    if (Queue.Count > MaxQueue) Queue.RemoveRange(MaxQueue, Queue.Count - MaxQueue);
                }
            }
        }

        /// <summary>
        /// M20：丢弃队列中尚未落盘的事件（<see cref="SettingsManager.SetTelemetry(false)"/> 时调用）。
        /// 关掉开关后不再 Flush——否则开关前缓存的事件仍会被写进文件，违背用户"停止采集"的意图。
        /// </summary>
        public static void DiscardQueue()
        {
            lock (Queue) Queue.Clear();
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
