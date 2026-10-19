using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StarPixel.Combat.Skill;
using StarPixel.Core;
using StarPixel.Effects;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M30「运行时创建的 GPU 资源」无头实测。
    ///
    /// 为什么要测：
    ///   前几轮（M24/M26/M27/M28/M29）抓的跨局残留，漏的都是 **GameObject 与委托引用**——
    ///   它们在场景里、在委托链上，数得出来。这一轮漏的是**第三类**：运行时 new 出来的
    ///   UnityEngine.Object（Texture2D / Sprite）。它们不挂在场景里、不进任何登记表，
    ///   `Object.Destroy` 也从来不会有人对它们调用——因为根本没人持有引用。
    ///   GC 管不到 Unity 的**原生侧内存**：托管包装被回收，显存里的那张纹理还在。
    ///   于是玩家玩得越久内存越高，而场景里干干净净、日志一片安静。
    ///
    /// 四个场景：
    ///   BASE    —— 计数器自检：必须真的数得出纹理，且能数出"新增"，否则后面全是假绿。
    ///   BURST   —— 真实高频路径（爆发环 / 警示圈 / 链闪电）连续放 N 次，纹理不许增长。
    ///   RESTART —— 重开三局（每局真打一会儿，让特效真的跑），纹理总数不许增长。
    ///   CACHE   —— 反射读工厂静态缓存，条目不许有已销毁的，数量不许无界增长。
    ///
    /// 判据的区分力：
    /// 1) BASE 先证明"数得到"再证明"数得出增量"。前者防计数器失灵假绿，
    ///    后者防计数器只会报一个恒定值（这两种失灵长得一模一样：都是"没增长"）。
    /// 2) BASE 另有一条**引用相等**判据：连调 20 次工厂，返回必须是同一个对象。
    ///    "总数没涨"可能被别的地方的释放抵消掉（假绿），引用相等直击缓存本身。
    /// 3) BURST 先预热一次再取基线：第一次调用本来就该建缓存，
    ///    不预热的话"建缓存那一次"会被算成泄漏，反过来又会掩盖真正的泄漏。
    /// 4) RESTART 每局必须真打一会儿——特效是自毁型的，不跑时间就不会生成，
    ///    那样"没有泄漏"和"压根没生成"长得一模一样。
    /// </summary>
    public class GpuProbe : MonoBehaviour
    {
        private string _scene = "BASE";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— BASE ——
        private int _baChecked;
        private int _baOk;
        private int _baTexBase;
        private int _baSprBase;
        private int _baRawGrow;
        private int _baShared;
        private bool _baSeenOk;
        private bool _baDeltaOk;
        private bool _baSharedOk;

        // —— BURST ——
        private int _buChecked;
        private int _buOk;
        private int _buRounds;
        private int _buTexBase;
        private int _buTexAfter;
        private int _buGrow;
        private int _buSprGrow;
        private bool _buMadeOk;
        private bool _buFlatOk;

        // —— RESTART ——
        private int _reChecked;
        private int _reOk;
        private int _reRounds;
        private int _reTexBase;
        private int _reTexAfter;
        private int _reGrow;
        private int _reSprBase;
        private int _reSprAfter;
        private int _reSprGrow;
        private string _reTrace = "";
        private int _reSpawned;
        private int _reLastMade;
        private bool _reMadeOk;
        private bool _reFlatOk;
        private bool _reSprOk;

        // —— CACHE ——
        private int _caChecked;
        private int _caOk;
        private int _caEntries;
        private int _caDead;
        private int _caGrow;
        private string _caDeadList = "";
        private bool _caSeenOk;
        private bool _caAliveOk;
        private bool _caFlatOk;
        private bool _caReuseOk;

        public static void Start(string scene)
        {
            var go = new GameObject("GpuProbe");
            go.SetActive(false);
            var p = go.AddComponent<GpuProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Start()
        {
            // 工厂自己建的纹理不会有人 Destroy，异常也不会冒到这里——但记下来总没错
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                {
                    _errors++;
                    if (_firstError == "") _firstError = msg.Split('\n')[0];
                }
            };
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
                case "BURST": yield return StartCoroutine(Burst()); break;
                case "RESTART": yield return StartCoroutine(Restart()); break;
                case "CACHE": yield return StartCoroutine(Cache()); break;
                default: yield return StartCoroutine(Base()); break;
            }

            Report();
            Application.Quit(0);
        }

        // =====================================================================================
        // 计数工具
        // =====================================================================================
        private static int CountTex()
        {
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (t != null) n++;
            return n;
        }

        private static int CountSpr()
        {
            int n = 0;
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null) n++;
            return n;
        }

        /// <summary>场上 SpriteRenderer 数量：用来证明特效对象真的造出来了。</summary>
        private static int CountRenderers()
        {
            int n = 0;
            foreach (var r in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
                if (r != null) n++;
            return n;
        }

        /// <summary>timeScale 可能被暂停/顿帧改掉，WaitForSeconds 会永远不返回——自己累加真实时间。</summary>
        private static IEnumerator WaitReal(float sec)
        {
            float acc = 0f;
            while (acc < sec)
            {
                acc += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>等几帧，让 Object.Destroy 真正生效（延迟到帧末）。</summary>
        private static IEnumerator Quiet(int frames = 3)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        // =====================================================================================
        // BASE：计数器自检——数得到、数得出增量、工厂返回同一实例
        // =====================================================================================
        private IEnumerator Base()
        {
            GameBuilder.Build();
            yield return Quiet();

            _baTexBase = CountTex();
            _baSprBase = CountSpr();

            _baChecked++;
            if (_baTexBase > 0) { _baOk++; _baSeenOk = true; }
            else Fail("建世界后纹理总数为 " + _baTexBase + "（期望 >0）——计数器失灵，后面全是假绿");

            // 直接 new 一批纹理：这是**必然**新增的操作，用来证明计数器看得出增量。
            // 不能拿 MakeSolidSprite 做这条自检——修复后它返回共享实例、本来就不该涨，
            // 拿它当自检会在修好之后反而报红。
            var raw = new List<Texture2D>();
            for (int i = 0; i < 20; i++) raw.Add(new Texture2D(8, 8, TextureFormat.RGBA32, false));
            yield return Quiet();
            int afterRaw = CountTex();
            _baRawGrow = afterRaw - _baTexBase;

            _baChecked++;
            if (_baRawGrow >= 20) { _baOk++; _baDeltaOk = true; }
            else Fail("手动 new 20 张纹理后只数出 +" + _baRawGrow
                      + "——计数器看不出增量，'没有泄漏'会假绿");

            foreach (var t in raw) Object.Destroy(t);
            yield return Quiet();

            // 引用相等：连调 20 次工厂，必须拿到同一个对象
            var first = SpriteFactory.MakeSolidSprite();
            int same = 0;
            for (int i = 0; i < 20; i++)
                if (ReferenceEquals(SpriteFactory.MakeSolidSprite(), first)) same++;
            _baShared = same;

            _baChecked++;
            if (same == 20) { _baOk++; _baSharedOk = true; }
            else Fail("连调 21 次 MakeSolidSprite 只有 " + same
                      + "/20 次返回同一实例——工厂没缓存，每次都在 new 一张纹理");
        }

        // =====================================================================================
        // BURST：真实高频路径连放 N 次，纹理不许增长
        // =====================================================================================
        private IEnumerator Burst()
        {
            GameBuilder.Build();
            yield return Quiet();

            // 预热：第一次调用本来就该建缓存，不预热会把"建缓存那一次"误判成泄漏
            DropEffects();
            yield return WaitReal(0.6f);
            yield return Quiet();

            _buTexBase = CountTex();
            int sprBase = CountSpr();

            const int rounds = 24;
            for (int i = 0; i < rounds; i++)
            {
                DropEffects();
                _buRounds++;
                yield return WaitReal(0.06f);
            }
            // 等最后一批特效自毁（最长生命 0.35s * 1.4）
            yield return WaitReal(0.8f);
            yield return Quiet();

            _buTexAfter = CountTex();
            _buGrow = _buTexAfter - _buTexBase;
            _buSprGrow = CountSpr() - sprBase;

            _buChecked++;
            if (_buRounds == rounds) { _buOk++; _buMadeOk = true; }
            else Fail("只放了 " + _buRounds + "/" + rounds + " 轮特效——生成失败，后面的'没增长'是假绿");

            _buChecked++;
            if (_buGrow <= 0) { _buOk++; _buFlatOk = true; }
            else Fail("连放 " + _buRounds + " 轮特效后纹理多了 " + _buGrow
                      + " 张（" + _buTexBase + " → " + _buTexAfter
                      + "）——每次生成都在 new 纹理且从不 Destroy，显存只涨不降");
        }

        /// <summary>真打一会儿：特效是自毁型的，不推进时间它压根不会生成。</summary>
        private IEnumerator PlayOneRound()
        {
            yield return WaitReal(1.2f);
            // 护栏：必须真的造出了特效对象。"没泄漏"和"压根没生成"长得一模一样，
            // 没有这条，一个什么都没跑的重开局照样能报 +0。
            // 数的是**对象**不是纹理——修好之后本来就不该新建纹理，数纹理会恒为 0。
            int n0 = CountRenderers();
            DropEffects();
            yield return null;
            yield return null;
            int made = CountRenderers() - n0;
            if (made >= 6) _reSpawned++;   // 环 1 + 圈 1 + 电弧 2 段 + 闪光 2
            if (_reLastMade == 0) _reLastMade = made;
            yield return WaitReal(1.2f);
        }

        /// <summary>把三个真实高频来源都走一遍：爆发环、警示圈、链闪电。</summary>
        private static void DropEffects()
        {
            TempEffect.SpawnRing(new Vector3(0f, 0f, 0f), 1.2f, Color.yellow);
            TempEffect.SpawnTelegraph(new Vector3(2f, 0f, 0f), 1.5f, Color.red, 0.3f);
            ChainBolt.Spawn(new Vector2(0f, 0f),
                new List<Vector2> { new Vector2(1f, 0f), new Vector2(2f, 1f) },
                Color.cyan);
        }

        // =====================================================================================
        // RESTART：重开三局（每局真打），纹理总数不许增长
        // =====================================================================================
        private IEnumerator Restart()
        {
            GameBuilder.Build();
            yield return Quiet();

            _reChecked++;
            if (CountTex() > 0) { _reOk++; _reMadeOk = true; }
            else Fail("首局建好时纹理总数为 " + CountTex() + "——计数器失灵");

            // 预热局：某些资源是"第一次遇到才建缓存"（按名字缓存，有界）。
            // 不预热的话那一次会被算成泄漏；但只预热、不看后半程，
            // 又会掩盖"每局都漏一点"。所以预热后**再跑三局**，要求后半程归零。
            yield return StartCoroutine(PlayOneRound());
            GameBuilder.TeardownWorld();
            yield return Quiet();
            GameBuilder.Build();
            yield return Quiet();

            _reTexBase = CountTex();
            _reSprBase = CountSpr();
            _reTrace = _reTexBase.ToString();

            for (int i = 0; i < 3; i++)
            {
                // 每局必须真打一会儿：特效是自毁型的，不跑时间它压根不会生成，
                // 那样"没泄漏"和"没生成"长得一模一样
                yield return StartCoroutine(PlayOneRound());
                _reRounds++;

                GameBuilder.TeardownWorld();
                yield return Quiet();
                GameBuilder.Build();
                yield return Quiet();
                _reTrace += "→" + CountTex();
            }

            _reTexAfter = CountTex();
            _reSprAfter = CountSpr();
            _reGrow = _reTexAfter - _reTexBase;
            _reSprGrow = _reSprAfter - _reSprBase;

            _reChecked++;
            if (_reRounds == 3 && _reSpawned == 4) { _reOk++; }
            else Fail("跑了 " + _reRounds + "/3 局、只有 " + _reSpawned
                      + "/4 局真的生成了特效（末局生成 " + _reLastMade
                      + " 个，期望 ≥6）——没生成的话'没增长'是假绿");

            _reChecked++;
            if (_reGrow <= 0) { _reOk++; _reFlatOk = true; }
            else Fail("预热之后再重开三局，纹理仍多了 " + _reGrow + " 张（" + _reTrace
                      + "）——每局新建的纹理从没被回收，玩得越久内存越高");

            _reChecked++;
            if (_reSprGrow <= 0) { _reOk++; _reSprOk = true; }
            else Fail("预热之后再重开三局，Sprite 仍多了 " + _reSprGrow + " 个（" + _reSprBase
                      + " → " + _reSprAfter + "）——Sprite 同样是运行时 new 出来的，一样没人回收");
        }

        // =====================================================================================
        // CACHE：工厂静态缓存——条目不许死、数量不许无界增长
        // =====================================================================================
        private IEnumerator Cache()
        {
            GameBuilder.Build();
            yield return Quiet();

            var cache = ReadCache();

            _caChecked++;
            if (cache != null && cache.Count > 0) { _caOk++; _caSeenOk = true; _caEntries = cache.Count; }
            else Fail("反射没读到 SpriteFactory 的静态缓存（" + (cache == null ? "字段取不到" : "条目 0")
                      + "）——缓存检查全是假绿");

            // 死条目：缓存里存着已销毁对象。Unity 重载了 ==，已销毁对象判 != null 为 false
            var dead = new List<string>();
            if (cache != null)
                foreach (var kv in cache)
                    if (kv.Value == null) dead.Add(kv.Key);
            _caDead = dead.Count;
            _caDeadList = dead.Count == 0 ? "无" : string.Join(" ", dead.ToArray());

            _caChecked++;
            if (_caDead == 0) { _caOk++; _caAliveOk = true; }
            else Fail("缓存里有 " + _caDead + " 个已销毁条目（" + _caDeadList
                      + "）——再次取用会拿到死对象");

            // 无界增长：同一个 key 反复取，条目数不许变。
            // 必须先取一次预热——新 key 的**第一次**本来就该新增一条，
            // 不预热的话"建缓存那一次"会被误判成泄漏（首轮实测就踩了这个坑：+1）。
            var first = SpriteFactory.GetSprite("gem", Color.gray);
            yield return Quiet();
            var warm = ReadCache();
            int before = warm != null ? warm.Count : 0;
            var last = first;
            for (int i = 0; i < 30; i++) last = SpriteFactory.GetSprite("gem", Color.gray);
            yield return Quiet();
            var cache2 = ReadCache();
            _caGrow = (cache2 != null ? cache2.Count : 0) - before;

            _caChecked++;
            if (_caGrow <= 0) { _caOk++; _caFlatOk = true; }
            else Fail("同一个 key 取 30 次后缓存条目 +" + _caGrow + "——缓存没按 key 复用");

            // 引用相等：条目数不涨抓不到"跳过缓存读取"——写入还在的话，
            // 同一个 key 反复取只是**覆盖**同一条，条目数照样不动（R2 实测就是这么漏过去的）。
            // 只有比对返回实例本身才抓得到。
            _caChecked++;
            if (ReferenceEquals(first, last)) { _caOk++; _caReuseOk = true; }
            else Fail("同一个 key 连取两次返回的不是同一个实例——缓存被跳过，每次都在新建");
        }

        private static Dictionary<string, Sprite> ReadCache()
        {
            var t = typeof(SpriteFactory);
            var f = t.GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
            return f != null ? f.GetValue(null) as Dictionary<string, Sprite> : null;
        }

        // =====================================================================================
        private void Fail(string msg)
        {
            if (_firstError == "") _firstError = msg;
            _fail.Add(msg);
        }

        private static string Yn(bool b) => b ? "是" : "否";

        private void Line(string k, string v) => Debug.Log("[SMOKE] " + k + "              " + v);

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "BASE") pass = pass && _baChecked == 3 && _baOk == 3;
            if (_scene == "BURST") pass = pass && _buChecked == 2 && _buOk == 2;
            if (_scene == "RESTART") pass = pass && _reChecked == 4 && _reOk == 4;
            if (_scene == "CACHE") pass = pass && _caChecked == 4 && _caOk == 4;

            Debug.Log("[SMOKE] ===== 运行时 GPU 资源实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "BASE":
                    Line("断点通过", _baOk + "/" + _baChecked);
                    Line("纹理基线", _baTexBase.ToString());
                    Line("Sprite基线", _baSprBase.ToString());
                    Line("手造20张后增量", _baRawGrow.ToString());
                    Line("工厂同实例次数", _baShared + "/20");
                    Line("数得到纹理", Yn(_baSeenOk));
                    Line("数得出增量", Yn(_baDeltaOk));
                    Line("工厂有缓存", Yn(_baSharedOk));
                    break;
                case "BURST":
                    Line("断点通过", _buOk + "/" + _buChecked);
                    Line("特效轮数", _buRounds.ToString());
                    Line("纹理基线", _buTexBase.ToString());
                    Line("纹理之后", _buTexAfter.ToString());
                    Line("纹理净增长", _buGrow.ToString());
                    Line("Sprite净增长", _buSprGrow.ToString());
                    Line("确实生成", Yn(_buMadeOk));
                    Line("纹理不增长", Yn(_buFlatOk));
                    break;
                case "RESTART":
                    Line("断点通过", _reOk + "/" + _reChecked);
                    Line("重开轮数", _reRounds.ToString());
                    Line("纹理基线", _reTexBase.ToString());
                    Line("纹理之后", _reTexAfter.ToString());
                    Line("纹理净增长", _reGrow.ToString());
                    Line("Sprite基线", _reSprBase.ToString());
                    Line("Sprite之后", _reSprAfter.ToString());
                    Line("Sprite净增长", _reSprGrow.ToString());
                    Line("逐局纹理序列", _reTrace);
                    Line("确实生成特效", _reSpawned + "/4 局（末局 " + _reLastMade + " 个）");
                    Line("计数有效", Yn(_reMadeOk));
                    Line("纹理不增长", Yn(_reFlatOk));
                    Line("Sprite不增长", Yn(_reSprOk));
                    break;
                default:
                    Line("断点通过", _caOk + "/" + _caChecked);
                    Line("缓存条目数", _caEntries.ToString());
                    Line("死条目数", _caDead.ToString());
                    Line("死条目构成", _caDeadList);
                    Line("重复取用增长", _caGrow.ToString());
                    Line("读得到缓存", Yn(_caSeenOk));
                    Line("条目都活着", Yn(_caAliveOk));
                    Line("条目数不涨", Yn(_caFlatOk));
                    Line("同key返回同实例", Yn(_caReuseOk));
                    break;
            }

            Line("运行时异常", _errors.ToString());
            Line("结论", pass ? "通过" : "不通过");
            if (_firstError != "") Line("首个失败", _firstError);
            foreach (var f in _fail) Line("失败项", f);
        }
    }
}
