using System;
using System.Collections;
using System.Collections.Generic;
using StarPixel.Combat;
using StarPixel.Enemy;
using StarPixel.Pickups;
using StarPixel.Core;
using StarPixel.Player;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M24「掉落 → 磁吸 → 入账」与「重开局清理」两条链路无头实测。
    ///
    /// 为什么必须测：
    ///   1) 掉落闭环（宝石被吸过来 → 经验入账 → 升级）此前**只在"跑一局看等级涨了没"里间接发生过**，
    ///      从没断言过"半径之外的宝石必须吸不动 / 半径之内必须被吸走"。
    ///      于是"忽略 pickupRadius、全局吸附"这种实现也能全绿——等级照样涨。
    ///   2) 经验倍率 xpGainMult 此前只在 M23 的 APPLY 里**比对字段值**，没验证过 AddXp 真的乘了它。
    ///   3) 回血只被"heal 过"，没人断言"超过上限必须截断"。
    ///   4) **重开局清理**：TeardownWorld 只销毁 Track 进去的管理器，而宝石/回血球/敌人
    ///      都是 PoolManager 从模板克隆出来的实例，挂场景根节点、不在 _created 里 → 整批残留到新世界。
    ///      PickupManager.ResetAll / EnemyManager.ResetAll 写好了却全仓库无 caller。
    ///   5) **重开局拿不到新玩家**：PlayerController.Instance 是静态字段且从不清理。
    ///      TeardownWorld 的 Destroy 延迟到帧末，而 OnRestart 是 TeardownWorld(); Build(); 紧挨着两行——
    ///      新玩家在同一个 Awake 里看到 Instance 还指着活得好好儿的旧玩家，于是没有注册上去；
    ///      帧末旧玩家一销毁，Instance 成了悬空引用，重开后整个玩法层静默空转。
    ///
    /// 五个场景：
    ///   MAGNET   —— 半径之外必须吸不动（距离不变、经验不入账）；大半径必须吸走且 3 点经验精确入账。
    ///   CREDIT   —— xpGainMult=2 必须逐颗生效；回血精确 +30；超额回血封顶在 maxHp。
    ///   LEFTOVER —— 真实重开路径后，上一局的宝石/回血球/敌人必须清零。
    ///   RESTART  —— 真实重开路径后，必须拿得到**新世界**的玩家，且新世界掉��闭环仍然通。
    ///   REPEAT   —— 连续 3 次重开，残留不得累积。
    ///
    /// 判据的区分力：
    /// 1) 计数一律走 **Resources.FindObjectsOfTypeAll + 排除模板本体**，
    ///    不读 PickupManager/EnemyManager 的内部列表 —— 用被测对象自己的账本判自己等于没判。
    /// 2) 每个场景开场都要把"建筑期蹭出来的" stray 掉落物清干净，否则开波敌人掉的那几颗
    ///    会被当成本次 fixture，断言建立在脏初始态上（M21 DECLINE 假通过同类教训）。
    /// 3) 磁吸的"被拾取"判据绑定**那一颗具体宝石的引用**（滴加��后取回来持有），
    ///    不是"场上还有没有宝石"——后者会被无关掉落物骗过去（本轮第一版就被骗过一次）。
    /// 4) 半径用**两态对比**：小半径必须不动、大半径必须被吸走。
    ///    只测单边的话，"永远吸附"和"永远不吸附"各能骗过一半。
    /// 5) 经验用**精确的 XpRatio**（初始 _xpToNext=5）而不是"等级涨了"，入账数量不对时能立刻指出。
    /// 6) RESTART 除了"Instance 非空"，还要求 **InstanceID 与上一局不同**，
    ///    并要求新玩家真的能吸走一颗宝石——证明世界接上了，而不只是引用存在。
    /// </summary>
    public class PickupProbe : MonoBehaviour
    {
        private string _scene = "MAGNET";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— MAGNET ——
        private int _magChecked;
        private int _magOk;
        private bool _magFarHeld;
        private float _magFarDist;
        private bool _magNearPulled;
        private float _magRatio;
        private string _magDetail = "";

        // —— CREDIT ——
        private int _creditChecked;
        private int _creditOk;
        private string _creditDetail = "";

        // —— LEFTOVER ——
        private int _leftoverBefore;
        private int _leftoverAfter;
        private string _leftoverBreak = "";

        // —— RESTART ——
        private int _restartChecked;
        private int _restartOk;
        private bool _restartNewPlayer;
        private bool _restartAlive;
        private bool _restartLoop;
        private string _restartDetail = "";

        // —— REPEAT ——
        private int _repeatChecked;
        private int _repeatOk;
        private string _repeatDetail = "";

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        public static void Start(string scene)
        {
            // AddComponent 会同步执行 Awake（M21/M22/M23 同一条坑）：先停用 → 挂载赋值 → 再激活。
            var go = new GameObject("PickupProbe");
            go.SetActive(false);
            var p = go.AddComponent<PickupProbe>();
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
                case "CREDIT": yield return Credit(); break;
                case "LEFTOVER": yield return Leftover(); break;
                case "RESTART": yield return Restart(); break;
                case "REPEAT": yield return Repeat(); break;
                default: yield return Magnet(); break;
            }

            Report();
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }

        // =====================================================================================
        // 场景公共helper：建世界、清敌人、清掉落物、取引用
        // =====================================================================================

        /// <summary>建世界 → 冻波次 → 立刻清掉开场那一波敌人 → 清掉建筑期蹭出来的掉落物。</summary>
        private IEnumerator BuildCleanWorld()
        {
            GameBuilder.Build();
            var em = EnemyManager.Instance;
            if (em != null) em.FreezeWaves = true;
            ClearEnemiesNow();                 // 不等帧：不给玩家武器击杀 → 掉落的机会
            ClearPicksNow();
            yield return null;
            yield return null;
            ClearPicksNow();                   // 兜底：万一上一帧又蹭出来
            yield return null;
        }

        /// <summary>同步销毁场上全部敌人：不留帧的时间给玩家武器把敌人打死并掉东西。</summary>
        private static void ClearEnemiesNow()
        {
            var em = EnemyManager.Instance;
            if (em == null) return;
            var snapshot = new List<Enemy.Enemy>(em.ActiveEnemies);
            foreach (var e in snapshot)
            {
                if (e == null) continue;
                em.RemoveEnemy(e);
                Destroy(e.gameObject);
            }
            em.ActiveEnemies.Clear();
        }

        /// <summary>同步销毁场上全部掉落物实例（只销毁实体，模板本体保留给下一次对局用）。</summary>
        private static void ClearPicksNow()
        {
            foreach (var g in Resources.FindObjectsOfTypeAll<XpGem>())
                if (g != null && !IsTemplate(g)) Destroy(g.gameObject);
            foreach (var h in Resources.FindObjectsOfTypeAll<HealthPickup>())
                if (h != null && !IsTemplate(h)) Destroy(h.gameObject);
        }

        // =====================================================================================
        //  计数：不读管理器内部列表，直接扫场景
        // =====================================================================================

        // 模板本体（GemTemplate / HealthTemplate / EnemyTemplate…）自身也挂着同名组件，
        // 但它是"出厂模具"不是实体：不排除的话基线永远不为 0，断言会假失败。
        private static bool IsTemplate(Component c) =>
            c != null && c.gameObject.name.EndsWith("Template");

        private static int CountGems()
        {
            int n = 0;
            foreach (var g in Resources.FindObjectsOfTypeAll<XpGem>())
                if (g != null && !IsTemplate(g)) n++;
            return n;
        }

        private static int CountHealth()
        {
            int n = 0;
            foreach (var h in Resources.FindObjectsOfTypeAll<HealthPickup>())
                if (h != null && !IsTemplate(h)) n++;
            return n;
        }

        private static int CountEnemies()
        {
            int n = 0;
            foreach (var e in Resources.FindObjectsOfTypeAll<Enemy.Enemy>())
                if (e != null && !IsTemplate(e)) n++;
            return n;
        }

        private static int CountAll() => CountGems() + CountHealth() + CountEnemies();

        /// <summary>取场上唯一一颗（非模板）宝石作为本次 fixture 的持有引用。</summary>
        private static XpGem SoleGem()
        {
            XpGem found = null;
            int n = 0;
            foreach (var g in Resources.FindObjectsOfTypeAll<XpGem>())
            {
                if (g == null || IsTemplate(g)) continue;
                found = g; n++;
            }
            return n == 1 ? found : null;
        }

        private static XpGem NearestGemTo(Vector2 pos)
        {
            XpGem best = null;
            float bd = float.MaxValue;
            foreach (var g in Resources.FindObjectsOfTypeAll<XpGem>())
            {
                if (g == null || IsTemplate(g)) continue;
                float d = Vector2.Distance(g.transform.position, pos);
                if (d < bd) { bd = d; best = g; }
            }
            return best;
        }

        private static HealthPickup SoleHealth()
        {
            HealthPickup found = null;
            int n = 0;
            foreach (var h in Resources.FindObjectsOfTypeAll<HealthPickup>())
            {
                if (h == null || IsTemplate(h)) continue;
                found = h; n++;
            }
            return n == 1 ? found : null;
        }

        /// <summary>被拾取 == 归还对象池并停用。等这一颗变成非激活。</summary>
        private static IEnumerator WaitCollected(Component c, float timeout)
        {
            float t = 0f;
            while (t < timeout && c != null && c.gameObject.activeSelf)
            {
                yield return new WaitForSeconds(0.1f);
                t += 0.1f;
            }
            yield return null;
        }

        // =====================================================================================
        //  MAGNET：半径之外必须吸不动，改成大半径必须被吸走且经验精确入账
        // =====================================================================================

        private IEnumerator Magnet()
        {
            yield return BuildCleanWorld();
            var p = PlayerController.Instance;
            var pm = PickupManager.Instance;
            if (p == null || pm == null) { Fail("玩家或拾取管理器缺失"); yield break; }

            int baseline = CountAll();
            if (baseline != 0) Fail("干净世界基线不为 0：" + baseline);

            // 本场景测的是"磁吸半径是否决定能否拾取"，所以把经验倍率钉死成 1。
            // 不锁死的话期望值会随角色漂移：默认角色（游侠）自带 xpMult=1.15，
            // 3 点宝石实际到手 3.45 → ratio 0.69 而不是 0.6 —— 那是**产品正确**，不是缺陷
            //（本轮第一版就把这个当成 bug 查了一轮）。倍率本身在 CREDIT 场景单独断言。
            p.stats.xpGainMult = 1f;

            Vector2 at = p.transform.position;
            Vector2 drop = new Vector2(at.x + 6f, at.y);

            // —— ① 小半径（默认 1.6）：必须一动不动，也不得入账 ——
            p.stats.pickupRadius = 1.6f;
            pm.DropGem(drop, 3f);
            yield return new WaitForSeconds(1.2f);

            var gem = SoleGem();
            if (gem == null) { Fail("小半径下宝石凭空消失（期望留在原地）"); _magChecked = 2; yield break; }
            _magFarDist = Vector2.Distance(gem.transform.position, at);
            _magFarHeld = Mathf.Abs(_magFarDist - 6f) < 0.05f && p.XpRatio <= 0.0001f;
            _magChecked++;
            if (_magFarHeld) _magOk++;
            else Fail("半径外禁磁吸失败：距离=" + _magFarDist.ToString("F2") + " 经验比=" + p.XpRatio.ToString("F3"));

            // —— ② 大半径：必须被吸走进入对象池，且 3 点经验精确入账（3/5 = 0.6）——
            p.stats.pickupRadius = 12f;
            yield return WaitCollected(gem, 4f);

            bool gone = gem == null || !gem.gameObject.activeSelf;
            _magRatio = p.XpRatio;
            bool credited = Mathf.Abs(_magRatio - 0.6f) < 0.01f;
            _magNearPulled = gone && credited;
            _magChecked++;
            if (_magNearPulled) _magOk++;
            else Fail("半径内磁吸入账失败：已回收=" + Yn(gone) + " 经验比=" + _magRatio.ToString("F3") + "（期望 0.600）");

            _magDetail = "外6.00→" + _magFarDist.ToString("F2") + " / 已回收=" + Yn(gone) +
                         " / 经验比=" + _magRatio.ToString("F3");
        }

        // =====================================================================================
        //  CREDIT：经验倍率逐颗生效 / 回血入账正确且不溢出上限
        // =====================================================================================

        private IEnumerator Credit()
        {
            yield return BuildCleanWorld();
            var p = PlayerController.Instance;
            var pm = PickupManager.Instance;
            if (p == null || pm == null) { Fail("玩家或拾取管理器缺失"); yield break; }

            p.stats.pickupRadius = 12f;      // 一定会被吸过来
            p.stats.xpGainMult = 2f;         // 双倍经验
            var d = p.damageable;
            Vector2 at = p.transform.position;

            // —— ① xpGainMult=2：每颗 1 点经验的宝石应实际到手 2 点（升级线仍是 5）——
            pm.DropGem(new Vector2(at.x + 3f, at.y), 1f);
            var g1 = SoleGem();
            yield return WaitCollected(g1, 4f);
            float r1 = p.XpRatio;                       // 期望 2/5 = 0.4
            _creditChecked++;
            if (Mathf.Abs(r1 - 0.4f) < 0.01f) _creditOk++;
            else Fail("双倍经验未生效：第 1 颗后 经验比=" + r1.ToString("F3") + "（期望 0.400）");

            pm.DropGem(new Vector2(at.x + 3f, at.y), 1f);
            var g2 = SoleGem();
            yield return WaitCollected(g2, 4f);
            float r2 = p.XpRatio;                       // 期望 4/5 = 0.8
            _creditChecked++;
            if (Mathf.Abs(r2 - 0.8f) < 0.01f) _creditOk++;
            else Fail("双倍经验第 2 颗累加错误：经验比=" + r2.ToString("F3") + "（期望 0.800）");

            if (d == null) { _creditDetail = "经验 " + r1.ToString("F2") + "/" + r2.ToString("F2") + " / 无 Damageable"; yield break; }

            // —— ② 回血：半血捡一颗 30 点，必须正好 +30 ——
            d.currentHp = d.maxHp * 0.5f;
            float before = d.currentHp;
            pm.DropHealth(new Vector2(at.x + 3f, at.y), 30f);
            var h1 = SoleHealth();
            yield return WaitCollected(h1, 4f);
            float after = d.currentHp;
            _creditChecked++;
            bool exact = Mathf.Abs(after - (before + 30f)) < 0.5f;
            if (exact) _creditOk++;
            else Fail("回血入账错误：" + before.ToString("F1") + " → " + after.ToString("F1") + "（期望 +30）");

            // —— ③ 上限截断：远超上限的回血不得溢出 ——
            d.currentHp = d.maxHp - 10f;
            pm.DropHealth(new Vector2(at.x + 3f, at.y), 999f);
            var h2 = SoleHealth();
            yield return WaitCollected(h2, 4f);
            float capped = d.currentHp;
            _creditChecked++;
            if (Mathf.Abs(capped - d.maxHp) < 0.001f) _creditOk++;
            else Fail("回血未在上限截断：currentHp=" + capped.ToString("F1") + " maxHp=" + d.maxHp.ToString("F1"));

            _creditDetail = "经验 " + r1.ToString("F2") + "/" + r2.ToString("F2") +
                            " / 回血 " + capped.ToString("F1") + "/" + d.maxHp.ToString("F1");
        }

        // =====================================================================================
        //  LEFTOVER：走真实重开路径，上一局实体必须清零
        // =====================================================================================

        private IEnumerator Leftover()
        {
            yield return BuildCleanWorld();
            var p = PlayerController.Instance;
            var pm = PickupManager.Instance;
            var em = EnemyManager.Instance;
            if (p == null || pm == null || em == null) { Fail("管理器缺失"); yield break; }

            int base0 = CountAll();
            if (base0 != 0) Fail("干净世界的实体基线不为 0：" + base0);

            // 制造"上一局的残留"：全部放远处，本局一定吸不到
            Vector2 far = new Vector2(40f, 40f);
            p.stats.pickupRadius = 1.6f;
            pm.DropGem(new Vector2(far.x + 0f, far.y), 1f);
            pm.DropGem(new Vector2(far.x + 1f, far.y), 1f);
            pm.DropGem(new Vector2(far.x + 2f, far.y), 1f);
            pm.DropHealth(new Vector2(far.x + 3f, far.y), 10f);
            em.SpawnAdd(new Vector2(far.x + 6f, far.y), EnemyKind.Tank, 500000f, 0f);
            em.SpawnAdd(new Vector2(far.x + 7f, far.y), EnemyKind.Tank, 500000f, 0f);
            yield return new WaitForSeconds(0.6f);

            _leftoverBefore = CountAll();
            if (_leftoverBefore != 6)
                Fail("残留构造失败：期望 6 个，实际 " + _leftoverBefore);

            // —— 真实重开路径：GameOverPanel.OnRestart 就是这两行 ——
            GameBuilder.TeardownWorld();
            GameBuilder.Build();
            var em2 = EnemyManager.Instance;
            if (em2 != null) em2.FreezeWaves = true;
            ClearEnemiesNow();                       // 清的是**新世界**那一波，剩下的才是上一局的残留
            yield return null;

            _leftoverAfter = CountAll();
            _leftoverBreak = "宝石" + CountGems() + " 回血" + CountHealth() + " 敌人" + CountEnemies();

            if (_leftoverAfter != 0)
                Fail("重开局后上一局实体残留 " + _leftoverAfter + " 个（" + _leftoverBreak + "），期望 0");
        }

        // =====================================================================================
        //  RESTART：真实重开路径后，必须拿到**新世界**的玩家，且掉落闭环仍然通
        // =====================================================================================

        private IEnumerator Restart()
        {
            yield return BuildCleanWorld();
            var p0 = PlayerController.Instance;
            if (p0 == null) { Fail("首次开局就拿不到玩家"); yield break; }
            int oldId = p0.GetInstanceID();

            // 完全复刻 GameOverPanel.OnRestart：中间不给任何帧的时间
            GameBuilder.TeardownWorld();
            GameBuilder.Build();
            var em2 = EnemyManager.Instance;
            if (em2 != null) em2.FreezeWaves = true;
            ClearEnemiesNow();
            ClearPicksNow();
            // 等 3 帧：TeardownWorld 的 Destroy 是延迟到帧末的，要让两者都真正落下
            yield return null;
            yield return null;
            yield return null;

            var p = PlayerController.Instance;
            var pm = PickupManager.Instance;

            // ① 还能拿到玩家吗？（旧引用悬空的话这里就是 null）
            bool got = p != null;
            _restartChecked++;
            _restartAlive = got;
            if (got) _restartOk++;
            else Fail("重开局后拿不到玩家（PlayerController.Instance 指向已销毁的旧玩家）");

            // ② 必须真的是新世界的那个，而不是上一局残留的引用
            bool isNew = got && p.GetInstanceID() != oldId;
            _restartChecked++;
            _restartNewPlayer = isNew;
            if (isNew) _restartOk++;
            else Fail("重开局后拿到的仍是上一局的玩家（InstanceID 未变）");

            // ③ 管理器也都接上了吗
            _restartChecked++;
            bool mgrs = pm != null && EnemyManager.Instance != null && CombatManager.Instance != null;
            if (mgrs) _restartOk++;
            else Fail("重开局后管理器缺失：Pickup=" + Yn(pm != null) +
                      " Enemy=" + Yn(EnemyManager.Instance != null) +
                      " Combat=" + Yn(CombatManager.Instance != null));

            // ④ 新世界的掉落闭环真的通吗？（引用存在 ≠ 世界接上了）
            if (p != null && pm != null)
            {
                // 与 MAGNET 同理：本场景测的是"闭环通不通"，经验倍率先钉成 1，
                // 否则期望值会随默认角色（游侠 xpMult=1.15）漂移到 0.69。
                p.stats.pickupRadius = 12f;
                p.stats.xpGainMult = 1f;
                pm.DropGem((Vector2)p.transform.position + new Vector2(3f, 0f), 3f);
                var g = SoleGem();
                yield return WaitCollected(g, 4f);
                bool collected = g == null || !g.gameObject.activeSelf;
                float ratio = p.XpRatio;
                bool credited = Mathf.Abs(ratio - 0.6f) < 0.01f;
                _restartChecked++;
                _restartLoop = collected && credited;
                if (_restartLoop) _restartOk++;
                else Fail("重开局后掉落闭环不通：已回收=" + Yn(collected) + " 经验比=" + ratio.ToString("F3"));
            }

            string playerDesc = got ? ("id=" + PlayerController.Instance.GetInstanceID()) : "null";
            _restartDetail = "旧 he=" + oldId + " / 新 " + playerDesc +
                             " / 掉落闭环=" + Yn(_restartLoop);
        }

        // =====================================================================================
        //  REPEAT：连续 3 次重开，残留不得累积
        // =====================================================================================

        private IEnumerator Repeat()
        {
            yield return BuildCleanWorld();

            for (int cycle = 1; cycle <= 3; cycle++)
            {
                var p = PlayerController.Instance;
                var pm = PickupManager.Instance;
                if (p == null || pm == null) { Fail("第 " + cycle + " 轮：管理器缺失"); break; }
                if (EnemyManager.Instance != null) EnemyManager.Instance.FreezeWaves = true;
                ClearEnemiesNow();
                ClearPicksNow();
                p.stats.pickupRadius = 1.6f;

                Vector2 far = new Vector2(40f, 40f);
                for (int i = 0; i < 4; i++)
                    pm.DropGem(new Vector2(far.x + i, far.y), 1f);
                yield return new WaitForSeconds(0.4f);

                int dropped = CountAll();
                GameBuilder.TeardownWorld();
                // Object.Destroy 的销毁延迟到帧末，必须让这一帧走完再数；
                // 否则数到的还是"已标记待销毁"的那一批，会把正常清理报成残留（本轮第一版就栽在这）。
                yield return null;
                yield return null;
                int after = CountAll();

                _repeatChecked++;
                if (after == 0) _repeatOk++;
                else Fail("第 " + cycle + " 轮重开后残留 " + after + " 个（本轮放下 " + dropped + "）");
                _repeatDetail += "第" + cycle + "轮 放" + dropped + "→残留" + after + "  ";

                // 进入下一轮：真实重开路径（TeardownWorld 后紧跟 Build）
                if (cycle < 3)
                {
                    GameBuilder.Build();
                    yield return null;
                }
            }
            _repeatDetail = _repeatDetail.Trim();
        }

        // =====================================================================================

        private void Fail(string msg) { _fail.Add(msg); }

        private static string Yn(bool b) => b ? "是" : "否";

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "MAGNET") pass = pass && _magChecked == 2 && _magOk == 2;
            if (_scene == "CREDIT") pass = pass && _creditChecked == 4 && _creditOk == 4;
            if (_scene == "LEFTOVER") pass = pass && _leftoverBefore == 6 && _leftoverAfter == 0;
            if (_scene == "RESTART") pass = pass && _restartChecked == 4 && _restartOk == 4;
            if (_scene == "REPEAT") pass = pass && _repeatChecked == 3 && _repeatOk == 3;

            Debug.Log("[SMOKE] ===== 掉落/磁吸/结算链路实测结果 =====");
            Debug.Log("[SMOKE] 场景              " + _scene);
            switch (_scene)
            {
                case "MAGNET":
                    Debug.Log("[SMOKE] 断点通过          " + _magOk + "/" + _magChecked);
                    Debug.Log("[SMOKE] 半径外不动        " + Yn(_magFarHeld));
                    Debug.Log("[SMOKE] 半径内回收并入账  " + Yn(_magNearPulled));
                    Debug.Log("[SMOKE] 明细              " + _magDetail);
                    break;
                case "CREDIT":
                    Debug.Log("[SMOKE] 入账通过          " + _creditOk + "/" + _creditChecked);
                    Debug.Log("[SMOKE] 明细              " + _creditDetail);
                    break;
                case "LEFTOVER":
                    Debug.Log("[SMOKE] 重开前存量        " + _leftoverBefore);
                    Debug.Log("[SMOKE] 重开后残留        " + _leftoverAfter);
                    Debug.Log("[SMOKE] 残留构成          " + _leftoverBreak);
                    break;
                case "RESTART":
                    Debug.Log("[SMOKE] 重开通过          " + _restartOk + "/" + _restartChecked);
                    Debug.Log("[SMOKE] 重开后拿到玩家    " + Yn(_restartAlive));
                    Debug.Log("[SMOKE] 是新世界玩家      " + Yn(_restartNewPlayer));
                    Debug.Log("[SMOKE] 新世界掉落闭环    " + Yn(_restartLoop));
                    Debug.Log("[SMOKE] 明细              " + _restartDetail);
                    break;
                default:
                    Debug.Log("[SMOKE] 重开轮次通过      " + _repeatOk + "/" + _repeatChecked);
                    Debug.Log("[SMOKE] 逐轮明细          " + _repeatDetail);
                    break;
            }
            Debug.Log("[SMOKE] 失败项            " + (_fail.Count == 0 ? "无" : string.Join(" | ", _fail.ToArray())));
            Debug.Log("[SMOKE] 运行期异常        " + _errors);
            if (_errors > 0) Debug.Log("[SMOKE] 首条错误          " + _firstError);
            Debug.Log("[SMOKE] 结论              " + (pass ? "通过" : "不通过"));
        }
    }
}
