using System;
using System.Collections.Generic;
using StarPixel.Combat;
using StarPixel.Enemy;
using StarPixel.Pickups;
using StarPixel.Player;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M41「长局稳定性」无头实测。
    ///
    /// 为什么要测：
    ///   此前全部实测都只跑 45～60 秒：单机冒烟 45s、联机双开 60s、各探针场景多为秒级。
    ///   短局能证明"开局没坏"，证明不了"一直玩下去不会烂"——而后者才是玩家真正会遇到的。
    ///   典型的长局专属缺陷长这样：**某个列表只进不出**。它在 45 秒里掉 30 个、捡走 28 个，
    ///   看不出任何异常；跑到第 30 分钟时里面已经堆了几千个，且每帧还要全表遍历一遍——
    ///   于是"越玩越卡"，而所有短局判据依旧全绿。
    ///
    /// 为什么必须能加速：
    ///   要看波次推到 30+ 之后的累积效应，真等 30 分钟既跑不动回归，也没法做四组区分力注入。
    ///   故 GameBuilder 提供 -smokeSpeed，把 <c>HitstopManager.BaseScale</c> 提上去，
    ///   用数分钟真实时间换几十分钟游戏时间。**它放大的是模拟步长，不是真实负载**——
    ///   因此本探针测的是"累积"，真实帧耗时另由常规冒烟覆盖，二者不可互相替代。
    ///
    /// 两个场景：
    ///   GROWTH   长局跑满，分 6 段采样：波次确实推进 / 掉落物有界且不加速堆积 /
    ///            根对象不随波次增长 / 托管堆收敛 / 数值不越界 / 全程零异常
    ///   RESTART  长局之后拆世界重开一局：根对象回落、掉落物清零、波次回到 1、全程零异常
    ///   PANEL    M46：走**玩家真实操作路径**重开——结算面板弹出 → 冻结 → 点「重新开始」按钮
    ///            → 时间恢复 / 面板消失 / HUD 唯一 / 波次回落 / 玩家可控 / 全程零异常
    ///   EXIT     M47：青少年模式当日时长到点的**强制退出**走真实路径
    ///            —— 预警弹出 → 死在结算面板上（时长照走）→ 面板挂起期间跨过上限 →
    ///            PlaytimeGuard.ForceExitMinor 真的执行 → 面板被收 / 主菜单可见且有提示 /
    ///            世界已拆 / 再点「开始游戏」被拦。M46 只模拟了 TeardownWorld 那一行，
    ///            「踢回主菜单 + 二次开局拦截」这两步从未被走到过。
    ///
    /// 判据的区分力：
    ///   1) "后半程不加速堆积"比"最终值有上限"更狠：只加上限的话，堆到上限就不再涨，
    ///      最终值判据照样绿——但前半程早就一路涨上去了，玩家在涨的那段时间里已经卡了。
    ///   2) GROWTH 必须同时断言"波次确实推进"：若加速没生效（比如 BaseScale 被写回 1），
    ///      世界几乎不动，所有增长类判据会一片假绿。
    ///   3) RESTART 反向断言重开后的世界：只测"长局没炸"的话，把重开路径改坏也照样全绿。
    /// </summary>
    public class LongRunProbe : MonoBehaviour
    {
        private string _scene = "GROWTH";
        private float _limit = 300f;
        private float _speed = 1f;

        private float _t;        // 真实经过时间（未缩放）
        private float _game;     // 游戏内经过时间（受 timeScale 影响）
        private int _frames;
        private int _errors;
        private int _kills;
        private string _firstError = "无";

        private const int SEGMENTS = 6;
        private readonly List<Sample> _samples = new List<Sample>();
        private int _done;

        private int _checked;
        private int _ok;
        private readonly List<string> _fail = new List<string>();

        // —— GROWTH 判据结果 ——
        private int _grFinalWave;
        private int _grPeakGems;
        private int _grPeakHealth;
        private float _grGemEarly, _grGemLate;
        private int _grRootFirst, _grRootLast;
        private float _grHeapEarly, _grHeapLate;
        private bool _grWaveOk, _grGemBoundOk, _grHealthOk, _grGemGrowOk, _grHeapOk, _grValueOk;
        private string _grGrowText = "否";   // 三态：是 / 否 / 不适用（世界未进稳态）

        // —— RESTART ——
        private int _rsRootBefore, _rsRootAfter, _rsGemsAfter, _rsWaveAfter;
        private bool _rsRootOk, _rsWaveOk, _rsPlayerOk, _rsNoErrOk;

        // —— PANEL（M46：真实重开路径 —— 结算面板 → 点「重新开始」按钮）——
        private bool _pnFired;
        private int _pnWaveBefore = -1;
        private int _pnPanelCanvas = -1, _pnPanelAfter = -1;
        private float _pnScaleBefore = -1f, _pnScalePaused = -1f, _pnScaleAfter = -1f;
        private int _pnHudBefore = -1, _pnHudAfter = -1;
        private int _pnCanvasBefore = -1, _pnCanvasAfter = -1;
        private int _pnWaveReset = -1, _pnWaveFinal = -1, _pnGemsAfter = -1;
        private bool _pnClicked, _pnPlayerOk;
        private bool _pnMid, _pnFired2;
        private int _pnTearCanvasBefore = -1, _pnTearCanvasAfter = -1;
        private float _pnTearScalePaused = -1f, _pnTearScaleAfter = -1f;
        private bool _pnTearGoneOk, _pnTearThawOk;
        private string _pnTearGoneText = "否", _pnTearThawText = "否", _pnTearLine = "?";
        private bool _pnPanelOk, _pnFreezeOk, _pnResumeOk, _pnGoneOk, _pnHudOk;
        private bool _pnWaveOk, _pnAdvanceOk, _pnPlayerOk2, _pnNoErrOk;
        private string _pnPanelText = "否", _pnFreezeText = "否", _pnResumeText = "否";
        private string _pnGoneText = "否", _pnHudText = "否", _pnWaveText = "否", _pnAdvanceText = "否";
        private string _pnScaleText = "?", _pnWaveLine = "?";

        // —— EXIT（M47：青少年模式强制退出走真实路径）——
        private bool _exF1, _exF2, _exF3, _exF4, _exF5, _exF6, _exF7, _exF8;
        private bool _exMinorOn, _exStartOk;
        private float _exSecWarn = -1f, _exSecDead = -1f, _exSecHang = -1f, _exSecEnd = -1f;
        private int _exWarn = -1, _exForce = -1, _exBlocked = -1;
        private bool _exHintAtWarn, _exHintAfterRestart;
        private int _exPanelDead = -1, _exPanelRestart = -1, _exPanelDead2 = -1, _exPanelAfterForce = -1;
        private int _exHudDead = -1, _exHudAfterForce = -1, _exCanvasAfterForce = -1;
        private float _exScaleDead = -1f, _exScaleAfterForce = -1f;
        private int _exWaveA = -1, _exWaveB = -1, _exGemsB = -1;
        private bool _exClicked;
        private bool _exMenuVisible, _exWorldGone, _exRunningFalse, _exSecondBlocked, _exNoErrOk;
        private string _exNotice = "", _exScaleLine = "?", _exSecLine = "?", _exMenuLine = "?";

        private struct Sample
        {
            public float game;
            public int wave, enemies, gems, healths, roots, objs;
            public long heap;
            public int gc0, gc1, gc2;
            public int level, kills;
            public float fps;
        }

        /// <summary>由 GameBuilder 调用。观察时长比 SmokeTest 短 1s，确保由本探针收尾输出。</summary>
        public static void Start(float seconds, string scene, float speed)
        {
            var go = new GameObject("LongRunProbe");
            var p = go.AddComponent<LongRunProbe>();
            p._limit = Mathf.Max(10f, seconds);
            p._scene = string.IsNullOrEmpty(scene) ? "GROWTH" : scene;
            p._speed = speed;
            Debug.Log("[SMOKE] 长局实测启动  场景=" + p._scene + " 真实时长=" + p._limit.ToString("F0") +
                      "s 加速=" + speed.ToString("F1") + "x");
        }

        // 静态事件必须退订（M29：销毁后委托链仍攥着本实例）——故持有委托引用而非就地 lambda
        private System.Action<int> _onKill;

        void OnEnable()
        {
            Application.logMessageReceived += OnLog;
            // 按"次数"计而非取事件参数：OnEnemyKilled 声明的是"累计击杀数"，
            // 但两处发出的语义并不一致（Enemy 传经验值、NetEnemy 传 1），取参数会得到无意义的数
            _onKill = _ => _kills++;
            Core.EventBus.OnEnemyKilled += _onKill;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
            if (_onKill != null) { Core.EventBus.OnEnemyKilled -= _onKill; _onKill = null; }
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            _errors++;
            if (_firstError == "无") _firstError = msg == null ? "?" : msg;
        }

        void Update()
        {
            _t += Time.unscaledDeltaTime;
            _game += Time.deltaTime;
            _frames++;

            float seg = _limit / SEGMENTS;
            while (_done < SEGMENTS && _t >= (_done + 1) * seg)
            {
                _samples.Add(Take());
                _done++;
            }

            // M46：真实重开路径在中途触发——后半程留给新世界跑，才能判"重开后世界真的在动"
            if (_scene == "PANEL")
            {
                if (!_pnFired && _t >= _limit * 0.5f)
                {
                    _pnFired = true;
                    StartCoroutine(PanelFlow());
                }
                // 新世界跑过一段后再数"推进"，且必须在第二次拆世界之前
                if (_pnClicked && !_pnMid && _t >= _limit * 0.8f)
                {
                    _pnMid = true;
                    SampleAdvanced();
                }
                if (_pnMid && !_pnFired2 && _t >= _limit * 0.85f)
                {
                    _pnFired2 = true;
                    StartCoroutine(PanelFlowTear());
                }
            }

            // M47：强制退出按时间线分七段推进（青少年模式一旦到点就无法再重开，
            // 故"点重开后遮罩残留"必须先于"死在面板上被踢"验证）
            if (_scene == "EXIT")
            {
                if (!_exF1 && _t >= _limit * 0.10f) { _exF1 = true; ExitSetup(); }
                if (!_exF2 && _t >= _limit * 0.18f) { _exF2 = true; ExitAdvanceToWarn(); }
                if (!_exF3 && _t >= _limit * 0.26f) { _exF3 = true; StartCoroutine(ExitDeathAndRestart()); }
                if (!_exF5 && _t >= _limit * 0.50f) { _exF5 = true; ExitSampleAdvance(); }
                if (!_exF6 && _t >= _limit * 0.58f) { _exF6 = true; StartCoroutine(ExitForceFlow()); }
                if (!_exF7 && _t >= _limit * 0.82f) { _exF7 = true; ExitSecondRun(); }
                if (!_exF8 && _t >= _limit * 0.90f) { _exF8 = true; ExitRestore(); }
            }

            if (_t >= _limit)
            {
                if (_scene == "RESTART") FinishRestart();
                else if (_scene == "PANEL") ReportPanel();
                else if (_scene == "EXIT") ReportExit();
                else Report();
                enabled = false;
            }
        }

        private Sample Take()
        {
            var s = new Sample();
            s.game = _game;
            var em = EnemyManager.Instance;
            s.wave = em != null ? em.CurrentWave : -1;
            s.enemies = CombatTargets.AliveEnemyCount;
            s.gems = CountGems();
            s.healths = CountHealth();
            s.roots = RootCount();
            s.objs = ObjectCount();
            s.heap = GC.GetTotalMemory(false);
            s.gc0 = GC.CollectionCount(0);
            s.gc1 = GC.CollectionCount(1);
            s.gc2 = GC.CollectionCount(2);
            var pc = PlayerController.Instance;
            s.level = pc != null ? pc.level : -1;
            s.kills = _kills;
            s.fps = _t > 0f ? _frames / _t : 0f;
            return s;
        }

        // =====================================================================================
        //  GROWTH
        // =====================================================================================

        private void Report()
        {
            if (_samples.Count < 2)
            {
                Debug.Log("[SMOKE] 长局采样不足（" + _samples.Count + " 段），无法判定");
                return;
            }

            var first = _samples[0];
            var last = _samples[_samples.Count - 1];

            // 前后半程均值（6 段 → 前 3 / 后 3）
            float gemEarly = 0f, gemLate = 0f, heapEarly = 0f, heapLate = 0f;
            int half = _samples.Count / 2;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (i < half) { gemEarly += _samples[i].gems; heapEarly += _samples[i].heap; }
                else { gemLate += _samples[i].gems; heapLate += _samples[i].heap; }
            }
            gemEarly /= Mathf.Max(1, half);
            gemLate /= Mathf.Max(1, _samples.Count - half);
            heapEarly /= Mathf.Max(1, half);
            heapLate /= Mathf.Max(1, _samples.Count - half);

            int peakGems = 0;
            float heapMin = float.MaxValue, heapMax = 0f;
            bool valueOk = true;
            foreach (var s in _samples)
            {
                if (s.gems > peakGems) peakGems = s.gems;
                if (s.heap < heapMin) heapMin = s.heap;
                if (s.heap > heapMax) heapMax = s.heap;
                // 数值不越界：波次/等级/击杀必须为正，堆不能为 0
                if (s.wave < 1 || s.level < 1 || s.kills < 0 || s.heap <= 0) valueOk = false;
            }

            _grFinalWave = last.wave;
            _grPeakGems = peakGems;
            _grGemEarly = gemEarly;
            _grGemLate = gemLate;
            _grRootFirst = first.roots;
            _grRootLast = last.roots;
            _grHeapEarly = heapEarly / (1024f * 1024f);
            _grHeapLate = heapLate / (1024f * 1024f);

            // 1) 波次确实推进：加速与世界演化都没停摆（否则下面所有增长判据都是假绿）
            _grWaveOk = _grFinalWave >= 12;
            Check("波次确实推进", _grWaveOk);

            // 2) 宝石有界。3) 回血球单独再断言一次——两类共用"只进不出"的写法，
            //    只修一半的话这里会红（修了宝石、忘了回血球是最容易发生的漏修）。
            _grGemBoundOk = peakGems <= 260;
            Check("宝石有界", _grGemBoundOk);

            _grPeakHealth = Peak(s => s.healths);
            _grHealthOk = _grPeakHealth <= 90;
            Check("回血球有界", _grHealthOk);

            // 4) 后半程不比前半程更快堆积（比"最终有界"更能抓住一路涨到上限的过程）
            //
            // 下限 60 不是放水，是挡小样本噪声：世界几乎没动时（R3 注入，倍率失效），
            // 前 3 段可能只有个位数宝石、后 3 段二十几个——按比例算“后段远多于前段”，
            // 但那只是开局预热，不是堆积。总量低于 60 时前后差异没有统计意义。
            // 前提是"世界已经进入稳态"。波次没推进到 12 以上时，前后半段都还在爬升期，
            // 掉落在涨是正常的——此时这条判据**不适用**，而不是"通过"也不是"不过"。
            // 三态是必须的：判成"通过"会掩盖真问题，判成"不过"会把"世界没跑起来"
            // 错误地记成"物品堆积"（R3 注入时就是这样，白白多红一条）。
            if (_grFinalWave < 12)
            {
                _grGrowText = "不适用";
                Check("后半程不加速堆积", true);
            }
            else
            {
                _grGemGrowOk = gemLate <= Mathf.Max(gemEarly * 1.25f + 20f, 60f);
                _grGrowText = Yn(_grGemGrowOk);
                Check("后半程不加速堆积", _grGemGrowOk);
            }

            // 5) 托管堆收敛：后段均值相对前段均值的增幅有界
            //
            // 这里**没有**"根对象/对象总数不增长"这条判据，它是错的：场上实体数随波次增长
            // 是设计使然（每波 8+4×波 只怪，清完才进下一波），波次 47 时场上就是该有几千个对象。
            // 拿它当泄漏指标会把正常游戏判成缺陷。真正的泄漏判定放在 RESTART——
            // 拆掉世界后还回不来，那才是泄漏。
            _grHeapOk = heapLate <= heapEarly * 1.6f + 8f * 1024f * 1024f;
            Check("托管堆收敛", _grHeapOk);

            // 6) 数值不越界
            _grValueOk = valueOk;
            Check("长程数值不越界", _grValueOk);

            Check("全程零异常", _errors == 0);

            Debug.Log("[SMOKE] ===== 长局稳定性实测结果 =====");
            Line("场景", _scene);
            Line("断点通过", _ok + "/" + _checked);
            Line("波次确实推进", Yn(_grWaveOk));
            Line("宝石有界", Yn(_grGemBoundOk));
            Line("回血球有界", Yn(_grHealthOk));
            Line("后半程不加速堆积", _grGrowText);
            Line("托管堆收敛", Yn(_grHeapOk));
            Line("长程数值不越界", Yn(_grValueOk));
            Line("全程零异常", Yn(_errors == 0));
            Line("加速倍率", _speed.ToString("F1") + "x");
            Line("游戏内时长", last.game.ToString("F0") + "s");
            Line("最终波次", _grFinalWave.ToString());
            Line("最终等级", last.level.ToString());
            Line("最终击杀", last.kills.ToString());
            Line("场上敌人", last.enemies.ToString());
            Line("宝石峰值", _grGemEarly.ToString("F0") + " → " + _grGemLate.ToString("F0") +
                            "（峰 " + peakGems + "）");
            Line("回血球峰值", Peak(s => s.healths).ToString());
            Line("根对象", _grRootFirst + " → " + _grRootLast);
            Line("对象总数", first.objs + " → " + last.objs);
            Line("托管堆MB", (heapMin / 1048576f).ToString("F1") + " → " + (heapMax / 1048576f).ToString("F1"));
            Line("GC次数", "gen0=" + last.gc0 + " gen1=" + last.gc1 + " gen2=" + last.gc2);
            Line("平均帧率", last.fps.ToString("F0"));
            Line("首个异常", _firstError);
            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        private int Peak(Func<Sample, int> sel)
        {
            int m = 0;
            foreach (var s in _samples) { int v = sel(s); if (v > m) m = v; }
            return m;
        }

        // =====================================================================================
        //  RESTART：长局之后重开一局，世界必须回到"刚开局"的样子
        // =====================================================================================

        private void FinishRestart()
        {
            var last = _samples[_samples.Count - 1];
            _rsRootBefore = last.roots;

            GameBuilder.TeardownWorld();
            // 拆完立刻建：不给中间帧，免得把"未清理"误判成"还没清理"
            GameBuilder.Build();

            // 给两帧让世界真正建起来再数
            StartCoroutine(AfterRebuild());
        }

        private System.Collections.IEnumerator AfterRebuild()
        {
            yield return null;
            yield return null;

            var em = EnemyManager.Instance;
            _rsRootAfter = RootCount();
            _rsGemsAfter = CountGems();
            _rsWaveAfter = em != null ? em.CurrentWave : -1;

            _rsRootOk = _rsRootAfter <= _rsRootBefore + 8;
            Check("重开后根对象回落", _rsRootOk);

            // 这里**没有**"重开后掉落物清零"这条判据：TeardownWorld 里的
            // PoolManager.DestroyAll() 会把所有池化实例（含还在场上的宝石）一并销毁，
            // 而 PickupManager 的账本是实例字段、随实例一起没了 —— 无论怎么改这条路，
            // 重开后宝石数都必然为 0。留着它就是一条永远绿的假判据。
            // 真正有区分力的是"根对象有没有回落"（漏拆某一类就回不来）和"玩家还能不能控"。

            _rsWaveOk = _rsWaveAfter >= 1 && _rsWaveAfter <= 2;
            Check("重开后波次重置", _rsWaveOk);

            var pc = PlayerController.Instance;
            _rsPlayerOk = pc != null && (pc.damageable == null || !pc.damageable.isDead);
            Check("重开后玩家可控", _rsPlayerOk);

            _rsNoErrOk = _errors == 0;
            Check("全程零异常", _rsNoErrOk);

            Debug.Log("[SMOKE] ===== 长局稳定性实测结果 =====");
            Line("场景", _scene);
            Line("断点通过", _ok + "/" + _checked);
            Line("重开后根对象回落", Yn(_rsRootOk));
            Line("重开后波次重置", Yn(_rsWaveOk));
            Line("重开后玩家可控", Yn(_rsPlayerOk));
            Line("全程零异常", Yn(_errors == 0));
            Line("长局末根对象", _rsRootBefore.ToString());
            Line("重开后根对象", _rsRootAfter.ToString());
            Line("重开后宝石", _rsGemsAfter.ToString());
            Line("重开后波次", _rsWaveAfter.ToString());
            Line("首个异常", _firstError);
            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        // =====================================================================================
        //  PANEL（M46）：走玩家真实操作路径重开一局
        //
        //  既有 RESTART 场景是 `TeardownWorld(); Build();` 两行——它验证的是"拆建这对 API"，
        //  不是"玩家点下那个按钮会发生什么"。真实路径是三条腿：
        //     EventBus.RaiseRunEnded → GameOverPanel.ShowRun（建结算画布 + 单机压 timeScale 到 0）
        //     → 玩家点 RestartBtn → OnRestart（恢复时间 + 收面板 + Teardown + Build）
        //  中间那一步（"恢复时间 + 收面板"）**从未被任何测试执行过**。
        //  它坏了的表现是：新世界建得完美无缺，但游戏冻在 timeScale=0，或者结算面板
        //  盖在新世界上永远不消失——而 RESTART 的 4 条判据一条都不会红。
        // =====================================================================================

        private System.Collections.IEnumerator PanelFlow()
        {
            var em = EnemyManager.Instance;
            var pc = PlayerController.Instance;
            _pnWaveBefore = em != null ? em.CurrentWave : -1;
            _pnScaleBefore = Time.timeScale;
            _pnHudBefore = CountNamed<UI.HUDController>(null);
            _pnCanvasBefore = CountNamed<Canvas>("HUDCanvas");

            // 玩家真实路径的第一步：一局结束（HUD 汇总后由游戏逻辑发出）
            Core.EventBus.RaiseRunEnded(_pnWaveBefore, _game, _kills, pc != null ? pc.level : 1, false);

            // 等两帧：结算面板是同步建的，但 Canvas 的 active 状态要等一帧才稳
            yield return null;
            yield return null;
            _pnPanelCanvas = CountNamed<Canvas>("GameOverCanvas");
            _pnScalePaused = Time.timeScale;

            // 第二步：**点按钮**（不是调 OnRestart——那样就又绕过了按钮这层）
            _pnClicked = ClickButton("RestartBtn");

            yield return null;
            yield return null;
            // 点完立刻看：面板收了没有、时间恢复了没有（这两条只在重开瞬间成立）
            _pnPanelAfter = CountNamed<Canvas>("GameOverCanvas");
            _pnScaleAfter = Time.timeScale;
            _pnHudAfter = CountNamed<UI.HUDController>(null);
            _pnCanvasAfter = CountNamed<Canvas>("HUDCanvas");
            // 波次必须**在重开瞬间**数：后半程还要给新世界留时间跑，
            // 等收尾再数的话新世界早爬回同一水位，"重置"就永远判不出来。
            var em2 = EnemyManager.Instance;
            _pnWaveReset = em2 != null ? em2.CurrentWave : -1;
        }

        private void SampleAdvanced()
        {
            var em = EnemyManager.Instance;
            var pc = PlayerController.Instance;
            _pnWaveFinal = em != null ? em.CurrentWave : -1;
            _pnGemsAfter = CountGems();
            _pnPlayerOk2 = pc != null && (pc.damageable == null || !pc.damageable.isDead);
        }

        /// <summary>
        /// 第二次：**只拆不建**（青少年模式当日时长到点的强制退出就走这条路）。
        /// 与第一次的区别是它压根不碰按钮——结算面板开着、世界被压在 timeScale=0，
        /// 然后世界被拆掉。收面板的责任落在 TeardownWorld 身上。
        /// </summary>
        private System.Collections.IEnumerator PanelFlowTear()
        {
            var em = EnemyManager.Instance;
            var pc = PlayerController.Instance;
            Core.EventBus.RaiseRunEnded(em != null ? em.CurrentWave : 1, _game, _kills,
                                        pc != null ? pc.level : 1, false);

            yield return null;
            yield return null;
            _pnTearCanvasBefore = CountNamed<Canvas>("GameOverCanvas");
            _pnTearScalePaused = Time.timeScale;

            GameBuilder.TeardownWorld();          // 不跟 Build()：模拟强制退出

            yield return null;
            yield return null;
            _pnTearCanvasAfter = CountNamed<Canvas>("GameOverCanvas");
            _pnTearScaleAfter = Time.timeScale;
            _pnTearLine = "画布 " + _pnTearCanvasBefore + " → " + _pnTearCanvasAfter +
                          "，timeScale " + _pnTearScalePaused.ToString("F2") + " → " +
                          _pnTearScaleAfter.ToString("F2");
        }

        /// <summary>按名字点一次场景里的 UI 按钮。找不到返回 false（判据会把它打成"未点到"）。</summary>
        private static bool ClickButton(string name)
        {
            foreach (var b in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
            {
                if (b == null) continue;
                var go = b.gameObject;
                if (!go.scene.IsValid()) continue;
                if (!go.activeInHierarchy) continue;
                if (go.name != name) continue;
                b.onClick.Invoke();
                return true;
            }
            return false;
        }

        private void ReportPanel()
        {
            if (!_pnMid) SampleAdvanced();       // 极端情况下（时长太短）收尾时补一次
            _pnScaleText = _pnScaleBefore.ToString("F2") + " → " + _pnScalePaused.ToString("F2") +
                           " → " + _pnScaleAfter.ToString("F2");
            _pnWaveLine = _pnWaveBefore + " → " + _pnWaveReset + "（重开瞬间）→ " + _pnWaveFinal + "（收尾）";

            // 1) 结算面板真的弹出来了。恒为 0 说明 RunEnded 没被消费，
            //    下面所有关于"重开"的判据都会平凡成立。
            _pnPanelOk = _pnPanelCanvas == 1;
            Check("结算面板已弹出", _pnPanelOk);
            _pnPanelText = _pnPanelCanvas == 1 ? "是" : ("否（画布数 " + _pnPanelCanvas + "）");

            // 2) 单机结算必须冻结世界。这是"恢复时间"这条判据的前提：
            //    没冻结过就谈不上恢复，判"恢复"是假绿。
            _pnFreezeOk = _pnScalePaused <= 0.0001f;
            Check("结算时世界冻结", _pnFreezeOk);
            _pnFreezeText = Yn(_pnFreezeOk);

            // 3) 点完重开，时间必须回到基准。卡在 0 就是"新世界建好了但游戏冻死"——
            //    RESTART 场景永远抓不到（它从不暂停）。
            //
            //    与第 9) 条同一个诚实标注：当前实现下这条也**被兜住**——
            //    LevelUpManager.OnDestroy 会先一步把 SetPaused(false) 打回去，
            //    于是"点按钮不主动解冻"的注入（Q2 组）实测这条是绿的；
            //    只有连那份兜底一起摘掉（Q6 组）它才会红。
            float expect = Core.HitstopManager.BaseScale;
            _pnResumeOk = _pnScaleAfter > 0.0001f && Mathf.Abs(_pnScaleAfter - expect) < 0.01f;
            Check("重开后时间恢复", _pnResumeOk);
            _pnResumeText = _pnResumeOk ? "是" : ("否（" + _pnScaleAfter.ToString("F2") + " 期望 " + expect.ToString("F2") + "）");

            // 4) 结算画布必须随重开一起消失。它不在 _created 里（GameOverPanel.Ensure 没 Track），
            //    只靠 OnRestart 里的 CloseAndResume 收——漏了就永远盖在新世界上。
            _pnGoneOk = _pnPanelAfter == 0;
            Check("重开后结算画布消失", _pnGoneOk);
            _pnGoneText = _pnGoneOk ? "是" : ("否（残留 " + _pnPanelAfter + "）");

            // 5) HUD 不能变成两份：旧 HUD 随拆世界销毁、新 HUD 随建世界重建，
            //    任何一环漏了都会双份（双份订阅 → 击杀数翻倍那类）。
            _pnHudOk = _pnHudAfter == 1 && _pnCanvasAfter == 1;
            Check("重开后 HUD 唯一", _pnHudOk);
            _pnHudText = _pnHudOk ? "是" : ("否（HUD " + _pnHudAfter + " / 画布 " + _pnCanvasAfter + "）");

            // 6) 波次在重开那一刻必须回到开局。这一条与"时间恢复"互补：
            //    timeScale 卡在 0 时波次也会是 1，看不出区别，所以 7）单独盯"有没有继续推进"。
            _pnWaveOk = _pnWaveBefore >= 2 && _pnWaveReset >= 1 && _pnWaveReset <= 2;
            Check("重开后波次重置", _pnWaveOk);
            _pnWaveText = _pnWaveOk ? "是" : ("否（" + _pnWaveBefore + " → " + _pnWaveReset + "）");

            // 7) 新世界必须**真的在跑**。这是"重开后时间恢复"的行为证据——
            //    timeScale 卡在 0 时前 6 条全部绿（波次停在 1 也算"重置"），只有这条会红。
            //    判"继续推进"而不是"等于某个值"：波次增速随波次递减，给不出稳定期望值。
            //    同样被 LevelUpManager 的兜底解冻盖住（Q2 绿、Q6 红），见第 3) 条的标注。
            _pnAdvanceOk = _pnWaveFinal > _pnWaveReset || _pnGemsAfter > 20;
            Check("重开后世界继续推进", _pnAdvanceOk);
            _pnAdvanceText = _pnAdvanceOk ? "是" : ("否（波次 " + _pnWaveReset + " → " + _pnWaveFinal +
                                                    "，宝石 " + _pnGemsAfter + "）");

            Check("重开后玩家可控", _pnPlayerOk2);
            _pnNoErrOk = _errors == 0;
            Check("全程零异常", _pnNoErrOk);

            // 8) 只拆不建也必须收掉结算面板。这一条与 4）**失败面不相交**：
            //    4）走的是"点按钮"路径（OnRestart 自己收），8）走的是"强制退出"路径
            //    （没人点按钮，只能靠 TeardownWorld 兜底）。
            _pnTearGoneOk = _pnTearCanvasBefore == 1 && _pnTearCanvasAfter == 0;
            Check("拆世界后结算画布消失", _pnTearGoneOk);
            _pnTearGoneText = _pnTearGoneOk ? "是" : ("否（" + _pnTearCanvasBefore + " → " + _pnTearCanvasAfter + "）");

            // 9) 拆世界顺带解冻：面板把世界压在 timeScale=0，只收画布不解冻的话
            //    回主菜单那一刻时间还是停的（菜单动画/下一局开局全受影响）。
            //
            //    诚实标注：这条判据在**当前实现下没有独立区分力**——M46 区分力注入的
            //    Q1 组（只摘 TeardownWorld 的收面板调用）实测它**不红**：
            //    LevelUpManager 也在 _created 里，它先于 HitstopManager 被销毁，
            //    其 OnDestroy 的兜底 `SetPaused(false)` 会把 timeScale 顶回 BaseScale
            //    （M4.5 加的"选卡面板打开中被销毁别把时间卡在 0"，比本场景早得多）。
            //    所以它现在是**被别处兜住的观测项**，不是有牙的断言：
            //    留着它，是因为哪天那个兜底被删掉时它应该红。
            _pnTearThawOk = _pnTearScalePaused <= 0.0001f &&
                            _pnTearScaleAfter > 0.0001f &&
                            Mathf.Abs(_pnTearScaleAfter - Core.HitstopManager.BaseScale) < 0.01f;
            Check("拆世界后时间解冻", _pnTearThawOk);
            _pnTearThawText = _pnTearThawOk ? "是" : ("否（" + _pnTearScaleAfter.ToString("F2") + "）");

            Debug.Log("[SMOKE] ===== 真实重开路径实测结果 =====");
            Line("场景", _scene);
            Line("断点通过", _ok + "/" + _checked);
            Line("结算面板已弹出", _pnPanelText);
            Line("结算时世界冻结", _pnFreezeText);
            Line("重开后时间恢复", _pnResumeText);
            Line("重开后结算画布消失", _pnGoneText);
            Line("重开后 HUD 唯一", _pnHudText);
            Line("重开后波次重置", _pnWaveText);
            Line("重开后世界继续推进", _pnAdvanceText);
            Line("重开后玩家可控", Yn(_pnPlayerOk2));
            Line("全程零异常", Yn(_pnNoErrOk));
            Line("拆世界后结算画布消失", _pnTearGoneText);
            Line("拆世界后时间解冻", _pnTearThawText);
            Line("强制退出", _pnTearLine);
            Line("timeScale", _pnScaleText);
            Line("结算画布", _pnPanelCanvas + " → " + _pnPanelAfter);
            Line("HUD", _pnHudBefore + " → " + _pnHudAfter + "（画布 " + _pnCanvasBefore + " → " + _pnCanvasAfter + "）");
            Line("波次轨迹", _pnWaveLine);
            Line("重开后宝石", _pnGemsAfter.ToString());
            Line("按钮点击", _pnClicked ? "成功" : "未找到 RestartBtn");
            Line("首个异常", _firstError);
            // 键名必须与 SmokeTest 的那份收尾报告区分开：本场景**故意**以"世界已拆"收尾
            // （强制退出路径），SmokeTest 随后会按空世界报「波次 0 / 结论 未通过」——
            // 那是本场景的预期状态，不是回归。用同一个键的话驱动脚本会把两者混在一起。
            Line("PANEL结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("PANEL失败项", f);
        }

        // =====================================================================================
        //  EXIT（M47）：青少年模式当日时长到点 —— 强制退出走真实路径
        //
        //  M46 修的是"只拆不建时结算面板会赖在屏幕上"，但它的证据是**模拟**来的：
        //  探针直接调 `TeardownWorld()` 冒充强制退出。真实那一串是
        //    PlaytimeGuard.Tick 累计 → 55 分钟弹预警 → 玩家死在面板上（时长照走）→
        //    跨过上限 → ForceExitMinor：关预警 → 主菜单 + 提示 → TeardownWorld
        //    → 玩家再点「开始游戏」→ CanStartRun 拦下
        //  头尾两段 M19 的 PlaytimeProbe 各自测过，但**中间那次组合**（面板开着 + 时长到点）
        //  从未被执行——而它恰恰是 M46 那处修复的全部理由。
        //
        //  本场景把两件事压进同一条时间线，顺序不能换：青少年模式一旦到点就无法再重开，
        //  所以"预警挂起时死亡 → 点重开"（M47 修复的那层遮罩）必须先于"死在面板上被踢"。
        // =====================================================================================

        private void ExitSetup()
        {
            Core.SettingsManager.Ensure();
            Core.PlaytimeGuard.Ensure();
            Core.SettingsManager.Instance.SetMinorMode(true);   // 走真实开关（会写设置，收尾还原）
            Core.PlaytimeGuard.ResetToday();                    // 从 0 起算，避免上一场景串味
            Core.PlaytimeGuard.ResetCounters();
            // 真实玩家路径上主菜单是合规门通过后建的（GameBuilder 启动流程里 Ensure），
            // 无头实测入口跳过了合规门，故这里补上同一个前置——否则"主菜单可见"这条判据
            // 判的是一个本来就不存在的东西（M47 修的正是"没建过菜单就静默踢人"）。
            UI.MainMenu.Ensure();
            var em = EnemyManager.Instance;
            _exStartOk = em != null && Core.PlaytimeGuard.GameRunning;
            _exMinorOn = Core.SettingsManager.Instance.Current.minorMode;
        }

        /// <summary>喂的是线上同一个 Tick（Update 只负责把真实 deltaTime 喂进去）。</summary>
        private static void AdvancePlaytimeTo(float target)
        {
            var g = Core.PlaytimeGuard.Instance;
            if (g == null) return;
            float remain = target - g.TodaySeconds;
            if (remain <= 0f) return;
            int steps = Mathf.CeilToInt(remain / 60f);
            for (int i = 0; i < steps; i++) g.Tick(60f);
        }

        private void ExitAdvanceToWarn()
        {
            AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds -
                              Core.PlaytimeGuard.MinorWarnBeforeSeconds + 60f);
            var g = Core.PlaytimeGuard.Instance;
            if (g == null) return;
            _exSecWarn = g.TodaySeconds;
            _exWarn = g.WarnCount;
            _exHintAtWarn = g.HintShowing;       // 故意不关：真实玩家常常就这么晾着它
        }

        private System.Collections.IEnumerator ExitDeathAndRestart()
        {
            var g = Core.PlaytimeGuard.Instance;
            var em = EnemyManager.Instance;
            var pc = PlayerController.Instance;
            _exWaveA = em != null ? em.CurrentWave : -1;

            // 预警遮罩还挂在玩家屏幕上时，他死了
            Core.EventBus.RaiseRunEnded(_exWaveA, _game, _kills, pc != null ? pc.level : 1, false);

            yield return null;
            yield return null;
            _exPanelDead = CountNamed<Canvas>("GameOverCanvas");
            _exScaleDead = Time.timeScale;
            _exHudDead = CountNamed<UI.HUDController>(null);
            _exSecDead = g != null ? g.TodaySeconds : -1f;

            // 玩家点「重新开始」（不是调 OnRestart——那样就绕过了按钮这层）
            _exClicked = ClickButton("RestartBtn");

            yield return null;
            yield return null;
            _exPanelRestart = CountNamed<Canvas>("GameOverCanvas");
            // M47：那层"今日时长已达 55 分钟"的模态遮罩不该跟着进新一局。
            // 它是 CreateModal 在场景根上 new 出来的，不属于被拆的世界——与结算面板同一个形态。
            _exHintAfterRestart = g != null && g.HintShowing;
            _exF4 = true;
        }

        private void ExitSampleAdvance()
        {
            var em = EnemyManager.Instance;
            _exWaveB = em != null ? em.CurrentWave : -1;
            _exGemsB = CountGems();
        }

        private System.Collections.IEnumerator ExitForceFlow()
        {
            var em = EnemyManager.Instance;
            var pc = PlayerController.Instance;
            // 第二次死亡：这一次**不点任何按钮**——就让它挂在结算面板上等着被踢
            Core.EventBus.RaiseRunEnded(em != null ? em.CurrentWave : 1, _game, _kills,
                                        pc != null ? pc.level : 1, false);

            yield return null;
            yield return null;
            _exPanelDead2 = CountNamed<Canvas>("GameOverCanvas");

            var g = Core.PlaytimeGuard.Instance;
            if (g != null)
            {
                // 面板挂起期间：时长必须照走（Tick 吃的是 unscaledDeltaTime，与 timeScale=0 无关）。
                // 旧实现在这里停止计时，玩家只要死在面板上就永远到不了上限——限时形同虚设。
                AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds - 60f);
                _exSecHang = g.TodaySeconds;

                // 跨过上限：**真实的** ForceExitMinor 在这一刻发生
                AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds + 60f);
                _exSecEnd = g.TodaySeconds;
                _exForce = g.ForceExitCount;
            }

            yield return null;
            yield return null;
            var mm = UI.MainMenu.Instance;
            _exPanelAfterForce = CountNamed<Canvas>("GameOverCanvas");
            _exHudAfterForce = CountNamed<UI.HUDController>(null);
            _exCanvasAfterForce = CountNamed<Canvas>("HUDCanvas");
            _exScaleAfterForce = Time.timeScale;
            _exMenuVisible = mm != null && mm.IsVisible;
            _exNotice = mm != null ? mm.NoticeText : "";
            _exWorldGone = EnemyManager.Instance == null;
            _exRunningFalse = !Core.PlaytimeGuard.GameRunning;
        }

        private void ExitSecondRun()
        {
            var g = Core.PlaytimeGuard.Instance;
            GameBuilder.Build();                 // 就是主菜单「开始游戏」按的同一个入口
            if (g != null) _exBlocked = g.BlockedStartCount;
            _exSecondBlocked = !Core.PlaytimeGuard.GameRunning && EnemyManager.Instance == null;
        }

        private void ExitRestore()
        {
            // 绝不把青少年模式与已累计时长留在用户的 settings.json / playtime.json 里
            Core.SettingsManager.Instance?.SetMinorMode(false);
            Core.PlaytimeGuard.ResetToday();
        }

        private void ReportExit()
        {
            _exScaleLine = _exScaleDead.ToString("F2") + " → " + _exScaleAfterForce.ToString("F2");
            _exSecLine = Mathf.RoundToInt(_exSecWarn) + "（预警）→ " + Mathf.RoundToInt(_exSecDead) +
                         "（死亡）→ " + Mathf.RoundToInt(_exSecHang) + "（面板挂起）→ " +
                         Mathf.RoundToInt(_exSecEnd) + "（到点）";
            _exMenuLine = _exMenuVisible ? ("可见，提示「" + _exNotice + "」") : "不可见";

            // 1) 前置：青少年模式真的开着、对局真的在计时。恒假的话下面全是假绿。
            Check("青少年模式且对局计时在走", _exMinorOn && _exStartOk);

            // 2) 到点前 5 分钟弹一次预警，且遮罩真的挂起了（后面要它跨局）
            Check("到点前弹出一次预警", _exWarn == 1 && _exHintAtWarn);

            // 3) 死亡 → 结算面板弹出（后面"被收掉"才是修复生效，否则是平凡成立）
            Check("死亡后结算面板弹出", _exPanelDead == 1);

            // 4) 单机死亡冻结世界。这是"强退后时间解冻"的前提。
            Check("死亡后世界冻结", _exScaleDead <= 0.0001f);

            // 5) 【M47 修复】点重开后，上一局那层时长提示遮罩不许跟着进新一局
            Check("重开后旧提示遮罩已收", !_exHintAfterRestart);

            // 6) 点重开后面板必须收（M46 已覆盖，这里是同一条时间线上的交叉验证）
            Check("重开后结算面板已收", _exPanelRestart == 0);

            // 7) 重开后新世界真的在跑（否则第 5/6 条可能是"世界没建起来"的假象）
            Check("重开后世界继续推进", _exWaveB > _exWaveA || _exGemsB > 20);

            // 8) 第二次死亡：面板再次弹出，为"死在面板上被踢"铺路
            Check("第二次死亡面板再次弹出", _exPanelDead2 == 1);

            // 9) 【核心】面板挂起期间时长仍在累计——旧实现在这里停表，玩家死在面板上就永远到不了上限
            Check("面板挂起期间时长仍在累计", _exSecHang > _exSecDead + 60f);

            // 10) 强制退出恰好触发一次（不是靠探针自己调 TeardownWorld）
            Check("到点恰好强制退出一次", _exForce == 1);

            // 11) 【M46 修复的真实路径】没人点按钮，面板也得被收掉
            Check("强制退出后结算面板被收", _exPanelAfterForce == 0);

            // 12) HUD 必须随世界一起清零；时间必须解冻（否则菜单动画/下一局都卡着）
            Check("强制退出后 HUD 清零", _exHudAfterForce == 0 && _exCanvasAfterForce == 0);
            float expect = Core.HitstopManager.BaseScale;
            Check("强制退出后时间解冻",
                  _exScaleAfterForce > 0.0001f && Mathf.Abs(_exScaleAfterForce - expect) < 0.01f);

            // 13) 【M47 修复】玩家必须看到"为什么被踢"——菜单可见且提示不是裸键
            Check("强制退出后主菜单可见且有提示",
                  _exMenuVisible && !string.IsNullOrEmpty(_exNotice) && !_exNotice.StartsWith("playtime."));

            // 14) 世界确实被拆了、计时也停了（不是只收了个面板）
            Check("强制退出后世界已拆", _exWorldGone && _exRunningFalse);

            // 15) 【核心】回主菜单再点「开始游戏」必须被拦下（M19 的绕过手法之一）
            Check("二次开局被拦下", _exBlocked >= 1 && _exSecondBlocked);

            _exNoErrOk = _errors == 0;
            Check("全程零异常", _exNoErrOk);

            bool thaw = _exScaleAfterForce > 0.0001f && Mathf.Abs(_exScaleAfterForce - expect) < 0.01f;
            bool noticeOk = !string.IsNullOrEmpty(_exNotice) && !_exNotice.StartsWith("playtime.");
            bool menuOk = _exMenuVisible && noticeOk;

            Debug.Log("[SMOKE] ===== 强制退出真实路径实测结果 =====");
            Line("场景", _scene);
            Line("断点通过", _ok + "/" + _checked);
            Line("青少年模式且对局计时在走", (_exMinorOn && _exStartOk) ? "是" : "否");
            Line("到点前弹出一次预警", _exWarn == 1 ? Yn(_exHintAtWarn) : ("否（次数 " + _exWarn + "）"));
            Line("死亡后结算面板弹出", _exPanelDead == 1 ? "是" : ("否（画布 " + _exPanelDead + "）"));
            Line("死亡后世界冻结", _exScaleDead <= 0.0001f ? "是" : ("否（" + _exScaleDead.ToString("F2") + "）"));
            Line("重开后旧提示遮罩已收", !_exHintAfterRestart ? "是" : "否（遮罩跨局残留）");
            Line("重开后结算面板已收", _exPanelRestart == 0 ? "是" : ("否（残留 " + _exPanelRestart + "）"));
            Line("重开后世界继续推进", (_exWaveB > _exWaveA || _exGemsB > 20) ? "是"
                 : ("否（波次 " + _exWaveA + " → " + _exWaveB + "，宝石 " + _exGemsB + "）"));
            Line("第二次死亡面板再次弹出", _exPanelDead2 == 1 ? "是" : ("否（画布 " + _exPanelDead2 + "）"));
            Line("面板挂起期间时长仍在累计", _exSecHang > _exSecDead + 60f ? "是"
                 : ("否（" + Mathf.RoundToInt(_exSecDead) + " → " + Mathf.RoundToInt(_exSecHang) + "）"));
            Line("到点恰好强制退出一次", _exForce == 1 ? "是" : ("否（" + _exForce + "）"));
            Line("强制退出后结算面板被收", _exPanelAfterForce == 0 ? "是" : ("否（残留 " + _exPanelAfterForce + "）"));
            Line("强制退出后 HUD 清零", (_exHudAfterForce == 0 && _exCanvasAfterForce == 0) ? "是"
                 : ("否（HUD " + _exHudAfterForce + " / 画布 " + _exCanvasAfterForce + "）"));
            Line("强制退出后时间解冻", thaw ? "是" : ("否（" + _exScaleAfterForce.ToString("F2") + "）"));
            Line("强制退出后主菜单可见且有提示", menuOk ? "是" : (_exMenuVisible ? "否（提示为空或裸键）" : "否（菜单不可见）"));
            Line("强制退出后世界已拆", (_exWorldGone && _exRunningFalse) ? "是"
                 : ("否（已拆 " + Yn(_exWorldGone) + " / 计时已停 " + Yn(_exRunningFalse) + "）"));
            Line("二次开局被拦下", (_exBlocked >= 1 && _exSecondBlocked) ? "是" : ("否（拦截 " + _exBlocked + " 次）"));
            Line("全程零异常", Yn(_exNoErrOk));
            // —— 详情（驱动脚本不判，仅供人读）——
            Line("死亡后 HUD", _exHudDead.ToString());
            Line("时长轨迹", _exSecLine);
            Line("timeScale", _exScaleLine);
            Line("强退后主菜单", _exMenuLine);
            Line("按钮点击", _exClicked ? "成功" : "未找到 RestartBtn");
            Line("首个异常", _firstError);
            // 键名同样要避开 SmokeTest 的收尾报告：本场景故意以"世界已拆"收尾
            Line("EXIT结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("EXIT失败项", f);
        }

        // =====================================================================================
        //  计数：不读管理器内部列表，直接扫场景（模板本体不算实体）
        // =====================================================================================

        /// <summary>
        /// 扫场数组件：只算"真的在场上"的（排除预制体资产与已停用对象）。
        /// name 为 null 时只看类型。与 NetLongRunProbe 同口径——
        /// 不去重 activeInHierarchy 的话，被 Destroy 但还没落地的对象会被算进来。
        /// </summary>
        private static int CountNamed<T>(string name) where T : Component
        {
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c == null) continue;
                var go = c.gameObject;
                if (!go.scene.IsValid()) continue;          // 预制体资产
                if (!go.activeInHierarchy) continue;        // 已停用/待销毁
                if (name != null && go.name != name) continue;
                n++;
            }
            return n;
        }

        private static bool IsTemplate(Component c) =>
            c != null && c.gameObject.name.EndsWith("Template");

        private static int CountGems()
        {
            int n = 0;
            foreach (var g in Resources.FindObjectsOfTypeAll<XpGem>())
                if (g != null && !IsTemplate(g)) n++;
            return n;
        }

        private static int CountHealth()
        {
            int n = 0;
            foreach (var h in Resources.FindObjectsOfTypeAll<HealthPickup>())
                if (h != null && !IsTemplate(h)) n++;
            return n;
        }

        private static int RootCount()
        {
            int n = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
                if (tr != null && tr.parent == null && !IsTemplate(tr)) n++;
            return n;
        }

        private static int ObjectCount()
        {
            int n = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
                if (tr != null && !IsTemplate(tr)) n++;
            return n;
        }

        // =====================================================================================

        private void Check(string name, bool ok)
        {
            _checked++;
            if (ok) _ok++;
            else _fail.Add(name);
        }

        private void Line(string k, string v) => Debug.Log("[SMOKE] " + k + "              " + v);

        private static string Yn(bool b) => b ? "是" : "否";
    }
}
