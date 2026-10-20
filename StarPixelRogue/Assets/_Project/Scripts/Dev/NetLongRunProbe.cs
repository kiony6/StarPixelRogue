#if FISHNET_ENABLED
using System;
using System.Collections.Generic;
using StarPixel.Net;
using StarPixel.Pickups;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M42「联机长局稳定性」无头实测。
    ///
    /// 为什么单机测完还要再测一遍联机：
    ///   M41 在单机侧（<see cref="PickupManager"/>）修掉了"掉落物账本只进不出"——30 分钟游戏时间
    ///   堆到 887 个宝石，每帧全表算距离 → 越玩越卡。但联机侧的掉落物走的是**另一条路**：
    ///   <c>NetEnemy.ServerDie()</c> 直接调 <c>NetWorld.SpawnGem()</c>，整条绕过 PickupManager，
    ///   既没有账本也没有上限。单机那边修得再干净，联机这边一分钱都没花。
    ///   而且联机更贵：每个 NetPickup 都带 SyncVar + NetworkTransform（磁吸时位置每帧在动，
    ///   每个都在持续占同步带宽），房主侧每帧还要对每个掉落物遍历一次玩家注册表。
    ///
    /// 两端都跑同一个探针：
    ///   房主侧断言**权威世界**（场上计数走场景扫描，不读账本——用被测对象自己的账本判自己等于没判）；
    ///   客户端侧采样**自己同步到的视图**，两端计数由驱动脚本（net_longrun_probe.py）交叉核对。
    ///   这正是 R4（回收不走权威 Despawn 而走本地 Destroy）唯一会露馅的地方：
    ///   房主本地销毁了，客户端对此一无所知，它的那份副本变成僵尸，双端计数从此分叉。
    ///
    /// 判据的区分力（承 M41 的教训）：
    ///   1) "上限真的生效"必须单独断言：世界没跑起来时（倍率失效 / 波次不推进），
    ///      宝石根本堆不到上限，"有界"会一片假绿。
    /// 3.5) 上面两条**只在"已经贴到上限附近"时才断言**：没贴到顶的世界里回收从未发生，
    ///      计数自然是 0——那报的是"世界太小"，不是"上限没生效"。
    ///      区分力注入第一次跑就靠这条把 R1 的失败面从四格收窄成有解释力的三格。
    ///   2) "账本与场景一致"：防止有生成路径绕过账本——那种写法下上限形同虚设，
    ///      而"宝石有界"仍可能因为世界没跑满而绿着。
    ///   3) "末段贴顶后不再抬升"用三态：还没贴到顶（末段 &lt; 上限的 80%）时无从区分
    ///      "健康的开局爬坡"与"无上限的爬坡"，判"不适用"——
    ///      判"通过"会掩盖问题，判"失败"会误报。
    ///   4) "上限真的生效"与"账本与场景一致"是**房主专属**判据。客户端从不生成也从不回收
    ///      （二者都是房主权威行为），它的账本恒空、计数器恒 0——在客户端断言这两条
    ///      只会得到结构性失败，那是判据写错了，不是被测对象错了。
    /// </summary>
    public class NetLongRunProbe : MonoBehaviour
    {
        private float _limit = 300f;
        private float _t;        // 真实经过时间（未缩放）
        private float _game;     // 本端游戏内经过时间（受本端 timeScale 影响）
        private int _frames;
        private int _errors;
        private int _kills;
        private string _firstError = "无";
        private bool _isHost;

        private const int SEGMENTS = 6;
        private readonly List<Sample> _samples = new List<Sample>();
        private int _done;

        private int _checked;
        private int _ok;
        private readonly List<string> _fail = new List<string>();

        // —— 判据结果 ——
        private int _finalWave;
        private int _peakGems, _peakHealth;
        private float _gemEarly, _gemLate;
        private long _heapEarly, _heapLate;
        private int _ledgerGems, _ledgerHealths;   // 末次采样的账本口径（诊断）
        private bool _waveOk, _gemBoundOk, _healthOk, _growOk, _trimOk, _ledgerOk, _valueOk;
        private string _growText = "否";
        private string _trimText = "否";
        private string _ledgerText = "否";
        private int _naCount;
        private int _gemTrimmed, _healthTrimmed;

        private struct Sample
        {
            public float game;
            public int wave, gems, healths, ledgerGems, ledgerHealths, level, kills;
            public long heap;
            public int gc0, gc1, gc2;
            public float fps;
        }

        /// <summary>由 GameBuilder 在连接就绪后调用。观察时长比 SmokeTest 短 1s，确保由本探针收尾。</summary>
        public static void Start(float seconds)
        {
            var go = new GameObject("NetLongRunProbe");
            var p = go.AddComponent<NetLongRunProbe>();
            p._limit = Mathf.Max(10f, seconds);
            p._isHost = NetSession.IsHost;
            Debug.Log("[SMOKE] 联机长局实测启动  角色=" + (p._isHost ? "房主" : "客户端") +
                      " 真实时长=" + p._limit.ToString("F0") + "s");
        }

        // 静态事件必须退订（M29：销毁后委托链仍攥着本实例）
        private System.Action<int> _onKill;

        void OnEnable()
        {
            Application.logMessageReceived += OnLog;
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
                Report();
                enabled = false;
            }
        }

        private Sample Take()
        {
            var s = new Sample();
            s.game = _game;
            var ws = NetWorldState.Instance;
            s.wave = ws != null ? ws.Wave : -1;
            CountPickups(out s.gems, out s.healths);
            s.ledgerGems = NetWorld.GemCount;
            s.ledgerHealths = NetWorld.HealthCount;
            s.heap = GC.GetTotalMemory(false);
            s.gc0 = GC.CollectionCount(0);
            s.gc1 = GC.CollectionCount(1);
            s.gc2 = GC.CollectionCount(2);
            var lp = NetPlayerRegistry.Local;
            s.level = lp != null ? lp.SyncedLevel : -1;
            s.kills = _kills;
            s.fps = _t > 0f ? _frames / _t : 0f;
            return s;
        }

        // =====================================================================================

        private void Report()
        {
            if (_samples.Count < 2)
            {
                Debug.Log("[SMOKE] 联机长局采样不足（" + _samples.Count + " 段），无法判定");
                return;
            }

            var first = _samples[0];
            var last = _samples[_samples.Count - 1];

            float gemEarly = 0f, gemLate = 0f;
            int half = _samples.Count / 2;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (i < half) { gemEarly += _samples[i].gems; _heapEarly += _samples[i].heap; }
                else { gemLate += _samples[i].gems; _heapLate += _samples[i].heap; }
            }
            gemEarly /= Mathf.Max(1, half);
            gemLate /= Mathf.Max(1, _samples.Count - half);
            _heapEarly /= Mathf.Max(1, half);
            _heapLate /= Mathf.Max(1, _samples.Count - half);

            int peakGems = 0, peakHealth = 0, maxLedgerSkew = 0, maxLedgerTol = 6;
            bool valueOk = true;
            foreach (var s in _samples)
            {
                if (s.gems > peakGems) peakGems = s.gems;
                if (s.healths > peakHealth) peakHealth = s.healths;
                // 账本与场景的差：有生成路径绕过账本时它会被拉开（那种写法下上限形同虚设）
                int skew = Mathf.Abs(s.gems - s.ledgerGems) + Mathf.Abs(s.healths - s.ledgerHealths);
                if (skew > maxLedgerSkew) maxLedgerSkew = skew;
                // 容差按**当时场上的规模**取，不取死数：残差来自"已决定回收、反生成还没处理完"
                // 的那几个对象，它随回收速率缩放。M42 区分力注入 R4 第一次跑时绝对值阈值 4
                // 把一次 8 的正常残差判成了"账本与场景不一致"——而真正的绕账本生成会是几百量级，
                // 按比例取阈值既不会误报，也不会放过。
                int tol = Mathf.Max(6, (int)((s.gems + s.healths) * 0.10f));
                if (tol > maxLedgerTol) maxLedgerTol = tol;
                if (s.wave < 1 || s.kills < 0 || s.heap <= 0) valueOk = false;
            }

            _finalWave = last.wave;
            _peakGems = peakGems;
            _peakHealth = peakHealth;
            _gemEarly = gemEarly;
            _gemLate = gemLate;
            _ledgerGems = last.ledgerGems;
            _ledgerHealths = last.ledgerHealths;
            _gemTrimmed = NetWorld.GemTrimmed;
            _healthTrimmed = NetWorld.HealthTrimmed;

            // 1) 波次确实推进：加速与权威世界演化都没停摆（否则下面所有增长判据都是假绿）
            _waveOk = _finalWave >= 12;
            Check("波次确实推进", _waveOk);

            // 2)/3) 两类各有界。回血球单独再断言一次——两类共用"只进不出"的写法，
            //       只修一半（修了宝石忘了回血球）是最容易发生的漏修。
            _gemBoundOk = peakGems <= PickupTrim.MaxGems + 20;
            Check("宝石有界", _gemBoundOk);
            _healthOk = peakHealth <= PickupTrim.MaxHealth + 10;
            Check("回血球有界", _healthOk);

            // 4) 上限真的生效：回收计数 > 0，证明上限真的在动手。
            //
            //    三态，两个"不适用"的口子，缺一个它就会撒谎：
            //      · **客户端不适用**：账本与回收都是房主权威行为，客户端从不生成也从不回收，
            //        它的计数器恒为 0——在客户端断言只会得到结构性失败，
            //        那是判据写错了，不是被测对象错了。
            //      · **没贴到顶就不适用**：世界压根没堆到上限附近时（掉率本来就低 / 世界没跑起来），
            //        回收从未发生、计数自然是 0——这报的是"世界太小"，
            //        不是"上限没生效"。M42 区分力注入 R1 第一次跑就是这么撞出来的：
            //        摘掉回收调用后它和"宝石有界"一起红，把两条判据的失败面粘成了一坨。
            //    真正该红的只有一种情形：宝石确实堆到了上限附近，却一次都没回收。
            float cap = PickupTrim.MaxGems;
            bool trimAssertable = _isHost && peakGems >= cap * 0.8f;
            if (!trimAssertable)
            {
                _trimOk = true;                       // 不适用
                _trimText = "不适用";
            }
            else
            {
                _trimOk = _gemTrimmed > 0;
                _trimText = Yn(_trimOk);
            }
            Check("上限真的生效", _trimOk);

            // 5) 账本与场景一致：防止有生成路径绕过账本（那种写法下上限形同虚设，
            //    而"宝石有界"仍可能因世界没跑满而绿着）。同上，只在房主侧成立——
            //    客户端的账本从来是空的，场景却有 240 个同步来的实体，差值是必然的。
            if (!_isHost)
            {
                _ledgerOk = true;
                _ledgerText = "不适用";
            }
            else
            {
                _ledgerOk = maxLedgerSkew <= maxLedgerTol;
                _ledgerText = Yn(_ledgerOk);
            }
            Check("账本与场景一致", _ledgerOk);

            // 6) 末段贴顶后不再抬升。
            //
            //    为什么不能用"后半程均值 vs 前半程均值"：开局宝石本来就要从 0 爬到上限，
            //    短局里这段爬坡会占掉大半时长，均值比必然超标——那报的是"世界刚起步"，
            //    不是"物品在堆积"。M41 单机侧没露出来，只因 300s/6x 下爬坡只占前两三段。
            //
            //    改成只问一件事：**够到顶之后，最后一段还在不在继续涨**。
            //      · 有上限：… 241, 241, 241 → 末段增量 ≈ 0
            //      · 无上限：… 480, 700, 887 → 末段增量 187
            //    三态：还没贴到顶（末段 < 上限的 80%）时无从区分"健康的爬坡"与"无上限的爬坡"，
            //    判"不适用"——判"通过"会掩盖问题，判"失败"会误报。
            int lastGems = last.gems;
            int prevGems = _samples.Count >= 2 ? _samples[_samples.Count - 2].gems : lastGems;
            bool saturated = lastGems >= cap * 0.8f;
            if (_finalWave < 12 || !saturated)
            {
                _growOk = true;                       // 不适用
                _growText = "不适用";
            }
            else
            {
                _growOk = (lastGems - prevGems) <= cap * 0.1f;
                _growText = Yn(_growOk);
            }
            Check("末段贴顶后不再抬升", _growOk);

            // 7) 长程数值不越界 + 全程零异常
            _valueOk = valueOk;
            Check("长程数值不越界", _valueOk);
            Check("全程零异常", _errors == 0);

            _naCount = 0;
            foreach (var t in new[] { _growText, _trimText, _ledgerText })
                if (t == "不适用") _naCount++;

            Debug.Log("[SMOKE] ===== 联机长局稳定性实测结果 =====");
            Line("角色", _isHost ? "房主" : "客户端");
            Line("断点通过", _ok + "/" + _checked + "（其中不适用 " + _naCount + "）");
            Line("波次确实推进", Yn(_waveOk));
            Line("宝石有界", Yn(_gemBoundOk));
            Line("回血球有界", Yn(_healthOk));
            Line("上限真的生效", _trimText);
            Line("账本与场景一致", _ledgerText);
            Line("末段贴顶后不再抬升", _growText);
            Line("长程数值不越界", Yn(_valueOk));
            Line("全程零异常", Yn(_errors == 0));
            Line("游戏内时长", last.game.ToString("F0") + "s");
            Line("最终波次", _finalWave.ToString());
            Line("最终等级", last.level.ToString());
            Line("本地击杀", last.kills.ToString());
            Line("宝石峰值", _gemEarly.ToString("F0") + " → " + _gemLate.ToString("F0") +
                            "（峰 " + peakGems + "）");
            Line("回血球峰值", peakHealth.ToString());
            Line("末段宝石", prevGems + " → " + lastGems + "（上限 " + cap + "）");
            Line("末次账本", _ledgerGems + " 宝石 / " + _ledgerHealths + " 回血球");
            Line("账本场景最大差", maxLedgerSkew + "（容差 " + maxLedgerTol + "）");
            Line("回收次数", _gemTrimmed + " 宝石 / " + _healthTrimmed + " 回血球");
            Line("托管堆MB", (_heapEarly / 1048576f).ToString("F1") + " → " +
                             (_heapLate / 1048576f).ToString("F1"));
            Line("GC次数", "gen0=" + last.gc0 + " gen1=" + last.gc1 + " gen2=" + last.gc2);
            Line("平均帧率", last.fps.ToString("F0"));
            Line("首个异常", _firstError);
            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        // =====================================================================================
        //  计数：场景扫描（不读账本），只数场景里的对象
        // =====================================================================================

        private static bool IsTemplate(Component c) =>
            c != null && c.gameObject.name.EndsWith("Template");

        /// <summary>
        /// 按种类数场上掉落物。两条过滤缺一不可：
        ///   · 预制体资产上的 NetPickup 也会被 FindObjectsOfTypeAll 返回（NetConfig 持引用 → 已加载），
        ///     不按 scene 过滤会把资产算成场上实体；
        ///   · "*Template" 命名兜底（与 M41 探针同一口径）。
        /// </summary>
        private static void CountPickups(out int gems, out int healths)
        {
            gems = 0;
            healths = 0;
            foreach (var p in Resources.FindObjectsOfTypeAll<NetPickup>())
            {
                if (p == null) continue;
                if (!p.gameObject.scene.IsValid()) continue;   // 预制体/资产，不是场上实体
                if (IsTemplate(p)) continue;
                if (p.Kind == NetPickupKind.Health) healths++;
                else gems++;
            }
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
#endif
