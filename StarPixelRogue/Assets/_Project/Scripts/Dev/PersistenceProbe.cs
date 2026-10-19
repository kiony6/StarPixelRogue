using System.IO;
using StarPixel.Core;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// 无头持久化实测探针（M20）：仅当进程带 <c>-persistenceProbe</c> 时由 <c>GameBuilder</c> 创建。
    /// 覆盖两个**纯本机、却始终零实测覆盖**的持久化模块：
    ///   SAVE     —— <see cref="SaveManager"/>：战绩提交 / 图鉴去重 / 落盘读档往返 / 损坏存档回退；
    ///   TELEMETRY —— <see cref="Telemetry"/>：JSONL 合法性 / 落盘失败重试 / 关开关清队列。
    ///
    /// 为什么必须测：这两类缺陷都是**静默作废**型的——
    /// 埋点文件是非法 JSON 时，游戏照常跑、日志零报错，采集了等于白采集；
    /// 存档读档丢字段时，只有玩家某天发现纪录没了才知道。
    ///
    /// 判据的区分力（本轮最要紧的地方）：
    /// - JSONL 的合法性**不由被测进程自判**——探针只负责制造含特殊字符的事件并落盘，
    ///   由驱动脚本用 Python 的 json.loads 逐行独立解析，并断言特殊字符串**精确往返**
    ///   （转义修坏一个字符都过不了）。期望值绝不来自被测对象自己。
    /// - 落盘失败用"目录与现有文件同名"这一真实冲突制造（CreateDirectory 必抛 IOException），
    ///   断言 FlushFailCount ≥1 且**批次回到队列**（QueueCount 不为 0）——
    ///   旧实现在这里队列被清空、事件无声消失。
    /// - 关开关断言 QueueCount == 0：旧实现残留的事件会在下一次 Flush 落盘。
    ///
    /// ⚠ 本探针会写真实的 save.json / telemetry 文件：驱动脚本负责**跑前备份、跑后恢复**，
    /// 探针自身只在 OnDestroy 里还原遥测开关。
    /// 正常启动不带该参数时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class PersistenceProbe : MonoBehaviour
    {
        private string _scene = "SAVE";
        private int _errors;
        private string _firstError = "";
        private bool _done;

        private int _phase = 0;
        private int _wait = 2;                       // 阶段间空帧（等 Destroy/文件系统生效）

        // —— SAVE 场景观测值 ——
        private bool _firstRunNewRecord;
        private bool _secondRunNoRecord;
        private bool _bestKeptAfterWeakerRun;
        private bool _lastUpdated;
        private bool _totalsAccumulated;
        private bool _characterRunsCounted;
        private bool _codexDeduped;
        private bool _roundTripOk;
        private bool _corruptRecovered;

        // —— TELEMETRY 场景观测值 ——
        private bool _queuedBeforeFlush;
        private bool _flushedClean;                  // 正常落盘：失败 0、队列清空
        private int _failCount;                      // 制造失败后的 FlushFailCount
        private bool _batchReturned;                 // 失败批次回到队列（旧实现为 0）
        private bool _retrySucceeded;                // 目录恢复后重试成功
        private bool _queueDiscardedOnDisable;       // 关开关即清队列（旧实现残留）
        private string _telemetryDir = "";

        /// <summary>含全部需要转义的字符：双引号、反斜杠、换行、制表、中文。
        /// 转义修复前，仅反斜杠一项就足以让该行变成非法 JSON。</summary>
        public const string TrickyString = "路径C:\\Games\\星界\"引号\"\n换行\t制表";

        public static void Start(string scene)
        {
            var go = new GameObject("PersistenceProbe");
            var p = go.AddComponent<PersistenceProbe>();
            p._scene = scene;
            Application.logMessageReceived += p.OnLog;
        }

        /// <summary>无头环境必然出现的渲染噪声（同 UISettingsProbe）。</summary>
        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            // 还原实测改动，避免把"遥测关闭"留在用户的 settings.json 里
            // （save.json / telemetry 文件由驱动脚本在进程外备份与恢复）
            var s = SettingsManager.Instance;
            if (s != null && !s.Current.telemetry) s.SetTelemetry(true);
            Telemetry.LogDirOverrideForTest = null;
        }

        void Update()
        {
            if (_done) return;
            if (_wait > 0) { _wait--; return; }

            switch (_phase)
            {
                case 0: Setup(); break;
                case 1:
                    if (_scene == "SAVE") SavePhase(); else TelemetryPhase1();
                    break;
                case 2:
                    if (_scene == "SAVE") Finish(); else TelemetryPhase2();
                    break;
                default: Finish(); return;
            }
            _phase++;
            _wait = 2;
        }

        private void Setup()
        {
            SettingsManager.Ensure();
            SaveManager.Ensure();
            if (_scene == "TELEMETRY")
            {
                // 从空队列、开着遥测的确定状态开始
                SettingsManager.Instance.SetTelemetry(true);
                Telemetry.DiscardQueue();
                _telemetryDir = Telemetry.ExportPath;
            }
        }

        // ============================== SAVE ==============================

        private void SavePhase()
        {
            var save = SaveManager.Instance;

            // 从空档开始（驱动脚本已在进程外备份原 save.json）
            save.Data = new SaveManager.Record();
            save.Save();

            // 第一局（强成绩）：应刷新全部三项最佳纪录
            _firstRunNewRecord = save.SubmitRun(5, 61.5f, 100, 3, 0);
            // 第二局（弱成绩 + 通关）：不应刷新任何纪录，但 last*/累计要更新
            _secondRunNoRecord = !save.SubmitRun(3, 40f, 50, 2, 1, true);
            _bestKeptAfterWeakerRun = save.Data.bestWave == 5
                && Mathf.Abs(save.Data.bestTime - 61.5f) < 0.01f && save.Data.bestLevel == 3;
            _lastUpdated = save.Data.lastWave == 3 && save.Data.lastLevel == 2;
            _totalsAccumulated = save.Data.totalRuns == 2 && save.Data.totalClears == 1
                && save.Data.totalKills == 150 && Mathf.Abs(save.Data.totalTime - 101.5f) < 0.01f;
            _characterRunsCounted = save.Data.characterRuns != null
                && save.Data.characterRuns[0] == 1 && save.Data.characterRuns[1] == 1;

            // 图鉴：同类发现只记一次，Boss 击败累计
            save.DiscoverWeapon(3); save.DiscoverWeapon(3);
            save.DiscoverEnemy(1);
            save.DiscoverCombo("orbital+light"); save.DiscoverCombo("orbital+light");
            save.DiscoverBossDefeat(); save.DiscoverBossDefeat();
            _codexDeduped = save.Data.discoveredWeapons != null && save.Data.discoveredWeapons.Length == 1
                && save.Data.discoveredEnemies != null && save.Data.discoveredEnemies.Length == 1
                && save.Data.discoveredCombos != null && save.Data.discoveredCombos.Length == 1
                && save.Data.bossesDefeated == 2;

            // 落盘 → 直接从磁盘读回（走真实 Load，验证往返不丢字段）
            save.Save();
            var before = save.Data;
            save.Load();
            var after = save.Data;
            _roundTripOk = after.bestWave == before.bestWave
                && Mathf.Abs(after.bestTime - before.bestTime) < 0.01f
                && after.bestLevel == before.bestLevel
                && after.totalRuns == before.totalRuns
                && after.totalClears == before.totalClears
                && after.totalKills == before.totalKills
                && Mathf.Abs(after.totalTime - before.totalTime) < 0.01f
                && after.lastWave == before.lastWave
                && after.bossesDefeated == before.bossesDefeated
                && after.discoveredWeapons != null && after.discoveredWeapons.Length == 1
                && after.discoveredCombos != null && after.discoveredCombos.Length == 1
                && after.characterRuns != null && after.characterRuns[0] == 1;

            // 损坏存档：写入非法 JSON 后 Load 应回退为空档且不抛异常
            try { File.WriteAllText(SaveFilePath(), "{{{ 不是 JSON"); } catch { }
            save.Load();
            _corruptRecovered = save.Data != null && save.Data.totalRuns == 0
                && save.Data.bestWave == 0 && save.Data.discoveredWeapons != null;
            // 写回一份合法空档，别把垃圾留在磁盘上（驱动脚本稍后会恢复原档）
            save.Data = new SaveManager.Record();
            save.Save();
        }

        private static string SaveFilePath() =>
            Path.Combine(Application.persistentDataPath, "save.json");

        // ============================ TELEMETRY ============================

        private void TelemetryPhase1()
        {
            // 制造一批含全部特殊字符的事件（事件名/键/值三处都埋）
            Telemetry.Log("probe_marker", "tricky", TrickyString);
            Telemetry.Log("probe_normal", "wave", 3, "time", 61.5f, "ok", true);
            Telemetry.Log("probe_第二类", "中文键", "中文值");
            _queuedBeforeFlush = Telemetry.QueueCount == 3;

            Telemetry.Flush();
            _flushedClean = Telemetry.FlushFailCount == 0 && Telemetry.QueueCount == 0;
            // 文件合法性由驱动脚本用独立的 json.loads 判定——被测进程不自判
        }

        private void TelemetryPhase2()
        {
            // —— 制造真实的落盘失败：目录名与一个**确定存在的文件**同名，CreateDirectory 必抛 ——
            // 不用 save.json 当靶子：它在驱动脚本跑 --only TELEMETRY 时已被挪走，
            // 而那会让 CreateDirectory 反而成功（实测踩过：目录被建出来，碰撞落空）。
            // 这里自带一个靶子文件，并先断言它确实是文件——碰撞不落空是可判定的。
            string target = Path.Combine(Application.persistentDataPath, "_probe_blocker.tmp");
            bool targetReady = false;
            try
            {
                File.WriteAllText(target, "x");
                targetReady = File.Exists(target) && !Directory.Exists(target);
            }
            catch { }

            Telemetry.LogDirOverrideForTest = target;
            Telemetry.Log("probe_retry_a", "n", 1);
            Telemetry.Log("probe_retry_b", "n", 2);
            Telemetry.Flush();
            _failCount = Telemetry.FlushFailCount;
            _batchReturned = targetReady && Telemetry.QueueCount == 2;   // 旧实现：0（事件无声消失）

            // —— 目录恢复后重试：批次应被写进真实日志 ——
            Telemetry.LogDirOverrideForTest = null;
            Telemetry.Flush();
            _retrySucceeded = Telemetry.QueueCount == 0 && Telemetry.FlushFailCount == _failCount;

            // —— 关开关即清队列：旧实现残留 2 条，之后仍会被 Flush 写进文件 ——
            Telemetry.Log("probe_should_discard", "n", 3);
            Telemetry.Log("probe_should_discard", "n", 4);
            bool queuedTwo = Telemetry.QueueCount == 2;
            SettingsManager.Instance.SetTelemetry(false);
            _queueDiscardedOnDisable = queuedTwo && Telemetry.QueueCount == 0;
            SettingsManager.Instance.SetTelemetry(true);        // OnDestroy 兜底也会还原

            try { File.Delete(target); } catch { }              // 清掉靶子文件
        }

        // ============================== 判定 ==============================

        private void Finish()
        {
            _done = true;
            bool pass;

            Debug.Log("[SMOKE] ===== 持久化实测结果 =====");
            Debug.Log("[SMOKE] 场景              " + _scene);

            if (_scene == "SAVE") pass = FinishSave();
            else pass = FinishTelemetry();

            Debug.Log("[SMOKE] 运行期异常        " + _errors);
            if (_errors > 0)
                Debug.Log("[SMOKE] 首条错误          " + _firstError);
            pass = pass && _errors == 0;

            Debug.Log("[SMOKE] 结论              " + (pass ? "通过" : "未通过"));
            Application.Quit(pass ? 0 : 1);
        }

        private bool FinishSave()
        {
            Debug.Log("[SMOKE] 强局刷新纪录      " + (_firstRunNewRecord ? "是" : "否"));
            Debug.Log("[SMOKE] 弱局不刷纪录      " + (_secondRunNoRecord ? "是" : "否"));
            Debug.Log("[SMOKE] 弱局后纪录保持    " + (_bestKeptAfterWeakerRun ? "是" : "否"));
            Debug.Log("[SMOKE] 上局成绩已更新    " + (_lastUpdated ? "是" : "否"));
            Debug.Log("[SMOKE] 累计统计正确      " + (_totalsAccumulated ? "是" : "否"));
            Debug.Log("[SMOKE] 角色使用计数      " + (_characterRunsCounted ? "是" : "否"));
            Debug.Log("[SMOKE] 图鉴去重          " + (_codexDeduped ? "是" : "否"));
            Debug.Log("[SMOKE] 落盘读档往返      " + (_roundTripOk ? "是" : "否"));
            Debug.Log("[SMOKE] 损坏存档回退      " + (_corruptRecovered ? "是" : "否"));

            return _firstRunNewRecord && _secondRunNoRecord && _bestKeptAfterWeakerRun
                && _lastUpdated && _totalsAccumulated && _characterRunsCounted
                && _codexDeduped && _roundTripOk && _corruptRecovered;
        }

        private bool FinishTelemetry()
        {
            Debug.Log("[SMOKE] 事件进入队列      " + (_queuedBeforeFlush ? "是" : "否"));
            Debug.Log("[SMOKE] 正常落盘          " + (_flushedClean ? "是" : "否"));
            Debug.Log("[SMOKE] 落盘失败次数      " + _failCount);
            Debug.Log("[SMOKE] 失败批次回队列    " + (_batchReturned ? "是" : "否"));
            Debug.Log("[SMOKE] 重试后落盘成功    " + (_retrySucceeded ? "是" : "否"));
            Debug.Log("[SMOKE] 关开关即清队列    " + (_queueDiscardedOnDisable ? "是" : "否"));
            Debug.Log("[SMOKE] 日志目录          " + _telemetryDir);

            return _queuedBeforeFlush && _flushedClean && _failCount >= 1
                && _batchReturned && _retrySucceeded && _queueDiscardedOnDisable;
        }
    }
}
