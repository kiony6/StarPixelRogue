using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StarPixel.Core;
using StarPixel.Enemy;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M29「静态事件总线订阅对称性」无头实测。
    ///
    /// 为什么要测：
    ///   EventBus 是**静态**的，而它的订阅者全是有生命周期的 MonoBehaviour。
    ///   订阅忘了退订，委托链就会一直攥着已经销毁的对象——这是与 M24/M26/M27 同类的
    ///   "跨局残留"，但形态完全不同：前几轮漏的是 GameObject（看得见、数得出来），
    ///   这一轮漏的是**委托引用**（场景里什么都多不出来，只能靠反射才看得到）。
    ///   它也不会自己暴露：已销毁对象上的方法大多只是写写字段，不抛异常，
    ///   于是日志一片干净，玩家却会遇到"重开三局后升一级连弹三次面板"。
    ///
    /// 三个场景：
    ///   BASE    —— 反射自检：必须真读到 EventBus 的委托链，否则后面全是假绿。
    ///   RESTART —— 重开三局后，订阅总数不许增长，链上不许出现已销毁对象。
    ///   LEVELUP —— 行为后果：重开三局后只升一级，升级面板必须恰好弹一次。
    ///
    /// 判据的区分力：
    /// 1) BASE 先证明"读得到"再看数字。反射取不到 backing field 时 Len() 一律返回 0，
    ///    "没有泄漏"和"根本没读着"长得一模一样——这条是全套判据的地基。
    /// 2) RESTART 同时看**总数**与**死订阅数**：总数不增长只能说明没有重复订阅，
    ///    抓不到"订阅了但对象已销毁"；死订阅数补的正是这一半。
    /// 3) 死订阅分两种形态分别识别：方法组委托（Target 就是订阅者本身）与
    ///    匿名 lambda（Target 是闭包，捕获的对象藏在闭包字段里）。
    ///    只判前者的检查会对 lambda 完全失明——UI/LevelUpManager 正是 lambda 形态。
    /// 4) LEVELUP 把"至少弹出 1 个"与"不超过 1 个"拆成两条：
    ///    前者防"压根没弹出来"的假绿，后者才是真正在抓重复订阅。
    /// </summary>
    public class SubProbe : MonoBehaviour
    {
        private static readonly string[] Events =
        {
            "OnEnemyKilled", "OnPlayerHealthRatio", "OnWaveChanged", "OnGameOver",
            "OnSurvivalTime", "OnXpChanged", "OnLevelUp", "OnDamaged",
            "OnBossSpawned", "OnBossHp", "OnBossDefeated", "OnZoneChanged",
            "OnPlayerHurt", "OnShakeRequest", "OnEliteKilled", "OnWeaponsChanged",
            "OnRunEnded", "OnComboActivated"
        };

        /// <summary>
        /// 事件总数的**绝对值**。
        /// EventBus 增加一个事件时这里会红——那正是想要的：提醒同步补上订阅对称性检查。
        /// </summary>
        private const int ExpectedEvents = 18;

        private string _scene = "BASE";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— BASE ——
        private int _baChecked;
        private int _baOk;
        private int _baFields;
        private int _baSubs;
        private string _baTop = "";

        // —— RESTART ——
        private int _reChecked;
        private int _reOk;
        private int _reBase;
        private int _reAfter;
        private int _reGrow;
        private int _reLuBase;
        private int _reLuAfter;
        private int _reDead;
        private string _reDeadList = "";
        private bool _reMadeOk;
        private bool _reFlatOk;
        private bool _reLuOk;
        private bool _reCleanOk;

        // —— LEVELUP ——
        private int _lvChecked;
        private int _lvOk;
        private int _lvRounds;
        private int _lvCanvas;
        private int _lvOrphan;
        private string _lvThrow = "";
        private bool _lvReadyOk;
        private bool _lvShownOk;
        private bool _lvOnceOk;
        private bool _lvNoOrphanOk;
        private bool _lvCleanOk;

        public static void Start(string scene)
        {
            var go = new GameObject("SubProbe");
            go.SetActive(false);
            var p = go.AddComponent<SubProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Start()
        {
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            SettingsManager.Ensure();
            SaveManager.Ensure();
            Application.targetFrameRate = 60;
            GameBuilder.PrepareStage();

            switch (_scene)
            {
                case "BASE": yield return StartCoroutine(Base()); break;
                case "RESTART": yield return StartCoroutine(Restart()); break;
                case "LEVELUP": yield return StartCoroutine(LevelUp()); break;
                default: yield return StartCoroutine(Base()); break;
            }

            Report();
            Application.Quit(0);
        }

        // =====================================================================================
        // BASE：反射自检——必须真读到委托链
        // =====================================================================================
        private IEnumerator Base()
        {
            GameBuilder.Build();
            yield return Quiet();

            int found = 0;
            foreach (var e in Events) if (HasField(e)) found++;
            _baFields = found;

            // 用绝对值判，不能写成 found == Events.Length：
            // 那样列表被改小时两边一起变小、恒真（R3 回退实测里 BASE 因此报了 2/2）。
            _baChecked++;
            if (found == ExpectedEvents && Events.Length == ExpectedEvents) { _baOk++; }
            else Fail("反射只读到 " + found + "/" + ExpectedEvents + " 个事件字段"
                      + "（列表长 " + Events.Length + "）——后面的订阅数全是 0，判据会假绿");

            _baSubs = Total();
            _baChecked++;
            if (_baSubs > 0) { _baOk++; }
            else Fail("建世界后 EventBus 总订阅数为 " + _baSubs + "（期望 >0：HUD/音效等都会订阅）");

            // 列出订阅最多的几个，供人工核对"读的是真东西"
            var tops = new List<string>();
            foreach (var e in Events)
            {
                int n = Len(e);
                if (n > 0) tops.Add(e + "=" + n);
            }
            _baTop = tops.Count == 0 ? "无" : string.Join(" ", tops.ToArray());
        }

        // =====================================================================================
        // RESTART：重开三局后订阅不许增长、链上不许有已销毁对象
        // =====================================================================================
        private IEnumerator Restart()
        {
            GameBuilder.Build();
            yield return Quiet();

            _reBase = Total();
            _reLuBase = Len("OnLevelUp");
            _reChecked++;
            if (_reBase > 0) { _reOk++; _reMadeOk = true; }
            else Fail("首局建好时总订阅数为 " + _reBase + "（期望 >0，否则是没读着）");

            for (int i = 0; i < 3; i++)
            {
                GameBuilder.TeardownWorld();
                yield return null;
                yield return null;
                GameBuilder.Build();
                yield return Quiet();
            }
            _lvRounds = 3;

            _reAfter = Total();
            _reGrow = _reAfter - _reBase;
            _reLuAfter = Len("OnLevelUp");
            _reDead = DeadTotal(out _reDeadList);

            _reChecked++;
            if (_reGrow == 0) { _reOk++; _reFlatOk = true; }
            else Fail("重开三局后订阅总数从 " + _reBase + " 涨到 " + _reAfter
                      + "（+" + _reGrow + "）——每局都多一份，事件会被重复处理");

            _reChecked++;
            if (_reLuAfter == _reLuBase) { _reOk++; _reLuOk = true; }
            else Fail("OnLevelUp 订阅数从 " + _reLuBase + " 涨到 " + _reLuAfter
                      + "——升一级会弹出 " + _reLuAfter + " 次面板");

            _reChecked++;
            if (_reDead == 0) { _reOk++; _reCleanOk = true; }
            else Fail("链上还有 " + _reDead + " 个已销毁对象的订阅（" + _reDeadList + "）");
        }

        // =====================================================================================
        // LEVELUP：行为后果——重开三局后升一级，面板必须恰好弹一次
        // =====================================================================================
        private IEnumerator LevelUp()
        {
            GameBuilder.Build();
            yield return Quiet();

            for (int i = 0; i < 3; i++)
            {
                GameBuilder.TeardownWorld();
                yield return null;
                yield return null;
                GameBuilder.Build();
                yield return Quiet();
            }

            _lvChecked++;
            if (UI.LevelUpManager.Instance != null && Player.PlayerController.Instance != null)
            {
                _lvOk++;
                _lvReadyOk = true;
            }
            else Fail("三局之后取不到 LevelUpManager/PlayerController，后面测不了");

            // 只升一级。
            // 这里必须自己接住异常：有缺陷时旧实例的 lambda 会在委托链里抛
            // NullReferenceException，异常冒到协程上会把整个 Run() 打断——
            // Report 与 Application.Quit 全都不执行，进程挂到超时、日志一片空白。
            // 那样"失败"和"探针坏了"长得一模一样，没法定位。接住后写成判据。
            try { EventBus.RaiseLevelUp(2); }
            catch (System.Exception e) { _lvThrow = e.GetType().Name; }
            yield return null;
            yield return null;

            int owned, orphan;
            CanvasStats(out owned, out orphan);
            _lvCanvas = owned;
            _lvOrphan = orphan;

            // 关键：只数"挂在管理器底下"的面板。
            // 有缺陷时旧实例抛异常的位置在 `new GameObject("LevelUpCanvas")` **之后**、
            // SetParent **之前**——于是场景根上留一个没有父级的幽灵 canvas。
            // 若笼统地数同名对象，"弹出 1 个"会看起来正常，判据就假绿了。
            _lvChecked++;
            if (_lvCanvas >= 1) { _lvOk++; _lvShownOk = true; }
            else Fail("升级事件触发后一个正常面板都没弹（挂管理器底下的 LevelUpCanvas 数="
                      + _lvCanvas + "）——判据本身失灵");

            _lvChecked++;
            if (_lvCanvas <= 1) { _lvOk++; _lvOnceOk = true; }
            else Fail("升一级弹出了 " + _lvCanvas + " 个面板"
                      + "——重开三局让订阅攒了三份，玩家要连点三次才能继续");

            _lvChecked++;
            if (_lvOrphan == 0) { _lvOk++; _lvNoOrphanOk = true; }
            else Fail("场景根上留了 " + _lvOrphan + " 个没有父级的 LevelUpCanvas"
                      + "——旧实例在 SetParent 时就抛了异常，面板建了一半卡在那儿");

            _lvChecked++;
            if (_lvThrow == "") { _lvOk++; _lvCleanOk = true; }
            else Fail("升级事件抛出 " + _lvThrow
                      + " —— 委托链被中断，排在其后的新实例收不到事件");

            var hs = HitstopManager.Instance;
            if (hs != null) hs.SetPaused(false);
        }

        // =====================================================================================
        // 反射：读 EventBus 静态事件的委托链
        // =====================================================================================
        /// <summary>
        /// C# 的 `public static event Action&lt;T&gt; X;` 由编译器生成一个**同名**的私有静态委托字段。
        /// 拿不到它的话，后面所有"链长 0"都分不清是"没泄漏"还是"没读着"，故单独判死。
        /// </summary>
        private static bool HasField(string name)
        {
            return Field(name) != null;
        }

        private static FieldInfo Field(string name)
        {
            var t = typeof(EventBus);
            var f = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (f != null && typeof(System.Delegate).IsAssignableFrom(f.FieldType)) return f;
            return null;
        }

        private static System.Delegate Chain(string name)
        {
            var f = Field(name);
            if (f == null) return null;
            return f.GetValue(null) as System.Delegate;
        }

        private static int Len(string name)
        {
            var d = Chain(name);
            return d == null ? 0 : d.GetInvocationList().Length;
        }

        private static int Total()
        {
            int n = 0;
            foreach (var e in Events) n += Len(e);
            return n;
        }

        /// <summary>
        /// 链上"指向已销毁对象"的订阅数。
        /// 两种形态都要认：
        ///   · 方法组 `EventBus.OnX += Handler;` —— Target 就是订阅者本身；
        ///   · 匿名 lambda —— Target 是编译器生成的闭包，捕获的对象藏在闭包字段里。
        /// 只认第一种的检查会对 lambda 完全失明。
        /// </summary>
        private static int Dead(string name, List<string> hit)
        {
            var d = Chain(name);
            if (d == null) return 0;
            int n = 0;
            foreach (var x in d.GetInvocationList())
            {
                var tgt = x.Target;
                if (tgt == null) continue;

                if (tgt is Object)
                {
                    // 注意：这里必须先用 is 判类型、再用 Unity 重载的 == 判销毁。
                    // 反过来写成 `var u = tgt as Object; if (u != null)` 的话，
                    // 已销毁对象会被 as 之后的 != null 判成 false，直接漏过去。
                    if ((Object)tgt == null)
                    {
                        n++;
                        if (hit != null) hit.Add(name + ":" + tgt.GetType().Name);
                    }
                    continue;
                }
                if (HasDeadObjectField(tgt))
                {
                    n++;
                    if (hit != null) hit.Add(name + ":" + tgt.GetType().Name + "(闭包)");
                }
            }
            return n;
        }

        /// <summary>闭包字段里是否有已销毁的 Unity 对象（字段本身是 null 不算）。</summary>
        private static bool HasDeadObjectField(object closure)
        {
            var t = closure.GetType();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(Object).IsAssignableFrom(f.FieldType)) continue;
                var raw = f.GetValue(closure);
                if (raw == null) continue;                 // 字段本来就是 null，不算捕获
                if ((Object)raw == null) return true;      // 捕获的对象已销毁
            }
            return false;
        }

        private int DeadTotal(out string desc)
        {
            var hit = new List<string>();
            int n = 0;
            foreach (var e in Events) n += Dead(e, hit);
            desc = hit.Count == 0 ? "无" : string.Join(" / ", hit.ToArray());
            return n;
        }

        // =====================================================================================
        // 辅助
        // =====================================================================================
        /// <summary>
        /// 建世界并让它"安静下来"。
        /// GameBuilder.Build() 是同步的，返回前就把第一波刷出来了，探针来不及冻结。
        /// </summary>
        private static IEnumerator Quiet()
        {
            var em = EnemyManager.Instance;
            if (em != null) em.FreezeWaves = true;
            yield return null;
            yield return null;
            if (em != null) em.ResetAll();
            yield return null;
            yield return null;
        }

        /// <summary>
        /// 分别统计"挂在 LevelUpManager 底下的正常面板"与"没有父级的孤儿面板"。
        /// 两者必须分开：幽灵 canvas 也是 LevelUpCanvas，混在一起数会让缺陷看起来正常。
        /// </summary>
        private static void CanvasStats(out int owned, out int orphan)
        {
            owned = 0;
            orphan = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (tr == null) continue;
                if (tr.gameObject.name != "LevelUpCanvas") continue;
                var p = tr.parent;
                if (p == null) { orphan++; continue; }          // 已销毁或压根没挂上
                if (p.GetComponent<UI.LevelUpManager>() != null) owned++;
                else orphan++;
            }
        }

        private void Fail(string msg)
        {
            _errors++;
            if (_firstError == "") _firstError = msg;
            _fail.Add(msg);
        }

        private static string Yn(bool b) => b ? "是" : "否";

        private void Line(string k, string v) => Debug.Log("[SMOKE] " + k + "              " + v);

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "BASE") pass = pass && _baChecked == 2 && _baOk == 2;
            if (_scene == "RESTART") pass = pass && _reChecked == 4 && _reOk == 4;
            if (_scene == "LEVELUP") pass = pass && _lvChecked == 5 && _lvOk == 5;

            Debug.Log("[SMOKE] ===== 事件总线订阅对称性实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "BASE":
                    Line("断点通过", _baOk + "/" + _baChecked);
                    Line("事件字段", _baFields + "/" + ExpectedEvents);
                    Line("订阅总数", _baSubs.ToString());
                    Line("各事件链长", _baTop);
                    break;
                case "RESTART":
                    Line("断点通过", _reOk + "/" + _reChecked);
                    Line("重开轮数", _lvRounds.ToString());
                    Line("订阅总数基线", _reBase.ToString());
                    Line("重开后总数", _reAfter.ToString());
                    Line("净增长", _reGrow.ToString());
                    Line("OnLevelUp基线", _reLuBase.ToString());
                    Line("OnLevelUp之后", _reLuAfter.ToString());
                    Line("死订阅数", _reDead.ToString());
                    Line("死订阅构成", _reDeadList);
                    Line("确有订阅", Yn(_reMadeOk));
                    Line("总数不增长", Yn(_reFlatOk));
                    Line("升级不重复", Yn(_reLuOk));
                    Line("链上无死对象", Yn(_reCleanOk));
                    break;
                default:
                    Line("断点通过", _lvOk + "/" + _lvChecked);
                    Line("重开轮数", _lvRounds.ToString());
                    Line("弹出面板数", _lvCanvas.ToString());
                    Line("孤儿面板数", _lvOrphan.ToString());
                    Line("升级事件异常", _lvThrow == "" ? "无" : _lvThrow);
                    Line("世界就绪", Yn(_lvReadyOk));
                    Line("至少弹一次", Yn(_lvShownOk));
                    Line("最多弹一次", Yn(_lvOnceOk));
                    Line("无孤儿面板", Yn(_lvNoOrphanOk));
                    Line("事件未抛异常", Yn(_lvCleanOk));
                    break;
            }

            Line("运行时异常", _errors.ToString());
            Line("结论", pass ? "通过" : "不通过");
            if (_firstError != "") Line("首个失败", _firstError);
            foreach (var f in _fail) Line("失败项", f);
        }
    }
}
