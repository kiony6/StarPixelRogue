using System;
using System.IO;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 本地存档（M5）：战绩纪录 + 累计统计。单文件 JSON，原子写。
    /// 隐私合规：仅本机数值统计，不含任何个人身份信息（见隐私政策）。
    /// </summary>
    public class SaveManager : MonoSingleton<SaveManager>
    {
        /// <summary>
        /// M13：存档 schema 版本。结构不兼容变更时递增；云端收到更高版本会跳过（拒绝用未知结构覆盖本地）。
        /// </summary>
        public const int SchemaVersion = 1;

        [Serializable]
        public class Record
        {
            // M13 云存档：schema 版本 + 最后保存时间（UTC，ISO8601 定长，可直接按字典序比较取新）。
            // 二者为纯技术字段，不含任何个人信息。
            public int schemaVersion = SchemaVersion;
            public string savedAt = "";

            public int bestWave = 0;
            public float bestTime = 0f;
            public int bestLevel = 0;
            public int totalRuns = 0;
            public int totalClears = 0;   // M8 方案 A：达标通关局数
            public int totalKills = 0;
            public float totalTime = 0f;
            // 上次一局
            public int lastWave = 0;
            public float lastTime = 0f;
            public int lastLevel = 0;

            // M6.2：角色选择与各角色使用次数（图鉴统计）
            public int lastCharacterId = 0;
            public int[] characterRuns = new int[2];

            // M6.4：图鉴发现记录（武器 id / 敌种 kind / 已激活组合 tag 对 / Boss 击败数）
            public int[] discoveredWeapons = new int[0];
            public int[] discoveredEnemies = new int[0];
            public string[] discoveredCombos = new string[0];
            public int bossesDefeated = 0;
        }

        public Record Data = new Record();

        private static readonly string FilePath =
            Path.Combine(Application.persistentDataPath, "save.json");

        protected override void Awake()
        {
            base.Awake();
            Load();
        }

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("SaveManager").AddComponent<SaveManager>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoEnsure() => Ensure();

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var loaded = JsonUtility.FromJson<Record>(File.ReadAllText(FilePath));
                if (loaded != null) Data = loaded;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] 读取失败: " + e.Message);
                Data = new Record();
            }
        }

        /// <summary>M13：本地存档落盘广播，云存档服务据此调度上行同步。</summary>
        public static event Action Saved;

        public void Save()
        {
            WriteLocal();
            Saved?.Invoke();
        }

        /// <summary>
        /// 只落盘、不广播（云端拉取写入时用）——避免"刚拉下来又立刻推回去"的回环。
        /// </summary>
        private void WriteLocal()
        {
            try
            {
                Data.savedAt = UtcNowIso();
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(Data, true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] 写入失败: " + e.Message);
            }
        }

        /// <summary>
        /// M13：采用云端存档（仅当裁决为"云端更新"时调用）。整体替换且不广播 <see cref="Saved"/>。
        /// </summary>
        public bool ReplaceFromCloud(Record remote)
        {
            if (remote == null) return false;
            Data = remote;
            WriteLocal();
            return true;
        }

        /// <summary>M13：当前 UTC 时间戳（ISO8601 定长格式，便于跨端字典序比较）。</summary>
        public static string UtcNowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        /// <summary>M13：比较两条存档的新旧，返回 >0 表示 a 更新，0 表示相当。</summary>
        public static int CompareSavedAt(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
            if (string.IsNullOrEmpty(a)) return -1;
            if (string.IsNullOrEmpty(b)) return 1;
            return string.CompareOrdinal(a, b);
        }

        /// <summary>
        /// 提交一局战绩（GameOverPanel 调用）。返回是否刷新了任一最佳纪录。
        /// characterId ≥ 0 时记录角色使用（图鉴统计）。
        /// victory = 达标通关（M8 方案 A），计入 totalClears。
        /// </summary>
        public bool SubmitRun(int wave, float time, int kills, int level, int characterId = -1, bool victory = false)
        {
            bool newRecord = false;
            if (wave > Data.bestWave) { Data.bestWave = wave; newRecord = true; }
            if (time > Data.bestTime) { Data.bestTime = time; newRecord = true; }
            if (level > Data.bestLevel) { Data.bestLevel = level; newRecord = true; }

            Data.lastWave = wave;
            Data.lastTime = time;
            Data.lastLevel = level;
            Data.totalRuns++;
            if (victory) Data.totalClears++;   // M8 方案 A
            Data.totalKills += Mathf.Max(0, kills);
            Data.totalTime += Mathf.Max(0f, time);

            if (characterId >= 0)
            {
                Data.lastCharacterId = Mathf.Clamp(characterId, 0, 1);
                if (Data.characterRuns == null || Data.characterRuns.Length != 2)
                    Data.characterRuns = new int[2];
                Data.characterRuns[Data.lastCharacterId]++;
            }

            Save();

            // M54：同一局同时进本机榜单。放在这里而不是结算面板里，
            // 是因为"提交一局战绩"只有这一个入口——分散到 UI 上迟早漏掉某条路径
            // （例如青少年模式强制结算）。未登录时 RankStore 自己会拒收（不记匿名条目）。
            var rank = RankStore.Instance;
            if (rank != null) rank.Submit(AccountStore.Instance != null ? AccountStore.Instance.Account : "",
                wave, level, time);

            return newRecord;
        }

        // —— M6.4 图鉴：发现记录（持久化于本机，仅数值/枚举，不含个人信息）——

        public void DiscoverWeapon(int id)
        {
            if (Contains(Data.discoveredWeapons, id)) return;
            Data.discoveredWeapons = Append(Data.discoveredWeapons, id);
            Save();
        }

        public void DiscoverEnemy(int kind)
        {
            if (Contains(Data.discoveredEnemies, kind)) return;
            Data.discoveredEnemies = Append(Data.discoveredEnemies, kind);
            Save();
        }

        public void DiscoverCombo(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var list = new System.Collections.Generic.List<string>(Data.discoveredCombos ?? new string[0]);
            if (list.Contains(key)) return;
            list.Add(key);
            Data.discoveredCombos = list.ToArray();
            Save();
        }

        public void DiscoverBossDefeat()
        {
            Data.bossesDefeated++;
            Save();
        }

        private static bool Contains(int[] arr, int v)
        {
            if (arr == null) return false;
            foreach (var x in arr) if (x == v) return true;
            return false;
        }

        private static int[] Append(int[] arr, int v)
        {
            var list = new System.Collections.Generic.List<int>(arr ?? new int[0]);
            list.Add(v);
            return list.ToArray();
        }

        /// <summary>格式化秒数为 m:ss。</summary>
        public static string FormatTime(float seconds)
        {
            if (seconds <= 0f) return "0:00";
            int m = Mathf.FloorToInt(seconds / 60f);
            int s = Mathf.FloorToInt(seconds % 60f);
            return m + ":" + s.ToString("00");
        }
    }
}
