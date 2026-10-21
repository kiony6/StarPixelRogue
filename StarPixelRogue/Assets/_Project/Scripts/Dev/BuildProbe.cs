using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using StarPixel.Combat;
using StarPixel.Combat.Skill;
using StarPixel.Core;
using StarPixel.Enemy;
using StarPixel.Player;
using StarPixel.UI;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M23「选角 ↔ 开局」与「升级 ↔ 组合技」两条链路无头实测。
    ///
    /// 为什么必须测（三类缺陷都是"界面一切正常，数值静默不对"）：
    ///   1) 选角面板的「选择」按钮**从没被点过**。M21 的 L10N 场景只是把面板画出来 dump 了一遍文案，
    ///      按钮回调（写 lastCharacterId → 落盘 → 刷新标记 → 打点）一次都没执行。
    ///      点击不落盘、或落了盘开局不读回，玩家下次启动就被换回默认角色，而"已选择"标记照样亮着。
    ///   2) 选完角色**开局那一下**从来没被验证：BuildWorld 里的 ApplyTo(Selected) 是唯一
    ///      把角色数值真正乘到玩家身上的地方。只调 ApplyTo 而不走真实开局，
    ///      就测不到"选角 → 存档 → 开局"之间那段。
    ///   3) 10 把武器里有 6 把（2/6/7/8/9/10）**从未在实战里开过火**——冒烟测试只走
    ///      "自动选第一张卡"，解锁到哪把武器是随机的。散射枪/雷霆之链/月刃/蜂群导弹/
    ///      脉冲地雷/圣光领域的开火分支等于零覆盖，任何一处异常都只能等线上炸。
    ///   4) 4 条组合技规则只有"统计意义上出现过"，没有一条被逐条断言；而 ComboManager 的实现是
    ///      "多条规则叠加写同一字段"（fire+storm ×1.5 与 holy+fire ×1.4 都写 damageMult）——
    ///      这到底是设计还是 bug，必须用判据钉死。
    ///
    /// 三个场景（每个独立进程：选角是存档态，武器/组合技是进程内状态）：
    ///   SELECT      —— 从主菜单点「角色」→ 点「选择」→ 断言内存 id / save.json 落盘 /
    ///                  卡片标记（◉ 已选择）/ 主菜单角色名四处同时跟上；关闭按钮真的能关。
    ///   APPLY       —— 用上一个进程点出来的存档**真实开局**（GameBuilder.Build），断言玩家 7 个乘区
    ///                  与 CharacterDef 一致、起始武器被替换、重复 Apply 幂等、换角色后数值不叠乘。
    ///   PROGRESSION —— 10 把武器逐个开火（全部 7 种武器类型都必须真的打出伤害或产生范围效果），
    ///                  4 条组合技逐条断言加成与"只有它激活"，7 项属性强化逐项断言，
    ///                  武器等级封顶且满级后不再出现在供选里。
    ///
    /// 判据的区分力（本轮最要紧的地方）：
    /// 1) 选角用**与默认不同**的角色（id=1）。默认是 0；若断言写成 0，一个"什么都没做"的实现也能过。
    ///    同时断言主菜单角色名跟着变——只查 id 证明不了玩家看得见。
    /// 2) 落盘判据在**进程外**：驱动脚本自己解析 save.json 的 lastCharacterId。重启判据由
    ///    **第二个进程**（APPLY）从文件读回——同进程读 Current 会被内存糊住（M22 踩过 var 快照别名）。
    /// 3) 开火判据必须落在"伤害真的打出"上：不查武器面板、不查 combo 字段，而是**用真实敌人**
    ///    （EnemyManager.SpawnAdd，走对象池 + CombatTargets 索敌），看目标血量有没有掉。
    ///    只看武器列表长度的话，"解锁了但开火抛异常/打不中"全绿。
    /// 4) 组合技逐条**单独**装武器：4 条规则同时满足会让加成互相覆盖，无法区分"这条规则有没有生效"。
    ///    判据要求"只有这一条激活"，顺带把"多条规则写同一字段互相覆盖"钉成可观测事实。
    /// 5) 每个场景前由驱动脚本清空 save.json，断言建立在已知初始态上（M21 的 DECLINE 假通过教训）。
    ///
    /// 正常启动不带 -buildProbe 时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class BuildProbe : MonoBehaviour
    {
        private string _scene = "SELECT";
        private bool _done;
        private int _errors;
        private string _firstError = "";

        private readonly List<string> _fail = new List<string>();

        // —— SELECT ——
        private bool _panelBuilt;
        private bool _selectClicked;
        private bool _selMem;
        private bool _selCard;
        private bool _selMenu;
        private bool _selDisk;
        private bool _selClose;
        private string _selDetail = "";
        private int _selRunsBefore = -1;

        // —— APPLY ——
        private int _applyChecked;
        private int _applyMatched;
        private string _applyDiff = "";
        private bool _applyWeapon;
        private int _applyWeaponId;
        private bool _applyIdempotent;
        private bool _applySwapClean;
        private string _applySwapDiff = "";

        // —— PROGRESSION ——
        private readonly List<string> _fireFail = new List<string>();
        private readonly List<string> _fireOk = new List<string>();
        private int _weaponsFired;
        private int _weaponsDamaged;
        private int _combosChecked, _combosOk;
        private readonly List<string> _comboFail = new List<string>();
        private string _comboDetail = "";
        private int _statsChecked, _statsOk;
        private readonly List<string> _statFail = new List<string>();
        private bool _capOk;
        private bool _noMaxedOffer;

        // —— MENU（M52）——
        // M51 把真实点击铺进了五个探针，但铺的是"探针原本就在点的地方"；
        // 主菜单上那几颗玩家最先按下的按钮反而没人碰过。这一组就是来收它们的。
        private int _menuBtnTotal, _menuBtnOk;
        private string _menuBtnBad = "";
        private bool _codexBuilt;
        private int _codexTotal, _codexOk;
        private string _codexBad = "";
        private bool _codexClosed;
        private bool _settBuilt;
        private int _settTotal, _settOk;
        private string _settBad = "";
        private bool _settClosed;
        private bool _startClicked;
        private bool _startWorld;

        private static readonly string[] CombatVisuals = { "AuraRing", "OrbitBlade", "BoltSeg", "BoltFlash" };

        public static void Start(string scene)
        {
            // AddComponent 会**同步执行 Awake**；本探针的初始态（日志订阅/语言）在 Awake 里摆好，
            // 若在 AddComponent 之后才赋值，Awake 读到的永远是默认值（M21/M22 同一条坑）。
            var go = new GameObject("BuildProbe");
            go.SetActive(false);
            var p = go.AddComponent<BuildProbe>();
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
            Localization.SetLanguage(Localization.LangZh, silent: true);
        }

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        private static string SavePath => Path.Combine(Application.persistentDataPath, "save.json");

        private IEnumerator Start()
        {
            MainMenu.Ensure();
            MainMenu.Instance?.Show();
            yield return null;

            switch (_scene)
            {
                case "APPLY": yield return Apply(); break;
                case "PROGRESSION": yield return Progression(); break;
                case "MENU": yield return Menu(); break;
                default: yield return Select(); break;
            }

            Report();
            _done = true;
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }

        // =====================================================================================
        //  SELECT：点「角色」→ 点「选择」→ 内存 / 落盘 / 卡片标记 / 主菜单四处同时跟上
        // =====================================================================================

        private IEnumerator Select()
        {
            var save = SaveManager.Instance;
            if (save == null) { Fail("存档管理器缺失"); yield break; }

            _selRunsBefore = RunsFor(1, save);

            // 1) 从主菜单点「角色」按钮——真实入口，不是直接 Ensure
            var menuRoot = MenuRoot();
            var charBtn = menuRoot != null ? menuRoot.Find("CharacterBtn") : null;
            if (charBtn == null) { Fail("主菜单找不到「角色」按钮"); yield break; }
            // M51：走真实点击那条路（先 raycast），点不到直接算失败——
            // 否则"按钮被全屏遮罩盖住"在探针里永远看不见。
            string why;
            if (!UIHitTest.Click(charBtn.GetComponent<Button>(), out why))
                Fail("主菜单「角色」按钮点不到：" + why);
            yield return null;

            var panel = CharacterSelectPanelNode();
            _panelBuilt = panel != null;
            if (!_panelBuilt) { Fail("选角面板未建出"); yield break; }

            // 2) 初始态：默认角色 0（驱动脚本清过存档），卡片标记必须落在 0 上
            if (CharacterManager.SelectedId != 0)
                Fail("初始角色不是默认值（存档未清空？id=" + CharacterManager.SelectedId + "）");

            // 3) 点角色 1 的「选择」按钮
            var card1 = panel != null ? panel.Find("Card1") : null;
            var btn1 = card1 != null ? card1.Find("SelectBtn") : null;
            if (btn1 == null) { Fail("找不到 Card1 的选择按钮"); yield break; }
            string whySel;
            _selectClicked = UIHitTest.Click(btn1.GetComponent<Button>(), out whySel);
            if (!_selectClicked) Fail("Card1「选择」按钮点不到：" + whySel);
            yield return null;

            // 4) 内存
            _selMem = CharacterManager.SelectedId == 1;
            if (!_selMem) Fail("点击后内存角色未变：" + CharacterManager.SelectedId);

            // 5) 卡片标记：点完之后面板会整体重建（Build），必须重新取节点
            var panel2 = CharacterSelectPanelNode();
            _selCard = CardMarked(panel2, 1) && !CardMarked(panel2, 0);
            if (!_selCard) Fail("卡片选中标记未刷新（「已选择」标记还停在旧角色）");

            // 6) 主菜单角色名（玩家唯一能直接看见的反馈）——面板挡在菜单前面，先隐掉再看
            if (CharacterSelectPanel.Instance != null) CharacterSelectPanel.Instance.Hide();
            MainMenu.Instance?.RefreshRecords();
            yield return null;
            var menuTxt = CurrentCharacterText();
            string want = Localization.T("menu.character") + "：" + Localization.T(CharacterManager.ById(1).nameKey);
            _selMenu = menuTxt == want;
            if (!_selMenu) Fail("主菜单角色名未跟进：文字=" + menuTxt + " 期望=" + want);

            // 7) 落盘：换一个**全新实例**把文件读回来（同进程查 Current 只能证明写了内存）
            _selDisk = false;
            try
            {
                var rec = JsonUtility.FromJson<SaveManager.Record>(File.ReadAllText(SavePath));
                _selDisk = rec != null && rec.lastCharacterId == 1;
            }
            catch (Exception e)
            {
                Fail("读档失败：" + e.Message);
            }
            if (!_selDisk) Fail("选择未落盘：save.json 里 lastCharacterId 不是 1");
            _selDetail = "id=" + CharacterManager.SelectedId + " 卡片=" + Yn(_selCard) +
                         " 菜单=" + Yn(_selMenu) + " 落盘=" + Yn(_selDisk);

            // 8) 选角不应该记一局战绩（characterRuns 只在 SubmitRun 里加）
            int runsAfter = RunsFor(1, save);
            if (runsAfter != _selRunsBefore)
                Fail("选角误记战绩：characterRuns[1] " + _selRunsBefore + "→" + runsAfter);

            // 9) 关闭按钮必须真的把面板藏起来（否则玩家点完角色没有出口）
            if (CharacterSelectPanel.Instance != null) CharacterSelectPanel.Instance.Show();
            yield return null;
            var panel3 = CharacterSelectPanelNode();
            var close = panel3 != null ? panel3.Find("CloseBtn") : null;
            if (close == null) Fail("找不到关闭按钮");
            else
            {
                string whyClose;
                if (!UIHitTest.Click(close.GetComponent<Button>(), out whyClose))
                    Fail("关闭按钮点不到：" + whyClose);
                yield return null;
                var p4 = CharacterSelectPanelNode();
                _selClose = p4 == null || !p4.gameObject.activeInHierarchy;
                if (!_selClose) Fail("点关闭后面板仍可见");
            }
        }

        // =====================================================================================
        //  MENU：主菜单上每一颗玩家真的会去按的按钮，逐个用射线点一条出来
        //
        //  为什么要单开一个场景：M51 铺真实点击时，铺的是"探针原本就在点"的那几处——
        //  选卡、关闭、云存档开关。主菜单这几颗（开始/图鉴/设置/角色/退出）是另一回事：
        //  它们是玩家按下次数最多的东西，却因为没有任何探针去按，一直没人替它们说话。
        //  M51 已经亲眼见过"两块画布同层 → 谁压谁听 Unity 内部排序"能害成什么样，
        //  所以这里一颗一颗单独报明细，不合成一个总数——合成总数就看不见是哪一颗坏了。
        // =====================================================================================

        private IEnumerator Menu()
        {
            var root = MenuRoot();
            if (root == null) { Fail("主菜单未建出"); yield break; }

            // 1) 主菜单五颗：只做命中测试，不做点击。
            //    退出那颗点了进程就没了；开始游戏留到最后一步单独点。
            string[] mains = { "StartBtn", "CharacterBtn", "CodexBtn", "SettingsBtn", "QuitBtn" };
            var bad = new List<string>();
            foreach (var n in mains)
            {
                var b = BtnIn(root, n);
                _menuBtnTotal++;
                if (b == null) { bad.Add(n + "(按钮缺失)"); continue; }
                var h = UIHitTest.Probe(b);
                if (h.Reachable) _menuBtnOk++;
                else bad.Add(n + "：" + UIHitTest.Describe(h));
            }
            _menuBtnBad = bad.Count == 0 ? "<无>" : string.Join(" | ", bad.ToArray());
            yield return null;

            // 2) 图鉴：真点进去 → 面板里每颗按钮都测一遍 → 真点「关闭」退出
            yield return StepIntoPanel(root, "CodexBtn", "CodexCanvas");
            _codexBuilt = _panel.Built; _codexTotal = _panel.Total;
            _codexOk = _panel.Ok; _codexBad = _panel.Bad; _codexClosed = _panel.Closed;
            yield return null;

            // 3) 设置：同上。导出诊断日志那颗只测不点——
            //    它要写文件、要看产物，都不是本场景问的事，SettingsProbe 另有覆盖。
            yield return StepIntoPanel(root, "SettingsBtn", "SettingsCanvas");
            _settBuilt = _panel.Built; _settTotal = _panel.Total;
            _settOk = _panel.Ok; _settBad = _panel.Bad; _settClosed = _panel.Closed;
            yield return null;

            // 4) 最后才点「开始游戏」——按下去世界就换了，
            //    放最后是为了前面几步都还站在主菜单上。
            var startBtn = BtnIn(root, "StartBtn");
            string whyStart = "";
            if (startBtn != null && UIHitTest.Click(startBtn, out whyStart))
            {
                _startClicked = true;
                yield return new WaitForSeconds(0.3f);
                _startWorld = GameObject.Find("HUDCanvas") != null;
                if (!_startWorld) Fail("点了「开始游戏」但世界没建出来（HUDCanvas 缺失）");
            }
            else Fail("主菜单「开始游戏」点不到：" + (startBtn == null ? "按钮缺失" : whyStart));
        }

        /// <summary>
        /// 从主菜单真点一颗按钮进面板 → 把面板里每颗 Button 都命中测试一遍 → 真点「关闭」退出。
        /// 三个面共用一条路径：图鉴、设置、选角走的是同一套进出规矩，
        /// 分开写三遍的结果一定是某一遍慢慢漏掉其中一步。
        /// </summary>
        private PanelResult _panel;

        /// <summary>
        /// 从主菜单真点一颗按钮进面板 → 把面板里每颗 Button 都命中测试一遍 → 真点「关闭」退出。
        /// 三个面共用一条路径：图鉴、设置、选角走的是同一套进出规矩，
        /// 抄三遍的结果一定是某一遍慢慢漏掉其中一步。
        /// 结果落在 <see cref="_panel"/> 上——迭代器不能带 ref/out 参数（CS1623）。
        /// </summary>
        private IEnumerator StepIntoPanel(Transform menuRoot, string btnName, string canvasName)
        {
            _panel = new PanelResult();

            var open = BtnIn(menuRoot, btnName);
            string whyOpen = "";
            if (open == null || !UIHitTest.Click(open, out whyOpen))
            {
                Fail("主菜单「" + btnName + "」点不到：" + (open == null ? "按钮缺失" : whyOpen));
                yield break;
            }
            yield return null;

            var cv = GameObject.Find(canvasName);
            if (cv == null) { Fail("点了「" + btnName + "」但面板没建出"); yield break; }
            _panel.Built = true;

            int t, k;
            _panel.Bad = ProbeTree(cv.transform, out t, out k);
            _panel.Total = t; _panel.Ok = k;

            var closeNode = cv.transform.Find("Panel/CloseBtn");
            string whyClose = "";
            if (closeNode == null || !UIHitTest.Click(closeNode.GetComponent<Button>(), out whyClose))
            {
                Fail(canvasName + "「关闭」点不到：" + (closeNode == null ? "按钮缺失" : whyClose));
                yield break;
            }
            yield return null;

            // Hide() 是把画布 SetActive(false)：对象还在，只是不可见
            var still = GameObject.Find(canvasName);
            if (still != null && still.activeInHierarchy)
                Fail(canvasName + "点了关闭仍然可见");
            else _panel.Closed = true;
        }

        private struct PanelResult
        {
            public bool Built;      // 面板建出来了
            public int Total;       // 面板里 Button 总数
            public int Ok;          // 其中 raycast 点得到的颗数
            public string Bad;      // 点不到的明细
            public bool Closed;     // 「关闭」点了真的收了
        }

        /// <summary>取某个子节点上的 Button（不存在返回 null，不抛）。</summary>
        private static Button BtnIn(Transform root, string child)
        {
            if (root == null) return null;
            var t = root.Find(child);
            return t != null ? t.GetComponent<Button>() : null;
        }

        /// <summary>
        /// 把一棵子树里所有 Button 逐个做命中测试，返回不可达明细。
        /// 逐颗过而不是只看面板根：<c>GetComponentsInChildren</c> 会把叶子里的每一颗都翻出来，
        /// 而点不到的那颗很可能正是唯一能关掉这个面板的那颗。
        /// </summary>
        private string ProbeTree(Transform root, out int total, out int ok)
        {
            total = 0; ok = 0;
            var bad = new List<string>();
            foreach (var b in root.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                total++;
                var h = UIHitTest.Probe(b);
                if (h.Reachable) ok++;
                else bad.Add(b.name + "：" + UIHitTest.Describe(h));
            }
            return bad.Count == 0 ? "<无>" : string.Join(" | ", bad.ToArray());
        }

        private static Transform MenuRoot()
        {
            var m = MainMenu.Instance;
            if (m == null) return null;
            var go = GameObject.Find("MainMenuCanvas");
            return go != null ? go.transform : null;
        }

        private static Transform CharacterSelectPanelNode()
        {
            var go = GameObject.Find("CharacterSelectCanvas");
            return go != null ? go.transform.Find("Panel") : null;
        }

        /// <summary>卡片上是否显示「◉ 名称」+「已选择」（而不是「○ 名称」+「选择」）。</summary>
        private static bool CardMarked(Transform panel, int id)
        {
            if (panel == null) return false;
            var card = panel.Find("Card" + id);
            if (card == null) return false;
            var name = card.Find("Name");
            var nameTxt = name != null ? name.GetComponent<Text>() : null;
            var btn = card.Find("SelectBtn");
            var btnTxt = btn != null ? btn.GetComponentInChildren<Text>(true) : null;
            return nameTxt != null && btnTxt != null &&
                   nameTxt.text.StartsWith("◉") &&
                   btnTxt.text == Localization.T("char.selected");
        }

        private static string CurrentCharacterText()
        {
            var root = MenuRoot();
            if (root == null) return "";
            var t = root.Find("CurrentCharacter");
            var txt = t != null ? t.GetComponent<Text>() : null;
            return txt != null ? txt.text : "";
        }

        private static int RunsFor(int id, SaveManager save)
        {
            var arr = save.Data.characterRuns;
            return arr != null && id < arr.Length ? arr[id] : 0;
        }

        // =====================================================================================
        //  APPLY：用存档里的角色真实开局，7 个乘区 + 起始武器 + 幂等 + 换角色不叠乘
        // =====================================================================================

        private IEnumerator Apply()
        {
            // 1) 存档里的角色是上一个进程（SELECT）点出来的 1——**跨进程**从文件读回，
            //    这才叫"重启后还记得"；同进程内读内存证明不了任何事。
            int fromDisk = ReadSelectedFromDisk();
            if (fromDisk != 1)
            {
                Fail("跨进程读回的角色不是 1（实际 " + fromDisk + "）——选择没落盘或没被读回");
                yield break;
            }

            // 2) 真实开局（与玩家点「开始游戏」同一条路径），但让波次保持静止——
            //    否则趁我们断言数值的这几帧，敌人已经把玩家撞掉血、把角色数值搅乱。
            GameBuilder.Build();
            var em = EnemyManager.Instance;
            if (em != null) em.FreezeWaves = true;
            yield return null;
            yield return null;

            var p = PlayerController.Instance;
            if (p == null) { Fail("开局后没有本地玩家"); yield break; }

            var def = CharacterManager.ById(1);
            var st = p.stats;

            // 3) 七个乘区逐个比对（PlayerStats 基准值 × CharacterDef 乘区）
            var diffs = new List<string>();
            _applyChecked = 7;
            Compare(diffs, "生命上限", 110f * def.hpMult, st.maxHp);
            Compare(diffs, "移动速度", 5.6f * def.speedMult, st.moveSpeed);
            Compare(diffs, "伤害倍率", 1f * def.damageMult, st.attackDamageMult);
            Compare(diffs, "冷却倍率", 1f * def.cooldownMult, st.cooldownMult);
            Compare(diffs, "拾取半径", 1.6f * def.pickupMult, st.pickupRadius);
            Compare(diffs, "范围倍率", 1f * def.areaMult, st.areaMult);
            Compare(diffs, "经验倍率", 1f * def.xpMult, st.xpGainMult);
            _applyMatched = _applyChecked - diffs.Count;
            _applyDiff = diffs.Count == 0 ? "无" : string.Join(" ", diffs.ToArray());
            foreach (var d in diffs) Fail(d);

            // 4) 起始武器被替换（默认是星能弹 id=1，守卫应是裂地波 id=4），且没有残留旧武器
            _applyWeaponId = p.Weapons.Count > 0 ? p.Weapons[0].def.id : -1;
            _applyWeapon = p.Weapons.Count == 1 && _applyWeaponId == def.startingWeaponId;
            if (!_applyWeapon)
                Fail("起始武器未被替换：武器数=" + p.Weapons.Count + " id=" + _applyWeaponId +
                     " 期望 id=" + def.startingWeaponId);

            // 5) 幂等：同一个角色再 Apply 一次，数值不得再乘一遍
            float hpBefore = st.maxHp, spdBefore = st.moveSpeed;
            CharacterManager.ApplyTo(p, def);
            _applyIdempotent = Mathf.Abs(st.maxHp - hpBefore) < 0.0001f &&
                               Mathf.Abs(st.moveSpeed - spdBefore) < 0.0001f &&
                               p.Weapons.Count == 1;
            if (!_applyIdempotent)
                Fail("重复 Apply 不幂等：HP " + hpBefore + "→" + st.maxHp + " 武器数=" + p.Weapons.Count);

            // 6) 换角色：数值必须整体换成新角色的（而不是在原数值上再乘）。
            //    这条最容易错——`stats.maxHp *= def.hpMult` 的写法一旦用在换角色上，
            //    守卫 1.35 倍再切游侠会变成 1.35×1.0（看着没错），而切回去就成 1.35×1.35。
            var ranger = CharacterManager.ById(0);
            CharacterManager.ApplyTo(p, ranger);
            var swapDiffs = new List<string>();
            Compare(swapDiffs, "换角色后生命上限", 110f * ranger.hpMult, st.maxHp);
            Compare(swapDiffs, "换角色后移动速度", 5.6f * ranger.speedMult, st.moveSpeed);
            Compare(swapDiffs, "换角色后伤害倍率", 1f * ranger.damageMult, st.attackDamageMult);
            Compare(swapDiffs, "换角色后起始武器", ranger.startingWeaponId,
                p.Weapons.Count > 0 ? p.Weapons[0].def.id : -1);
            _applySwapClean = swapDiffs.Count == 0;
            _applySwapDiff = swapDiffs.Count == 0 ? "无" : string.Join(" ", swapDiffs.ToArray());

            // 收尾：把角色还原成存档里的守卫，避免同机后续用例看到游侠的数值
            CharacterManager.SelectedId = 1;
            Time.timeScale = 1f;
            yield return null;
        }

        private static void Compare(List<string> diffs, string label, float expect, float got)
        {
            if (Mathf.Abs(expect - got) <= 0.0001f) return;
            diffs.Add(label + " 实际 " + got.ToString("0.####") + " 期望 " + expect.ToString("0.####"));
        }

        private static int ReadSelectedFromDisk()
        {
            try
            {
                if (!File.Exists(SavePath)) return -1;
                var rec = JsonUtility.FromJson<SaveManager.Record>(File.ReadAllText(SavePath));
                return rec != null ? rec.lastCharacterId : -1;
            }
            catch
            {
                return -1;
            }
        }

        // =====================================================================================
        //  PROGRESSION：每把武器开火 / 4 条组合技 / 7 项属性强化 / 武器等级封顶
        // =====================================================================================

        private IEnumerator Progression()
        {
            GameBuilder.Build();
            var em = EnemyManager.Instance;
            // 波次静止：我们要自己摆靶子并数伤害，不能有额外的敌人挤进 ActiveEnemies
            if (em != null) em.FreezeWaves = true;
            yield return null;
            yield return null;

            var p = PlayerController.Instance;
            if (p == null) { Fail("开局后没有本地玩家"); yield break; }
            if (em == null) { Fail("没有 EnemyManager，无法生成靶子"); yield break; }

            // 玩家站原点
            p.transform.position = Vector3.zero;
            // 开局那一帧波次已经抢跑刷出若干敌人（GameBuilder.Build → StartWaves → NextWave 是同步的，
            // 我们的 FreezeWaves 只能拦住之后的帧）。不清掉的话它们会抢走"最近目标"，
            // 链闪电/月刃这类**单体索敌**武器就会打在远处的杂兵身上，靶子一滴血不掉——
            // 本轮就因此误判"雷霆之链零伤害"（实际是打到了别人）。
            ClearWaveEnemies(em);

            // —— 1) 十把武器逐个开火：伤害必须真的落在目标身上 ——
            // 每把武器清空武器槽后单独装，避免"上一把武器顺手打死了靶子"造成假通过。
            foreach (int id in UpgradeCatalog.AllWeaponIds)
            {
                ClearWeapons(p);
                var def = UpgradeCatalog.CreateById(id);
                p.AddSkill(def);
                yield return null;

                var target = SpawnTarget(em, TargetOffsetFor(id));
                if (target == null) { Fail("靶子生成失败（武器" + id + "）"); break; }
                yield return null;

                var dmg = target.damageable;
                float hp0 = dmg != null ? dmg.currentHp : 0f;
                // 等到至少完成一次结算：瞬时型走冷却（最长 2.6s 的裂地波 + 射程飞行），
                // 持续型（光环/环绕）走 hitTimer（0.3~0.5s）。
                yield return new WaitForSeconds(3.4f);

                bool visual = HasCombatVisual();
                // 靶子可能已被打回对象池（死亡）——那就是打出了全部血量，算满额伤害
                float dealt = (dmg == null || dmg.isDead) ? hp0 : hp0 - dmg.currentHp;

                _weaponsFired++;
                if (dealt > 0.01f)
                {
                    _weaponsDamaged++;
                    _fireOk.Add(id + "=" + dealt.ToString("0"));
                }
                else
                {
                    _fireFail.Add(def.DisplayName + "(id" + id + ")" +
                                  (visual ? " 有视觉但零伤害" : " 零伤害且无视觉"));
                }
                if (target != null) target.gameObject.SetActive(false);   // 还回池（模板在 Enemy 内部持有）
                em.RemoveEnemy(target);                                 // 同时移出索敌列表，别让下一把武器打到它
                // 等一帧让禁用生效：链闪电/月刃按"最近目标"索敌，残留敌人会把首发抢走
                yield return null;
                yield return null;
            }

            // —— 2) 4 条组合技逐条断言（必须"只有它激活"）——
            ClearWeapons(p);
            yield return null;
            CheckCombo(p, new[] { 1, 2 }, "fire_storm", 1.5f, 0, false, "火焰+风暴 伤害×1.5");
            CheckCombo(p, new[] { 3, 10 }, "ice_earth", 1f, 0, true, "寒冰+大地 命中减速");
            CheckCombo(p, new[] { 5, 1 }, "lightning_fire", 1f, 2, false, "雷+火 穿透+2");
            CheckCombo(p, new[] { 9, 1 }, "holy_fire", 1.4f, 0, false, "圣光+火 伤害×1.4");
            _combosOk = _combosChecked - _comboFail.Count;
            foreach (var f in _comboFail) Fail(f);

            // 组合技叠加现状（不是判据，是把行为钉成可观测事实，供设计侧决定要不要改）
            ClearWeapons(p);
            p.AddSkill(UpgradeCatalog.CreateById(1));    // fire
            p.AddSkill(UpgradeCatalog.CreateById(2));    // storm
            p.AddSkill(UpgradeCatalog.CreateById(9));    // holy
            yield return null;
            var stack = ComboManager.Compute(TagsOf(p));
            _comboDetail = "火+风暴+圣光 伤害倍率=" + stack.damageMult.ToString("0.##") +
                           "（相加应 1.9 / 只认最后一条应 1.4）";

            // —— 3) 7 项属性强化逐项断言 ——
            ClearWeapons(p);
            p.AddSkill(UpgradeCatalog.CreateById(1));
            yield return null;
            CheckStats(p);

            // —— 4) 武器等级封顶 + 满级后不再出现在供选里 ——
            ClearWeapons(p);
            p.AddSkill(UpgradeCatalog.CreateById(1));
            for (int i = 0; i < SkillDef.MaxLevel + 3; i++) p.LevelUpWeapon(1);
            var w = p.Weapons.Count > 0 ? p.Weapons[0] : null;
            _capOk = w != null && w.level == SkillDef.MaxLevel;
            if (!_capOk) Fail("武器等级未封顶：" + (w != null ? w.level.ToString() : "无武器") +
                              " 期望 " + SkillDef.MaxLevel);

            // 供选池有 9 把未解锁武器 + 0 条等级强化 + 7 项属性强化 = 16 张，只取前 3 张的话
            // 「有没有漏出满级卡」会被洗牌概率淹掉（取样太小 = 判据没区分力）。
            // 故取**整个池子**（远大于池容量）逐张检查，任一满级卡出现即失败。
            var offers = UpgradeCatalog.BuildOffers(64, p);
            bool hasMaxed = false;
            string maxedTitle = "";
            foreach (var o in offers)
                if (o.kind == 200 + 1) { hasMaxed = true; maxedTitle = o.title; }
            _noMaxedOffer = !hasMaxed && offers.Count > 0;
            if (hasMaxed) Fail("满级武器仍出现在升级供选里[" + maxedTitle + "]（玩家会选到一张无效卡）");
            if (offers.Count == 0) Fail("升级供选池为空（玩家升级时无卡可选）");
            Debug.Log("[Probe] 供选池大小 " + offers.Count + "（MaxLevel=" + SkillDef.MaxLevel + "）");

            Time.timeScale = 1f;
        }

        private void CheckCombo(PlayerController p, int[] weaponIds, string key,
            float expectDmg, int expectPierce, bool expectSlow, string label)
        {
            ClearWeapons(p);
            foreach (int id in weaponIds) p.AddSkill(UpgradeCatalog.CreateById(id));
            var tags = TagsOf(p);
            var b = ComboManager.Compute(tags);
            var keys = ComboManager.ActiveComboKeys(tags);

            bool only = keys.Count == 1 && keys[0] == key;
            bool dmg = Mathf.Abs(b.damageMult - expectDmg) < 0.0001f;
            bool pierce = b.pierce == expectPierce;
            bool slow = b.slowOnHit == expectSlow;
            _combosChecked++;
            if (only && dmg && pierce && slow) return;

            _comboFail.Add(label + " 未达标：激活=[" + string.Join(",", keys.ToArray()) + "]" +
                           " 伤害=" + b.damageMult.ToString("0.##") + "/" + expectDmg.ToString("0.##") +
                           " 穿透=" + b.pierce + "/" + expectPierce +
                           " 减速=" + Yn(b.slowOnHit) + "/" + Yn(expectSlow));
        }

        private void CheckStats(PlayerController p)
        {
            int[] indices = { 0, 1, 2, 3, 4, 5, 6 };
            string[] labels = { "最大生命+20", "移速+8%", "伤害+15%", "冷却-12%", "弹体+1", "拾取+30%", "范围+15%" };
            for (int k = 0; k < indices.Length; k++)
            {
                int idx = indices[k];
                float hp0 = p.stats.maxHp, spd0 = p.stats.moveSpeed, dmg0 = p.stats.attackDamageMult,
                      cd0 = p.stats.cooldownMult, pick0 = p.stats.pickupRadius, area0 = p.stats.areaMult;
                int proj0 = p.stats.projectileBonus;

                UpgradeCatalog.ApplyStat(idx, p);
                _statsChecked++;

                bool ok;
                switch (idx)
                {
                    case 0:
                        ok = Mathf.Abs(p.stats.maxHp - (hp0 + 20f)) < 0.0001f &&
                             p.damageable != null &&
                             Mathf.Abs(p.damageable.maxHp - p.stats.maxHp) < 0.0001f &&
                             Mathf.Abs(p.damageable.currentHp - p.damageable.maxHp) < 0.0001f;
                        break;
                    case 1: ok = Mathf.Abs(p.stats.moveSpeed - spd0 * 1.08f) < 0.0001f; break;
                    case 2: ok = Mathf.Abs(p.stats.attackDamageMult - dmg0 * 1.15f) < 0.0001f; break;
                    case 3: ok = Mathf.Abs(p.stats.cooldownMult - cd0 * 0.88f) < 0.0001f; break;
                    case 4: ok = p.stats.projectileBonus == proj0 + 1; break;
                    case 5: ok = Mathf.Abs(p.stats.pickupRadius - pick0 * 1.3f) < 0.0001f; break;
                    default: ok = Mathf.Abs(p.stats.areaMult - area0 * 1.15f) < 0.0001f; break;
                }
                if (ok) { _statsOk++; continue; }

                _statFail.Add(labels[k] + " 未生效：血量 " + hp0.ToString("0.#") + "→" + p.stats.maxHp.ToString("0.#") +
                              " 移速 " + spd0.ToString("0.##") + "→" + p.stats.moveSpeed.ToString("0.##") +
                              " 伤害 " + dmg0.ToString("0.##") + "→" + p.stats.attackDamageMult.ToString("0.##") +
                              " 冷却 " + cd0.ToString("0.##") + "→" + p.stats.cooldownMult.ToString("0.##") +
                              " 弹体 " + proj0 + "→" + p.stats.projectileBonus +
                              " 拾取 " + pick0.ToString("0.##") + "→" + p.stats.pickupRadius.ToString("0.##") +
                              " 范围 " + area0.ToString("0.##") + "→" + p.stats.areaMult.ToString("0.##"));
            }
            foreach (var f in _statFail) Fail(f);
        }

        // —— 辅助 ——

        /// <summary>清空武器槽（PlayerController 没有公开的清空入口，探针直接操作列表再重算组合）。</summary>
        private static void ClearWeapons(PlayerController p)
        {
            p.Weapons.Clear();
            ComboManager.Instance?.Recompute(TagsOf(p));
        }

        /// <summary>
        /// 每把武器各自的靶子距离（都在其有效射程内，但落点不同）：
        /// - 雷环刃（环绕）只在 orbitRadius ± 0.6 的圆环上伤害，3.0 太远、0 太近 → 取 2.0；
        /// - 脉冲地雷埋在玩家脚下，触发半径 1.6，靶子必须踩在雷上 → 取 0.5；
        /// - 其余武器射程 7~9，取 3.0（足够远，避免爆发型的球形范围与瞬发伤害互相干扰）。
        /// 距离写错会让判据变成"武器坏了"，本轮三种武器各踩了一次。
        /// </summary>
        private static Vector2 TargetOffsetFor(int weaponId)
        {
            switch (weaponId)
            {
                case 5: return new Vector2(2.0f, 0f);    // 雷环刃：环绕半径 2.0
                case 10: return new Vector2(0.5f, 0f);   // 脉冲地雷：需踩进 1.6 触发半径
                default: return new Vector2(3.0f, 0f);
            }
        }

        /// <summary>清掉波次抢跑刷进来的敌人（只清普通怪；Boss 波不会在第 1 波出现）。</summary>
        private static void ClearWaveEnemies(EnemyManager em)
        {
            var list = new List<Enemy.Enemy>(em.ActiveEnemies);
            foreach (var e in list)
            {
                if (e == null) continue;
                em.RemoveEnemy(e);
                e.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 生成一个靶子敌人：走 EnemyManager.SpawnAdd（对象池 + ActiveEnemies 登记 + CombatTargets 可索敌），
        /// 血量给得极高，保证一轮开火打不死——血量差即这一轮打出的伤害。
        /// </summary>
        private static Enemy.Enemy SpawnTarget(EnemyManager em, Vector2 pos)
        {
            // 用「列表长度前的引用」定位新元素，**不要**用 GetInstanceID 做前后差集：
            // Unity 在 SetActive(true) 之后并不保证当帧就分配 instanceID（实测返回 0），
            // 于是新旧两个敌人的 id 都是 0，差集判定会把"已存在的老敌人"当成本次生成的靶子——
            // 本轮就这样让每把武器都打到了上一轮残留的敌人身上，错误地显示"雷霆之链零伤害"。
            var list = em.ActiveEnemies;
            int n0 = list.Count;
            em.SpawnAdd(pos, EnemyKind.Tank, 500000f, 0f);   // 速度 0：靶子不追人，位置稳定
            if (list.Count <= n0) return null;
            var target = list[list.Count - 1];
            if (target != null) target.transform.position = pos;
            return target;
        }

        private static List<string> TagsOf(PlayerController p)
        {
            var tags = new List<string>();
            foreach (var w in p.Weapons)
                if (w != null && w.def != null && w.def.tags != null) tags.AddRange(w.def.tags);
            return tags;
        }

        private static bool HasCombatVisual()
        {
            foreach (var n in CombatVisuals)
                if (GameObject.Find(n) != null) return true;
            return false;
        }

        // =====================================================================================

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "SELECT")
                pass = pass && _panelBuilt && _selectClicked && _selMem && _selCard && _selMenu &&
                       _selDisk && _selClose;
            if (_scene == "APPLY")
                pass = pass && _applyMatched == _applyChecked && _applyWeapon && _applyIdempotent &&
                       _applySwapClean;
            if (_scene == "PROGRESSION")
                pass = pass && _weaponsDamaged == _weaponsFired && _combosOk == _combosChecked &&
                       _statsOk == _statsChecked && _capOk && _noMaxedOffer;
            if (_scene == "MENU")
                pass = pass && _menuBtnOk == _menuBtnTotal && _codexBuilt && _codexOk == _codexTotal &&
                       _codexClosed && _settBuilt && _settOk == _settTotal && _settClosed &&
                       _startClicked && _startWorld;

            Debug.Log("[SMOKE] ===== 选角/成长链路实测结果 =====");
            Debug.Log("[SMOKE] 场景              " + _scene);
            switch (_scene)
            {
                case "APPLY":
                    Debug.Log("[SMOKE] 乘区匹配          " + _applyMatched + "/" + _applyChecked);
                    Debug.Log("[SMOKE] 乘区差异          " + _applyDiff);
                    Debug.Log("[SMOKE] 起始武器已替换    " + Yn(_applyWeapon) + "（id=" + _applyWeaponId + "）");
                    Debug.Log("[SMOKE] 重复应用幂等      " + Yn(_applyIdempotent));
                    Debug.Log("[SMOKE] 换角色不叠乘      " + Yn(_applySwapClean));
                    Debug.Log("[SMOKE] 换角色差异        " + _applySwapDiff);
                    break;
                case "MENU":
                    Debug.Log("[SMOKE] 主菜单按钮可达    " + _menuBtnOk + "/" + _menuBtnTotal);
                    Debug.Log("[SMOKE] 不可达明细        " + _menuBtnBad);
                    Debug.Log("[SMOKE] 图鉴面板已建出    " + Yn(_codexBuilt));
                    Debug.Log("[SMOKE] 图鉴按钮可达      " + _codexOk + "/" + _codexTotal);
                    Debug.Log("[SMOKE] 图鉴不可达明细    " + _codexBad);
                    Debug.Log("[SMOKE] 图鉴关闭已收      " + Yn(_codexClosed));
                    Debug.Log("[SMOKE] 设置面板已建出    " + Yn(_settBuilt));
                    Debug.Log("[SMOKE] 设置按钮可达      " + _settOk + "/" + _settTotal);
                    Debug.Log("[SMOKE] 设置不可达明细    " + _settBad);
                    Debug.Log("[SMOKE] 设置关闭已收      " + Yn(_settClosed));
                    Debug.Log("[SMOKE] 开始游戏已点击    " + Yn(_startClicked));
                    Debug.Log("[SMOKE] 开局世界已建出    " + Yn(_startWorld));
                    break;
                case "PROGRESSION":
                    Debug.Log("[SMOKE] 武器开火覆盖      " + _weaponsFired + "/10");
                    Debug.Log("[SMOKE] 打出伤害          " + _weaponsDamaged + "/" + _weaponsFired);
                    Debug.Log("[SMOKE] 伤害明细          " + (_fireOk.Count == 0 ? "<无>" : string.Join(" ", _fireOk.ToArray())));
                    Debug.Log("[SMOKE] 开火失败项        " + (_fireFail.Count == 0 ? "无" : string.Join(" | ", _fireFail.ToArray())));
                    Debug.Log("[SMOKE] 组合技达标        " + _combosOk + "/" + _combosChecked);
                    Debug.Log("[SMOKE] 组合技叠加        " + _comboDetail);
                    Debug.Log("[SMOKE] 属性强化达标      " + _statsOk + "/" + _statsChecked);
                    Debug.Log("[SMOKE] 等级封顶          " + Yn(_capOk));
                    Debug.Log("[SMOKE] 满级不再供选      " + Yn(_noMaxedOffer));
                    break;
                default:
                    Debug.Log("[SMOKE] 面板已建出        " + Yn(_panelBuilt));
                    Debug.Log("[SMOKE] 已点击选择        " + Yn(_selectClicked));
                    Debug.Log("[SMOKE] 选中态四处一致    " + _selDetail);
                    Debug.Log("[SMOKE] 关闭按钮有效      " + Yn(_selClose));
                    Debug.Log("[SMOKE] 选中角色          " + CharacterManager.SelectedId);
                    break;
            }
            Debug.Log("[SMOKE] 失败项            " + (_fail.Count == 0 ? "无" : string.Join(" | ", _fail.ToArray())));
            Debug.Log("[SMOKE] 运行期异常        " + _errors);
            if (_errors > 0) Debug.Log("[SMOKE] 首条错误          " + _firstError);
            Debug.Log("[SMOKE] 结论              " + (pass ? "通过" : "不通过"));
        }

        private static string Yn(bool b) => b ? "是" : "否";

        private void Fail(string msg)
        {
            if (_fail.Contains(msg)) return;
            _fail.Add(msg);
            Debug.LogWarning("[Probe] " + msg);
        }

        void Update()
        {
            // 兜底：协程意外没跑完也要出结论（"没结果"不能当通过）
            if (!_done && Time.timeSinceLevelLoad > 150f)
            {
                _done = true;
                Debug.Log("[SMOKE] 结论              不通过（超时未产出结果）");
                Application.Quit(1);
            }
        }
    }
}
