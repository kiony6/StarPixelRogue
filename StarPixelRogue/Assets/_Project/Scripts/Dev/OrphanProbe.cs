using System.Collections;
using System.Collections.Generic;
using StarPixel.Combat.Skill;
using StarPixel.Core;
using StarPixel.Enemy;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M28「新建对象归属」无头实测。
    ///
    /// 为什么要测：
    ///   「new GameObject 出来的东西没人负责回收」这个项目连续三轮踩到，每次都靠人肉复查才找到：
    ///     M24 —— 池化出来的宝石/敌人挂在场景根，TeardownWorld 只销毁管理器，整批漏到新世界；
    ///     M26 —— Boss 前摇警示圈 / Nova 爆发环同样建在场景根，漏到新世界的开局画面里；
    ///     M27 —— Track(组件) 只摘组件、留下 GameObject 本体与子物体，每次重开遗留一整套空壳管理器。
    ///   三轮的修法都是"补一份登记表/补一行 SetParent"，但**检查手段**始终是人肉 grep——
    ///   本轮把它做成 audit_project.py 的第 7 项静态检查，而这里是配套的运行时证明：
    ///   静态检查说"有归属"是不够的，必须证明"没有归属时，东西真的会漏过去"。
    ///
    /// 三个场景：
    ///   CHAIN —— 链闪电的电弧段/闪光（与 M26 警示圈同形）：暂停冻结后重开，一个都不许留下。
    ///   BOSS  —— Boss 是 Instantiate 出来、独由 ActiveBoss 持有的：重开后不许带着上一局的 Boss。
    ///   ROOT  —— 通用兜底：重开一局后，场景根对象集合不得比首次建世界时多出任何一个。
    ///            这条不针对具体对象，是"以后再写漏了也能当场抓住"的那张网。
    ///
    /// 判据的区分力：
    /// 1) 每个"不许残留"都配一个"确实造出来了"的前置断言——否则"残留 0"可能只是压根没生成。
    /// 2) CHAIN 必须先把时间冻住（SetPaused(true)）再造：这正是真实场景——
    ///    选卡/结算面板的 timeScale=0 会把自毁计时冻住，重开后才播完。不冻结的话
    ///    电弧 0.16s 就自己消失了，测不出任何东西（这是最容易写成"假绿"的地方）。
    /// 3) ROOT 比对的是**重名对象的个数差**（multiset 差），不是总数：
    ///    新世界本来就会重建一批同名对象，只看总数会被"建了又漏了"互相抵消。
    /// 4) 存活构成一律列出对象名——"残留 3 个"必须能指出是哪 3 个，否则无法定位。
    /// </summary>
    public class OrphanProbe : MonoBehaviour
    {
        private string _scene = "CHAIN";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— CHAIN ——
        private int _chChecked;
        private int _chOk;
        private int _chBefore;
        private int _chAfter;
        private int _chSurvived;
        private string _chBreak = "";
        private bool _chMadeOk;
        private bool _chCleanOk;
        private bool _chRebuiltOk;

        // —— BOSS ——
        private int _boChecked;
        private int _boOk;
        private int _boBefore;
        private int _boAfter;
        private int _boSurvived;
        private string _boBreak = "";
        private bool _boMadeOk;
        private bool _boCleanOk;

        // —— ROOT ——
        private int _roChecked;
        private int _roOk;
        private int _roFirst;
        private int _roSecond;
        private int _roExtra;
        private string _roBreak = "";
        private bool _roMadeOk;
        private bool _roCleanOk;

        public static void Start(string scene)
        {
            var go = new GameObject("OrphanProbe");
            go.SetActive(false);
            var p = go.AddComponent<OrphanProbe>();
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
                case "CHAIN": yield return StartCoroutine(Chain()); break;
                case "BOSS": yield return StartCoroutine(BossScene()); break;
                case "ROOT": yield return StartCoroutine(Root()); break;
                default: yield return StartCoroutine(Chain()); break;
            }

            Report();
            Application.Quit(0);
        }

        // =====================================================================================
        // CHAIN：链闪电（BoltSeg / BoltFlash）
        // =====================================================================================
        private IEnumerator Chain()
        {
            yield return EnsureWorld();

            var hs = HitstopManager.Instance;
            if (hs != null) hs.SetPaused(true);      // 冻结时间：模拟选卡/结算面板
            yield return WaitReal(0.1f);

            var pts = new List<Vector2> { new Vector2(1f, 0f), new Vector2(2f, 1f), new Vector2(3f, -1f) };
            ChainBolt.Spawn(Vector2.zero, pts, Color.cyan);
            yield return null;

            var before = NamedRoots("BoltSeg", "BoltFlash");
            _chBefore = before.Count;
            _chChecked++;
            if (_chBefore >= 5) { _chOk++; _chMadeOk = true; }
            else Fail("没能造出链闪电对象，实测 " + _chBefore + "（期望 ≥5：3 段 + 3 闪）");

            // 玩家点「重新开始」
            if (hs != null) hs.SetPaused(false);
            GameBuilder.TeardownWorld();
            yield return null;
            yield return null;
            GameBuilder.Build();
            yield return null;
            yield return null;

            int survived = 0;
            var parts = new List<string>();
            foreach (var g in before)
            {
                if (g == null) continue;             // Unity：已销毁对象 == null
                survived++;
                parts.Add(g.name);
            }
            _chSurvived = survived;
            _chBreak = parts.Count == 0 ? "无" : string.Join(" / ", parts.ToArray());
            _chAfter = NamedRoots("BoltSeg", "BoltFlash").Count;

            _chChecked++;
            if (survived == 0) { _chOk++; _chCleanOk = true; }
            else Fail("重开后仍有 " + survived + " 个上一局的链闪电活着（" + _chBreak + "）");

            _chChecked++;
            if (_chAfter == 0) { _chOk++; _chRebuiltOk = true; }
            else Fail("新世界开局就带着 " + _chAfter + " 个链闪电（应当为 0）");
        }

        // =====================================================================================
        // BOSS：重开一局时上一局还活着的 Boss
        // =====================================================================================
        private IEnumerator BossScene()
        {
            yield return EnsureWorld();

            var em = EnemyManager.Instance;
            if (em == null) { Fail("EnemyManager 缺失"); yield break; }

            em.SpawnBoss();
            yield return null;

            var before = LiveBosses();
            _boBefore = before.Count;
            _boChecked++;
            if (_boBefore >= 1) { _boOk++; _boMadeOk = true; }
            else Fail("没能造出 Boss，实测 " + _boBefore + "（期望 ≥1）");

            GameBuilder.TeardownWorld();
            yield return null;
            yield return null;
            GameBuilder.Build();
            yield return null;
            yield return null;

            int survived = 0;
            var parts = new List<string>();
            foreach (var b in before)
            {
                if (b == null) continue;
                survived++;
                parts.Add(b.gameObject.name);
            }
            _boSurvived = survived;
            _boBreak = parts.Count == 0 ? "无" : string.Join(" / ", parts.ToArray());
            _boAfter = LiveBosses().Count;

            _boChecked++;
            if (survived == 0) { _boOk++; _boCleanOk = true; }
            else Fail("重开后上一局的 Boss 还活着（" + _boBreak + "）"
                      + "——它会继续追着新玩家跑、继续开火");
        }

        // =====================================================================================
        // ROOT：通用兜底——重开后场景根不得比首次建世界多出任何对象
        // =====================================================================================
        private IEnumerator Root()
        {
            yield return EnsureWorld();
            yield return WaitReal(0.2f);

            var first = RootNames();
            _roFirst = first.Count;
            _roChecked++;
            if (_roFirst >= 1) { _roOk++; _roMadeOk = true; }
            else Fail("首次建世界后场景根一个对象都没有（" + _roFirst + "）");

            // 走完整一遍：暂停 → 拆世界 → 重建
            var hs = HitstopManager.Instance;
            if (hs != null) hs.SetPaused(true);
            ChainBolt.Spawn(Vector2.zero, new List<Vector2> { new Vector2(1f, 1f) }, Color.white);
            var em = EnemyManager.Instance;
            if (em != null) em.SpawnBoss();
            yield return null;
            if (hs != null) hs.SetPaused(false);

            GameBuilder.TeardownWorld();
            yield return null;
            yield return null;
            GameBuilder.Build();
            yield return Quiet();          // 新世界也要立刻冻结清场，否则当波敌人会被当成残留
            // 刻意**不再多等**：会自毁的对象（链闪电 0.16s）只要多等 0.2s 就自己消失了，
            // ROOT 于是会漏掉它们（实测回退链闪电清理时 ROOT 仍是绿的，只有 CHAIN 红）。
            // 残留必须在"新世界刚建好"这一刻取，才抓得住那些"重开后才播完"的家伙。
            var second = RootNames();
            _roSecond = second.Count;

            // multiset 差：新世界会重建一批同名对象，只看总数会被"建了又漏了"抵消
            var pool = new List<string>(first);
            var extra = new List<string>();
            foreach (var n in second)
            {
                int k = pool.IndexOf(n);
                if (k >= 0) pool.RemoveAt(k);
                else extra.Add(n);
            }
            _roExtra = extra.Count;
            _roBreak = extra.Count == 0 ? "无" : string.Join(" / ", extra.ToArray());

            _roChecked++;
            if (_roExtra == 0) { _roOk++; _roCleanOk = true; }
            else Fail("重开后场景根多出 " + _roExtra + " 个对象（" + _roBreak + "）");
        }

        // =====================================================================================
        // 辅助
        // =====================================================================================
        /// <summary>
        /// 建世界并让它"安静下来"。
        ///
        /// GameBuilder.Build() 是同步的：StartWaves → NextWave 在返回之前就把第一波刷出来了，
        /// 探针来不及设 FreezeWaves。所以必须建完**立刻**冻结 + 清场，
        /// 否则场景根会多出几个当波敌人——ROOT 的 multiset 差会把它们当成残留报出来
        /// （实测首次跑就误报了 2 个 EnemyTemplate(Clone)，其实是新世界自己刷的）。
        /// </summary>
        private IEnumerator EnsureWorld()
        {
            GameBuilder.Build();
            yield return Quiet();
        }

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

        /// <summary>真实时间等待（不受 timeScale 影响——冻结期间 WaitForSeconds 永不返回）。</summary>
        private static IEnumerator WaitReal(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                yield return null;
                t += Time.unscaledDeltaTime;
            }
        }

        private static List<GameObject> NamedRoots(params string[] names)
        {
            var list = new List<GameObject>();
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (tr == null || tr.parent != null) continue;
                foreach (var n in names)
                    if (tr.gameObject.name == n) list.Add(tr.gameObject);
            }
            return list;
        }

        private static List<string> RootNames()
        {
            var list = new List<string>();
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
                if (tr != null && tr.parent == null) list.Add(tr.gameObject.name);
            return list;
        }

        private static List<Boss> LiveBosses()
        {
            var list = new List<Boss>();
            foreach (var b in Resources.FindObjectsOfTypeAll<Boss>())
            {
                if (b == null) continue;
                if (b.gameObject.name.EndsWith("Template")) continue;   // 模板不算"场上的"
                list.Add(b);
            }
            return list;
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
            if (_scene == "CHAIN") pass = pass && _chChecked == 3 && _chOk == 3;
            if (_scene == "BOSS") pass = pass && _boChecked == 2 && _boOk == 2;
            if (_scene == "ROOT") pass = pass && _roChecked == 2 && _roOk == 2;

            Debug.Log("[SMOKE] ===== 新建对象归属实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "CHAIN":
                    Line("断点通过", _chOk + "/" + _chChecked);
                    Line("重开前个数", _chBefore.ToString());
                    Line("上一局存活", _chSurvived.ToString());
                    Line("存活构成", _chBreak);
                    Line("新世界存量", _chAfter.ToString());
                    Line("已造出电弧", Yn(_chMadeOk));
                    Line("上一局清零", Yn(_chCleanOk));
                    Line("新世界干净", Yn(_chRebuiltOk));
                    break;
                case "BOSS":
                    Line("断点通过", _boOk + "/" + _boChecked);
                    Line("重开前个数", _boBefore.ToString());
                    Line("上一局存活", _boSurvived.ToString());
                    Line("存活构成", _boBreak);
                    Line("新世界存量", _boAfter.ToString());
                    Line("已造出Boss", Yn(_boMadeOk));
                    Line("上一局清零", Yn(_boCleanOk));
                    break;
                default:
                    Line("断点通过", _roOk + "/" + _roChecked);
                    Line("首次根对象", _roFirst.ToString());
                    Line("重开后根对象", _roSecond.ToString());
                    Line("多出的对象", _roExtra.ToString());
                    Line("多出构成", _roBreak);
                    Line("已建成世界", Yn(_roMadeOk));
                    Line("无新增残留", Yn(_roCleanOk));
                    break;
            }

            Line("运行时异常", _errors.ToString());
            Line("结论", pass ? "通过" : "不通过");
            if (_firstError != "") Line("首个失败", _firstError);
            foreach (var f in _fail) Line("失败项", f);
        }
    }
}
