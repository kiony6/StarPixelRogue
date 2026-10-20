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
    ///
    /// M19 修掉三处**会让限时形同虚设**的缺陷（详见 <see cref="Tick"/> 与 <see cref="CanStartRun"/>）：
    /// ① 提示弹窗挂起时停止计时——玩家只要不点「关闭」就永远到不了上限；
    /// ② 强制退出只生效一次——回主菜单再点「开始游戏」即可继续玩；
    /// ③ 强制退出不关残留弹窗——主菜单被遮罩盖住，且挂起标记会一直冻结计时。
    /// 计时判定统一收敛到 <see cref="Tick"/>，实测探针与线上共用同一份逻辑。
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
            /// <summary>当日预警是否已弹过（M19：落盘，避免重启进程后被重复弹一次）。</summary>
            public bool warned = false;
        }

        private PlaytimeData _today = new PlaytimeData();
        private float _sinceAdultHint;
        private int _lastSavedSecond;
        private bool _minorForcedExit;
        private bool _wasRunning;
        private GameObject _hintRoot;

        private static readonly string FilePath =
            Path.Combine(Application.persistentDataPath, "playtime.json");

        /// <summary>今日已游戏秒数（青少年模式下受上限约束）。</summary>
        public float TodaySeconds => _today.seconds;

        // —— M19 实测可观测指标：验证「该发生的事发生了几次」必须先有计数器 ——

        /// <summary>青少年模式到点前预警的弹出次数。</summary>
        public int WarnCount { get; private set; }

        /// <summary>青少年模式到点强制退出的次数。</summary>
        public int ForceExitCount { get; private set; }

        /// <summary>成人模式健康提示的弹出次数。</summary>
        public int HintCount { get; private set; }

        /// <summary>时长用尽后被拦下的开局次数（<see cref="CanStartRun"/> 返回 false 的次数）。</summary>
        public int BlockedStartCount { get; private set; }

        /// <summary>当前是否有提示弹窗挂起。</summary>
        public bool HintShowing => _hintRoot != null;

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
            Instance._minorForcedExit = false;
            Instance._sinceAdultHint = 0f;
            // M19：一并关掉可能挂着的提示弹窗——它在旧实现里会一直冻结计时，
            // 若跨模式切换时残留，此后**永远不再累计时长**（等于限时整个失效）。
            Instance.CloseHint();
            Instance.Save();
        }

        /// <summary>M19 实测：把计数器清零（分场景断言时避免上一场景的计数串味）。</summary>
        public static void ResetCounters()
        {
            if (Instance == null) return;
            Instance.WarnCount = 0;
            Instance.ForceExitCount = 0;
            Instance.HintCount = 0;
            Instance.BlockedStartCount = 0;
        }

        /// <summary>
        /// M19 实测：改写记录里存的日期（不落盘），使下一次 <see cref="Tick"/> 走真实的跨天重置分支。
        /// 用它验证"明天能重新玩"——而不是另写一份日期判断来冒充。
        /// </summary>
        public void SetStoredDateForTest(string date)
        {
            _today.date = date ?? "";
            _lastSavedSecond = -1;
        }

        /// <summary>
        /// M19 实测：重新从磁盘读一次（验证 Save/Load 往返不会把当日时长读没，
        /// 也不会因为读档而误判成跨天）。走的是线上同一个 <see cref="Load"/>。
        /// </summary>
        public void ReloadForTest() => Load();

        private bool IsMinor()
        {
            return SettingsManager.Instance != null && SettingsManager.Instance.Current.minorMode;
        }

        /// <summary>青少年模式当日时长是否已用尽。</summary>
        public static bool MinorExhausted =>
            Instance != null && Instance.IsMinor() && Instance._today.seconds >= MinorDailyLimitSeconds;

        /// <summary>
        /// 是否允许开始新一局。
        /// M19 缺陷修复：此前仅靠「强制退出」把玩家踢回主菜单，而该动作每天只触发一次——
        /// 回菜单再点「开始游戏」照样能建世界，限时被完整绕过。故在开局入口统一拦截，
        /// 只要当日累计已达上限就**不允许再开局**（跨天由 <see cref="RollDateIfNeeded"/> 自动解禁）。
        /// </summary>
        public static bool CanStartRun()
        {
            if (Instance == null) return true;
            if (!MinorExhausted) return true;
            Instance.BlockedStartCount++;
            return false;
        }

        void Update()
        {
            Telemetry.Tick(Time.unscaledDeltaTime);        // 遥测队列定时落盘（与时长统计同源驱动）
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 时长推进的**唯一入口**：线上由 Update 以真实 deltaTime 驱动，
        /// 无头实测由探针以固定步长驱动——两者共用同一份判定逻辑，
        /// 因此探针断言的是产品代码本身，而不是照抄一份的副本。
        ///
        /// M19 关键修复：旧实现开头有 `if (_hintShowing) return;`，
        /// 导致「提示弹窗未关闭 → 时长停止累计 → 永远到不了上限」，
        /// 玩家只要把预警弹窗晾在那儿就能无限续玩。计时与弹窗状态自此彻底解耦：
        /// 弹窗只负责"不重复弹"，绝不负责"暂停计时"。
        /// </summary>
        public void Tick(float dt)
        {
            // 新一局开始：清掉上一局的"已强制退出"标记，使每局独立判定
            // （旧实现只在跨天/重置时清，故第二局起永远不再踢人）。
            if (GameRunning && !_wasRunning) _minorForcedExit = false;
            _wasRunning = GameRunning;

            // 跨天判定与"是否正在对局"无关：在菜单里过零点同样该重置，
            // 否则要等到下一次开局才翻页（旧实现正是把它关在对局分支里）。
            RollDateIfNeeded();

            if (!GameRunning) return;                 // 菜单/暂停不计时
            if (dt <= 0f) return;

            _today.seconds += dt;                     // 无论是否有弹窗挂起，一律累计
            _sinceAdultHint += dt;

            if (IsMinor())
            {
                if (!_today.warned && _today.seconds >= MinorDailyLimitSeconds - MinorWarnBeforeSeconds)
                {
                    _today.warned = true;
                    ShowHint(Localization.T("settings.minor_mode"), Localization.T("playtime.warn"), true);
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
                ShowHint(Localization.T("playtime.health_title"), Localization.T("playtime.health"), false);
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
            ForceExitCount++;
            // M19：先关掉可能还挂着的预警弹窗。旧实现不关，于是
            // ① 主菜单被这层遮罩盖住；② _hintShowing 一直为真，下一局完全不计时。
            CloseHint();
            Debug.Log("[Playtime] 青少年模式达到每日时长上限，强制返回主菜单");
            // M47：主菜单必须先 **Ensure** 再提示，不能写成 `if (Instance != null)`。
            // 那样写的后果是：任何没建过主菜单的启动路径（无头实测入口、将来可能的
            // 快捷开局/续玩入口）一旦到点，玩家会被**静默**踢出——世界拆了、菜单没有、
            // 一行解释也没有，屏幕上只剩一个空场景。合规功能最忌讳的就是这种沉默失败：
            // 玩家不知道自己为什么不能玩了。可惜它此前从未被任何测试走到过
            // （PlaytimeProbe 自己会先 MainMenu.Ensure()，恰好把这条静默路径遮住了）。
            UI.MainMenu.Ensure();
            UI.MainMenu.Instance.ShowWithNotice(Localization.T("playtime.force_exit"));
            GameBuilder.TeardownWorld();
        }

        /// <summary>
        /// 弹出提示弹窗（青少年预警 / 成人健康提示共用）。
        /// 同时只允许挂一个：重复调用直接忽略，因此**不会**叠出多层遮罩。
        /// </summary>
        private void ShowHint(string title, string body, bool isMinorWarn)
        {
            if (_hintRoot != null) return;            // 已有弹窗挂起，不再叠
            if (isMinorWarn) WarnCount++; else HintCount++;

            var (mask, root) = UIFactory.CreateModal("PlaytimeHint", new Color(0f, 0f, 0f, 0.75f));
            _hintRoot = mask;
            UIFactory.CreateText(root.transform, "Title", title, new Vector2(0, 90), 28);
            UIFactory.CreateText(root.transform, "Body", body, new Vector2(0, 20), 20);
            UIFactory.CreateButton(root.transform, "OK", Localization.T("legal.close"), new Vector2(0, -80),
                new Vector2(200, 48), () => CloseHint());
        }

        /// <summary>关闭挂起的提示弹窗（点击「关闭」、强制退出、重置当日计时时调用）。</summary>
        private void CloseHint()
        {
            if (_hintRoot == null) return;
            var go = _hintRoot;
            _hintRoot = null;                         // 先解引用：Destroy 延迟到帧末，不能靠它判空
            UIFactory.Destroy(go);
        }

        /// <summary>
        /// M47：新一局开始时收起**跨局残留**的提示遮罩（由 <c>GameBuilder.Build</c> 调用）。
        ///
        /// 它与 M46 修的结算面板是同一个形态：<c>ShowHint</c> 用
        /// <c>UIFactory.CreateModal</c> 在场景根上 new 出一层全屏遮罩，
        /// 既不归 GameBuilder 的 <c>_created</c> 管，也不会随拆世界消失。
        /// 于是这条真实路径会漏：玩家在 55 分钟预警挂起时没点「关闭」→ 继续玩到死 →
        /// 结算面板弹出（两层模态叠在一起）→ 点「重新开始」→ 新世界建好了，
        /// 而那层写着"今日时长已达 55 分钟"的遮罩**原封不动盖在新世界上**。
        /// 提示本身跨局有效（剩下的 5 分钟还在走），但"遮罩跨局残留"不是——
        /// 它挡的是新一局的操作界面。故收起它的责任放在"建世界"这一侧。
        /// </summary>
        public void DismissHint() => CloseHint();

        /// <summary>M19 实测：模拟玩家点击弹窗的「关闭」按钮（走产品同一个入口）。</summary>
        public void DismissHintForTest() => DismissHint();

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
