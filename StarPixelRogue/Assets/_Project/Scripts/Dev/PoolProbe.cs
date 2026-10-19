using System.Collections;
using System.Collections.Generic;
using StarPixel.Core;
using StarPixel.Enemy;
using StarPixel.UI;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M27「伤害飘字对象池」无头实测。
    ///
    /// 为什么要测：
    ///   敌人 / 弹丸 / 回旋镖 / 追踪弹 / 地雷 / 宝石 / 回血球**全都**走了 PoolManager，
    ///   唯独伤害飘字（DamageNumberManager）每次伤害都 new GameObject + AddComponent&lt;Text&gt;
    ///   + 两次 Resources.GetBuiltinResource（字体回退链）。重战况下每秒可达数百次分配，
    ///   与项目「对象池抗 GC」的原则直接相悖——而它恰好是**每秒触发次数最多**的那一个
    ///   （每个敌人每次被打一次就一张）。
    ///
    ///   但"改成池"这一步本身才是危险的地方：复用实例时**字段初始化器不会重跑**。
    ///   FloatingText._life 的初值 0.6f 只在 AddComponent 时赋过一次，
    ///   漏了重置的话，第二次被复用的实例 _life 仍是 0，Update 里立刻归还——
    ///   玩家看到的就是"伤害数字只出现前几次，之后全消失"，而崩溃日志一行都没有。
    ///   这正是 M25「字段默认值不是未初始化，它会被立刻使用」的同一类陷阱。
    ///
    /// 四个场景：
    ///   POOL     —— 120 次高频伤害：累计新建必须有上限（而不是 120）、复用率必须过半、
    ///               峰值并发必须真的上去、结束后必须全部归还。
    ///   REUSE    —— 同一个实例被第二次使用：必须复用同一实例、文案必须更新、
    ///               生命周期必须被重置、必须仍然可见、字体必须还在。
    ///   BURST    —— 单帧 40 次伤害（AOE 清场）：一个都不能丢，随后全部归还。
    ///   LEFTOVER —— 真实重开序列后，上一局的飘字对象一个都不许活下来。
    ///
    /// 判据的区分力：
    /// 1) 累计新建数用**逐帧采样 instanceID 去重**得到，不读被测对象的账本
    ///    （产品代码里根本没有"新建计数"这种东西，加了就是为测试而写的假账本）。
    /// 2) "新建少"必须配"峰值并发确实很大"的对偶判据：
    ///    只测前者的话，"压根没显示几张"也能让新建数很小。
    /// 3) REUSE 专门盯**第二次**使用：单发命中完全正常，复用才暴露（M23/M26 叠乘同类）。
    /// 4) LEFTOVER 用 Unity 的"已销毁对象 == null"语义比对**对象引用**，
    ///    不比对 instanceID（Unity 可能回收 ID，比对引用才可靠）。
    /// </summary>
    public class PoolProbe : MonoBehaviour
    {
        private string _scene = "POOL";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— POOL ——
        private int _poolChecked;
        private int _poolOk;
        private int _total;
        private int _created;
        private int _peakActive;
        private float _reuse;
        private int _activeLeft;
        private bool _peakOk;
        private bool _capOk;
        private bool _reuseOk;
        private bool _returnOk;

        // —— REUSE ——
        private int _reChecked;
        private int _reOk;
        private int _idFirst;
        private int _idSecond;
        private string _firstText = "?";
        private string _secondText = "?";
        private float _secondAlpha = -1f;
        private int _stillAlive;
        private bool _sameOk;
        private bool _textOk;
        private bool _lifeOk;
        private bool _alphaOk;
        private bool _fontOk;
        private bool _reReturnOk;

        // —— BURST ——
        private int _burstChecked;
        private int _burstOk;
        private int _burstPeak;
        private int _burstCreated;
        private int _burstLeft;
        private bool _burstAllOk;
        private bool _burstCapOk;
        private bool _burstCleanOk;

        // —— LEFTOVER ——
        private int _leftChecked;
        private int _leftOk;
        private int _leftBefore;
        private int _leftAfter;
        private int _leftSurvived;
        private string _leftBreak = "";
        private int _leftShells;
        private int _leftCanvas;
        private bool _leftMadeOk;
        private bool _leftShellOk;
        private bool _leftCanvasOk;
        private bool _leftCleanOk;
        private bool _leftRebuiltOk;

        private readonly HashSet<int> _seen = new HashSet<int>();

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        public static void Start(string scene)
        {
            // AddComponent 会同步执行 Awake（M21–M26 同一条坑）：先停用 → 挂载赋值 → 再激活。
            var go = new GameObject("PoolProbe");
            go.SetActive(false);
            var p = go.AddComponent<PoolProbe>();
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
                case "REUSE": yield return Reuse(); break;
                case "BURST": yield return Burst(); break;
                case "LEFTOVER": yield return Leftover(); break;
                default: yield return Pool(); break;
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

        /// <summary>边等边逐帧采样（统计累计新建与峰值并发）。</summary>
        private IEnumerator WaitRealSample(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                Sample();
                yield return null;
            }
        }

        /// <summary>建世界 → 冻波次 → 清掉开场那一波敌人（走真实路径）。</summary>
        private IEnumerator EnsureWorld()
        {
            GameBuilder.Build();
            var em = EnemyManager.Instance;
            if (em != null) em.FreezeWaves = true;
            ClearEnemiesNow();
            yield return null;
            yield return null;
            ClearEnemiesNow();
            yield return null;
        }

        private static void ClearEnemiesNow()
        {
            var em = EnemyManager.Instance;
            if (em == null) return;
            // StarPixel.Enemy 既是命名空间又是类名，必须写全 Enemy.Enemy（M24 同一坑）
            var snapshot = new List<Enemy.Enemy>(em.ActiveEnemies);
            foreach (var e in snapshot)
            {
                if (e == null) continue;
                em.RemoveEnemy(e);
                Destroy(e.gameObject);
            }
            em.ActiveEnemies.Clear();
        }

        private static IEnumerable<FloatingText> AllFloating()
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<FloatingText>())
                if (f != null) yield return f;
        }

        private static bool IsTemplate(FloatingText f)
        {
            return f.gameObject.name.EndsWith("Template");
        }

        /// <summary>场上正在显示的飘字（不含模板、不含已归还到池里的）。</summary>
        private static int CountActive()
        {
            int n = 0;
            foreach (var f in AllFloating())
            {
                if (IsTemplate(f)) continue;
                if (!f.gameObject.activeInHierarchy) continue;
                n++;
            }
            return n;
        }

        private static FloatingText FirstActive()
        {
            foreach (var f in AllFloating())
            {
                if (IsTemplate(f)) continue;
                if (!f.gameObject.activeInHierarchy) continue;
                return f;
            }
            return null;
        }

        /// <summary>逐帧采样：把见过的实例记进取重集合，并跟踪峰值并发。</summary>
        private void Sample()
        {
            int active = 0;
            foreach (var f in AllFloating())
            {
                if (IsTemplate(f)) continue;
                _seen.Add(f.gameObject.GetInstanceID());
                if (f.gameObject.activeInHierarchy) active++;
            }
            if (active > _peakActive) _peakActive = active;
        }

        /// <summary>Build 期创建的管理器名字（单机与联机两条路径都有）。</summary>
        private static readonly string[] ManagerNames =
        {
            "PoolManager", "CombatManager", "ComboManager", "SfxManager",
            "DamageNumberManager", "LevelUpManager", "HitstopManager", "HUD",
        };

        /// <summary>
        /// 只剩空壳的管理器数量：GameObject 还在，但它身上的组件已经被销毁。
        /// Track(组件) 的后果就是这个——GameObject 与它的子物体全都留下来，一局一局堆。
        /// </summary>
        private static int CountShells()
        {
            int n = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (tr == null) continue;
                var go = tr.gameObject;
                bool named = false;
                foreach (var nm in ManagerNames)
                    if (go.name == nm) { named = true; break; }
                if (!named) continue;

                bool hasBehaviour = false;
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                    if (mb != null) { hasBehaviour = true; break; }
                if (!hasBehaviour) n++;
            }
            return n;
        }

        private static int CountNamed(string name)
        {
            int n = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
                if (tr != null && tr.gameObject.name == name) n++;
            return n;
        }

        private static string TextOf(FloatingText f)
        {
            if (f == null) return "null";
            var t = f.GetComponent<Text>();
            return t != null ? t.text : "no-text";
        }

        private static float AlphaOf(FloatingText f)
        {
            if (f == null) return -1f;
            var t = f.GetComponent<Text>();
            return t != null ? t.color.a : -1f;
        }

        private static Font FontOf(FloatingText f)
        {
            if (f == null) return null;
            var t = f.GetComponent<Text>();
            return t != null ? t.font : null;
        }

        // =====================================================================================
        //  POOL：120 次高频伤害，累计新建必须有上限
        // =====================================================================================

        private IEnumerator Pool()
        {
            yield return EnsureWorld();

            _seen.Clear();
            _peakActive = 0;

            const int rounds = 12;
            const int per = 10;
            for (int r = 0; r < rounds; r++)
            {
                for (int i = 0; i < per; i++)
                    EventBus.RaiseDamaged(10f + i, new Vector3(i * 0.3f, 0f, 0f));
                yield return null;
                Sample();
                // 间隔 0.35s：飘字寿命 0.6s，于是稳定态下最多两轮同时在场
                yield return WaitRealSample(0.35f);
            }

            yield return WaitRealSample(1.20f);

            _total = rounds * per;
            _created = _seen.Count;
            _reuse = 1f - (float)_created / _total;
            _activeLeft = CountActive();

            _poolChecked++;
            if (_peakActive >= 10) { _poolOk++; _peakOk = true; }
            else Fail("峰值并发只有 " + _peakActive + "（期望 ≥10）：压力没上去的话，'新建得少'没有意义");

            _poolChecked++;
            if (_created <= 40) { _poolOk++; _capOk = true; }
            else Fail("累计新建 " + _created + " 个飘字对象（120 次伤害，期望 ≤40：池化后不该每次新建）");

            _poolChecked++;
            if (_reuse >= 0.5f) { _poolOk++; _reuseOk = true; }
            else Fail("复用率只有 " + Pct(_reuse) + "（期望 ≥50%）");

            _poolChecked++;
            if (_activeLeft == 0) { _poolOk++; _returnOk = true; }
            else Fail("结束后仍有 " + _activeLeft + " 个飘字没有归还");
        }

        // =====================================================================================
        //  REUSE：同一个实例被第二次使用时，生命周期必须被重置
        // =====================================================================================

        private IEnumerator Reuse()
        {
            yield return EnsureWorld();

            // 第一次：等它到期归还
            EventBus.RaiseDamaged(37.4f, new Vector3(1f, 1f, 0f));
            yield return null;
            var f1 = FirstActive();
            _idFirst = f1 != null ? f1.gameObject.GetInstanceID() : 0;
            _firstText = TextOf(f1);
            yield return WaitReal(1.20f);

            // 第二次：必须是**同一个**实例被复用
            EventBus.RaiseDamaged(88.6f, new Vector3(2f, 2f, 0f));
            yield return null;
            var f2 = FirstActive();
            _idSecond = f2 != null ? f2.gameObject.GetInstanceID() : 0;
            _secondText = TextOf(f2);

            _reChecked++;
            if (f2 != null && _idFirst != 0 && _idSecond == _idFirst) { _reOk++; _sameOk = true; }
            else Fail("第二次飘字不是复用同一个实例（首次 id=" + _idFirst + " / 复用 id=" + _idSecond + "）");

            _reChecked++;
            if (_secondText == "89") { _reOk++; _textOk = true; }
            else Fail("复用实例的文案是 " + _secondText + "（期望 89：88.6 取整）");

            // 生命周期必须被重置：0.30s 后仍在场。漏了重置的话它会在第一帧就归还。
            yield return WaitReal(0.30f);
            _stillAlive = CountActive();
            _secondAlpha = AlphaOf(f2);

            _reChecked++;
            if (_stillAlive == 1) { _reOk++; _lifeOk = true; }
            else Fail("复用后的飘字立刻消失了（Init 没有重置 _life）：0.30s 后场上 " + _stillAlive + " 个");

            _reChecked++;
            if (_secondAlpha > 0.2f) { _reOk++; _alphaOk = true; }
            else Fail("复用后的飘字不可见，alpha=" + N3(_secondAlpha) + "（Init 没有重置透明度）");

            _reChecked++;
            if (FontOf(f2) != null) { _reOk++; _fontOk = true; }
            else Fail("复用实例的字体为 null（玩家看不到数字）");

            // 最后仍必须归还
            yield return WaitReal(0.80f);
            int left = CountActive();
            _reChecked++;
            if (left == 0) { _reOk++; _reReturnOk = true; }
            else Fail("复用后没有归还，仍活跃 " + left + " 个");
        }

        // =====================================================================================
        //  BURST：单帧 40 次伤害，一个都不能丢
        // =====================================================================================

        private IEnumerator Burst()
        {
            yield return EnsureWorld();

            _seen.Clear();
            _peakActive = 0;

            for (int i = 0; i < 40; i++)
                EventBus.RaiseDamaged(20f + i, new Vector3(i * 0.2f, 0f, 0f));
            yield return null;
            Sample();

            _burstPeak = _peakActive;
            yield return WaitRealSample(1.20f);
            _burstCreated = _seen.Count;
            _burstLeft = CountActive();

            _burstChecked++;
            if (_burstPeak == 40) { _burstOk++; _burstAllOk = true; }
            else Fail("单帧 40 次伤害只显示出 " + _burstPeak + " 个（池化必须一张都不丢）");

            _burstChecked++;
            if (_burstCreated <= 48) { _burstOk++; _burstCapOk = true; }
            else Fail("单帧爆发新建了 " + _burstCreated + " 个（峰值才 " + _burstPeak + "）");

            _burstChecked++;
            if (_burstLeft == 0) { _burstOk++; _burstCleanOk = true; }
            else Fail("爆发后仍有 " + _burstLeft + " 个没有归还");
        }

        // =====================================================================================
        //  LEFTOVER：真实重开序列后，上一局的飘字一个都不许活下来
        // =====================================================================================

        private IEnumerator Leftover()
        {
            yield return EnsureWorld();

            for (int i = 0; i < 5; i++)
                EventBus.RaiseDamaged(10f + i, new Vector3(i * 0.3f, 0f, 0f));
            yield return null;

            // 结算面板弹出 → 世界被冻结（飘字会在冻结期间一直挂着）
            var hs = HitstopManager.Instance;
            if (hs != null) hs.SetPaused(true);
            yield return WaitReal(0.15f);

            // 记下**对象引用**：Unity 里已销毁的对象 == null，比对引用比比对 instanceID 可靠
            var before = new List<FloatingText>();
            foreach (var f in AllFloating()) before.Add(f);
            _leftBefore = before.Count;

            _leftChecked++;
            if (_leftBefore >= 6) { _leftOk++; _leftMadeOk = true; }
            else Fail("没能造出飘字对象，实测 " + _leftBefore + "（期望 ≥6：5 张 + 模板）");

            // 玩家点「重新开始」：CloseAndResume → TeardownWorld → Build
            if (hs != null) hs.SetPaused(false);
            GameBuilder.TeardownWorld();
            yield return null;
            yield return null;
            GameBuilder.Build();
            yield return null;
            yield return null;

            // 存活构成直接列名字：和 M24/M26 一样，"残留 1 个"必须能指出是哪一个
            int survived = 0;
            var parts = new List<string>();
            foreach (var f in before)
            {
                if (f == null) continue;
                survived++;
                parts.Add(f.gameObject.name + (f.gameObject.activeInHierarchy ? "(活跃)" : "(已归还)"));
            }
            _leftSurvived = survived;
            _leftBreak = parts.Count == 0 ? "无" : string.Join(" / ", parts.ToArray());

            int after = 0;
            foreach (var f in AllFloating()) after++;
            _leftAfter = after;

            // 空壳管理器：Track(组件) 的后果——组件被销毁了，GameObject 与它的子物体全留下
            _leftShells = CountShells();
            _leftChecked++;
            if (_leftShells == 0) { _leftOk++; _leftShellOk = true; }
            else Fail("重开后残留 " + _leftShells + " 个空壳管理器 GameObject（组件被销毁、本体与子物体还在）");

            // 飘字画布：上一局那块 Overlay 画布必须跟着管理器一起没
            _leftCanvas = CountNamed("DmgCanvas");
            _leftChecked++;
            if (_leftCanvas == 1) { _leftOk++; _leftCanvasOk = true; }
            else Fail("重开后 DmgCanvas 有 " + _leftCanvas + " 块（期望 1：只剩新世界那一块）");

            _leftChecked++;
            if (survived == 0) { _leftOk++; _leftCleanOk = true; }
            else Fail("重开后仍有 " + survived + " 个上一局的飘字对象活着（含模板/克隆体）");

            _leftChecked++;
            if (after >= 1) { _leftOk++; _leftRebuiltOk = true; }
            else Fail("重开后连新的池模板都没有（" + after + "）：这一轮可能只是没建成世界");
        }

        // =====================================================================================

        private void Fail(string msg) { _fail.Add(msg); }

        private static string Yn(bool b) => b ? "是" : "否";

        private static string N3(float v) { return v.ToString("0.000"); }

        private static string Pct(float v) { return (v * 100f).ToString("0.0") + "%"; }

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
            if (_scene == "POOL") pass = pass && _poolChecked == 4 && _poolOk == 4;
            if (_scene == "REUSE") pass = pass && _reChecked == 6 && _reOk == 6;
            if (_scene == "BURST") pass = pass && _burstChecked == 3 && _burstOk == 3;
            if (_scene == "LEFTOVER") pass = pass && _leftChecked == 5 && _leftOk == 5;

            Debug.Log("[SMOKE] ===== 飘字对象池实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "POOL":
                    Line("断点通过", _poolOk + "/" + _poolChecked);
                    Line("伤害次数", _total.ToString());
                    Line("累计新建", _created.ToString());
                    Line("峰值并发", _peakActive.ToString());
                    Line("复用率", Pct(_reuse));
                    Line("结束后活跃", _activeLeft.ToString());
                    Line("并发够大", Yn(_peakOk));
                    Line("新建有上限", Yn(_capOk));
                    Line("复用过半", Yn(_reuseOk));
                    Line("全部归还", Yn(_returnOk));
                    break;
                case "REUSE":
                    Line("断点通过", _reOk + "/" + _reChecked);
                    Line("首次实例号", _idFirst.ToString());
                    Line("复用实例号", _idSecond.ToString());
                    Line("首次文案", _firstText);
                    Line("复用文案", _secondText);
                    Line("0.3s后活跃", _stillAlive.ToString());
                    Line("0.3s后透明度", N3(_secondAlpha));
                    Line("复用同一实例", Yn(_sameOk));
                    Line("文案已更新", Yn(_textOk));
                    Line("寿命已重置", Yn(_lifeOk));
                    Line("仍然可见", Yn(_alphaOk));
                    Line("字体仍在", Yn(_fontOk));
                    Line("会归还", Yn(_reReturnOk));
                    break;
                case "BURST":
                    Line("断点通过", _burstOk + "/" + _burstChecked);
                    Line("单帧次数", "40");
                    Line("峰值并发", _burstPeak.ToString());
                    Line("累计新建", _burstCreated.ToString());
                    Line("爆发后活跃", _burstLeft.ToString());
                    Line("一张不丢", Yn(_burstAllOk));
                    Line("新建不超峰值", Yn(_burstCapOk));
                    Line("全部归还", Yn(_burstCleanOk));
                    break;
                default:
                    Line("断点通过", _leftOk + "/" + _leftChecked);
                    Line("重开前存量", _leftBefore.ToString());
                    Line("重开后存量", _leftAfter.ToString());
                    Line("上一局存活", _leftSurvived.ToString());
                    Line("存活构成", _leftBreak);
                    Line("空壳管理器", _leftShells.ToString());
                    Line("飘字画布数", _leftCanvas.ToString());
                    Line("已造出飘字", Yn(_leftMadeOk));
                    Line("上一局清零", Yn(_leftCleanOk));
                    Line("无空壳残留", Yn(_leftShellOk));
                    Line("画布只剩一块", Yn(_leftCanvasOk));
                    Line("新世界已建", Yn(_leftRebuiltOk));
                    break;
            }
            Line("失败项", _fail.Count == 0 ? "无" : string.Join(" | ", _fail.ToArray()));
            Line("运行期异常", _errors.ToString());
            if (_errors > 0) Line("首条错误", _firstError);
            Line("结论", pass ? "通过" : "不通过");
        }
    }
}
