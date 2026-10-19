using StarPixel.Core;
using StarPixel.UI;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// 无头防沉迷实测探针（M19）：仅当进程带 <c>-playtimeProbe</c> 时由 <c>GameBuilder</c> 创建。
    ///
    /// 为什么它必须存在：防沉迷是**合规功能**，而且是一个纯时间驱动的状态机——
    /// 真实等待 1 小时去验证它不现实，此前它也就完全没有实测覆盖。
    /// 解决办法是把时长推进做成可注入的：<c>PlaytimeGuard.Tick(dt)</c> 是线上唯一的计时入口
    /// （Update 只是把真实 deltaTime 喂进去），探针用固定步长喂同样的入口，
    /// 于是"几秒钟跑完一天"验证的是**产品代码本身**，而不是照抄一份判定逻辑的副本。
    ///
    /// 三个场景覆盖的正是三类"限时形同虚设"的绕过手法：
    ///   ① 青少年模式：55 分钟弹预警后**不点关闭**，继续推进——旧实现此时停止计时，
    ///      玩家把弹窗晾着就能无限续玩；
    ///   ② 二次开局：被强制退出后回主菜单再点「开始游戏」——旧实现每天只踢一次，
    ///      再开局就能继续玩；
    ///   ③ 成人模式：健康提示弹出后同样不点关闭——旧实现同样冻结计时，提示间隔失真。
    ///
    /// 判据的区分力（本轮最要紧的一条）：
    ///   每条断言都指向"修复前必然失败"的可观测量——强制退出次数、世界是否还活着、
    ///   拦截次数、弹窗挂起期间的时长增量。只断言"没崩溃"的话，
    ///   一个压根不执行限时的实现也能全绿。
    ///
    /// 正常启动不带该参数时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class PlaytimeProbe : MonoBehaviour
    {
        private const int FrameGap = 2;          // 每个阶段之间空出的帧数（等 Destroy 真正生效）
        private const float Step = 60f;          // 单步推进 60 秒（一天 = 60 步，远快于真实等待）

        private string _scene = "MINOR";
        private string _lang = Localization.LangZh;
        private int _errors;
        private string _firstError = "";
        private bool _done;

        private int _phase;                      // -1 = 未开始；0..N = 各阶段
        private int _wait;                       // 阶段间等待计数

        // —— 青少年模式（MINOR）观测值 ——
        private bool _startOk;                   // 阶段 0：开局成功（世界建起来了）
        private float _secAt54Min;               // 推进到 54 分钟时的累计秒数
        private bool _aliveAt54Min;              // 54 分钟时世界还在（对局未被误踢）
        private int _warnAt54Min;                // 54 分钟时不应已有预警
        private int _warnAt55Min;                // 55 分钟应恰好弹 1 次预警
        private bool _hintAt55Min;               // 55 分钟时弹窗确实挂起了
        private bool _hintStillOpen;             // 后续阶段**故意不关**它
        private float _secAt65Min;               // 不关弹窗继续推进后的累计秒数（关键：必须在涨）
        private int _forceAt65Min;               // 65 分钟应强制退出 1 次
        private bool _runningAfterForce;         // 强制退出后对局标记应已复位
        private bool _worldTornDown;             // 强制退出后世界应被拆掉
        private int _blocked;                    // 二次开局被拦下的次数
        private bool _secondRunBlocked;          // 二次开局确实没建起世界
        private float _secAfterRollover;         // 跨天后应归零
        private bool _thirdRunOk;                // 跨天后应能重新开局
        private string _noticeZh = "";           // 强制退出提示（中文）
        private string _noticeEn = "";           // 强制退出提示（英文）

        // —— 成人模式（ADULT）观测值 ——
        private int _hintAt45Min;                // 45 分钟应弹 1 次健康提示
        private bool _hintAt45MinShowing;
        private float _adultSecBefore;           // 提示挂起时的累计秒数
        private float _adultSecAfter;            // 不关提示再推进 10 分钟后的秒数（关键：必须涨 600）
        private int _hintAt90Min;                // 关掉提示后再推进 45 分钟应弹第 2 次
        private float _persistBefore;            // 落盘前秒数
        private float _persistAfter;             // 重新读档后的秒数（关键：不能被读没）

        public static void Start(string scene, string lang)
        {
            var go = new GameObject("PlaytimeProbe");
            var p = go.AddComponent<PlaytimeProbe>();
            p._scene = scene;
            p._lang = lang;
            Application.logMessageReceived += p.OnLog;
        }

        /// <summary>无头环境必然出现的渲染噪声（同 UISettingsProbe）：不计数，否则淹没真错误。</summary>
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

        void OnDestroy() => Application.logMessageReceived -= OnLog;

        void Update()
        {
            if (_done) return;
            if (_wait > 0) { _wait--; return; }      // 阶段之间空几帧，等 Destroy/单例清理生效

            bool minor = _scene != "ADULT";
            switch (_phase)
            {
                case 0: PhaseSetup(minor); break;
                case 1: PhaseBeforeLimit(minor); break;
                case 2: PhaseAtWarnLine(minor); break;
                case 3: PhasePastLimit(minor); break;
                case 4: PhaseSecondRun(minor); break;
                case 5: PhaseRollover(minor); break;
                case 6: PhaseLanguage(); break;
                default: Finish(); return;
            }
            _phase++;
            _wait = FrameGap;
        }

        // —— 阶段 0：进入场景设定并开一局 ——

        private void PhaseSetup(bool minor)
        {
            Localization.SetLanguage(_lang, silent: true);
            SettingsManager.Ensure();
            SettingsManager.Instance.SetMinorMode(minor);     // 走真实接口（会写设置）
            PlaytimeGuard.Ensure();
            PlaytimeGuard.ResetToday();                       // 从 0 开始，避免上一场景残留
            PlaytimeGuard.ResetCounters();
            MainMenu.Ensure();
            GameBuilder.Build();                              // 开局（应成功）
            _startOk = PlaytimeGuard.GameRunning && StarPixel.Enemy.EnemyManager.Instance != null;
        }

        /// <summary>按 60 秒步长推进到指定秒数（喂的是线上同一个 Tick）。</summary>
        private static void AdvanceTo(float targetSeconds)
        {
            var g = PlaytimeGuard.Instance;
            if (g == null) return;
            float remain = targetSeconds - g.TodaySeconds;
            if (remain <= 0f) return;
            int steps = Mathf.CeilToInt(remain / Step);
            for (int i = 0; i < steps; i++) g.Tick(Step);
        }

        /// <summary>再推进固定秒数（用于"弹窗挂起期间是否还在计时"这类增量断言）。</summary>
        private static void AdvanceBy(float seconds)
        {
            var g = PlaytimeGuard.Instance;
            if (g == null) return;
            int steps = Mathf.CeilToInt(seconds / Step);
            for (int i = 0; i < steps; i++) g.Tick(Step);
        }

        // —— 阶段 1：54 分钟——还没到预警线，世界必须照常 ——

        private void PhaseBeforeLimit(bool minor)
        {
            AdvanceTo(minor ? 54f * 60f : 44f * 60f);
            var g = PlaytimeGuard.Instance;
            _secAt54Min = g != null ? g.TodaySeconds : -1f;
            _aliveAt54Min = StarPixel.Enemy.EnemyManager.Instance != null && PlaytimeGuard.GameRunning;
            _warnAt54Min = g != null ? g.WarnCount : -1;
        }

        // —— 阶段 2：跨过预警 / 健康提示线，弹窗挂起后**故意不关** ——

        private void PhaseAtWarnLine(bool minor)
        {
            AdvanceTo(minor ? 55f * 60f : 45f * 60f);
            var g = PlaytimeGuard.Instance;
            _warnAt55Min = g != null ? g.WarnCount : -1;
            _hintAt45Min = g != null ? g.HintCount : -1;
            _hintAt55Min = g != null && g.HintShowing;
            _hintAt45MinShowing = _hintAt55Min;
            _adultSecBefore = g != null ? g.TodaySeconds : -1f;
            // 关键：从此刻起**不调用 DismissHint**——模拟玩家把弹窗晾在那儿继续玩。
            _hintStillOpen = _hintAt55Min;
        }

        // —— 阶段 3：不关弹窗继续推进 10 分钟（旧实现在这里时间完全不动） ——

        private void PhasePastLimit(bool minor)
        {
            AdvanceBy(10f * 60f);
            var g = PlaytimeGuard.Instance;
            _secAt65Min = g != null ? g.TodaySeconds : -1f;
            _adultSecAfter = _secAt65Min;
            _forceAt65Min = g != null ? g.ForceExitCount : -1;
            _runningAfterForce = PlaytimeGuard.GameRunning;
            _worldTornDown = StarPixel.Enemy.EnemyManager.Instance == null;
        }

        // —— 阶段 4 ——
        // 青少年模式：被踢回主菜单后再点一次「开始游戏」（旧实现在这里放行）；
        // 成人模式：关掉健康提示后再推进一个间隔，应弹第二次。

        private void PhaseSecondRun(bool minor)
        {
            var g = PlaytimeGuard.Instance;
            if (g == null) return;

            if (minor)
            {
                GameBuilder.Build();                 // 走的就是主菜单"开始游戏"按的同一个入口
                _blocked = g.BlockedStartCount;
                _secondRunBlocked = !PlaytimeGuard.GameRunning
                                    && StarPixel.Enemy.EnemyManager.Instance == null;
                _noticeZh = MainMenu.Instance != null ? MainMenu.Instance.NoticeText : "";
                return;
            }

            g.DismissHintForTest();                  // 玩家终于点了「关闭」
            AdvanceBy(PlaytimeGuard.AdultHintIntervalSeconds);
            _hintAt90Min = g.HintCount;
        }

        // —— 阶段 5：跨天重置（青少年） / 时长持久化往返（成人） ——

        private void PhaseRollover(bool minor)
        {
            var g = PlaytimeGuard.Instance;
            if (g == null) return;

            // 成人模式在这里验证"落盘/读档不把时长读没"（青少年模式此时已被踢出对局，时长不变化）
            if (!minor)
            {
                _persistBefore = g.TodaySeconds;
                g.ReloadForTest();
                _persistAfter = g.TodaySeconds;
                return;
            }

            g.SetStoredDateForTest("2000-01-01");    // 让下一次 Tick 走真实的跨天分支
            g.Tick(1f);
            _secAfterRollover = g.TodaySeconds;
            GameBuilder.Build();                     // 新的一天：应能重新开局
            _thirdRunOk = PlaytimeGuard.GameRunning && StarPixel.Enemy.EnemyManager.Instance != null;
        }

        // —— 阶段 6：强制退出提示的本地化（英文界面不许冒中文 / 裸键） ——

        private void PhaseLanguage()
        {
            var mm = MainMenu.Instance;
            if (mm == null) return;

            Localization.SetLanguage(Localization.LangZh, silent: true);
            mm.ShowWithNotice(Localization.T("playtime.force_exit"));
            _noticeZh = mm.NoticeText;

            Localization.SetLanguage(Localization.LangEn, silent: true);
            mm.ShowWithNotice(Localization.T("playtime.force_exit"));
            _noticeEn = mm.NoticeText;
        }

        // —— 判定 ——

        private void Finish()
        {
            _done = true;
            bool minor = _scene != "ADULT";

            Debug.Log("[SMOKE] ===== 防沉迷实测结果 =====");
            Debug.Log("[SMOKE] 场景              " + _scene);

            bool pass;
            if (minor) pass = FinishMinor();
            else pass = FinishAdult();

            Debug.Log("[SMOKE] 运行期异常        " + _errors);
            if (_errors > 0)
                Debug.Log("[SMOKE] 首条错误          " + _firstError);
            pass = pass && _errors == 0;

            // 还原：绝不把青少年模式与已累计时长留在用户的 settings.json / playtime.json 里
            SettingsManager.Instance?.SetMinorMode(false);
            PlaytimeGuard.ResetToday();
            Localization.SetLanguage(Localization.LangZh, silent: true);

            Debug.Log("[SMOKE] 结论              " + (pass ? "通过" : "未通过"));
            Application.Quit(pass ? 0 : 1);
        }

        private bool FinishMinor()
        {
            // 1) 开局正常 + 54 分钟前不应有任何干预
            bool start = _startOk && _aliveAt54Min && _warnAt54Min == 0;
            // 2) 55 分钟恰好弹一次预警，且弹窗真的挂起了
            bool warn = _warnAt55Min == 1 && _hintAt55Min && _hintStillOpen;
            // 3) 【核心】不关弹窗也必须照常计时并到点强制退出
            bool counted = _secAt65Min >= PlaytimeGuard.MinorDailyLimitSeconds;
            bool forced = _forceAt65Min == 1 && !_runningAfterForce && _worldTornDown;
            // 4) 【核心】二次开局必须被拦下
            bool blocked = _secondRunBlocked && _blocked >= 1;
            // 5) 跨天重置：归零且能重新开局
            bool rollover = _secAfterRollover < 5f && _thirdRunOk;
            // 6) 提示文案：非空、非裸键、英文不含中日韩字符
            bool notice = !string.IsNullOrEmpty(_noticeZh) && !IsRawKey(_noticeZh)
                          && !string.IsNullOrEmpty(_noticeEn) && !IsRawKey(_noticeEn)
                          && _noticeEn != _noticeZh && !HasCjk(_noticeEn);

            Debug.Log("[SMOKE] 开局正常          " + (_startOk ? "是" : "否"));
            Debug.Log("[SMOKE] 54分钟世界存活    " + (_aliveAt54Min ? "是" : "否"));
            Debug.Log("[SMOKE] 54分钟预警次数    " + _warnAt54Min);
            Debug.Log("[SMOKE] 55分钟预警次数    " + _warnAt55Min);
            Debug.Log("[SMOKE] 55分钟弹窗挂起    " + (_hintAt55Min ? "是" : "否"));
            Debug.Log("[SMOKE] 不关弹窗累计秒数  " + Mathf.RoundToInt(_secAt65Min));
            Debug.Log("[SMOKE] 强制退出次数      " + _forceAt65Min);
            Debug.Log("[SMOKE] 强退后对局已停    " + (!_runningAfterForce ? "是" : "否"));
            Debug.Log("[SMOKE] 强退后世界已拆    " + (_worldTornDown ? "是" : "否"));
            Debug.Log("[SMOKE] 二次开局被拦      " + (_secondRunBlocked ? "是" : "否"));
            Debug.Log("[SMOKE] 开局拦截计数      " + _blocked);
            Debug.Log("[SMOKE] 跨天后累计秒数    " + Mathf.RoundToInt(_secAfterRollover));
            Debug.Log("[SMOKE] 跨天后可重开      " + (_thirdRunOk ? "是" : "否"));
            Debug.Log("[SMOKE] 中文强退提示      " + _noticeZh);
            Debug.Log("[SMOKE] 英文强退提示      " + _noticeEn);

            if (!counted)
                Debug.Log("[SMOKE] 失败原因          " + "弹窗挂起期间时长未累计（当前 " +
                          Mathf.RoundToInt(_secAt65Min) + "s < 上限 " +
                          PlaytimeGuard.MinorDailyLimitSeconds + "s）");

            return start && warn && counted && forced && blocked && rollover && notice;
        }

        private bool FinishAdult()
        {
            // 1) 45 分钟弹一次健康提示且确实挂起
            bool hinted = _hintAt45Min == 1 && _hintAt45MinShowing;
            // 2) 【核心】提示挂起期间时长必须继续涨（旧实现这里增量为 0）
            float delta = _adultSecAfter - _adultSecBefore;
            bool counted = delta >= 590f;
            // 3) 关掉提示后再推进一个间隔，应弹第二次
            bool second = _hintAt90Min >= 2;
            // 4) 落盘/读档往返不能把时长读没
            bool persisted = _persistBefore > 0f && Mathf.Abs(_persistAfter - _persistBefore) < 2f;
            // 5) 成人模式不该走青少年限时分支（即便累计已远超 1 小时也不强制退出）
            bool noForce = _forceAt65Min == 0;

            Debug.Log("[SMOKE] 45分钟提示次数    " + _hintAt45Min);
            Debug.Log("[SMOKE] 提示弹窗挂起      " + (_hintAt45MinShowing ? "是" : "否"));
            Debug.Log("[SMOKE] 挂起前累计秒数    " + Mathf.RoundToInt(_adultSecBefore));
            Debug.Log("[SMOKE] 挂起后累计秒数    " + Mathf.RoundToInt(_adultSecAfter));
            Debug.Log("[SMOKE] 挂起期间增量      " + Mathf.RoundToInt(delta));
            Debug.Log("[SMOKE] 第二次提示次数    " + _hintAt90Min);
            Debug.Log("[SMOKE] 读档前秒数        " + Mathf.RoundToInt(_persistBefore));
            Debug.Log("[SMOKE] 读档后秒数        " + Mathf.RoundToInt(_persistAfter));
            Debug.Log("[SMOKE] 未走青少年限时    " + (noForce ? "是" : "否"));

            if (!counted)
                Debug.Log("[SMOKE] 失败原因          " + "提示挂起期间时长增量为 " +
                          Mathf.RoundToInt(delta) + "s（应约 600s）");

            return hinted && counted && second && persisted && noForce;
        }

        // —— 判据辅助 ——

        /// <summary>裸本地化键判定（漏翻时 Localization.T 原样返回键名）。</summary>
        private static bool IsRawKey(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Contains(" ")) return false;
            if (s[0] < 'a' || s[0] > 'z') return false;
            bool hasDot = false;
            foreach (var c in s)
            {
                if (c == '.') { hasDot = true; continue; }
                if (c == '_' || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) continue;
                return false;
            }
            return hasDot;
        }

        /// <summary>是否含中日韩字符或全角标点——英文界面出现它就说明有中文漏出来。</summary>
        private static bool HasCjk(string s)
        {
            foreach (var c in s)
            {
                if (c >= '\u4e00' && c <= '\u9fff') return true;
                if (c >= '\u3000' && c <= '\u303f') return true;
                if (c >= '\uff00' && c <= '\uffef') return true;
            }
            return false;
        }
    }
}
