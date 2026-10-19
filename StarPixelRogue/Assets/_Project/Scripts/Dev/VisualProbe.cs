using System.Collections;
using System.Collections.Generic;
using StarPixel.Core;
using StarPixel.Effects;
using StarPixel.Enemy;
using StarPixel.UI;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M26「看得见的第二批」无头实测：运行时字体 / 伤害飘字 / 受击冲击 / Boss 前摇警示圈。
    ///
    /// 为什么必须测（承接 M25）：
    ///   M25 抓出的"相机基准被自己拉塌"证明了一件事——**无头实测能证明"跑通"，证明不了"看得见"**。
    ///   那一轮只覆盖了镜头与顿帧，手感层还剩一整批"产出只在画面上"的东西没被断言过：
    ///     · 运行时字体：三处各自实现（UIFactory / LevelUpManager / DamageNumberManager），
    ///       一旦内置字体取不到，uGUI 的 Text 就是**空白**——不报错、不崩溃，玩家什么都看不到。
    ///     · 伤害飘字：从没断言过"数字对不对""会不会上浮淡出""会不会自己消失"。
    ///       一个"创建了但永远不销毁"的实现，在既有实测里同样全绿（内存慢慢涨而已）。
    ///     · 受击冲击：敌人被打时的缩放冲击（1.12×）从未被断言；若写成叠乘，尺寸会越打越大。
    ///     · Boss 前摇警示圈：最关键的一处——它是**玩家唯一的躲避依据**，
    ///       从没断言过"真的画出来了""透明度在脉动""到点真的消失"。
    ///
    /// 四个场景：
    ///   FONT      —— UIFactory 字体与飘字字体都必须非空，且必须是同一实例（回退链不得分叉）。
    ///   DMG       —— 飘字必须生成、文案必须等于取整后的伤害、必须上浮且淡出、8 次后必须全部回收。
    ///   PUNCH     —— 受击必须放大到 ~1.12×、必须回落、连续受击**不得叠乘**。
    ///   TELEGRAPH —— 警示圈必须生成、必须有精灵、透明度必须脉动、真实时长≈请求时长、到点必须自毁。
    ///   LEFTOVER  —— 重开局后不得残留上一局的特效对象（警示圈/爆发环建在场景根上，无人负责回收）。
    ///
    /// 判据的区分力：
    /// 1) 一律读**组件上的实测值**（Text.font / Text.text / localScale / SpriteRenderer.sprite），
    ///    不采信被测对象自己的账本（M21 DECLINE 假通过同类教训）。
    /// 2) 飘字与警示圈都同时断言"必须出现"和"必须消失"两侧：
    ///    只测单边的话，"创建了不销毁"和"压根没创建"各能骗过一半。
    /// 3) 冲击倍率用**相对比值**（受击后 / 受击前），不依赖私有字段 _baseScale，
    ///    这样"基线是多少"不会成为断言的前提，也顺带覆盖了精英怪呼吸脉冲的差异。
    /// 4) 连续受击**不叠乘**单独成条：若实现写成 scale *= 1.12，单发命中照样能过，
    ///    只有第二发才暴露——这条判据是专门用来抓"乘了又乘"的（M23 叠乘同类）。
    /// 5) 计时一律用 <see cref="WaitReal"/>（unscaledDeltaTime）：
    ///    被测对象自己用缩放时间，探针若也用 WaitForSeconds 就会被一起拖住，测不出真实时长。
    /// </summary>
    public class VisualProbe : MonoBehaviour
    {
        private string _scene = "FONT";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— FONT ——
        private int _fontChecked;
        private int _fontOk;
        private bool _fontFactoryOk;
        private bool _fontFloatOk;
        private bool _fontSameOk;
        private string _fontName = "?";

        // —— DMG ——
        private int _dmgChecked;
        private int _dmgOk;
        private int _dmgCount;
        private string _dmgText = "?";
        private float _dmgRise;
        private float _dmgFade;
        private int _dmgLeft;
        private bool _dmgSpawnOk;
        private bool _dmgTextOk;
        private bool _dmgRiseOk;
        private bool _dmgFadeOk;
        private bool _dmgCleanOk;

        // —— PUNCH ——
        private int _punchChecked;
        private int _punchOk;
        private float _punchRatio = -1f;
        private float _punchBack = -1f;
        private float _punchTwice = -1f;
        private bool _punchUpOk;
        private bool _punchBackOk;
        private bool _punchNoStackOk;

        // —— TELEGRAPH ——
        private int _tgChecked;
        private int _tgOk;
        private bool _tgSpawnOk;
        private bool _tgSpriteOk;
        private float _tgBlink;
        private float _tgReal = -1f;
        private int _tgLeft;
        private bool _tgBlinkOk;
        private bool _tgDurOk;
        private bool _tgCleanOk;

        // —— LEFTOVER ——
        private int _leftChecked;
        private int _leftOk;
        private int _leftBefore;
        private int _leftAfter;
        private bool _leftMadeOk;
        private bool _leftCleanOk;
        private string _leftBreak = "";

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        public static void Start(string scene)
        {
            // AddComponent 会同步执行 Awake（M21–M25 同一条坑）：先停用 → 挂载赋值 → 再激活。
            var go = new GameObject("VisualProbe");
            go.SetActive(false);
            var p = go.AddComponent<VisualProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Awake()
        {
            Application.logMessageReceived += OnLog;
            Localization.SetLanguage(Localization.LangZh, silent: true);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        private IEnumerator Start()
        {
            yield return null;

            switch (_scene)
            {
                case "DMG": yield return Dmg(); break;
                case "PUNCH": yield return Punch(); break;
                case "TELEGRAPH": yield return Telegraph(); break;
                case "LEFTOVER": yield return Leftover(); break;
                default: yield return Font(); break;
            }

            Report();
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }

        // =====================================================================================
        //  公共 helper
        // =====================================================================================

        /// <summary>真实时间等待。WaitForSeconds 会被 timeScale 拖住，测不出被测对象的真实时长。</summary>
        private static IEnumerator WaitReal(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>
        /// 建世界 → 冻波次 → 清掉开场那一波敌人。
        /// 走真实路径（Build 里创建的 DamageNumberManager / EnemyManager 才是玩家真正用到的那些）。
        /// </summary>
        private IEnumerator EnsureWorld()
        {
            GameBuilder.Build();
            var em = EnemyManager.Instance;
            if (em != null) em.FreezeWaves = true;
            ClearEnemiesNow();
            yield return null;
            yield return null;
            ClearEnemiesNow();     // 兜底：万一上一帧又蹭出来一只
            yield return null;
        }

        private static void ClearEnemiesNow()
        {
            var em = EnemyManager.Instance;
            if (em == null) return;
            // 注意：StarPixel.Enemy 既是命名空间又是类名，必须写全 Enemy.Enemy（M24 同一坑）
            var snapshot = new List<Enemy.Enemy>(em.ActiveEnemies);
            foreach (var e in snapshot)
            {
                if (e == null) continue;
                em.RemoveEnemy(e);
                Destroy(e.gameObject);
            }
            em.ActiveEnemies.Clear();
        }

        /// <summary>
        /// 场上**正在显示**的飘字数：排除池模板，也排除已归还到池里的（inactive）实例。
        /// M27：跳字改为对象池复用后，归还池里的实例仍然是场景里的 FloatingText 对象，
        /// 再按「全部」计数的话，「打完后残留 0」会永远为假——那时它只是被回收，不是泄漏。
        /// </summary>
        private static int CountFloating()
        {
            int n = 0;
            foreach (var f in Resources.FindObjectsOfTypeAll<FloatingText>())
            {
                if (f == null) continue;
                if (f.gameObject.name.EndsWith("Template")) continue;   // 池模板不是"在用的"
                if (!f.gameObject.activeInHierarchy) continue;          // 已归还
                n++;
            }
            return n;
        }

        private static FloatingText FirstFloating()
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<FloatingText>())
            {
                if (f == null) continue;
                if (f.gameObject.name.EndsWith("Template")) continue;
                if (!f.gameObject.activeInHierarchy) continue;
                return f;
            }
            return null;
        }

        private static int CountTelegraphs()
        {
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<TempEffect>())
                if (t != null && t.gameObject.name == "Telegraph") n++;
            return n;
        }

        private static TempEffect FirstTelegraph()
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<TempEffect>())
                if (t != null && t.gameObject.name == "Telegraph") return t;
            return null;
        }

        private static int CountEffects()
        {
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<TempEffect>())
                if (t != null) n++;
            return n;
        }

        /// <summary>残留构成：警示圈 / 爆发环各几个——失败时能直接指出漏的是哪一类。</summary>
        private static string BreakdownEffects()
        {
            int tg = 0, ring = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<TempEffect>())
            {
                if (t == null) continue;
                if (t.gameObject.name == "Telegraph") tg++;
                else ring++;
            }
            return "警示圈" + tg + " 爆发环" + ring;
        }

        // =====================================================================================
        //  FONT：运行时字体必须真的取得到，且两处不得分叉
        // =====================================================================================

        private IEnumerator Font()
        {
            yield return EnsureWorld();

            var factoryFont = UIFactory.UIFont;
            _fontChecked++;
            if (factoryFont != null) { _fontOk++; _fontFactoryOk = true; }
            else Fail("UIFactory.UIFont 为 null（所有运行时生成的文字都会是空白）");
            _fontName = factoryFont != null ? factoryFont.name : "null";

            // 飘字用的是自己那份 GetUIFont()：必须也取得到，且必须是同一个字体实例
            EventBus.RaiseDamaged(12f, Vector3.zero);
            yield return null;
            var ft = FirstFloating();
            var tx = ft != null ? ft.GetComponent<Text>() : null;
            _fontChecked++;
            if (tx != null && tx.font != null) { _fontOk++; _fontFloatOk = true; }
            else Fail("伤害飘字的 Text.font 为 null（玩家永远看不到伤害数字）");

            _fontChecked++;
            if (factoryFont != null && tx != null && ReferenceEquals(tx.font, factoryFont))
            {
                _fontOk++;
                _fontSameOk = true;
            }
            else Fail("两处字体不是同一实例：UIFactory=" + _fontName +
                      " / 飘字=" + (tx != null && tx.font != null ? tx.font.name : "null"));

            yield return WaitReal(1.0f);
        }

        // =====================================================================================
        //  DMG：飘字必须生成、文案正确、上浮淡出、并自行回收
        // =====================================================================================

        private IEnumerator Dmg()
        {
            yield return EnsureWorld();

            // 1) 必须生成，且文案必须等于取整后的伤害
            EventBus.RaiseDamaged(37.4f, new Vector3(1f, 1f, 0f));
            yield return null;
            _dmgCount = CountFloating();
            var ft = FirstFloating();
            var tx = ft != null ? ft.GetComponent<Text>() : null;
            if (tx != null) _dmgText = tx.text;

            _dmgChecked++;
            if (_dmgCount == 1) { _dmgOk++; _dmgSpawnOk = true; }
            else Fail("飘字数量 " + _dmgCount + "（期望 1）");

            _dmgChecked++;
            if (_dmgText == "37") { _dmgOk++; _dmgTextOk = true; }
            else Fail("飘字文案 " + _dmgText + "（期望 37）");

            // 2) 必须上浮且淡出（同一段采样里同时取两个量）
            float y0 = tx != null ? ft.GetComponent<RectTransform>().anchoredPosition.y : 0f;
            float a0 = tx != null ? tx.color.a : 1f;
            yield return SampleFloating(ft, 0.30f);

            _dmgRise = _sampledY - y0;
            _dmgChecked++;
            if (_dmgRise > 5f) { _dmgOk++; _dmgRiseOk = true; }
            else Fail("飘字没有上浮，Δy=" + N2(_dmgRise));

            _dmgFade = a0 - _sampledA;
            _dmgChecked++;
            if (_dmgFade > 0.1f) { _dmgOk++; _dmgFadeOk = true; }
            else Fail("飘字没有淡出，Δalpha=" + N2(_dmgFade));

            // 3) 连打 8 次，全部必须自行回收（抓"创建了不销毁"的泄漏）
            for (int i = 0; i < 8; i++)
                EventBus.RaiseDamaged(10f + i, new Vector3(i * 0.3f, 0f, 0f));
            yield return null;
            int peak = CountFloating();
            yield return WaitReal(1.50f);
            _dmgLeft = CountFloating();
            _dmgChecked++;
            if (_dmgLeft == 0) { _dmgOk++; _dmgCleanOk = true; }
            else Fail("打完 8 次并等 1.5s 后仍残留 " + _dmgLeft + " 个飘字（峰值 " + peak + "）");
        }

        // =====================================================================================
        //  PUNCH：受击冲击必须放大、回落，且连续受击不得叠乘
        // =====================================================================================

        private IEnumerator Punch()
        {
            yield return EnsureWorld();
            var em = EnemyManager.Instance;
            if (em == null) { Fail("没有 EnemyManager，无法生成靶子"); yield break; }

            // 速度 0 的肉盾当靶子：不追人、位置稳定；放到远处避免被玩家自动开火打到（污染冲击判据）
            Vector2 far = new Vector2(600f, 600f);
            em.SpawnAdd(far, EnemyKind.Tank, 500000f, 0f);
            yield return null;
            yield return null;

            if (em.ActiveEnemies.Count == 0) { Fail("靶子未生成"); yield break; }
            var e = em.ActiveEnemies[em.ActiveEnemies.Count - 1];
            if (e == null) { Fail("靶子为空"); yield break; }

            float baseScale = e.transform.localScale.x;

            // 1) 受击必须放大
            e.damageable.TakeDamage(1f);
            yield return null;
            _punchRatio = e.transform.localScale.x / baseScale;
            _punchChecked++;
            if (_punchRatio > 1.08f && _punchRatio < 1.20f) { _punchOk++; _punchUpOk = true; }
            else Fail("受击没有冲击，倍率 " + N3(_punchRatio) + "（期望≈1.120）");

            // 2) 必须回落
            yield return WaitReal(0.30f);
            _punchBack = e.transform.localScale.x / baseScale;
            _punchChecked++;
            if (Mathf.Abs(_punchBack - 1f) < 0.02f) { _punchOk++; _punchBackOk = true; }
            else Fail("冲击没有回落，倍率 " + N3(_punchBack));

            // 3) 连续受击不得叠乘：若实现写成 scale *= 1.12，第二发就会露出马脚
            e.damageable.TakeDamage(1f);
            yield return null;
            e.damageable.TakeDamage(1f);
            yield return null;
            _punchTwice = e.transform.localScale.x / baseScale;
            _punchChecked++;
            if (_punchTwice > 1.08f && _punchTwice < 1.20f) { _punchOk++; _punchNoStackOk = true; }
            else Fail("连续受击叠乘了，倍率 " + N3(_punchTwice) + "（期望仍≈1.120）");
        }

        // =====================================================================================
        //  TELEGRAPH：Boss 前摇警示圈——玩家唯一的躲避依据
        // =====================================================================================

        private IEnumerator Telegraph()
        {
            // 前摇圈不需要世界：它是纯视觉对象，直接调产品里的同一个静态方法
            TempEffect.SpawnTelegraph(new Vector3(2f, 0f, 0f), 2.0f, new Color(1f, 0.32f, 0.42f), 0.50f);
            yield return null;

            var tg = FirstTelegraph();
            _tgChecked++;
            if (tg != null) { _tgOk++; _tgSpawnOk = true; }
            else Fail("警示圈未生成");
            if (tg == null) yield break;

            var sr = tg.GetComponent<SpriteRenderer>();
            _tgChecked++;
            if (sr != null && sr.sprite != null) { _tgOk++; _tgSpriteOk = true; }
            else Fail("警示圈没有精灵（SpriteRenderer.sprite 为 null，画面上什么都看不到）");

            // 必须脉动：静止不动的圈起不到"越接近释放越急促"的警示作用
            yield return SampleTelegraph(tg, 0.30f);
            _tgBlink = _tgMaxA - _tgMinA;
            _tgChecked++;
            if (_tgBlink > 0.05f) { _tgOk++; _tgBlinkOk = true; }
            else Fail("警示圈没有脉动，透明度振幅 " + N3(_tgBlink));

            // 真实时长必须≈请求时长 0.50s，且到点必须自毁
            float t = 0.30f;                       // 采样阶段已经用掉的真实时间
            while (tg != null && t < 6f)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            _tgReal = t;
            _tgChecked++;
            if (_tgReal >= 0.35f && _tgReal <= 0.80f) { _tgOk++; _tgDurOk = true; }
            else Fail("警示圈真实时长 " + N3(_tgReal) + "s（期望≈0.500s）");

            yield return null;
            _tgLeft = CountTelegraphs();
            _tgChecked++;
            if (_tgLeft == 0) { _tgOk++; _tgCleanOk = true; }
            else Fail("到点后仍残留 " + _tgLeft + " 个警示圈");
        }

        // =====================================================================================
        //  LEFTOVER：重开局后不得残留上一局的特效对象
        // =====================================================================================

        /// <summary>
        /// 走真实的「重开一局」序列（GameOverPanel.OnRestart：CloseAndResume → TeardownWorld → Build），
        /// 断言上一局的警示圈/爆发环不会漏进新世界。
        /// TempEffect 的对象是 new GameObject 建在**场景根节点**上的，既不在 GameBuilder 的 _created 里、
        /// 也不是任何管理器的子物体 —— 与 M24 抓到的"池化实体残留"是同一类所有权缺口。
        /// </summary>
        private IEnumerator Leftover()
        {
            yield return EnsureWorld();

            // 造出上一局的特效：2 个前摇警示圈 + 1 个 Nova 爆发环
            TempEffect.SpawnTelegraph(new Vector3(1f, 0f, 0f), 2.0f, new Color(1f, 0.32f, 0.42f), 0.90f);
            TempEffect.SpawnTelegraph(new Vector3(-1f, 0f, 0f), 3.0f, new Color(1f, 0.75f, 0.25f), 0.90f);
            TempEffect.SpawnRing(new Vector3(0f, 1f, 0f), 1.5f, Color.yellow);

            // 模拟「结算面板弹出 → 世界被冻结」
            var hs = HitstopManager.Instance;
            if (hs != null) hs.SetPaused(true);
            yield return WaitReal(0.15f);

            _leftBefore = CountEffects();
            _leftChecked++;
            if (_leftBefore == 3) { _leftOk++; _leftMadeOk = true; }
            else Fail("没能造出 3 个特效，实测 " + _leftBefore +
                      "（少了前一步，'残留=0' 可能只是压根没造出来）");

            // 玩家点「重新开始」：CloseAndResume → TeardownWorld → Build（GameOverPanel.OnRestart 原序列）
            if (hs != null) hs.SetPaused(false);
            GameBuilder.TeardownWorld();
            yield return null;
            yield return null;
            GameBuilder.Build();
            yield return null;
            yield return null;

            _leftAfter = CountEffects();
            _leftBreak = BreakdownEffects();
            _leftChecked++;
            if (_leftAfter == 0) { _leftOk++; _leftCleanOk = true; }
            else Fail("重开后残留 " + _leftAfter + " 个上一局特效（" + _leftBreak + "）");
        }

        // =====================================================================================

        private float _sampledY;
        private float _sampledA;

        /// <summary>采样飘字：记录窗口内的最大 y 与最小 alpha。</summary>
        private IEnumerator SampleFloating(Component c, float seconds)
        {
            float maxY = float.MinValue;
            float minA = float.MaxValue;
            float t = 0f;
            while (t < seconds && c != null)
            {
                t += Time.unscaledDeltaTime;
                var rt = c.GetComponent<RectTransform>();
                var tx = c.GetComponent<Text>();
                if (rt != null && rt.anchoredPosition.y > maxY) maxY = rt.anchoredPosition.y;
                if (tx != null && tx.color.a < minA) minA = tx.color.a;
                yield return null;
            }
            _sampledY = maxY;
            _sampledA = minA;
        }

        private float _tgMaxA;
        private float _tgMinA;

        /// <summary>采样警示圈：记录窗口内 alpha 的极大/极小，用于判定"是否在脉动"。</summary>
        private IEnumerator SampleTelegraph(Component c, float seconds)
        {
            float maxA = float.MinValue;
            float minA = float.MaxValue;
            float t = 0f;
            while (t < seconds && c != null)
            {
                t += Time.unscaledDeltaTime;
                var sr = c.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    if (sr.color.a > maxA) maxA = sr.color.a;
                    if (sr.color.a < minA) minA = sr.color.a;
                }
                yield return null;
            }
            _tgMaxA = maxA;
            _tgMinA = minA;
        }

        private void Fail(string msg) { _fail.Add(msg); }

        private static string Yn(bool b) => b ? "是" : "否";

        private static string N2(float v) { return v.ToString("0.00"); }

        private static string N3(float v) { return v.ToString("0.000"); }

        /// <summary>SMOKE 行的键宽对齐：中日韩字符按两列算，保证键值之间至少两个空格（驱动脚本靠它切分）。</summary>
        private static string Pad(string key)
        {
            int w = 0;
            foreach (var ch in key) w += ch > '\u2E80' ? 2 : 1;
            int pad = 18 - w;
            if (pad < 2) pad = 2;
            return key + new string(' ', pad);
        }

        private static void Line(string key, string value)
        {
            Debug.Log("[SMOKE] " + Pad(key) + value);
        }

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "FONT") pass = pass && _fontChecked == 3 && _fontOk == 3;
            if (_scene == "DMG") pass = pass && _dmgChecked == 5 && _dmgOk == 5;
            if (_scene == "PUNCH") pass = pass && _punchChecked == 3 && _punchOk == 3;
            if (_scene == "TELEGRAPH") pass = pass && _tgChecked == 5 && _tgOk == 5;
            if (_scene == "LEFTOVER") pass = pass && _leftChecked == 2 && _leftOk == 2;

            Debug.Log("[SMOKE] ===== 可见层第二批实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "FONT":
                    Line("断点通过", _fontOk + "/" + _fontChecked);
                    Line("字体", _fontName);
                    Line("工厂字体非空", Yn(_fontFactoryOk));
                    Line("飘字字体非空", Yn(_fontFloatOk));
                    Line("两处同一实例", Yn(_fontSameOk));
                    break;
                case "DMG":
                    Line("断点通过", _dmgOk + "/" + _dmgChecked);
                    Line("飘字数量", _dmgCount.ToString());
                    Line("飘字文案", _dmgText);
                    Line("上浮量", N2(_dmgRise));
                    Line("淡出量", N2(_dmgFade));
                    Line("打完后残留", _dmgLeft.ToString());
                    Line("已生成", Yn(_dmgSpawnOk));
                    Line("文案正确", Yn(_dmgTextOk));
                    Line("会上浮", Yn(_dmgRiseOk));
                    Line("会淡出", Yn(_dmgFadeOk));
                    Line("会回收", Yn(_dmgCleanOk));
                    break;
                case "PUNCH":
                    Line("断点通过", _punchOk + "/" + _punchChecked);
                    Line("受击倍率", N3(_punchRatio));
                    Line("回落后倍率", N3(_punchBack));
                    Line("连击后倍率", N3(_punchTwice));
                    Line("受击有冲击", Yn(_punchUpOk));
                    Line("会回落", Yn(_punchBackOk));
                    Line("不叠乘", Yn(_punchNoStackOk));
                    break;
                case "TELEGRAPH":
                    Line("断点通过", _tgOk + "/" + _tgChecked);
                    Line("闪烁振幅", N3(_tgBlink));
                    Line("真实时长", N3(_tgReal) + "s");
                    Line("到点残留", _tgLeft.ToString());
                    Line("已生成", Yn(_tgSpawnOk));
                    Line("有精灵", Yn(_tgSpriteOk));
                    Line("会脉动", Yn(_tgBlinkOk));
                    Line("时长正确", Yn(_tgDurOk));
                    Line("会自毁", Yn(_tgCleanOk));
                    break;
                default:
                    Line("断点通过", _leftOk + "/" + _leftChecked);
                    Line("重开前存量", _leftBefore.ToString());
                    Line("重开后残留", _leftAfter.ToString());
                    Line("残留构成", _leftBreak);
                    Line("已造出残留", Yn(_leftMadeOk));
                    Line("重开后清零", Yn(_leftCleanOk));
                    break;
            }
            Line("失败项", _fail.Count == 0 ? "无" : string.Join(" | ", _fail.ToArray()));
            Line("运行期异常", _errors.ToString());
            if (_errors > 0) Line("首条错误", _firstError);
            Line("结论", pass ? "通过" : "不通过");
        }
    }
}
