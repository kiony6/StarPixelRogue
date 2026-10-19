using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StarPixel.Combat;
using StarPixel.Combat.Skill;
using StarPixel.Core;
using StarPixel.Player;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M32「联机本地守卫对称性」无头实测。
    ///
    /// 为什么要测：
    ///   PlayerController 上有一批"只有本地玩家该做的事"——往 HUD 广播事件、往全局单体
    ///   写本地加成。这些动作此前**从未被断言过**，因为它们只在联机下才可能出错：
    ///   单机时 isLocal 恒为 true，走哪条路都一样，于是"远程玩家串台"这类缺陷天然测不出。
    ///   M31 收尾时就留下这么一处：`AddSkill`（解锁武器）里的 RaiseWeaponsChanged 没有
    ///   isLocal 守卫，而同类的 `LevelUpWeapon`（武器升级）有——同一件事两条路不一致。
    ///
    /// 怎么在单机无头环境里测到"只有联机才成立"的分支：
    ///   `isLocal` 是 public 字段（默认 false，单机由 GameBuilder 显式置 true），
    ///   所以探针直接把它翻成 false，就等价于"这是一个远程玩家对象"。
    ///   这一手法本身必须由 BASE 场景先证明有效，否则后面全绿是假的。
    ///
    /// 五个场景：
    ///   BASE    —— 地基自检：isLocal 字段可写、单机初始为 true、翻转后已守卫的路径真的不广播。
    ///   REMOTE  —— 远程玩家不得广播：解锁 / 升级 / 重复解锁三条路都必须零广播。
    ///   LOCAL   —— 本地必须广播：同上三条路在本地都必须恰好一次（防"修过头把本地也堵死"）。
    ///   STATE   —— 远程玩家不得污染本地全局状态：ComboManager 单例的加成不许被远程玩家改写。
    ///   RESTORE —— 恢复与无副作用：翻转后要恢复、恢复后本地广播照常、全程零异常。
    ///
    /// 判据的区分力：
    /// 1) REMOTE 与 LOCAL 必须成对——只写"远程不许广播"的话，把 RaiseWeaponsChanged 整行
    ///    删掉也能全绿（本地也收不到了，玩家 HUD 彻底不刷新），必须靠 LOCAL 的正向判据抓住。
    /// 2) BASE 第 3 条是"手法自检"：若翻转没生效，REMOTE 会一片假绿，必须先在 BASE 红掉。
    /// 3) STATE 先做本地对照再加远程断言——不先证明"加武器确实会改加成"，
    ///    "远程加武器后加成没变"就可能只是因为这条路径本来就不改加成（假绿）。
    /// </summary>
    public class GuardProbe : MonoBehaviour
    {
        private string _scene = "BASE";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // OnWeaponsChanged 广播计数
        private int _weaponEvents;
        private Action _onWeapons;

        // —— BASE ——
        private int _baChecked;
        private int _baOk;
        private bool _baFieldOk;
        private bool _baInitOk;
        private bool _baFlipOk;
        private string _baFieldDesc = "?";
        private int _baFlipGot;

        // —— REMOTE ——
        private int _reChecked;
        private int _reOk;
        private int _reAddSkill;
        private int _reLevelUp;
        private int _reDup;
        private bool _reAddOk;
        private bool _reLvOk;
        private bool _reDupOk;

        // —— LOCAL ——
        private int _loChecked;
        private int _loOk;
        private int _loAddSkill;
        private int _loLevelUp;
        private int _loTotal;
        private bool _loAddOk;
        private bool _loLvOk;
        private bool _loTotalOk;

        // —— STATE ——
        private int _stChecked;
        private int _stOk;
        private float _stDmgBase;
        private float _stDmgLocal;
        private float _stDmgRemote;
        private int _stPierceLocal;
        private int _stPierceRemote;
        private bool _stLocalOk;
        private bool _stDmgOk;
        private bool _stPierceOk;

        // —— RESTORE ——
        private int _rsChecked;
        private int _rsOk;
        private bool _rsFlagOk;
        private bool _rsAliveOk;
        private bool _rsNoErrOk;
        private int _rsFinal;

        public static void Start(string scene)
        {
            var go = new GameObject("GuardProbe");
            go.SetActive(false);
            var p = go.AddComponent<GuardProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Start()
        {
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                {
                    _errors++;
                    if (_firstError == "") _firstError = msg.Split('\n')[0];
                }
            };
            // 与被测代码同款纪律：静态事件必须退订（M29 判据）
            _onWeapons = () => { _weaponEvents++; };
            EventBus.OnWeaponsChanged += _onWeapons;
            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            if (_onWeapons != null) { EventBus.OnWeaponsChanged -= _onWeapons; _onWeapons = null; }
        }

        private IEnumerator Run()
        {
            SettingsManager.Ensure();
            SaveManager.Ensure();
            Application.targetFrameRate = 60;
            GameBuilder.PrepareStage();

            IEnumerator body;
            switch (_scene)
            {
                case "REMOTE":  body = Remote();  break;
                case "LOCAL":   body = Local();   break;
                case "STATE":   body = State();   break;
                case "RESTORE": body = Restore(); break;
                default:        body = Base();    break;
            }

            // 手动驱动迭代器而不是 try{ yield } 包一层：C# 不允许在带 catch 的 try 里
            // yield（CS1626）。但探针又必须自己吞掉异常——协程一旦被打断，后面的断言
            // 全不执行、日志一片空白，表现出来是"悄悄挂死"而不是"判据变红"（M29 踩过）。
            bool more = true;
            while (more)
            {
                object cur = null;
                try
                {
                    more = body.MoveNext();
                    if (more) cur = body.Current;
                }
                catch (Exception e)
                {
                    Fail("探针自身抛异常——协程被打断会导致日志空白、结果假绿: " + e.Message);
                    more = false;
                }
                if (more) yield return cur;
            }

            Report();
            Application.Quit(0);
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

        /// <summary>等几帧，让延迟到帧末的 Destroy / Awake 真正落地。</summary>
        private static IEnumerator Quiet(int frames = 3)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        private static PlayerController P => PlayerController.Instance;

        private void ResetCount() { _weaponEvents = 0; }

        // =====================================================================================
        // BASE：地基自检——翻转手法本身必须成立，否则后面的绿全是假的
        // =====================================================================================
        private IEnumerator Base()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            // 1) isLocal 必须是可读写的 public 字段——整套手法的前提
            FieldInfo f = null;
            try
            {
                f = typeof(PlayerController).GetField("isLocal", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                _baFieldDesc = "反射异常: " + e.Message;
            }
            _baFieldOk = f != null && f.FieldType == typeof(bool) && !f.IsInitOnly;
            _baFieldDesc = f == null
                ? "反射找不到该字段"
                : ("public=" + f.IsPublic + "、只读=" + f.IsInitOnly + "、类型=" + f.FieldType.Name);

            _baChecked++;
            if (_baFieldOk) _baOk++;
            else Fail("读不到可写的 PlayerController.isLocal 字段（" + _baFieldDesc + "）——"
                      + "本探针靠翻转这个标志来模拟远程玩家，前提不成立的话后面全是假绿");

            if (P == null) { Fail("读不到玩家——BASE 后两条判据失灵"); yield break; }

            // 2) 单机路径下 isLocal 必须为 true（由 GameBuilder 显式置位）
            _baInitOk = P.isLocal;
            _baChecked++;
            if (_baInitOk) _baOk++;
            else Fail("单机下 PlayerController.isLocal 不是 true（实际 " + P.isLocal + "）——"
                      + "单机路径应由 GameBuilder 显式置 true，否则本地 HUD 收不到任何事件");

            // 3) 翻转生效验证：翻成 false 后，已知有守卫的 LevelUpWeapon 必须不广播
            bool old = P.isLocal;
            P.isLocal = false;
            ResetCount();
            P.LevelUpWeapon(1);
            yield return null;
            _baFlipGot = _weaponEvents;
            P.isLocal = old;

            _baFlipOk = _baFlipGot == 0;
            _baChecked++;
            if (_baFlipOk) _baOk++;
            else Fail("把 isLocal 翻成 false 后，LevelUpWeapon 仍广播了 " + _baFlipGot + " 次——"
                      + "说明翻转根本没起作用，REMOTE 场景的绿会是假的");
        }

        // =====================================================================================
        // REMOTE：远程玩家不得往本地 HUD 广播——三条改武器状态的路都必须零广播
        // =====================================================================================
        private IEnumerator Remote()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null) { Fail("读不到玩家——REMOTE 判据全部失灵"); yield break; }

            bool old = P.isLocal;
            P.isLocal = false;   // 等价于"这是一个远程玩家对象"

            // 路一：解锁新武器（AddSkill 的新增分支）
            ResetCount();
            P.AddSkill(UpgradeCatalog.CreateById(2));   // id=2 Scatter（storm）
            yield return null;
            _reAddSkill = _weaponEvents;

            // 路二：武器升级
            ResetCount();
            P.LevelUpWeapon(1);                          // id=1 Bolt（fire）
            yield return null;
            _reLevelUp = _weaponEvents;

            // 路三：重复解锁同一把（走 AddSkill 的"已持有→升级"分支）
            ResetCount();
            P.AddSkill(UpgradeCatalog.CreateById(2));
            yield return null;
            _reDup = _weaponEvents;

            P.isLocal = old;

            _reChecked++;
            if (_reAddSkill == 0) { _reOk++; _reAddOk = true; }
            else Fail("远程玩家解锁武器时广播了 " + _reAddSkill + " 次 OnWeaponsChanged——"
                      + "房主的 HUD 会被别人的武器变动刷新（AddSkill 缺 isLocal 守卫，"
                      + "而同类的 LevelUpWeapon 有），这种串台只在联机下出现、单机永远测不到");

            _reChecked++;
            if (_reLevelUp == 0) { _reOk++; _reLvOk = true; }
            else Fail("远程玩家升级武器时广播了 " + _reLevelUp + " 次 OnWeaponsChanged——"
                      + "LevelUpWeapon 的 isLocal 守卫失效，房主 HUD 会被远程玩家带着刷新");

            _reChecked++;
            if (_reDup == 0) { _reOk++; _reDupOk = true; }
            else Fail("远程玩家重复解锁同一把武器时广播了 " + _reDup + " 次 OnWeaponsChanged——"
                      + "这是 AddSkill 的「已持有→升级」分支，与路一同一个方法、同一处漏守卫");
        }

        // =====================================================================================
        // LOCAL：本地必须广播——防"修过头把本地也堵死"
        // =====================================================================================
        private IEnumerator Local()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null) { Fail("读不到玩家——LOCAL 判据全部失灵"); yield break; }

            P.isLocal = true;

            ResetCount();
            P.AddSkill(UpgradeCatalog.CreateById(2));   // id=2 Scatter（storm），初始只有 fire 故属新武器
            yield return null;
            _loAddSkill = _weaponEvents;

            P.LevelUpWeapon(1);
            yield return null;
            _loLevelUp = _weaponEvents - _loAddSkill;
            _loTotal = _weaponEvents;

            _loChecked++;
            if (_loAddSkill == 1) { _loOk++; _loAddOk = true; }
            else Fail("本地玩家解锁武器后广播 " + _loAddSkill + " 次（应为 1）——"
                      + "少了 HUD 不刷新，玩家看不到新武器；多了会连刷");

            _loChecked++;
            if (_loLevelUp == 1) { _loOk++; _loLvOk = true; }
            else Fail("本地玩家升级武器后广播 " + _loLevelUp + " 次（应为 1）——"
                      + "守卫加错地方会把本地一起堵死，玩家升级了却毫无反馈");

            _loChecked++;
            if (_loTotal == 2) { _loOk++; _loTotalOk = true; }
            else Fail("本地连续改动两次武器后累计广播 " + _loTotal + " 次（应为 2）——"
                      + "重复广播会让 HUD 刷新两次，缺广播则一次都不刷");
        }

        // =====================================================================================
        // STATE：远程玩家不得污染本地全局状态（ComboManager 是全局单体）
        // =====================================================================================
        private IEnumerator State()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var cm = ComboManager.Instance;
            if (P == null || cm == null)
            {
                Fail("读不到玩家或 ComboManager（玩家=" + (P == null ? "null" : "有")
                     + "、ComboManager=" + (cm == null ? "null" : "有") + "）——STATE 判据全部失灵");
                yield break;
            }

            _stDmgBase = cm.bonusDamageMult;

            // 先做本地对照：加 storm 凑成 fire+storm → 伤害倍率必须变（1.5）
            bool old = P.isLocal;
            P.isLocal = true;
            P.AddSkill(UpgradeCatalog.CreateById(2));   // storm
            yield return null;
            _stDmgLocal = cm.bonusDamageMult;
            _stPierceLocal = cm.bonusPierce;

            // 再做远程断言：加 holy 会凑成 holy+fire（1.4，且覆盖掉上面的 1.5）
            // ——若远程玩家照样 Recompute，本地玩家的伤害加成就被别人改写了
            P.isLocal = false;
            P.AddSkill(UpgradeCatalog.CreateById(9));   // holy
            yield return null;
            _stDmgRemote = cm.bonusDamageMult;
            _stPierceRemote = cm.bonusPierce;

            P.isLocal = old;

            _stChecked++;
            _stLocalOk = !Near(_stDmgLocal, _stDmgBase);
            if (_stLocalOk) _stOk++;
            else Fail("本地对照失效：本地加武器前后加成都是 " + F(_stDmgBase) + " → " + F(_stDmgLocal)
                      + "——判据数不出变化，那「远程后没变」就只是因为这条路径本来就不改加成（假绿）");

            _stChecked++;
            if (Near(_stDmgRemote, _stDmgLocal)) { _stOk++; _stDmgOk = true; }
            else Fail("远程玩家解锁武器把本地伤害加成从 " + F(_stDmgLocal) + " 改写成了 "
                      + F(_stDmgRemote) + "——ComboManager 是全局单体，远程玩家的 Recompute "
                      + "会覆盖本地玩家的组合加成，房主的实际伤害被别人决定");

            _stChecked++;
            if (_stPierceRemote == _stPierceLocal) { _stOk++; _stPierceOk = true; }
            else Fail("远程玩家解锁武器把本地穿透加成从 " + _stPierceLocal + " 改写成了 "
                      + _stPierceRemote + "——同一处 Recompute 污染，影响的不止伤害一项");
        }

        // =====================================================================================
        // RESTORE：翻转必须恢复、恢复后本地照常、全程零异常
        // =====================================================================================
        private IEnumerator Restore()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null) { Fail("读不到玩家——RESTORE 判据全部失灵"); yield break; }

            bool old = P.isLocal;

            // 先当远程玩家用一轮
            P.isLocal = false;
            P.AddSkill(UpgradeCatalog.CreateById(2));
            yield return null;

            // 恢复
            P.isLocal = old;

            _rsFlagOk = P.isLocal;
            _rsChecked++;
            if (_rsFlagOk) _rsOk++;
            else Fail("探针结束后 isLocal 没恢复成 true（实际 " + P.isLocal + "）——"
                      + "留下副作用会让同一进程里后续所有本地判据失真");

            ResetCount();
            P.LevelUpWeapon(1);
            yield return null;
            _rsFinal = _weaponEvents;

            _rsAliveOk = _rsFinal == 1;
            _rsChecked++;
            if (_rsAliveOk) _rsOk++;
            else Fail("恢复后本地广播异常：升一级收到 " + _rsFinal + " 次（应为 1）——"
                      + "翻转没真正还原，或广播链路被上一轮远程调用带歪了");

            _rsNoErrOk = _errors == 0;
            _rsChecked++;
            if (_rsNoErrOk) _rsOk++;
            else Fail("全程出现 " + _errors + " 条异常/错误（首条：" + _firstError + "）——"
                      + "事件链上抛异常会中断整条委托链，比漏一次广播严重得多");
        }

        // =====================================================================================
        // 工具
        // =====================================================================================
        /// <summary>浮点比较：倍率连乘会有极小误差，用相对容差。</summary>
        private static bool Near(float a, float b) => Mathf.Abs(a - b) < 0.0001f * Mathf.Max(1f, Mathf.Abs(b));

        private static string F(float v) => v.ToString("0.####");

        private void Fail(string msg)
        {
            _fail.Add(msg);
            if (_firstError == "") _firstError = msg;
        }

        private static string Yn(bool b) => b ? "是" : "否";

        private void Line(string k, string v) => Debug.Log("[SMOKE] " + k + "              " + v);

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "BASE") pass = pass && _baChecked == 3 && _baOk == 3;
            if (_scene == "REMOTE") pass = pass && _reChecked == 3 && _reOk == 3;
            if (_scene == "LOCAL") pass = pass && _loChecked == 3 && _loOk == 3;
            if (_scene == "STATE") pass = pass && _stChecked == 3 && _stOk == 3;
            if (_scene == "RESTORE") pass = pass && _rsChecked == 3 && _rsOk == 3;

            Debug.Log("[SMOKE] ===== 联机本地守卫对称性实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "BASE":
                    Line("断点通过", _baOk + "/" + _baChecked);
                    Line("isLocal字段", _baFieldDesc);
                    Line("字段可读写", Yn(_baFieldOk));
                    Line("单机初始为true", Yn(_baInitOk));
                    Line("翻转后广播次数", _baFlipGot.ToString());
                    Line("翻转手法生效", Yn(_baFlipOk));
                    break;
                case "REMOTE":
                    Line("断点通过", _reOk + "/" + _reChecked);
                    Line("远程解锁广播", _reAddSkill.ToString());
                    Line("远程升级广播", _reLevelUp.ToString());
                    Line("远程重复解锁广播", _reDup.ToString());
                    Line("解锁零广播", Yn(_reAddOk));
                    Line("升级零广播", Yn(_reLvOk));
                    Line("重复解锁零广播", Yn(_reDupOk));
                    break;
                case "LOCAL":
                    Line("断点通过", _loOk + "/" + _loChecked);
                    Line("本地解锁广播", _loAddSkill.ToString());
                    Line("本地升级广播", _loLevelUp.ToString());
                    Line("累计广播", _loTotal.ToString());
                    Line("解锁恰好一次", Yn(_loAddOk));
                    Line("升级恰好一次", Yn(_loLvOk));
                    Line("累计恰好两次", Yn(_loTotalOk));
                    break;
                case "STATE":
                    Line("断点通过", _stOk + "/" + _stChecked);
                    Line("加成基线", F(_stDmgBase));
                    Line("本地加武器后", F(_stDmgLocal));
                    Line("远程加武器后", F(_stDmgRemote));
                    Line("穿透 本地/远程", _stPierceLocal + " / " + _stPierceRemote);
                    Line("本地对照数得出变化", Yn(_stLocalOk));
                    Line("远程未改伤害加成", Yn(_stDmgOk));
                    Line("远程未改穿透加成", Yn(_stPierceOk));
                    break;
                case "RESTORE":
                    Line("断点通过", _rsOk + "/" + _rsChecked);
                    Line("恢复后isLocal", Yn(_rsFlagOk));
                    Line("恢复后广播次数", _rsFinal.ToString());
                    Line("异常条数", _errors.ToString());
                    Line("标志已恢复", Yn(_rsFlagOk));
                    Line("本地广播照常", Yn(_rsAliveOk));
                    Line("全程零异常", Yn(_rsNoErrOk));
                    break;
            }
            Line("运行时异常", _errors.ToString());
            Line("结论", pass ? "通过" : "不通过");
            if (_firstError != "") Line("首个失败", _firstError);
            foreach (var f in _fail) Line("失败项", f);
        }
    }
}
