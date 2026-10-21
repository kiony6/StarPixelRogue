using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 本地排行榜（M54）：每个账号保留它<b>最好的一局</b>，全榜按成绩排序。
    /// 单文件 JSON（Application.persistentDataPath/ranks.json），原子写。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只留"最好一局"而不是逐局追加：榜的意义是"谁打得更远"，
    /// 逐局追加会让它变成一份流水日志，前十名被同一个人刷满是迟早的事。
    /// </para>
    /// <para>
    /// <b>这是本机榜单</b>：没有服务端，看不见别人的成绩。名字里不带"全球/世界"，
    /// UI 上也要照实说——假装有对手比没有榜单更糟。
    /// </para>
    /// </remarks>
    public class RankStore : MonoSingleton<RankStore>
    {
        [Serializable]
        public class Entry
        {
            public string account = "";
            public int wave = 0;
            public int level = 0;
            public float time = 0f;      // 该局存活秒数
            public string at = "";       // 达成时间（UTC，ISO8601）
        }

        [Serializable]
        public class Data
        {
            public List<Entry> entries = new List<Entry>();
        }

        public Data Current = new Data();

        private static readonly string FilePath =
            Path.Combine(Application.persistentDataPath, "ranks.json");

        protected override void Awake()
        {
            base.Awake();
            Load();
        }

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("RankStore").AddComponent<RankStore>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoEnsure() => Ensure();

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var loaded = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    Current = loaded;
                    if (Current.entries == null) Current.entries = new List<Entry>();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Rank] 读取失败: " + e.Message);
                Current = new Data();
            }
        }

        public void Save()
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(Current, true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Rank] 写入失败: " + e.Message);
            }
        }

        /// <summary>
        /// 比较两条成绩：波次多者靠前 → 同级看等级 → 再看存活时长（长者优先）→
        /// 全平手时先达成者靠前（后到的人不该把先到的人挤下去）。
        /// </summary>
        public static int Compare(Entry a, Entry b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            if (a.wave != b.wave) return b.wave.CompareTo(a.wave);
            if (a.level != b.level) return b.level.CompareTo(a.level);
            if (Math.Abs(a.time - b.time) > 0.001f) return b.time.CompareTo(a.time);
            return string.CompareOrdinal(a.at ?? "", b.at ?? "");
        }

        /// <summary>
        /// 提交一局成绩。仅在比该账号既有成绩更好时覆盖，返回是否刷新了纪录。
        /// 未登录不记：榜上一串空名次的"匿名"条目没有意义。
        /// </summary>
        public bool Submit(string account, int wave, int level, float time)
        {
            if (string.IsNullOrEmpty(account)) return false;
            if (Current.entries == null) Current.entries = new List<Entry>();

            Entry mine = null;
            for (int i = 0; i < Current.entries.Count; i++)
            {
                var e = Current.entries[i];
                if (e != null && string.Equals(e.account, account, StringComparison.Ordinal)) { mine = e; break; }
            }

            var fresh = new Entry
            {
                account = account,
                wave = wave,
                level = level,
                time = time,
                at = SaveManager.UtcNowIso(),
            };

            if (mine == null)
            {
                Current.entries.Add(fresh);
                Save();
                return true;
            }
            if (Compare(fresh, mine) >= 0) return false;   // 没打过自己，不覆盖

            mine.wave = fresh.wave;
            mine.level = fresh.level;
            mine.time = fresh.time;
            mine.at = fresh.at;
            Save();
            return true;
        }

        /// <summary>排序后的全榜（不改动库内顺序）。</summary>
        public List<Entry> Sorted()
        {
            var list = new List<Entry>();
            if (Current.entries != null)
                for (int i = 0; i < Current.entries.Count; i++)
                    if (Current.entries[i] != null) list.Add(Current.entries[i]);
            list.Sort(Compare);
            return list;
        }

        public List<Entry> Top(int n)
        {
            var all = Sorted();
            if (all.Count <= n) return all;
            return all.GetRange(0, n);
        }

        public Entry BestFor(string account)
        {
            if (string.IsNullOrEmpty(account) || Current.entries == null) return null;
            for (int i = 0; i < Current.entries.Count; i++)
            {
                var e = Current.entries[i];
                if (e != null && string.Equals(e.account, account, StringComparison.Ordinal)) return e;
            }
            return null;
        }

        /// <summary>某账号的名次（1 起；无记录返回 0）。</summary>
        public int RankOf(string account)
        {
            var all = Sorted();
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].account, account, StringComparison.Ordinal)) return i + 1;
            return 0;
        }

        public int Count => Current.entries != null ? Current.entries.Count : 0;

        /// <summary>清空榜单（仅供无头探针造干净初始态）。</summary>
        public void ResetForProbe()
        {
            Current = new Data();
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception) { }
        }
    }
}
