using System.Collections;
using System.Collections.Generic;
using StarPixel.Combat;
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

        // —— FX（M53）：池化特效的收支守恒与纹理零增量 ——
        private int _fxChecked;
        private int _fxOk;
        private int _fxTexDelta = -999;
        private int _fxSpawned;
        private int _fxPeak = -1;
        private int _fxAfter = -1;
        private int _fxLive = -1;
        private bool _fxSpawnOk, _fxTexOk, _fxCapOk, _fxCleanOk, _fxIdentityOk, _fxTearOk;

        // —— SHAPE（M53）：形状精灵必须是缓存的同一实例 ——
        private int _shChecked;
        private int _shOk;
        private int _shKinds;
        private int _shTexDelta = -999;
        private bool _shNonNullOk, _shSameOk, _shDistinctOk, _shTexOk;

        // —— SKIN（M53）：选卡卡片必须按 kind 上稀有度色，且真点得到 ——
        private int _skChecked;
        private int _skOk;
        private string _skColors = "?";
        private string _skTags = "?";
        private bool _skPaintOk, _skDistinctOk, _skTagOk, _skClickOk;
        private string _skClickWhy = "未尝试";

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
                case "FX": yield return FxPool(); break;
                case "SHAPE": yield return Shapes(); break;
                case "SKIN": yield return Skin(); break;
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
        //  FX（M53）：池化特效——收支守恒、有上限、纹理零增量、拆世界清零
        // =====================================================================================

        /// <summary>进程里此刻存在的 Texture2D 总数（含 Unity 自带的，故一律比**增量**）。</summary>
        private static int CountTextures()
        {
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (t != null) n++;
            return n;
        }

        /// <summary>场上**正在演出**的粒子实例数：排除池模板，也排除已归还的（inactive）。</summary>
        private static int CountLiveFx()
        {
            int n = 0;
            foreach (var f in Resources.FindObjectsOfTypeAll<FxSprite>())
            {
                if (f == null) continue;
                if (f.gameObject.name.EndsWith("Template")) continue;   // 模板不是"在用的"
                if (!f.gameObject.activeInHierarchy) continue;          // 已归还池里
                n++;
            }
            return n;
        }

        /// <summary>一轮"完整战斗特效"：六种发射器各来一次，与实战里的构成一致。</summary>
        private static void OneFxRound(Vector2 p)
        {
            Fx.Trail(p, Color.cyan, 0.4f, 0.25f);
            Fx.Flash(p, Color.yellow, 0.9f, 0.18f);
            Fx.Shockwave(p, 0.2f, 1.6f, Color.red, 0.30f);
            Fx.Glow(p, Color.blue, 0.8f, 0.30f);
            Fx.Burst(p, Color.white, 6, 3f, 0.25f, 0.30f);
            Fx.Spray(p, Vector2.right, Color.magenta, 3, 4f, 0.2f, 0.25f, 0.4f);
        }

        /// <summary>
        /// 为什么必须测这一组：
        /// 特效是**唯一一类"越爽越危险"的产出**——它按帧产生、成百上千，
        /// 而"跑通"的实测从来判不出"是不是每颗都在 new 一张纹理"。
        /// M30 已经在 SpriteFactory 上吃过一次（连放 24 轮纹理 29→173），
        /// M53 又引入了六个形状，任何一个走回"按次生成"都会把那一处修复推翻。
        ///
        /// 判据的区分力：
        /// 1) 「纹理增量 == 0」与「确实放出来了」必须**同时**成立：
        ///    只测前者的话，"一颗都没生成"的实现同样拿 0 增量。
        /// 2) 上限单独成条：没有 MaxActive 的实现，连放时在场数会一路涨过 240 才暴露。
        /// 3) 恒等式「取用 − 回收 == 在场」单独成条：只看"最后归零"的话，
        ///    "登记表漏减"这种缺陷会被最后的清零掩盖掉。
        /// 4) 拆世界后登记表必须归零：静态表不清，上限判定在下一局形同虚设（M28 同类）。
        /// </summary>
        private IEnumerator FxPool()
        {
            yield return EnsureWorld();

            int tex0 = CountTextures();
            int spawned0 = Fx.SpawnedCount;

            // 1) 连放 24 轮（与 gpu_probe BURST 同强度），纹理增量必须为 0
            for (int i = 0; i < 24; i++)
                OneFxRound(new Vector2(i * 0.7f, (i % 5) * 0.5f));
            yield return null;

            _fxSpawned = Fx.SpawnedCount - spawned0;
            _fxChecked++;
            if (_fxSpawned > 0) { _fxOk++; _fxSpawnOk = true; }
            else Fail("24 轮特效一颗都没放出来（取用增量 0）——模板没注入，或池没建起来");

            _fxTexDelta = CountTextures() - tex0;
            _fxChecked++;
            if (_fxTexDelta == 0) { _fxOk++; _fxTexOk = true; }
            else Fail("连放 24 轮特效新增了 " + _fxTexDelta + " 张纹理（应 0）——" +
                      "有形状是按次生成的，M30 修的那条漏又回来了");

            // 2) 猛放到远超上限：在场数不得越过 MaxActive
            for (int i = 0; i < 400; i++)
                Fx.Burst(new Vector2(i * 0.1f, 0f), Color.green, 2, 1f, 0.2f, 0.25f);
            yield return null;
            _fxPeak = Fx.ActiveCount;
            _fxChecked++;
            if (_fxPeak > 0 && _fxPeak <= Fx.MaxActive) { _fxOk++; _fxCapOk = true; }
            else Fail("在场粒子 " + _fxPeak + "（上限 " + Fx.MaxActive + "）——" +
                      "没有上限的话打得越爽越卡，而卡是敌人统治不到的一种失败");

            // 3) 等所有粒子自然演完：账上归零，同时场上也不许还剩实例
            yield return WaitReal(2.0f);
            _fxAfter = Fx.ActiveCount;
            _fxLive = CountLiveFx();
            _fxChecked++;
            if (_fxAfter == 0 && _fxLive == 0) { _fxOk++; _fxCleanOk = true; }
            else Fail("等 2s 后账上仍有 " + _fxAfter + " 颗、场上仍有 " + _fxLive +
                      " 个实例（演完没归还池）");

            // 4) 恒等式：累计取用 − 累计回收 == 在场
            int left = Fx.SpawnedCount - Fx.RecycledCount;
            _fxChecked++;
            if (left == Fx.ActiveCount) { _fxOk++; _fxIdentityOk = true; }
            else Fail("取用−回收=" + left + " 与在场 " + Fx.ActiveCount +
                      " 对不上——有粒子卡在半路，既不演也不还");

            // 5) 拆世界后登记表必须跟着清
            GameBuilder.TeardownWorld();
            yield return null;
            _fxChecked++;
            if (Fx.ActiveCount == 0) { _fxOk++; _fxTearOk = true; }
            else Fail("拆世界后登记表中仍有 " + Fx.ActiveCount + " 颗（下一局会带着残账起步）");
        }

        // =====================================================================================
        //  SHAPE（M53）：六个程序化形状必须是缓存的同一实例
        // =====================================================================================

        /// <summary>
        /// 六个形状是 M53 全部新观感的**唯一来源**（圆角面板/高光/柔光/冲击环/星芒/火花）。
        /// 它们的共同点是**脸谱固定**：key 不随颜色、半径、调用次数变化，
        /// 因此"每个 key 只造一次"是能成立、也必须成立的约束。
        /// 一旦有人把 key 里掺进了可变参数（比如把半径写进去），
        /// 缓存会瞬间退化成"每次一张"，而画面上完全看不出来——只有这条判据能抓到。
        /// </summary>
        private IEnumerator Shapes()
        {
            // 预热一次（落基线），之后两次预热不得再新增任何纹理
            SpriteFactory.PrecacheShapes();
            int tex1 = CountTextures();
            SpriteFactory.PrecacheShapes();
            SpriteFactory.PrecacheShapes();
            int tex2 = CountTextures();

            var a = new[]
            {
                SpriteFactory.MakeRoundPanel(), SpriteFactory.MakeGloss(), SpriteFactory.MakeGlow(),
                SpriteFactory.MakeRing(), SpriteFactory.MakeStar(), SpriteFactory.MakeSparkPixel()
            };
            var b = new[]
            {
                SpriteFactory.MakeRoundPanel(), SpriteFactory.MakeGloss(), SpriteFactory.MakeGlow(),
                SpriteFactory.MakeRing(), SpriteFactory.MakeStar(), SpriteFactory.MakeSparkPixel()
            };

            // 1) 六个都必须真的有图（Sprite.texture 为空 = 画面上什么都不画）
            bool nonNull = true;
            for (int i = 0; i < a.Length; i++)
                if (a[i] == null || a[i].texture == null) nonNull = false;
            _shChecked++;
            if (nonNull) { _shOk++; _shNonNullOk = true; }
            else Fail("有形状的 Sprite 或它的 texture 为 null（那条观感压根画不出来）");

            // 2) 两次取用必须是**同一实例**——缓存生效的直接证据
            bool same = true;
            for (int i = 0; i < a.Length; i++)
                if (!ReferenceEquals(a[i], b[i])) same = false;
            _shChecked++;
            if (same) { _shOk++; _shSameOk = true; }
            else Fail("同一形状两次取用返回了不同实例——缓存没生效（每调一次一张纹理）");

            // 3) 六个必须是六张**不同**的图（防止"拿一张图糊过去"的实现冒充）
            bool distinct = true;
            for (int i = 0; i < a.Length; i++)
                for (int j = i + 1; j < a.Length; j++)
                    if (a[i] != null && ReferenceEquals(a[i], a[j])) distinct = false;
            _shKinds = a.Length;
            _shChecked++;
            if (distinct) { _shOk++; _shDistinctOk = true; }
            else Fail("六个形状里出现了同一实例——有形状是拿现成的图顶替的");

            // 4) 预热之后再预热两次，不得新增纹理
            _shTexDelta = tex2 - tex1;
            _shChecked++;
            if (_shTexDelta == 0) { _shOk++; _shTexOk = true; }
            else Fail("重复预热新增了 " + _shTexDelta + " 张纹理（应 0）——形状缓存有漏网的按次生成");

            yield return null;
        }

        // =====================================================================================
        //  SKIN（M53）：选卡卡片必须按 kind 上稀有度色，且真点得到
        // =====================================================================================

        /// <summary>颜色按 RGB 距离比对（浮点相等会误判）。</summary>
        private static bool SameColor(Color x, Color y)
        {
            return Mathf.Abs(x.r - y.r) < 0.02f && Mathf.Abs(x.g - y.g) < 0.02f
                   && Mathf.Abs(x.b - y.b) < 0.02f;
        }

        private static string Hex3(Color c)
        {
            return UIFactory.Hex(c);
        }

        /// <summary>
        /// 为什么必须测这一组：
        /// 稀有度着色的**全部价值**在于"玩家不读字也能分辨这一张是什么"。
        /// 而"三张卡都画出来了"这种断言，对"三张同色"的实现同样成立——
        /// 那正是 M53 之前的状态（一块 440×80 的纯色方块，三张一模一样）。
        ///
        /// 判据的区分力：
        /// 1) 颜色读的是**组件上的实测值**（RarityBar 的 Image.color），不是被测对象自述的分类；
        /// 2) 「三色互不相同」与「各自等于调色板那一档」分开断言：
        ///    只测前者的话，"按索引随机染色"的实现也能三色不同；
        /// 3) 「真点得到」走 <see cref="UIHitTest.Click"/> 而不是直接 Invoke——
        ///    M48 的教训：卡片上叠了高光层/色条之后能不能点，只有走真实射线才看得出。
        /// </summary>
        private IEnumerator Skin()
        {
            yield return EnsureWorld();

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("没有 LevelUpManager，无法建选卡面板"); yield break; }
            lum.SetExternalCards(true);

            var offers = new List<UpgradeOffer>
            {
                new UpgradeOffer { title = "SKIN_STAT", desc = "d0", kind = 3 },
                new UpgradeOffer { title = "SKIN_NEW",  desc = "d1", kind = 105 },
                new UpgradeOffer { title = "SKIN_RANK", desc = "d2", kind = 205 }
            };
            int chosen = -1;
            lum.PresentExternal(offers, i => { chosen = i; });
            yield return null;
            yield return null;

            var panel = lum.CurrentPanel;
            if (panel == null) { Fail("选卡面板没建出来"); yield break; }

            var bars = new Color[3];
            var tags = new string[3];
            Button first = null;
            for (int i = 0; i < 3; i++)
            {
                var card = panel.Find("Offer" + i);
                if (card == null) continue;
                if (i == 0) first = card.GetComponent<Button>();
                var bar = card.Find("RarityBar");
                var bi = bar != null ? bar.GetComponent<Image>() : null;
                bars[i] = bi != null ? bi.color : Color.clear;
                var tag = card.Find("Tag");
                var tt = tag != null ? tag.GetComponent<Text>() : null;
                tags[i] = tt != null ? tt.text : "";
            }

            _skColors = Hex3(bars[0]) + "," + Hex3(bars[1]) + "," + Hex3(bars[2]);
            _skTags = string.Join("/", tags);

            // 1) 三张卡的色条必须分别等于「属性=青 / 新武器=金 / 进阶=紫」
            bool paint = SameColor(bars[0], UIFactory.Palette.Accent)
                         && SameColor(bars[1], UIFactory.Palette.Gold)
                         && SameColor(bars[2], UIFactory.Palette.AccentAlt);
            _skChecked++;
            if (paint) { _skOk++; _skPaintOk = true; }
            else Fail("稀有度色不对，实测 " + _skColors + "（应 青/金/紫）——" +
                      "颜色不是信息的话，玩家只能靠读字分辨这一级拿到了什么");

            // 2) 三色必须互不相同（防止"按索引随机染色"冒充分档）
            bool distinct = !SameColor(bars[0], bars[1]) && !SameColor(bars[1], bars[2])
                            && !SameColor(bars[0], bars[2]);
            _skChecked++;
            if (distinct) { _skOk++; _skDistinctOk = true; }
            else Fail("三张卡的稀有度色有重复（" + _skColors + "）——分档没落到颜色上");

            // 3) 标签必须已本地化：非空，且不是裸键（缺键时 Localization.T 会原样返回键名）
            bool tagOk = true;
            for (int i = 0; i < 3; i++)
                if (string.IsNullOrEmpty(tags[i]) || tags[i].StartsWith("upgrade.")) tagOk = false;
            _skChecked++;
            if (tagOk) { _skOk++; _skTagOk = true; }
            else Fail("稀有度标签是空的或是裸键（" + _skTags + "）——文案没进本地化表");

            // 4) 卡片必须真点得到，且点了真的回传了选择
            //    先等入场动画演完：动画期间卡片是缩放中的（0→1），
            //    那时点它测的是"动画中点的到吗"，不是玩家真正面对的稳定态。
            yield return WaitReal(0.45f);
            string why = "";
            bool clicked = first != null && UIHitTest.Click(first, out why);
            _skClickWhy = first == null ? "否（没有 Offer0 按钮）" : why;
            yield return null;
            _skChecked++;
            if (clicked && chosen == 0) { _skOk++; _skClickOk = true; }
            else Fail("第一张卡点不到或点了没收回调（" + _skClickWhy + "、回传=" + chosen + "）——" +
                      "加了高光层/色条之后卡片被挡住的话，玩家升级时点不动");

            yield return WaitReal(0.1f);
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
            if (_scene == "FX") pass = pass && _fxChecked == 6 && _fxOk == 6;
            if (_scene == "SHAPE") pass = pass && _shChecked == 4 && _shOk == 4;
            if (_scene == "SKIN") pass = pass && _skChecked == 4 && _skOk == 4;

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
                case "LEFTOVER":
                    Line("断点通过", _leftOk + "/" + _leftChecked);
                    Line("重开前存量", _leftBefore.ToString());
                    Line("重开后残留", _leftAfter.ToString());
                    Line("残留构成", _leftBreak);
                    Line("已造出残留", Yn(_leftMadeOk));
                    Line("重开后清零", Yn(_leftCleanOk));
                    break;
                case "FX":
                    Line("断点通过", _fxOk + "/" + _fxChecked);
                    Line("取用增量", _fxSpawned.ToString());
                    Line("纹理增量", _fxTexDelta.ToString());
                    Line("峰值在场", _fxPeak.ToString() + "/" + Fx.MaxActive);
                    Line("演完账上", _fxAfter.ToString());
                    Line("演完场上", _fxLive.ToString());
                    Line("确实放出来了", Yn(_fxSpawnOk));
                    Line("纹理零增量", Yn(_fxTexOk));
                    Line("未越过上限", Yn(_fxCapOk));
                    Line("全部归还", Yn(_fxCleanOk));
                    Line("收支守恒", Yn(_fxIdentityOk));
                    Line("拆世界清零", Yn(_fxTearOk));
                    break;
                case "SHAPE":
                    Line("断点通过", _shOk + "/" + _shChecked);
                    Line("形状种数", _shKinds.ToString());
                    Line("重预热纹理增量", _shTexDelta.ToString());
                    Line("六个都有图", Yn(_shNonNullOk));
                    Line("两次同一实例", Yn(_shSameOk));
                    Line("六个互不相同", Yn(_shDistinctOk));
                    Line("重复预热零增量", Yn(_shTexOk));
                    break;
                case "SKIN":
                    Line("断点通过", _skOk + "/" + _skChecked);
                    Line("三卡色条", _skColors);
                    Line("稀有度标签", _skTags);
                    Line("分档正确", Yn(_skPaintOk));
                    Line("三色互不相同", Yn(_skDistinctOk));
                    Line("标签已本地化", Yn(_skTagOk));
                    Line("点击结果", _skClickWhy);
                    Line("卡片可点", Yn(_skClickOk));
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
