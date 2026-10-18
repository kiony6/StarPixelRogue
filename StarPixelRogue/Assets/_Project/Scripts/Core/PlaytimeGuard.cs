using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 游戏时长守护（M5 合规框架，对应国家防沉迷要求的本地实现层）：
    /// - 青少年模式：每日累计游戏时长上限 <see cref="MinorDailyLimitSeconds"/>，
    ///   到点前 5 分钟预警、到点强制返回主菜单；跨天自动重置；
    ///   开启模式时重置当日计时（防通过切换绕过）。
    /// - 成人模式：每 <see cref="AdultHintIntervalSeconds"/> 分钟弹出一次健康游戏提示。
    /// - <see cref="GameRunning"/> 由 GameBuilder 维护：只有对局进行中才计时（菜单/暂停不计）。
    ///
    /// ⚠ 正式上线合规要求：必须接入国家新闻出版署实名认证系统校验年龄，
    /// 届时在 <see cref="VerifyMinor"/> 的 TODO 处替换为官方 SDK 调用即可，
    /// 本类的限时/提示逻辑与展示层无需改动。
    /// </summary>
    public class PlaytimeGuard : MonoSingleton<PlaytimeGuard>
    {
        /// <summary>青少年模式每日时长上限（秒）。官方规则为 1 小时，此处一致。</summary>
        public const int MinorDailyLimitSeconds = 60 * 60;

        /// <summary>预警提前量（秒）。</summary>
        public const int MinorWarnBeforeSeconds = 5 * 60;

        /// <summary>成人健康提示间隔（秒）。</summary>
        public const int AdultHintIntervalSeconds = 45 * 60;

        /// <summary>对局进行中标记（GameBuilder.Build/TeardownWorld 维护）。</summary>
        public static bool GameRunning { get; set; }

        [Serializable]
        private class PlaytimeData
        {
            public string date = "";
            public float seconds = 0f;
        }

        private PlaytimeData _today = new PlaytimeData();
        private float _sinceAdultHint;
        private int _lastSavedSecond;
        private bool _minorWarned;
        private bool _minorForcedExit;
        private bool _hintShowing;

        private static readonly string FilePath =
            Path.Combine(Application.persistentDataPath, "playtime.json");

        /// <summary>今日已游戏秒数（青少年模式下受上限约束）。</summary>
        public float TodaySeconds => _today.seconds;

        protected override void Awake()
        {
            base.Awake();
            Load();
        }

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("PlaytimeGuard").AddComponent<PlaytimeGuard>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoEnsure() => Ensure();

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var loaded = JsonUtility.FromJson<PlaytimeData>(File.ReadAllText(FilePath));
                if (loaded != null) _today = loaded;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Playtime] 读取失败: " + e.Message);
                _today = new PlaytimeData();
            }
            RollDateIfNeeded();
        }

        private void Save()
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_today, true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e) { Debug.LogWarning("[Playtime] 写入失败: " + e.Message); }
        }

        private string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private void RollDateIfNeeded()
        {
            if (_today.date != Today())
            {
                _today = new PlaytimeData { date = Today(), seconds = 0f };
                _lastSavedSecond = -1;
                _minorWarned = false;
                _minorForcedExit = false;
                Save();
            }
        }

        /// <summary>重置当日计时（SettingsManager 开启青少年模式时调用）。</summary>
        public static void ResetToday()
        {
            if (Instance == null) return;
            Instance._today = new PlaytimeData { date = Instance.Today(), seconds = 0f };
            Instance._lastSavedSecond = -1;
            Instance._minorWarned = false;
            Instance._minorForcedExit = false;
            Instance.Save();
        }

        private bool IsMinor()
        {
            return SettingsManager.Instance != null && SettingsManager.Instance.Current.minorMode;
        }

        void Update()
        {
            Telemetry.Tick(Time.unscaledDeltaTime);        // 遥测队列定时落盘（与时长统计同源驱动）
            if (!GameRunning) return;                 // 菜单/暂停不计时
            if (_hintShowing) return;                 // 提示挂起时不累计，避免反复弹

            RollDateIfNeeded();
            float dt = Time.unscaledDeltaTime;        // 选卡面板 timeScale=0 时也应计时
            _today.seconds += dt;
            _sinceAdultHint += dt;

            if (IsMinor())
            {
                if (!_minorWarned && _today.seconds >= MinorDailyLimitSeconds - MinorWarnBeforeSeconds)
                {
                    _minorWarned = true;
                    ShowMinorWarning(Localization.T("settings.minor_mode"),
                        "今日游戏时间即将达到上限，请注意休息。");
                }
                if (!_minorForcedExit && _today.seconds >= MinorDailyLimitSeconds)
                {
                    _minorForcedExit = true;
                    Save();
                    ForceExitMinor();
                    return;
                }
            }
            else if (_sinceAdultHint >= AdultHintIntervalSeconds)
            {
                _sinceAdultHint = 0f;
                ShowMinorWarning("健康游戏提示", "您已连续游戏 45 分钟，适度游戏益脑，沉迷游戏伤身。");
            }

            if (Mathf.FloorToInt(_today.seconds) != _lastSavedSecond)   // 每秒最多落盘一次
            {
                _lastSavedSecond = Mathf.FloorToInt(_today.seconds);
                Save();
            }
        }

        private void OnApplicationPause(bool pause)
        {
            if (pause) Save();                       // 切后台立即落盘，防杀进程丢计时
        }

        private void OnApplicationQuit() => Save();

        private void ForceExitMinor()
        {
            Debug.Log("[Playtime] 青少年模式达到每日时长上限，强制返回主菜单");
            if (UI.MainMenu.Instance != null)
                UI.MainMenu.Instance.ShowWithNotice("今日游戏时间已达上限（青少年模式），请明天再来。");
            GameBuilder.TeardownWorld();
        }

        private void ShowMinorWarning(string title, string body)
        {
            if (_hintShowing) return;
            _hintShowing = true;
            var (mask, root) = UIFactory.CreateModal("PlaytimeHint", new Color(0f, 0f, 0f, 0.75f));
            UIFactory.CreateText(root.transform, "Title", title, new Vector2(0, 90), 28);
            UIFactory.CreateText(root.transform, "Body", body, new Vector2(0, 20), 20);
            UIFactory.CreateButton(root.transform, "OK", Localization.T("legal.close"), new Vector2(0, -80),
                new Vector2(200, 48), () =>
                {
                    UIFactory.Destroy(mask);
                    _hintShowing = false;
                });
        }

        /// <summary>
        /// 实名认证结果接入点（TODO：正式版对接官方实名认证 SDK）。
        /// 返回值 true = 未成年人（走青少年模式限时），false = 已成年。
        /// </summary>
        public static bool VerifyMinor()
        {
            // TODO(M5.1): 调用国家新闻出版署实名认证系统 / 渠道 SDK 的年龄校验接口，
            // 依据返回值 SettingsManager.SetMinorMode(...)。在此之前，
            // 年龄判定以玩家在设置中手动开启的青少年模式为准（本地框架）。
            return SettingsManager.Instance != null && SettingsManager.Instance.Current.minorMode;
        }
    }
}
