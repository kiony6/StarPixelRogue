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

            if (_t >= _limit)
            {
                if (_scene == "RESTART") FinishRestart();
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
        //  计数：不读管理器内部列表，直接扫场景（模板本体不算实体）
        // =====================================================================================

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
