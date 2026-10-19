using System.Collections;
using System.Collections.Generic;
using StarPixel.Combat;
using StarPixel.Combat.Skill;
using StarPixel.Core;
using StarPixel.Player;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M31「升级选卡生效链路」无头实测。
    ///
    /// 为什么要测：
    ///   roguelite 的全部成长都压在"选卡"这一个动作上——玩家每升一级弹出三张卡，
    ///   选了就必须真的变强。这条链路此前**从未被断言过**：M29 只证明了"面板弹得出来、
    ///   且只弹一次"，至于弹出来之后选的那张卡有没有真的生效，没有任何判据。
    ///   而失效方式有两类，且都不报错：① 选了没生效（数值没动）；
    ///   ② 生效了但**同一条效果被两条不同路径实现、其中一条漏了副作用**——
    ///   玩家看不到提示、听不到音效，而功能数值其实是对的，这种最难发现。
    ///
    /// 五个场景：
    ///   BASE   —— 地基自检：玩家/武器/stats 可读、初始只有一把火系武器、选卡不重复。
    ///   STAT   —— 属性强化精确生效：倍率精确到单次与两次（平方），且上限同步回满。
    ///   WEAPON —— 武器等级强化：升一级 +1，连升不越 MaxLevel 封顶。
    ///   UNLOCK —— 解锁新武器：武器数 +1、该武器的解锁卡从池里消失、重复解锁改为升级。
    ///   COMBO  —— 组合技广播：凑齐 tag 必须广播一次，且恰好一次。
    ///
    /// 判据的区分力：
    /// 1) STAT 用"一次"与"两次（平方）"两条，而不是只测"变了"。只测"变了"的话，
    ///    "漏应用"与"应用了三次"分不开（都是 != 基线），而平方是乘性叠加唯一正确的形状。
    /// 2) WEAPON 用"连升 10 次必须停在 MaxLevel"来钉封顶——只测"升一级 +1"的话，
    ///    越界的实现会在后期把数值算飞，而前期测不出来。
    /// 3) COMBO 订阅的是**事件**而非字段：数值可能是对的（Recompute 写了 bonus），
    ///    但提示/音效走的是另一条路（CheckCombos 广播），只看数值会完全漏掉。
    /// 4) BASE 必须证明 offers 不重复——"选卡池有重复项"会让玩家在同一屏看到两张一样的卡，
    ///    而它不会报错，只是让 build 变窄。
    /// </summary>
    public class UpgradeProbe : MonoBehaviour
    {
        private string _scene = "BASE";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // 组合技广播计数（COMBO 场景）
        private readonly List<string> _combos = new List<string>();
        private System.Action<string> _onCombo;

        // —— BASE ——
        private int _baChecked;
        private int _baOk;
        private int _baWeapons;
        private float _baDmg;
        private int _baOffers;
        private int _baDup;
        private bool _baPlayerOk;
        private bool _baCountOk;
        private bool _baOfferOk;

        // —— STAT ——
        private int _stChecked;
        private int _stOk;
        private float _stBase;
        private float _stOnce;
        private float _stTwice;
        private float _stHpBase;
        private float _stHpAfter;
        private float _stCurAfter;
        private bool _stOnceOk;
        private bool _stTwiceOk;
        private bool _stHpOk;

        // —— WEAPON ——
        private int _weChecked;
        private int _weOk;
        private int _weBase;
        private int _weAfter;
        private int _weMax;
        private int _weCap;
        private bool _weUpOk;
        private bool _weCapOk;
        private bool _weStayOk;

        // —— UNLOCK ——
        private int _unChecked;
        private int _unOk;
        private int _unBefore;
        private int _unAfter;
        private int _unUnlockCard;
        private int _unLevelAfter;
        private bool _unAddOk;
        private bool _unGoneOk;
        private bool _unDupOk;

        // —— COMBO ——
        private int _coChecked;
        private int _coOk;
        private int _coBase;
        private int _coAfter;
        private string _coKeys = "";
        private bool _coIdleOk;
        private bool _coFireOk;
        private bool _coOnceOk;

        public static void Start(string scene)
        {
            var go = new GameObject("UpgradeProbe");
            go.SetActive(false);
            var p = go.AddComponent<UpgradeProbe>();
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
            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            // 与被测代码同款纪律：静态事件必须退订（M29 判据）
            if (_onCombo != null) { EventBus.OnComboActivated -= _onCombo; _onCombo = null; }
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
                case "STAT": yield return StartCoroutine(Stat()); break;
                case "WEAPON": yield return StartCoroutine(Weapon()); break;
                case "UNLOCK": yield return StartCoroutine(Unlock()); break;
                case "COMBO": yield return StartCoroutine(Combo()); break;
                default: yield return StartCoroutine(Base()); break;
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

        // =====================================================================================
        // BASE：地基自检——读得到玩家、初始武器正确、选卡池不重复
        // =====================================================================================
        private IEnumerator Base()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            _baChecked++;
            if (P != null && P.Weapons != null && P.Weapons.Count >= 1 && P.Weapons[0].def != null)
            {
                _baOk++; _baPlayerOk = true;
                _baWeapons = P.Weapons.Count;
                _baDmg = P.stats.attackDamageMult;
            }
            else Fail("读不到玩家武器（Instance=" + (P == null ? "null" : "有")
                      + "、武器数=" + (P != null && P.Weapons != null ? P.Weapons.Count : -1)
                      + "）——后面所有断言都会假绿");

            // 初始必须恰好一把、且是火系 Bolt（id=1）——COMBO 场景靠这个前提构造 fire+storm
            _baChecked++;
            if (_baWeapons == 1 && P != null && P.Weapons != null && P.Weapons[0].def.id == 1)
            { _baOk++; _baCountOk = true; }
            else Fail("初始武器不是恰好一把 Bolt（数量=" + _baWeapons
                      + "、首把 id=" + (P != null && P.Weapons != null && P.Weapons.Count > 0
                                        ? P.Weapons[0].def.id : -1)
                      + "）——COMBO 场景的 fire+storm 前提不成立");

            var offers = P != null ? UpgradeCatalog.BuildOffers(3, P) : new List<UpgradeOffer>();
            _baOffers = offers.Count;
            var kinds = new HashSet<int>();
            foreach (var o in offers) if (!kinds.Add(o.kind)) _baDup++;

            _baChecked++;
            if (_baOffers == 3 && _baDup == 0) { _baOk++; _baOfferOk = true; }
            else Fail("选卡池 " + _baOffers + " 张、其中重复 " + _baDup
                      + " 张——玩家会在同一屏看到两张一样的卡，build 被白白收窄");
        }

        // =====================================================================================
        // STAT：属性强化必须精确生效——一次是单次倍率，两次是平方
        // =====================================================================================
        private IEnumerator Stat()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null) { Fail("读不到玩家——STAT 判据全部失灵"); yield break; }

            _stBase = P.stats.attackDamageMult;

            // kind=2 → attackDamageMult *= 1.15（乘性叠加，见 UpgradeCatalog.ApplyStat）
            UpgradeCatalog.ApplyKind(2, P);
            yield return null;
            _stOnce = P.stats.attackDamageMult;

            UpgradeCatalog.ApplyKind(2, P);
            yield return null;
            _stTwice = P.stats.attackDamageMult;

            _stChecked++;
            // 只测"变了"是不够的：漏应用与"应用了三次"都表现为 != 基线。
            // 精确到单次倍率才能把两者分开。
            if (Near(_stOnce, _stBase * 1.15f)) { _stOk++; _stOnceOk = true; }
            else Fail("选一次攻击强化后倍率 " + F(_stBase) + " → " + F(_stOnce)
                      + "（应为 " + F(_stBase * 1.15f) + "）——卡没生效或倍率不对");

            _stChecked++;
            if (Near(_stTwice, _stBase * 1.15f * 1.15f)) { _stOk++; _stTwiceOk = true; }
            else Fail("又选一次后倍率 " + F(_stTwice) + "（应为 " + F(_stBase * 1.15f * 1.15f)
                      + "）——连选两次要么没叠加、要么叠了三倍");

            // 生命上限：+20 且必须同步回满，否则玩家看着上限涨了、血条却还是空的
            _stHpBase = P.stats.maxHp;
            UpgradeCatalog.ApplyKind(0, P);
            yield return null;
            _stHpAfter = P.stats.maxHp;
            _stCurAfter = P.damageable != null ? P.damageable.currentHp : -1f;

            _stChecked++;
            if (Near(_stHpAfter, _stHpBase + 20f) && Near(_stCurAfter, _stHpAfter))
            { _stOk++; _stHpOk = true; }
            else Fail("选生命强化后上限 " + F(_stHpBase) + " → " + F(_stHpAfter)
                      + "（应为 " + F(_stHpBase + 20f) + "）、当前血 " + F(_stCurAfter)
                      + "（应同步回满）——上限涨了血条不补满，选了等于没选");
        }

        // =====================================================================================
        // WEAPON：武器等级强化——升一级 +1，连升必须停在 MaxLevel
        // =====================================================================================
        private IEnumerator Weapon()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null || P.Weapons.Count == 0) { Fail("读不到武器——WEAPON 判据全部失灵"); yield break; }

            int id = P.Weapons[0].def.id;
            _weBase = P.Weapons[0].level;

            P.LevelUpWeapon(id);
            yield return null;
            _weAfter = Lv(id);

            _weChecked++;
            if (_weAfter == _weBase + 1) { _weOk++; _weUpOk = true; }
            else Fail("武器升级后等级 " + _weBase + " → " + _weAfter
                      + "（应为 " + (_weBase + 1) + "）——卡没生效");

            // 连升 10 次：必须停在 MaxLevel。只测"升一级 +1"抓不到越界——
            // 越界的后果要玩到后期才炸，而前期一切正常。
            for (int i = 0; i < 10; i++) P.LevelUpWeapon(id);
            yield return null;
            _weMax = Lv(id);

            _weChecked++;
            if (_weMax == SkillDef.MaxLevel) { _weOk++; _weCapOk = true; }
            else Fail("连升 10 次后等级 " + _weMax + "（上限 " + SkillDef.MaxLevel
                      + "）——等级越界，后期数值会被算飞");

            // 封顶之后再升，不许继续涨也不许回退
            P.LevelUpWeapon(id);
            yield return null;
            _weCap = Lv(id);

            _weChecked++;
            if (_weCap == SkillDef.MaxLevel) { _weOk++; _weStayOk = true; }
            else Fail("已满级再升一次后等级 " + _weCap + "（应保持 " + SkillDef.MaxLevel + "）");
        }

        // =====================================================================================
        // UNLOCK：解锁新武器——加进来、解锁卡消失、重复解锁改为升级
        // =====================================================================================
        private IEnumerator Unlock()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null) { Fail("读不到玩家——UNLOCK 判据全部失灵"); yield break; }

            _unBefore = P.Weapons.Count;
            P.AddSkill(UpgradeCatalog.CreateById(2));   // id=2 Scatter（storm）
            yield return null;
            _unAfter = P.Weapons.Count;

            _unChecked++;
            if (_unAfter == _unBefore + 1 && P.HasSkill(2)) { _unOk++; _unAddOk = true; }
            else Fail("解锁 Scatter 后武器数 " + _unBefore + " → " + _unAfter
                      + "、HasSkill(2)=" + P.HasSkill(2) + "——新武器没进武器池");

            // 已持有之后，池里不该再出现"解锁这张卡"（kind = 100 + id）
            var offers = UpgradeCatalog.BuildOffers(20, P);
            int unlockCard = 0;
            foreach (var o in offers) if (o.kind == 102) unlockCard++;
            _unUnlockCard = unlockCard;

            _unChecked++;
            if (unlockCard == 0) { _unOk++; _unGoneOk = true; }
            else Fail("已持有 Scatter 却仍抽出 " + unlockCard
                      + " 张它的解锁卡——选中后什么都不会发生，玩家以为自己变强了");

            // 重复解锁同一把：不新增条目，改为升级（AddSkill 的"已持有"分支）
            int lvBefore = Lv(2);
            P.AddSkill(UpgradeCatalog.CreateById(2));
            yield return null;
            int cnt2 = P.Weapons.Count;
            _unLevelAfter = Lv(2);

            _unChecked++;
            if (cnt2 == _unAfter && _unLevelAfter == lvBefore + 1) { _unOk++; _unDupOk = true; }
            else Fail("重复解锁 Scatter：武器数 " + _unAfter + " → " + cnt2
                      + "（应不变）、等级 " + lvBefore + " → " + _unLevelAfter
                      + "（应 +1）——要么插了重复条目，要么没转成升级");
        }

        // =====================================================================================
        // COMBO：组合技激活必须广播——数值对了不等于玩家收到了
        // =====================================================================================
        private IEnumerator Combo()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            if (P == null) { Fail("读不到玩家——COMBO 判据全部失灵"); yield break; }

            // 订阅的是**事件**而不是字段：bonus 由 Recompute 写入（数值层面是对的），
            // 而提示/音效走 OnComboActivated 广播。只看数值会把"广播漏了"完全漏掉。
            _onCombo = key => { _combos.Add(key); };
            EventBus.OnComboActivated += _onCombo;

            _coBase = _combos.Count;

            _coChecked++;
            if (_coBase == 0) { _coOk++; _coIdleOk = true; }
            else Fail("还没凑齐组合就广播了 " + _coBase + " 次——基线不干净，后面的计数全是假的");

            // 初始只有 fire（Bolt），补一把 storm（Scatter）→ 应激活 fire_storm
            P.AddSkill(UpgradeCatalog.CreateById(2));
            yield return Quiet(2);
            _coAfter = _combos.Count;
            _coKeys = _combos.Count == 0 ? "无" : string.Join(" / ", _combos.ToArray());

            _coChecked++;
            if (_coAfter >= 1) { _coOk++; _coFireOk = true; }
            else Fail("凑齐 fire+storm 后组合技一次都没广播（" + _coKeys
                      + "）——加成其实算出来了，但玩家收不到提示也听不到音效，"
                      + "等于白凑了一个组合");

            _coChecked++;
            if (_coAfter == 1) { _coOk++; _coOnceOk = true; }
            else if (_coAfter <= 0) Fail("凑齐一个组合却一次都没广播——加成其实算出来了，"
                                         + "但玩家收不到提示也听不到音效，等于白凑");
            else Fail("凑齐一个组合却广播了 " + _coAfter + " 次（" + _coKeys
                      + "）——提示会连弹，玩家以为激活了两个");
        }

        // =====================================================================================
        // 工具
        // =====================================================================================
        private static int Lv(int id)
        {
            var p = P;
            if (p == null) return -1;
            foreach (var w in p.Weapons)
                if (w != null && w.def != null && w.def.id == id) return w.level;
            return -1;
        }

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
            if (_scene == "STAT") pass = pass && _stChecked == 3 && _stOk == 3;
            if (_scene == "WEAPON") pass = pass && _weChecked == 3 && _weOk == 3;
            if (_scene == "UNLOCK") pass = pass && _unChecked == 3 && _unOk == 3;
            if (_scene == "COMBO") pass = pass && _coChecked == 3 && _coOk == 3;

            Debug.Log("[SMOKE] ===== 升级选卡生效链路实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "BASE":
                    Line("断点通过", _baOk + "/" + _baChecked);
                    Line("初始武器数", _baWeapons.ToString());
                    Line("攻击倍率基线", F(_baDmg));
                    Line("选卡张数", _baOffers.ToString());
                    Line("重复卡数", _baDup.ToString());
                    Line("读得到玩家", Yn(_baPlayerOk));
                    Line("初始一把Bolt", Yn(_baCountOk));
                    Line("选卡不重复", Yn(_baOfferOk));
                    break;
                case "STAT":
                    Line("断点通过", _stOk + "/" + _stChecked);
                    Line("倍率基线", F(_stBase));
                    Line("选一次后", F(_stOnce));
                    Line("选两次后", F(_stTwice));
                    Line("生命上限", F(_stHpBase) + " → " + F(_stHpAfter));
                    Line("当前血", F(_stCurAfter));
                    Line("一次精确", Yn(_stOnceOk));
                    Line("两次是平方", Yn(_stTwiceOk));
                    Line("上限同步回满", Yn(_stHpOk));
                    break;
                case "WEAPON":
                    Line("断点通过", _weOk + "/" + _weChecked);
                    Line("等级基线", _weBase.ToString());
                    Line("升一级后", _weAfter.ToString());
                    Line("连升10次后", _weMax.ToString());
                    Line("满级再升", _weCap.ToString());
                    Line("等级上限", SkillDef.MaxLevel.ToString());
                    Line("升级生效", Yn(_weUpOk));
                    Line("封顶不越界", Yn(_weCapOk));
                    Line("满级不再涨", Yn(_weStayOk));
                    break;
                case "UNLOCK":
                    Line("断点通过", _unOk + "/" + _unChecked);
                    Line("解锁前武器数", _unBefore.ToString());
                    Line("解锁后武器数", _unAfter.ToString());
                    Line("残留解锁卡", _unUnlockCard.ToString());
                    Line("重复解锁后等级", _unLevelAfter.ToString());
                    Line("新武器进池", Yn(_unAddOk));
                    Line("解锁卡消失", Yn(_unGoneOk));
                    Line("重复解锁转升级", Yn(_unDupOk));
                    break;
                default:
                    Line("断点通过", _coOk + "/" + _coChecked);
                    Line("基线广播数", _coBase.ToString());
                    Line("凑齐后广播数", _coAfter.ToString());
                    Line("广播内容", _coKeys);
                    Line("基线干净", Yn(_coIdleOk));
                    Line("凑齐会广播", Yn(_coFireOk));
                    Line("恰好一次", Yn(_coOnceOk));
                    break;
            }

            Line("运行时异常", _errors.ToString());
            Line("结论", pass ? "通过" : "不通过");
            if (_firstError != "") Line("首个失败", _firstError);
            foreach (var f in _fail) Line("失败项", f);
        }
    }
}
