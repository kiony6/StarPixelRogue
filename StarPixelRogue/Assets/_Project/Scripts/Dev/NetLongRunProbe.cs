#if FISHNET_ENABLED
using System;
using System.Collections.Generic;
using StarPixel.Enemy;
using StarPixel.Net;
using StarPixel.Pickups;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M42「联机长局稳定性」无头实测。
    ///
    /// 为什么单机测完还要再测一遍联机：
    ///   M41 在单机侧（<see cref="PickupManager"/>）修掉了"掉落物账本只进不出"——30 分钟游戏时间
    ///   堆到 887 个宝石，每帧全表算距离 → 越玩越卡。但联机侧的掉落物走的是**另一条路**：
    ///   <c>NetEnemy.ServerDie()</c> 直接调 <c>NetWorld.SpawnGem()</c>，整条绕过 PickupManager，
    ///   既没有账本也没有上限。单机那边修得再干净，联机这边一分钱都没花。
    ///   而且联机更贵：每个 NetPickup 都带 SyncVar + NetworkTransform（磁吸时位置每帧在动，
    ///   每个都在持续占同步带宽），房主侧每帧还要对每个掉落物遍历一次玩家注册表。
    ///
    /// 两端都跑同一个探针：
    ///   房主侧断言**权威世界**（场上计数走场景扫描，不读账本——用被测对象自己的账本判自己等于没判）；
    ///   客户端侧采样**自己同步到的视图**，两端计数由驱动脚本（net_longrun_probe.py）交叉核对。
    ///   这正是 R4（回收不走权威 Despawn 而走本地 Destroy）唯一会露馅的地方：
    ///   房主本地销毁了，客户端对此一无所知，它的那份副本变成僵尸，双端计数从此分叉。
    ///
    /// 判据的区分力（承 M41 的教训）：
    ///   1) "上限真的生效"必须单独断言：世界没跑起来时（倍率失效 / 波次不推进），
    ///      宝石根本堆不到上限，"有界"会一片假绿。
    /// 3.5) 上面两条**只在"已经贴到上限附近"时才断言**：没贴到顶的世界里回收从未发生，
    ///      计数自然是 0——那报的是"世界太小"，不是"上限没生效"。
    ///      区分力注入第一次跑就靠这条把 R1 的失败面从四格收窄成有解释力的三格。
    ///   2) "账本与场景一致"：防止有生成路径绕过账本——那种写法下上限形同虚设，
    ///      而"宝石有界"仍可能因为世界没跑满而绿着。
    ///   3) "末段贴顶后不再抬升"用三态：还没贴到顶（末段 &lt; 上限的 80%）时无从区分
    ///      "健康的开局爬坡"与"无上限的爬坡"，判"不适用"——
    ///      判"通过"会掩盖问题，判"失败"会误报。
    ///   4) "上限真的生效"与"账本与场景一致"是**房主专属**判据。客户端从不生成也从不回收
    ///      （二者都是房主权威行为），它的账本恒空、计数器恒 0——在客户端断言这两条
    ///      只会得到结构性失败，那是判据写错了，不是被测对象错了。
    /// </summary>
    public class NetLongRunProbe : MonoBehaviour
    {
        private float _limit = 300f;
        private string _scene = "GROWTH";
        private float _t;        // 真实经过时间（未缩放）
        private float _game;     // 本端游戏内经过时间（受本端 timeScale 影响）
        private int _frames;
        private int _errors;
        private int _kills;
        private string _firstError = "无";
        private bool _isHost;

        /// <summary>
        /// M43：重开要提前这么多秒动手。收尾必须早于 SmokeTest（它在 _limit+1 就 Quit），
        /// 而重开之后还要等新世界真正建起来、广播走完才数得准——两头都要留出余量。
        /// </summary>
        private const float RestartTail = 4f;

        // —— RESTART 状态 ——
        private bool _rsStarted, _rsSeen, _rsSampled, _rsPre;
        private bool _rsTornDown, _rsBuilt;
        private float _rsBuiltAt;
        private float _rsSeenAt;
        private int _rsWaveBefore = -1, _rsWaveAfter = -1;
        private int _rsGemsBefore = -1, _rsHealthBefore, _rsGemsAfter, _rsHealthAfter;
        // 拆完世界、尚未重建的那一瞬间的残留数（房主专属）。
        // 这是唯一能精确回答"旧实体到底有没有被反生成"的时刻：一旦新世界建起来，
        // 旧敌人会在 1～2 秒内被玩家打死，与"被反生成"再无区别。
        private int _rsEnemyLeak = -1, _rsGemsLeak = -1, _rsHealthLeak, _rsStateLeak = -1;
        private int _rsEnemyBefore = -1, _rsEnemyAfter;
        private int _rsStateBefore = -1, _rsStateAfter = -1;
        private int _rsRootBefore, _rsRootAfter;
        private bool _rsPlayerOk;
        private string _rsWaveText = "否", _rsPickupText = "否", _rsStateText = "否", _rsEnemyText = "否";
        private string _rsLeakGemText = "否", _rsLeakStateText = "否";

        // —— M44 弹丸 ——
        //
        // 弹丸是拆世界时唯一"谁都不持有它"的一类实体：没有账本、没有注册表，
        // 只能靠按类型扫场才找得到（M43 漏掉的正是它）。
        // 而它是否真的被清掉，靠自然对局观测不到——重开那一刻场上有几个在飞的弹丸
        // 完全取决于运气（弹丸寿命只有 2.5s 游戏内，6x 下不到半秒真实时间）。
        // 于是在重开前主动放一批**长寿命哨兵弹丸**当作"上一局还在飞的那些"：
        // 它们用回旋镖弹道 + 极慢速度，寿命按 range/speed 折算约 41 秒游戏内，
        // 足够跨越重开的整个过程，也让判据在任何一局里都可断言。
        // 哨兵数量必须**明显多于**新世界在采样窗口里自然产出的那一批（实测十几到三十几个），
        // 否则"房主清了、客户端没收到广播"这种残留会被自然波动盖住——
        // 首版放 24 个时，客户端残留 24 与新世界的 12 只差一轮采样的抖动，判据全程绿灯。
        private const int SentinelProjectiles = 48;
        private bool _rsSeeded;
        private int _rsProjBefore = -1, _rsProjLeak = -1, _rsProjAfter = -1;
        private string _rsLeakProjText = "否", _rsProjCleanText = "否";
        private bool _rsLeakProjOk, _rsProjCleanOk;

        // —— M45 玩家 ——
        //
        // 4 人局之前没人测过，而"拆世界"是全项目唯一一次**批量反生成**的动作：
        // DespawnAllWorldObjects() 按类型扫场，清掉它列出的每一类。
        // 玩家**不在**那个列表里是对的——玩家跨局存活，拆世界不该动它。
        // 但这条边界目前没有任何判据在守：只要有人"顺手补全"把 NetPlayer 也加进扫场，
        // 重开一局就会把所有人踢出去，而且**现有 11 条判据一条都不会红**
        // （它们只数敌人 / 掉落物 / 世界状态 / 弹丸）。
        //
        // 2 人局下这个缺陷同样会发生，但 4 人局才暴露得彻底：掉 1 个人时另外 3 个还在打，
        // 世界照样推进，日志里连一条异常都没有。
        //
        // 口径同样是扫场（不读 NetPlayerRegistry.Count）：M43 的教训是注册表不是事实，
        // 玩家注册表在跨端更不可信——客户端的注册表里可能只有自己。
        private int _rsPlayerBefore = -1, _rsPlayerLeak = -1, _rsPlayerAfter = -1;
        private string _rsPlayerIntactText = "否", _rsPlayerKeepText = "否";
        private bool _rsPlayerIntactOk, _rsPlayerKeepOk;

        private bool _rsWaveOk, _rsPickupOk, _rsStateOk, _rsEnemyOk, _rsLeakGemOk, _rsLeakStateOk;

        // —— M50 EXIT：联机侧到点强制退出 ——
        //
        // 单机那条链 M47 已经走通了（预警 → 死在面板上 → 到点被踢 → 回主菜单）。
        // 联机这条**从没被走过**，而它多出一个单机根本没有的后果：
        // 到点时世界拆了，**连接还在**。
        //   · 客户端：房主仍认为他在场——敌人继续追着那个再也不会响应的角色，
        //     房主还在为他刷怪、算伤害、掉宝石；
        //   · 房主：权威世界被自己拆空，客户端却还连着，看到的是一张不会再刷新的空地图。
        // 判据因此分两侧，而且**必须分两侧**：
        //   客户端侧问"我走了吗"（IsNetworked 归零 / 玩家对象被清）；
        //   房主侧问"他走了吗"（扫场的玩家数 2 → 1）——后者是唯一能证明
        //   "真的挂断了"而不是"只在本地拆了世界"的证据。只在自己这一端断言，
        //   旧实现（只拆世界不断连）会一路全绿：本地世界确实干净了。
        private bool _exPrepped, _exForced, _exSampled, _exRestored;
        private bool _exMinorOn, _exStartOk, _exHintAtWarn;
        private int _exWarnCount, _exForceCount, _exPlayersBefore = -1, _exPlayersAfter = -1;
        private int _exWaveBefore = -1, _exWaveAfter = -1, _exPlayersAfterSelf = -1;
        private float _exSecAtWarn, _exSecAtForce, _exScaleHost = -1f;
        private bool _exMenuVisible, _exNetworkedAfter, _exWorldGone, _exRunningAfter, _exLocalAlive;
        private string _exNotice = "";

        // —— M51 LEAVE：「离开对局」按钮走的那条路 ——
        //
        // M50 修掉了一个很安静的缺陷：FishNetBackend.Stop() 第一行把 OnClientConnectionState
        // 退订了，而"主动离开"的全部收尾都写在那个回调里。于是结算面板上「离开对局」
        // 点下去的真实后果是：连接断了，**世界还在、大厅没建、Mode 仍是 Client**。
        // 注释写着"触发断线回调 → 重建联机大厅"，代码做的恰好是让那个回调不可能触发。
        //
        // 修完之后这条分支**没有任何测试走过**：M50 的 EXIT 场景走的是
        // ForceExitMinor → Stop(false)（明确不重建大厅），恰好是相反的那一半。
        // 也就是说 M50 只证明了"到点那条路不重建大厅是对的"，没有证明
        // "离开对局这条路**会**重建大厅"。
        //
        // 场景分工同 EXIT：**客户端**当动作方（死 → 结算面板 → 点「离开对局」），
        // 房主只旁观（问"他走了吗"）。理由一样——房主自己走会把整间房带走，
        // 客户端那边是"被断线"，两条链会缠在一起，红一条分不清是谁的错。
        private bool _lvPrepped, _lvClicked, _lvSampled;
        private bool _lvPanelShown, _lvBtnFound, _lvHitOk, _lvBtnGone;
        private bool _lvUnnetworked, _lvLobbyBack, _lvLobbyStatusOk, _lvWorldGone;
        private bool _lvPlayersSelf, _lvLocalAliveHost;
        private int _lvPlayersBefore = -1, _lvPlayersAfter = -1, _lvPlayersNow = -1;
        private int _lvWaveBefore = -1, _lvWaveAfter = -1;
        private float _lvScaleHost = -1f;
        private string _lvHitDetail = "", _lvLobbyStatusText = "", _lvLobbyStatusBefore = "";

        // —— M51 HOSTEXIT：房主自己到点，把整间房带走 ——
        //
        // M50 把"客户端到点"那条链走通了，房主那条一直没走。它不一样：
        // 房主到点 = 服务端停止 = **整间房解散**，客户端不是"被踢"而是"房子没了"。
        // 客户端在这条链上会先被 NetReconnect 抓走（它自己不知道这是主动关闭），
        // 重试 5 次 × 1.5s 全失败后才 GiveUp → 回大厅。
        // 这正是要测的：**玩家最终落到哪儿**。落到大厅是对的（还能再开一局，
        // 也可以换一家）；卡在"正在重连"里出不来、或者落到一张空白场景，都是缺陷。
        private bool _hxPrepped, _hxForced, _hxHostSampled, _hxRestored;
        private bool _hxMinorOn, _hxStartOk, _hxHintAtWarn, _hxMenuVisible;
        private bool _hxWorldGone, _hxUnnetworked, _hxLobbyBack, _hxLobbyStatusOk;
        private int _hxWarnCount, _hxForceCount;
        private float _hxSecAtForce, _hxSecAtWarn;
        private string _hxNotice = "", _hxLobbyStatusText = "";

        private const int SEGMENTS = 6;
        private readonly List<Sample> _samples = new List<Sample>();
        private int _done;

        private int _checked;
        private int _ok;
        private readonly List<string> _fail = new List<string>();

        // —— 判据结果 ——
        private int _finalWave;
        private int _peakGems, _peakHealth;
        private float _gemEarly, _gemLate;
        private long _heapEarly, _heapLate;
        private int _ledgerGems, _ledgerHealths;   // 末次采样的账本口径（诊断）
        private bool _waveOk, _gemBoundOk, _healthOk, _growOk, _trimOk, _ledgerOk, _valueOk, _snapOk;
        private string _growText = "否";
        private string _trimText = "否";
        private string _ledgerText = "否";
        private string _snapText = "否";
        private int _naCount;
        private int _gemTrimmed, _healthTrimmed;

        private struct Sample
        {
            public float game;
            public int wave, gems, healths, ledgerGems, ledgerHealths, level, kills;
            public long heap;
            public int gc0, gc1, gc2;
            public float fps;
        }

        /// <summary>由 GameBuilder 在连接就绪后调用。观察时长比 SmokeTest 短 1s，确保由本探针收尾。</summary>
        public static void Start(float seconds, string scene)
        {
            var go = new GameObject("NetLongRunProbe");
            var p = go.AddComponent<NetLongRunProbe>();
            p._limit = Mathf.Max(10f, seconds);
            p._scene = string.IsNullOrEmpty(scene) ? "GROWTH" : scene;
            p._isHost = NetSession.IsHost;
            Debug.Log("[SMOKE] 联机长局实测启动  场景=" + p._scene + " 角色=" + (p._isHost ? "房主" : "客户端") +
                      " 真实时长=" + p._limit.ToString("F0") + "s");

            // M51：这两个场景要观察"回到联机大厅"，那就得先有大厅。
            //
            // 真实流程是：主菜单 → 联机大厅 → 点「创建/加入房间」→ NetLobby.OnBackendReady
            // → EnterGame()（**销毁大厅 UI，保留组件**）→ GameBuilder.Build()。
            // 无头入口（StartNetSmoke）直接 StartHost/StartClient，把大厅整段跳过了——
            // 于是断线时 NotifyDisconnected 里的 FindObjectOfType<NetLobby>() 找不到任何东西，
            // 直接 return，"大厅重建"这件事无从观察，判据也就只能退化成"没崩"。
            //
            // 这里补的就是被跳过的那一步：建出大厅（组件），再按 EnterGame 的做法销毁它的 UI。
            // 为什么必须销毁：大厅画布是后建的 overlay，画布同 sortOrder 时后建的在上，
            // 留着它会盖住结算面板的按钮——那会把"点不到按钮"变成一条假红。
            if (p._scene == "LEAVE" || p._scene == "HOSTEXIT") EnsureLobbyScaffold();
        }

        /// <summary>补出"从大厅进入对局"那一步（M51）：大厅组件在、UI 不在。</summary>
        private static void EnsureLobbyScaffold()
        {
            var lobby = UnityEngine.Object.FindObjectOfType<NetLobby>();
            if (lobby == null)
            {
                lobby = new GameObject("NetLobby").AddComponent<NetLobby>();
                Debug.Log("[SMOKE] 已补建联机大厅（无头入口跳过了主菜单 → 大厅这一段）");
            }
            if (lobby.PanelRoot != null) UnityEngine.Object.Destroy(lobby.PanelRoot);
        }


        // 静态事件必须退订（M29：销毁后委托链仍攥着本实例）
        private System.Action<int> _onKill;

        void OnEnable()
        {
            Application.logMessageReceived += OnLog;
            _onKill = _ => _kills++;
            Core.EventBus.OnEnemyKilled += _onKill;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
            if (_onKill != null) { Core.EventBus.OnEnemyKilled -= _onKill; _onKill = null; }
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            _errors++;
            if (_firstError == "无") _firstError = msg == null ? "?" : msg;
        }

        void Update()
        {
            _t += Time.unscaledDeltaTime;
            _game += Time.deltaTime;
            _frames++;

            if (_scene == "RESTART")
            {
                TickRestart();
                return;
            }

            if (_scene == "EXIT")
            {
                TickExit();
                return;
            }

            if (_scene == "LEAVE")
            {
                TickLeave();
                return;
            }

            if (_scene == "HOSTEXIT")
            {
                TickHostExit();
                return;
            }

            float seg = _limit / SEGMENTS;
            while (_done < SEGMENTS && _t >= (_done + 1) * seg)
            {
                _samples.Add(Take());
                _done++;
            }

            if (_t >= _limit)
            {
                Report();
                enabled = false;
            }
        }

        // =====================================================================================
        //  RESTART：长局之后重开一局，联机实体必须真的被反生成、新世界必须真的建起来
        // =====================================================================================

        /// <summary>
        /// 为什么这条必须单独测：
        ///   M42 给联机掉落物加了账本与上限，也写了 <c>NetWorld.ResetPickups()</c>，
        ///   但**从来没有验证过**它会不会被真正走到。而拆世界的入口
        ///   <c>GameBuilder.TeardownWorld()</c> 此前只清单机那套容器
        ///   （PoolManager 池实例 / EnemyManager.ActiveEnemies / PickupManager 账本），
        ///   联机实体一个都不在里面——敌人注册在 NetEnemyRegistry、掉落物注册在 NetWorld 账本、
        ///   世界状态对象无人持有。于是重开一局后：
        ///     · 旧宝石原封不动躺在脚下（还捡不起来，账本已清零）；
        ///     · 旧敌人继续追着新玩家跑；
        ///     · 两份 NetWorldState 抢 Instance，波次在两份之间来回跳；
        ///     · 客户端更糟：房主若本地 Destroy，客户端副本变僵尸并永久留在场上。
        ///
        /// 两端都跑：房主侧断言权威世界被清干净，客户端侧断言**它真的收到了反生成广播**
        /// ——后者是唯一能证明"走的是 Despawn 而不是本地 Destroy"的证据。
        /// </summary>
        private void TickRestart()
        {
            float tail = Mathf.Max(2f, _limit - RestartTail);

            // 客户端拿不到"房主即将重开"的信号，只能靠自己的时钟。若它的 tail 比房主的
            // 重开晚了一两帧（启动抖动/网络延迟），"重开前"采到的就已经是新世界了——
            // 于是"重开前波次 1"这种平凡数据会让判据退化成"不适用"，白测一轮。
            // 故在 tail 前 1 秒多采一次、取最大值，让"重开前"代表这一局的**峰值**。
            // M44：哨兵弹丸要在"重开前基线"之前放，否则基线采到的是没有弹丸的旧世界，
            // 判据会因为"重开前弹丸 = 0"退化成不适用，白测一轮。
            if (_isHost && !_rsSeeded && _t >= tail - 1.2f)
            {
                _rsSeeded = true;
                SeedProjectiles();
            }

            if (!_rsStarted && _t >= tail - 1f && !_rsPre)
            {
                _rsPre = true;
                RecordBefore();
            }

            if (!_rsStarted && _t >= tail)
            {
                _rsStarted = true;
                RecordBefore();
                if (_isHost)
                {
                    GameBuilder.TeardownWorld();   // 内部先走房主权威反生成（M43）
                    _rsTornDown = true;
                }
            }
            if (!_rsStarted) return;

            // 房主：拆完**下一帧**再点数，然后才重建。
            //
            // 为什么不在同一帧数：Fish-Net 的反生成不是全部即时生效的，
            // 当帧刚生成、网络初始化还没落定的那几个对象，Despawn 当场无效。
            // 首跑就撞上了——反生成数 317，但同帧仍数到 4 个敌人，判据报"有残留"。
            // 隔一帧后：旧世界已拆、新世界还没建，中间没有任何东西会生成，
            // 这一刻的数就是"真的有没有清干净"，不需要任何容差。
            if (_isHost && _rsTornDown && !_rsBuilt)
            {
                _rsEnemyLeak = EnemyCount();
                if (_rsEnemyLeak > 0) Debug.Log("[SMOKE] 拆完残留明细  " + EnemyLeakDetail());
                _rsGemsLeak = GemCountNow(out _rsHealthLeak);
                _rsStateLeak = WorldStateCount();
                _rsProjLeak = ProjectileCount();   // M44
                _rsPlayerLeak = PlayerCount();     // M45：这一刻玩家必须一个不少
                GameBuilder.Build();           // 立刻重建：不给中间帧，免得把"没清"误判成"还没清"
                _rsBuilt = true;
                _rsBuiltAt = _t;
            }

            // 客户端：等"波次真的掉下来"再采样。它自己没有能力拆/建世界，
            // 观测到的每一个变化都只能来自房主广播——这条判据才叫有区分力。
            if (!_isHost && !_rsSeen)
            {
                int w = Wave();
                if (w >= 1 && w <= 2) { _rsSeen = true; _rsSeenAt = _t; }
            }

            if (!_isHost && _rsSeen && _t >= _rsSeenAt + 1.0f)
            {
                ReportRestart();
                enabled = false;
                return;
            }

            if (_isHost && _rsBuilt && _t >= _rsBuiltAt + 1.6f)
            {
                ReportRestart();
                enabled = false;
                return;
            }

            // 兜底：绝不能晚于 SmokeTest（它在 _limit+1 直接 Quit）
            if (_t >= _limit)
            {
                ReportRestart();
                enabled = false;
            }
        }

        /// <summary>重开前基线：多次采样取最大，避免客户端比房主晚一两帧就采到新世界。</summary>
        private void RecordBefore()
        {
            int w = Wave();
            if (w > _rsWaveBefore) _rsWaveBefore = w;

            int hp;
            int g = GemCountNow(out hp);
            if (g > _rsGemsBefore) { _rsGemsBefore = g; _rsHealthBefore = hp; }

            int e = EnemyCount();
            if (e > _rsEnemyBefore) _rsEnemyBefore = e;

            int st = WorldStateCount();
            if (st > _rsStateBefore) _rsStateBefore = st;

            int pr = ProjectileCount();
            if (pr > _rsProjBefore) _rsProjBefore = pr;   // M44

            int pl = PlayerCount();
            if (pl > _rsPlayerBefore) _rsPlayerBefore = pl;   // M45

            _rsRootBefore = RootCount();
        }

        /// <summary>
        /// M44：在重开前放一批"仍在飞的弹丸"。
        ///
        /// 为什么不靠自然对局：弹丸寿命只有 2.5 秒游戏内，6x 加速下不到半秒真实时间，
        /// 重开那一刻场上有几个完全看运气——判据会随机退化成"不适用"。
        /// 用回旋镖弹道（behavior=1）配极慢速度：寿命 = range/speed×2 + 1.5 ≈ 41 秒游戏内，
        /// 横跨整个重开过程；伤害给 0，它不会打死任何东西，只作为"该被清掉的实体"存在。
        /// </summary>
        private void SeedProjectiles()
        {
            var lp = NetPlayerRegistry.Local;
            Vector3 c = lp != null ? lp.transform.position : Vector3.zero;
            for (int i = 0; i < SentinelProjectiles; i++)
            {
                float a = i * (Mathf.PI * 2f / SentinelProjectiles);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                NetWorld.SpawnProjectileEx(c + new Vector3(dir.x, dir.y, 0f) * 2f,
                    dir, 0f, 0.3f, 6f, 1, false);
            }
            Debug.Log("[SMOKE] 已放置哨兵弹丸 " + SentinelProjectiles + " 个（模拟重开瞬间仍在飞的弹丸）");
        }

        private void ReportRestart()
        {
            if (_rsSampled) return;
            _rsSampled = true;

            _rsWaveAfter = Wave();
            _rsGemsAfter = GemCountNow(out _rsHealthAfter);
            _rsEnemyAfter = EnemyCount();
            _rsRootAfter = RootCount();
            _rsStateAfter = WorldStateCount();
            _rsProjAfter = ProjectileCount();
            _rsPlayerAfter = PlayerCount();

            var lp = NetPlayerRegistry.Local;
            _rsPlayerOk = lp != null && (lp.Damageable == null || !lp.Damageable.isDead);

            // 1) 波次重置。前提是"重开前世界确实推进过"——否则本局压根没跑起来，
            //    重开后是 1 也说明不了任何事，只能判"不适用"。
            if (_rsWaveBefore < 5)
            {
                _rsWaveOk = true;
                _rsWaveText = "不适用";
            }
            else
            {
                _rsWaveOk = _rsWaveAfter >= 1 && _rsWaveAfter <= 2;
                _rsWaveText = Yn(_rsWaveOk);
            }
            Check("重开后波次重置", _rsWaveOk);

            // 2) 掉落物清零。
            //
            //    这与 M41 那条被删掉的假判据**不是**同一件事：单机宝石是池实例，
            //    PoolManager.DestroyAll() 无论怎么改清理路径都会把它们销毁；而联机宝石
            //    是网络生成的独立对象，池管理器管不到——不反生成就一定会留在场上。
            //
            //    但不能用绝对阈值：6x 加速下重开后 1.6s 已是游戏内约 10 秒，
            //    新世界打到第 2 波、掉了几十个宝石，那都是**新产出**（首跑就把
            //    阈值写死成 6，结果 33 判红——报的是"世界在正常运转"，不是"没清干净"）。
            //    要问的是"旧的那批还在不在"：旧账是几百个，只剩不到三分之一才叫清干净。
            if (_rsGemsBefore < 20)
            {
                _rsPickupOk = true;
                _rsPickupText = "不适用";
            }
            else
            {
                _rsPickupOk = _rsGemsAfter <= Mathf.Max(20, _rsGemsBefore / 3)
                              && _rsHealthAfter <= Mathf.Max(8, _rsHealthBefore / 3);
                _rsPickupText = Yn(_rsPickupOk);
            }
            Check("重开后掉落物清零", _rsPickupOk);

            // 3) 世界状态唯一。旧的那个没被反生成时这里是 2——
            //    两份都在写 Wave，HUD 的波次会在两个数字之间跳。
            if (_rsStateBefore < 1)
            {
                _rsStateOk = true;
                _rsStateText = "不适用";
            }
            else
            {
                _rsStateOk = _rsStateAfter == 1;
                _rsStateText = Yn(_rsStateOk);
            }
            Check("重开后世界状态唯一", _rsStateOk);

            // 4) 拆完瞬间的残留（房主专属，三条分开断言）。
            //
            //    为什么必须在这一刻数：新世界一建起来，旧敌人 1～2 秒内就会被玩家打死，
            //    "没反生成"与"被反生成"从画面到计数再无区别。M43 的四组注入第一次跑
            //    就是这么撞出来的——S3 摘掉敌人反生成后，场上只剩 4 只（旧的那批已被打死），
            //    「重开后敌人清空」照样判绿。**判据不红不代表代码没问题，
            //    可能只是它在错误的时间点看了错的东西。**
            //
            //    三条拆开而不是合成一条：三种残留的成因不同（敌人注册表 / 掉落物账本 /
            //    无人持有的世界状态对象），合成一条的话注入任何一处都报同一个键，
            //    失败面会粘成一坨，也就失去了"每条判据各守一个面"的意义。
            //    M45：前提从"重开前至少 8 个敌人"放宽到"至少 1 个"。
            //
            //    8 这个数字是 M43 定的，当时的观察窗口是波次 40 上下（场上 8~16 个）。
            //    本轮跑到**波次 209** 时房主侧只剩 7 个敌人——波次越高清怪越快，
            //    重开那一刻场上反而越干净，于是这条判据在最该测的长局里退化了：
            //    "拆完残留 = 0"是**真的**断言，还是"压根没东西可残留"？看不出来。
            //
            //    "残留 == 0"这个断言在"重开前有 1 个"时就成立且有区分力
            //    （Q3 注入证明：摘掉敌人扫场后它照样红）。改成 ≥1，与弹丸那条同口径。
            if (!_isHost || _rsEnemyBefore < 1)
            {
                _rsEnemyOk = true;
                _rsEnemyText = "不适用";
            }
            else
            {
                _rsEnemyOk = _rsEnemyLeak == 0;
                _rsEnemyText = Yn(_rsEnemyOk);
            }
            Check("拆完立刻无残留敌人", _rsEnemyOk);

            if (!_isHost || _rsGemsBefore < 20)
            {
                _rsLeakGemOk = true;
                _rsLeakGemText = "不适用";
            }
            else
            {
                _rsLeakGemOk = _rsGemsLeak == 0 && _rsHealthLeak == 0;
                _rsLeakGemText = Yn(_rsLeakGemOk);
            }
            Check("拆完立刻无残留掉落物", _rsLeakGemOk);

            if (!_isHost || _rsStateBefore < 1)
            {
                _rsLeakStateOk = true;
                _rsLeakStateText = "不适用";
            }
            else
            {
                _rsLeakStateOk = _rsStateLeak == 0;
                _rsLeakStateText = Yn(_rsLeakStateOk);
            }
            Check("拆完立刻无残留世界状态", _rsLeakStateOk);

            // 4.5) M44：拆完立刻无残留弹丸。
            //
            //     与上面三条同属"拆完那一帧"，但值得单独说一句为什么它必须是一条**独立**判据：
            //     弹丸的成因和那三类不同——它没有账本也没有注册表，唯一能找到它的途径
            //     就是按类型扫场。合成一条的话，将来有人摘掉弹丸那行，报出来的键
            //     和"敌人没清干净"是同一个，谁也不知道到底漏了哪一类。
            //
            //     前提同样是"重开前确实有弹丸"：哨兵保证了这一点，若某局哨兵生成失败
            //     （网络生成静默失败），判"不适用"而不是"通过"。
            if (!_isHost || _rsProjBefore < 1)
            {
                _rsLeakProjOk = true;
                _rsLeakProjText = "不适用";
            }
            else
            {
                _rsLeakProjOk = _rsProjLeak == 0;
                _rsLeakProjText = Yn(_rsLeakProjOk);
            }
            Check("拆完立刻无残留弹丸", _rsLeakProjOk);

            // 4.6) M44：重开后弹丸清零——**客户端专属**。
            //
            //     它与上一条守的不是同一个面：上一条问"房主有没有清"，这一条问
            //     "客户端有没有收到广播"。房主清得再干净，只要走的是本地 Destroy
            //     而不是房主权威 Despawn，客户端那份就一个都不会消失——
            //     而房主侧的残留判据在那种写法下反而是绿的（它确实清了）。
            //
            //     为什么房主侧"不适用"：房主 6x 重建后 1.6 秒已经是游戏内十秒，
            //     新世界自己就打出了几十发弹丸，拿"重开前的三分之一"去卡它只会得到
            //     "世界在正常运转"的假红。客户端是 1x，它看到的新产出只有十几个，
            //     而残留的哨兵是整整一批——两个量级差得开，才判得动。
            if (_isHost || _rsProjBefore < 1)
            {
                _rsProjCleanOk = true;
                _rsProjCleanText = "不适用";
            }
            else
            {
                _rsProjCleanOk = _rsProjAfter <= Mathf.Max(12, _rsProjBefore / 3);
                _rsProjCleanText = Yn(_rsProjCleanOk);
            }
            Check("重开后弹丸清零", _rsProjCleanOk);

            // 6) M45：拆完玩家一个都不能少（房主专属）。
            //
            //    这是"拆世界"这件事的**反向判据**：前面五条都在问"该清的清没清"，
            //    这一条问的是"不该清的有没有被顺手清掉"。玩家跨局存活，
            //    DespawnAllWorldObjects() 的类型列表里没有 NetPlayer 是**故意的**；
            //    而这条边界此前没有任何判据在守，顺手补进去就会把全房人踢出去。
            //
            //    为什么必须是房主：只有房主在"拆完、尚未重建"的那一帧点过数（_rsPlayerLeak），
            //    那一刻玩家若被误清，随后 Build() 会立刻把人重新建出来（甚至建出重复的），
            //    1.6 秒后再数就已经看不出发生过什么了。
            if (!_isHost || _rsPlayerBefore < 1)
            {
                _rsPlayerIntactOk = true;
                _rsPlayerIntactText = "不适用";
            }
            else
            {
                _rsPlayerIntactOk = _rsPlayerLeak >= _rsPlayerBefore;
                _rsPlayerIntactText = Yn(_rsPlayerIntactOk);
            }
            Check("拆完玩家未被误清", _rsPlayerIntactOk);

            // 7) M45：重开后玩家数不减少（两端都判）。
            //
            //    与上一条是两个面：上一条守"拆的动作"，这一条守"拆完到重建之后"的整段。
            //    客户端也必须判——它的玩家副本若因某种原因没跟着重建，
            //    房主侧那条照样是绿的（房主的世界里人好好的），只有客户端自己看得见。
            if (_rsPlayerBefore < 1)
            {
                _rsPlayerKeepOk = true;
                _rsPlayerKeepText = "不适用";
            }
            else
            {
                _rsPlayerKeepOk = _rsPlayerAfter >= _rsPlayerBefore;
                _rsPlayerKeepText = Yn(_rsPlayerKeepOk);
            }
            Check("重开后玩家齐全", _rsPlayerKeepOk);

            Check("重开后玩家可控", _rsPlayerOk);

            // 5) 客户端：收到重开广播。房主侧"不适用"——它就是广播的发出方。
            if (_isHost)
            {
                Check("收到重开广播", true);   // 不适用
            }
            else
            {
                Check("收到重开广播", _rsSeen);
            }

            Check("全程零异常", _errors == 0);

            Debug.Log("[SMOKE] ===== 联机重开局实测结果 =====");
            Line("场景", _scene);
            Line("角色", _isHost ? "房主" : "客户端");
            Line("断点通过", _ok + "/" + _checked);
            Line("重开后波次重置", _rsWaveText);
            Line("重开后掉落物清零", _rsPickupText);
            Line("重开后世界状态唯一", _rsStateText);
            Line("拆完立刻无残留敌人", _rsEnemyText);
            Line("拆完立刻无残留掉落物", _rsLeakGemText);
            Line("拆完立刻无残留世界状态", _rsLeakStateText);
            Line("拆完立刻无残留弹丸", _rsLeakProjText);
            Line("重开后弹丸清零", _rsProjCleanText);
            Line("拆完玩家未被误清", _rsPlayerIntactText);
            Line("重开后玩家齐全", _rsPlayerKeepText);
            Line("重开后玩家可控", Yn(_rsPlayerOk));
            Line("收到重开广播", _isHost ? "不适用" : Yn(_rsSeen));
            Line("全程零异常", Yn(_errors == 0));
            Line("重开前波次", _rsWaveBefore.ToString());
            Line("重开前宝石", _rsGemsBefore + " 宝石 / " + _rsHealthBefore + " 回血球");
            Line("重开前敌人", _rsEnemyBefore.ToString());
            Line("重开前弹丸", _rsProjBefore.ToString());
            Line("重开前玩家", _rsPlayerBefore.ToString());
            Line("重开后波次", _rsWaveAfter.ToString());
            Line("重开后宝石", _rsGemsAfter + " 宝石 / " + _rsHealthAfter + " 回血球");
            Line("重开后敌人", _rsEnemyAfter.ToString());
            Line("重开后弹丸", _rsProjAfter.ToString());
            Line("重开后玩家", _rsPlayerAfter.ToString());
            Line("拆完残留", _rsEnemyLeak + " 敌人 / " + _rsGemsLeak + " 宝石 / " + _rsStateLeak +
                            " 状态 / " + _rsProjLeak + " 弹丸");
            Line("拆完玩家", _rsPlayerLeak + " / 重开前 " + _rsPlayerBefore);
            // 注册表 vs 扫场：M43 的教训是"注册表不是事实"，玩家注册表在客户端
            // 尤其不可信（可能只登记了自己）。两个数都打出来，扫场数才是判据口径。
            Line("玩家注册表", NetPlayerRegistry.Count + "（扫场 " + _rsPlayerAfter + "）");
            Line("世界状态数", _rsStateBefore + " → " + _rsStateAfter);
            Line("根对象", _rsRootBefore + " → " + _rsRootAfter);
            Line("托管堆MB", (GC.GetTotalMemory(false) / 1048576f).ToString("F1"));
            Line("平均帧率", (_t > 0f ? _frames / _t : 0f).ToString("F0"));
            Line("反生成数", _isHost ? NetWorld.DespawnedOnReset.ToString() : "不适用");
            Line("首次异常", _firstError);
            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        // =====================================================================================
        //  EXIT：到点被踢的那一端必须真的离开房间，另一端必须看见他消失
        // =====================================================================================

        /// <summary>
        /// 两端跑同一个场景，但做的事完全不同：
        ///   客户端 —— 打开青少年模式 → 推到预警 → 跨过上限（**真实的** ForceExitMinor）
        ///            → 采样：主菜单 / 提示 / 会话是否真的闭合 / 世界是否拆了；
        ///   房主   —— 只旁观：开局记一次人数与波次，等客户端走掉之后再记一次。
        ///
        /// 为什么把"到点"放在**客户端**而不是房主：
        ///   房主到点会把整间房带走（服务端停止），客户端那边是"被断线"而不是"到点"，
        ///   两条链会缠在一起，红了一条分不清是谁的错。客户端到点则干净得多——
        ///   房主侧只需要回答"他还在不在"，而这个问题恰好就是旧实现的缺陷所在。
        /// </summary>
        private void TickExit()
        {
            float L = _limit;

            if (_isHost)
            {
                // 0.25：客户端早已连上（房主 Ready 早于客户端连接，故要给足余量）
                if (_exPlayersBefore < 0 && _t >= L * 0.25f)
                {
                    _exPlayersBefore = PlayerCount();
                    _exWaveBefore = Wave();
                }
                // 0.65：客户端在 0.36 就走掉了，这里差着二十多秒，够广播跑完
                if (_exPlayersAfter < 0 && _t >= L * 0.65f)
                {
                    _exPlayersAfter = PlayerCount();
                    _exWaveAfter = Wave();
                    var lp = NetPlayerRegistry.Local;
                    _exLocalAlive = lp != null && (lp.Damageable == null || !lp.Damageable.isDead);
                    _exScaleHost = Time.timeScale;
                }
            }
            else
            {
                if (!_exPrepped && _t >= L * 0.20f) { _exPrepped = true; ExitPrep(); }
                if (!_exForced && _t >= L * 0.36f) { _exForced = true; ExitForce(); }
                // 让出几帧：TeardownWorld 走的是 Object.Destroy（延迟到帧末），
                // 当场采样会读到还没清掉的引用（M49 §5.2 同一类错）。
                if (!_exSampled && _t >= L * 0.40f) { _exSampled = true; ExitSample(); }
                if (!_exRestored && _t >= L * 0.46f) { _exRestored = true; ExitRestore(); }
            }

            if (_t >= L)
            {
                ReportExit();
                enabled = false;
            }
        }

        private void ExitPrep()
        {
            Core.SettingsManager.Instance?.SetMinorMode(true);
            Core.PlaytimeGuard.ResetToday();
            Core.PlaytimeGuard.ResetCounters();

            var g = Core.PlaytimeGuard.Instance;
            _exStartOk = g != null && Core.PlaytimeGuard.GameRunning;
            _exMinorOn = Core.SettingsManager.Instance != null &&
                         Core.SettingsManager.Instance.Current.minorMode;

            // 推到"预警已弹、还没到点"的位置。60 秒一档：预警在 55 分钟，
            // 这里停到 59 分钟，正好跨过预警、没碰上限。
            AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds - 60f);
            if (g != null)
            {
                _exSecAtWarn = g.TodaySeconds;
                _exWarnCount = g.WarnCount;
                _exHintAtWarn = g.HintShowing;    // 故意不关：真实玩家常常就这么晾着它
            }
        }

        private void ExitForce()
        {
            var g = Core.PlaytimeGuard.Instance;
            if (g == null) return;
            // 跨过上限：**真实的** ForceExitMinor 在这一步发生（探针不自己拆世界）
            AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds + 60f);
            _exSecAtForce = g.TodaySeconds;
            _exForceCount = g.ForceExitCount;
        }

        private void ExitSample()
        {
            var mm = UI.MainMenu.Instance;
            _exMenuVisible = mm != null && mm.IsVisible;
            _exNotice = mm != null ? mm.NoticeText : "";
            _exNetworkedAfter = NetSession.IsNetworked;
            _exWorldGone = WorldGone();
            _exRunningAfter = !Core.PlaytimeGuard.GameRunning;
            _exPlayersAfterSelf = PlayerCount();
        }

        private void ExitRestore()
        {
            // 绝不把青少年模式与已累计时长留在用户的 settings.json / playtime.json 里。
            // 两端共用一个数据目录，不恢复的话下一轮回归一开局就是"已到点"。
            Core.SettingsManager.Instance?.SetMinorMode(false);
            Core.PlaytimeGuard.ResetToday();
        }

        private void ReportExit()
        {
            if (_exForced && !_exSampled) ExitSample();     // 时长余量不足时的兜底

            Debug.Log("[SMOKE] ===== 联机到点强制退出实测结果 =====");
            Line("场景", _scene);
            Line("角色", _isHost ? "房主" : "客户端");

            if (_isHost)
            {
                // 1) 前置：开局对端真在场。恒假的话"他走了"是平凡成立。
                Check("开局对端在场", _exPlayersBefore >= 2);
                // 2) 【核心】对端到点后必须真的从房主的世界里消失。
                //    旧实现只拆了客户端本地镜像：这一条会停在 2——房主还在替一个
                //    再也不会响应的角色刷怪、算伤害、掉宝石。
                Check("对端到点后已离开", _exPlayersAfter == _exPlayersBefore - 1);
                // 3) 房主自己的世界不许被带停（客户端退房不是房间解散）
                Check("房主世界继续推进", _exWaveBefore >= 1 && _exWaveAfter > _exWaveBefore);
                // 4) 房主自身完好：角色活着、时间没被冻住
                Check("房主自身不受影响", _exLocalAlive && _exScaleHost > 0.0001f);
                Check("全程零异常", _errors == 0);

                Line("断点通过", _ok + "/" + _checked);
                Line("开局对端在场", Yn(_exPlayersBefore >= 2));
                Line("对端到点后已离开", Yn(_exPlayersAfter == _exPlayersBefore - 1));
                Line("房主世界继续推进", Yn(_exWaveBefore >= 1 && _exWaveAfter > _exWaveBefore));
                Line("房主自身不受影响", Yn(_exLocalAlive && _exScaleHost > 0.0001f));
                Line("全程零异常", Yn(_errors == 0));
                Line("开局场上玩家", _exPlayersBefore.ToString());
                Line("对端离开后玩家", _exPlayersAfter.ToString());
                // 键名不能叫"波次"：SmokeTest 收尾也会打一行「波次 N」，
                // 驱动脚本按最后一条取，会把轨迹覆盖成单个数字（M46 踩过同一个坑）。
                Line("波次轨迹", _exWaveBefore + " → " + _exWaveAfter);
                Line("房主 timeScale", _exScaleHost.ToString("F2"));
                Line("首次异常", _firstError);
            }
            else
            {
                Check("青少年模式且对局计时在走", _exMinorOn && _exStartOk);
                Check("到点前弹出一次预警", _exWarnCount == 1 && _exHintAtWarn);
                Check("到点恰好强制退出一次", _exForceCount == 1);
                // 【核心】会话必须真的闭合。旧实现只拆世界：这一条红，而其余全绿——
                // 本地世界确实干净了，只有"还连着"这件事没有本地症状。
                Check("强制退出后已离开会话", !_exNetworkedAfter);
                Check("强制退出后世界已拆", _exWorldGone && _exRunningAfter);
                Check("强制退出后本端玩家已清", _exPlayersAfterSelf == 0);
                Check("强制退出后主菜单可见且有提示",
                      _exMenuVisible && !string.IsNullOrEmpty(_exNotice) &&
                      !_exNotice.StartsWith("playtime."));
                Check("全程零异常", _errors == 0);

                Line("断点通过", _ok + "/" + _checked);
                Line("青少年模式且对局计时在走", Yn(_exMinorOn && _exStartOk));
                Line("到点前弹出一次预警", Yn(_exWarnCount == 1 && _exHintAtWarn));
                Line("到点恰好强制退出一次", Yn(_exForceCount == 1));
                Line("强制退出后已离开会话", Yn(!_exNetworkedAfter));
                Line("强制退出后世界已拆", Yn(_exWorldGone && _exRunningAfter));
                Line("强制退出后本端玩家已清", Yn(_exPlayersAfterSelf == 0));
                Line("强制退出后主菜单可见且有提示",
                     Yn(_exMenuVisible && !string.IsNullOrEmpty(_exNotice) &&
                        !_exNotice.StartsWith("playtime.")));
                Line("全程零异常", Yn(_errors == 0));
                Line("时长轨迹", Mathf.RoundToInt(_exSecAtWarn) + "（预警）→ " +
                                Mathf.RoundToInt(_exSecAtForce) + "（到点）");
                Line("强制退出后会话", _exNetworkedAfter ? "仍连接" : "已断开");
                Line("强制退出后主菜单", _exMenuVisible ? ("可见，提示「" + _exNotice + "」") : "不可见");
                Line("强制退出后玩家", _exPlayersAfterSelf.ToString());
                Line("首次异常", _firstError);
            }

            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        // =====================================================================================
        //  LEAVE：「离开对局」必须真的把人带回大厅
        // =====================================================================================

        /// <summary>
        /// M50 修的那个缺陷（Stop 先退订回调 → 主动离开分支永远走不到）修完之后，
        /// **没有任何测试走过修好的那一半**：EXIT 场景走的是 Stop(false)，
        /// 断言的恰恰是"**不**重建大厅"。所以到今天为止，"重建大厅"这件事一次都没被验证过——
        /// 它可以一直是坏的，而所有判据全绿。
        ///
        /// 客户端当动作方：死 → 结算面板 → 点「离开对局」→ 必须落到大厅且带断线提示。
        /// 房主只旁观，回答"他走了吗"（扫场 2 → 1）。
        /// </summary>
        private void TickLeave()
        {
            float L = _limit;

            if (_isHost)
            {
                if (_lvPlayersBefore < 0 && _t >= L * 0.25f)
                {
                    _lvPlayersBefore = PlayerCount();
                    _lvWaveBefore = Wave();
                }
                if (_lvPlayersAfter < 0 && _t >= L * 0.60f)
                {
                    _lvPlayersAfter = PlayerCount();
                    _lvWaveAfter = Wave();
                    var lp = NetPlayerRegistry.Local;
                    _lvLocalAliveHost = lp != null && (lp.Damageable == null || !lp.Damageable.isDead);
                    _lvScaleHost = Time.timeScale;
                }
            }
            else
            {
                if (!_lvPrepped && _t >= L * 0.20f) { _lvPrepped = true; LeavePrep(); }
                if (!_lvClicked && _t >= L * 0.30f) { _lvClicked = true; LeaveClick(); }
                if (!_lvSampled && _t >= L * 0.40f) { _lvSampled = true; LeaveSample(); }
            }

            if (_t >= L)
            {
                ReportLeave();
                enabled = false;
            }
        }

        private void LeavePrep()
        {
            // 走**真实的**结算入口。NetPlayer 在服务端血量同步到 0 时调的就是
            // EventBus.RaiseGameOver()（NetPlayer.cs 那个分支），HUDController 收到后
            // 才发 OnRunEnded → GameOverPanel.ShowRun 建面板。
            // 探针扮演的是"服务端告诉我我死了"这一步，**不碰面板**——
            // 面板、按钮、回调全由产品代码自己建、自己接。
            Core.EventBus.RaiseGameOver();
            _lvPanelShown = FindLeaveButton() != null;
        }

        private static UnityEngine.UI.Button FindLeaveButton()
        {
            var canvas = GameObject.Find("GameOverCanvas");
            if (canvas == null) return null;
            var t = canvas.transform.Find("Panel/LeaveBtn");
            return t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
        }

        private void LeaveClick()
        {
            var btn = FindLeaveButton();
            _lvBtnFound = btn != null;
            if (btn == null) { _lvHitDetail = "找不到「离开对局」按钮"; return; }
            // 真实点击路径（先 raycast）。这条按钮尤其不能裸 Invoke：
            // 玩家点它的时刻，屏幕上常常还摞着别的东西（结算面板自己、时长预警遮罩）。
            _lvHitOk = UIHitTest.Click(btn, out _lvHitDetail);
        }

        private void LeaveSample()
        {
            _lvUnnetworked = !NetSession.IsNetworked;          // ResetToOffline 之后才是 false
            var lobby = UnityEngine.Object.FindObjectOfType<NetLobby>();
            _lvLobbyBack = lobby != null && lobby.PanelRoot != null && lobby.PanelRoot.activeInHierarchy;
            _lvLobbyStatusText = lobby != null ? lobby.StatusText : "";
            _lvLobbyStatusOk = _lvLobbyStatusText == Core.Localization.T("net.disconnected");
            _lvWorldGone = WorldGone();
            _lvBtnGone = GameObject.Find("GameOverCanvas") == null;
            _lvPlayersNow = PlayerCount();
        }

        private void ReportLeave()
        {
            Debug.Log("[SMOKE] ===== 联机「离开对局」实测结果 =====");
            Line("场景", _scene);
            Line("角色", _isHost ? "房主" : "客户端");

            if (_isHost)
            {
                Check("开局对端在场", _lvPlayersBefore >= 2);
                Check("对端离开后被清", _lvPlayersAfter == _lvPlayersBefore - 1);
                Check("房主世界继续推进", _lvWaveBefore >= 1 && _lvWaveAfter > _lvWaveBefore);
                Check("房主自身不受影响", _lvLocalAliveHost && _lvScaleHost > 0.0001f);
                Check("全程零异常", _errors == 0);

                Line("断点通过", _ok + "/" + _checked);
                Line("开局对端在场", Yn(_lvPlayersBefore >= 2));
                Line("对端离开后被清", Yn(_lvPlayersAfter == _lvPlayersBefore - 1));
                Line("房主世界继续推进", Yn(_lvWaveBefore >= 1 && _lvWaveAfter > _lvWaveBefore));
                Line("房主自身不受影响", Yn(_lvLocalAliveHost && _lvScaleHost > 0.0001f));
                Line("全程零异常", Yn(_errors == 0));
                Line("开局场上玩家", _lvPlayersBefore.ToString());
                Line("对端离开后玩家", _lvPlayersAfter.ToString());
                Line("波次轨迹", _lvWaveBefore + " → " + _lvWaveAfter);
                Line("房主 timeScale", _lvScaleHost.ToString("F2"));
                Line("首次异常", _firstError);
            }
            else
            {
                // 1) 前置：面板真的建出来了。恒假的话下面"按钮点得到"之类都是平凡成立。
                Check("结算面板已建出", _lvPanelShown);
                // 2) 【核心前半】按钮必须真的点得到：M50 的修复让回调能跑到，
                //    但玩家得先能点到它。这两件事是分开的两个缺陷来源。
                Check("「离开对局」按钮点得到", _lvBtnFound && _lvHitOk);
                // 3) 【核心】会话闭合。
                Check("离开后已离开会话", _lvUnnetworked);
                // 4) 【核心后半】大厅重建 + 断线提示。这两条是 M50 修复的
                //    `_returnToLobbyOnStop` 分支，**至今没有任何测试覆盖过**：
                //    旧实现下断线回调根本不会触发，这两条会同时红。
                Check("离开后大厅已重建", _lvLobbyBack);
                Check("离开后大厅有断线提示", _lvLobbyStatusOk);
                Check("离开后世界已拆", _lvWorldGone);
                Check("离开后结算面板已收", _lvBtnGone);
                Check("离开后本端玩家已清", _lvPlayersNow == 0);
                Check("全程零异常", _errors == 0);

                Line("断点通过", _ok + "/" + _checked);
                Line("结算面板已建出", Yn(_lvPanelShown));
                Line("「离开对局」按钮点得到", Yn(_lvBtnFound && _lvHitOk));
                Line("离开后已离开会话", Yn(_lvUnnetworked));
                Line("离开后大厅已重建", Yn(_lvLobbyBack));
                Line("离开后大厅有断线提示", Yn(_lvLobbyStatusOk));
                Line("离开后世界已拆", Yn(_lvWorldGone));
                Line("离开后结算面板已收", Yn(_lvBtnGone));
                Line("离开后本端玩家已清", Yn(_lvPlayersNow == 0));
                Line("全程零异常", Yn(_errors == 0));
                Line("按钮命中详情", _lvHitDetail);
                Line("大厅提示文案", _lvLobbyStatusText);
                Line("离开后玩家", _lvPlayersNow.ToString());
                Line("首次异常", _firstError);
            }

            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        // =====================================================================================
        //  HOSTEXIT：房主自己到点，整间房被带走
        // =====================================================================================

        /// <summary>
        /// M50 测的是"客户端到点"：他走，房主留下来。这一条是它的反面——
        /// **房主到点**，服务端停止，整间房解散。客户端那侧根本不知道这是一次
        /// 主动关闭，它会先被 NetReconnect 抓走（5 次 × 1.5s 全失败）才 GiveUp。
        ///
        /// 所以这一轮真正要回答的问题是：**玩家最后落到哪儿**。
        ///   · 落到联机大厅：对。大厅还能再开一局/换一家；
        ///   · 卡在"正在重连"里出不来：错；
        ///   · 落到一张空白场景：错。
        /// 这三种结局在"没崩、退出码 0"这一层是完全一样的，必须靠落点判据区分。
        /// </summary>
        private void TickHostExit()
        {
            float L = _limit;

            if (_isHost)
            {
                if (!_hxPrepped && _t >= L * 0.20f) { _hxPrepped = true; HostExitPrep(); }
                if (!_hxForced && _t >= L * 0.34f) { _hxForced = true; HostExitForce(); }
                if (!_hxHostSampled && _t >= L * 0.44f) { _hxHostSampled = true; HostExitSample(); }
            }
            else if (_t >= L * 0.34f && !_hxLobbyBack)
            {
                // 客户端只能等，而且**不能单点采样**：重连有 5 次、每次 1.5s，
                // 中途采样会时红时绿——那种判据跑三遍能得三个结论。
                // 这里问的是"最终落点"，所以一旦落到大厅就锁存，之后不再改。
                var lobby = UnityEngine.Object.FindObjectOfType<NetLobby>();
                bool back = lobby != null && lobby.PanelRoot != null && lobby.PanelRoot.activeInHierarchy;
                if (back)
                {
                    _hxLobbyBack = true;
                    _hxLobbyStatusText = lobby.StatusText;
                    _hxLobbyStatusOk = _hxLobbyStatusText == Core.Localization.T("net.disconnected");
                    _hxWorldGone = WorldGone();
                    _hxUnnetworked = !NetSession.IsNetworked;
                }
            }

            if (_t >= L)
            {
                ReportHostExit();
                enabled = false;
            }
        }

        private void HostExitPrep()
        {
            Core.SettingsManager.Instance?.SetMinorMode(true);
            Core.PlaytimeGuard.ResetToday();
            Core.PlaytimeGuard.ResetCounters();

            var g = Core.PlaytimeGuard.Instance;
            _hxStartOk = g != null && Core.PlaytimeGuard.GameRunning;
            _hxMinorOn = Core.SettingsManager.Instance != null &&
                         Core.SettingsManager.Instance.Current.minorMode;

            AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds - 60f);
            if (g != null)
            {
                _hxSecAtWarn = g.TodaySeconds;
                _hxWarnCount = g.WarnCount;
                _hxHintAtWarn = g.HintShowing;
            }
        }

        private void HostExitForce()
        {
            var g = Core.PlaytimeGuard.Instance;
            if (g == null) return;
            // 跨过上限：**真实的** ForceExitMinor（探针不自己拆世界、不自己断连）
            AdvancePlaytimeTo(Core.PlaytimeGuard.MinorDailyLimitSeconds + 60f);
            _hxSecAtForce = g.TodaySeconds;
            _hxForceCount = g.ForceExitCount;
        }

        private void HostExitSample()
        {
            var mm = UI.MainMenu.Instance;
            _hxMenuVisible = mm != null && mm.IsVisible;
            _hxNotice = mm != null ? mm.NoticeText : "";
            _hxUnnetworked = !NetSession.IsNetworked;
            _hxWorldGone = WorldGone();
        }

        private void ReportHostExit()
        {
            // 绝不把青少年模式与已累计时长留在 settings.json / playtime.json 里：
            // 两端共用一份数据目录，不恢复的话下一轮回归一开局就是"已到点"。
            if (_isHost)
            {
                Core.SettingsManager.Instance?.SetMinorMode(false);
                Core.PlaytimeGuard.ResetToday();
            }

            Debug.Log("[SMOKE] ===== 联机房主到点实测结果 =====");
            Line("场景", _scene);
            Line("角色", _isHost ? "房主" : "客户端");

            if (_isHost)
            {
                Check("青少年模式且对局计时在走", _hxMinorOn && _hxStartOk);
                Check("到点前弹出一次预警", _hxWarnCount == 1 && _hxHintAtWarn);
                Check("到点恰好强制退出一次", _hxForceCount == 1);
                Check("强制退出后已离开会话", _hxUnnetworked);
                Check("强制退出后世界已拆", _hxWorldGone);
                Check("强制退出后主菜单可见且有提示",
                      _hxMenuVisible && !string.IsNullOrEmpty(_hxNotice) &&
                      !_hxNotice.StartsWith("playtime."));
                Check("全程零异常", _errors == 0);

                Line("断点通过", _ok + "/" + _checked);
                Line("青少年模式且对局计时在走", Yn(_hxMinorOn && _hxStartOk));
                Line("到点前弹出一次预警", Yn(_hxWarnCount == 1 && _hxHintAtWarn));
                Line("到点恰好强制退出一次", Yn(_hxForceCount == 1));
                Line("强制退出后已离开会话", Yn(_hxUnnetworked));
                Line("强制退出后世界已拆", Yn(_hxWorldGone));
                Line("强制退出后主菜单可见且有提示", Yn(_hxMenuVisible && !string.IsNullOrEmpty(_hxNotice)));
                Line("全程零异常", Yn(_errors == 0));
                Line("时长轨迹", Mathf.RoundToInt(_hxSecAtWarn) + "（预警）→ " +
                                Mathf.RoundToInt(_hxSecAtForce) + "（到点）");
                Line("强制退出后主菜单", _hxMenuVisible ? ("可见，提示「" + _hxNotice + "」") : "不可见");
                Line("首次异常", _firstError);
            }
            else
            {
                Check("房主到点后已离开会话", _hxUnnetworked);
                Check("被带回联机大厅", _hxLobbyBack);
                Check("大厅有断线提示", _hxLobbyStatusOk);
                Check("本地世界已拆", _hxWorldGone);
                Check("全程零异常", _errors == 0);

                Line("断点通过", _ok + "/" + _checked);
                Line("房主到点后已离开会话", Yn(_hxUnnetworked));
                Line("被带回联机大厅", Yn(_hxLobbyBack));
                Line("大厅有断线提示", Yn(_hxLobbyStatusOk));
                Line("本地世界已拆", Yn(_hxWorldGone));
                Line("全程零异常", Yn(_errors == 0));
                Line("大厅提示文案", _hxLobbyStatusText);
                Line("首次异常", _firstError);
            }

            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        /// <summary>按 60 秒一档把当日时长推到目标（线上由 Update 逐帧推进，这里只是压缩时间）。</summary>
        private static void AdvancePlaytimeTo(float target)
        {
            var g = Core.PlaytimeGuard.Instance;
            if (g == null) return;
            float remain = target - g.TodaySeconds;
            if (remain <= 0f) return;
            int steps = Mathf.CeilToInt(remain / 60f);
            for (int i = 0; i < steps; i++) g.Tick(60f);
        }

        private Sample Take()
        {
            var s = new Sample();
            s.game = _game;
            var ws = NetWorldState.Instance;
            s.wave = ws != null ? ws.Wave : -1;
            CountPickups(out s.gems, out s.healths);
            s.ledgerGems = NetWorld.GemCount;
            s.ledgerHealths = NetWorld.HealthCount;
            s.heap = GC.GetTotalMemory(false);
            s.gc0 = GC.CollectionCount(0);
            s.gc1 = GC.CollectionCount(1);
            s.gc2 = GC.CollectionCount(2);
            var lp = NetPlayerRegistry.Local;
            s.level = lp != null ? lp.SyncedLevel : -1;
            s.kills = _kills;
            s.fps = _t > 0f ? _frames / _t : 0f;
            return s;
        }

        // =====================================================================================

        private void Report()
        {
            if (_samples.Count < 2)
            {
                Debug.Log("[SMOKE] 联机长局采样不足（" + _samples.Count + " 段），无法判定");
                return;
            }

            var first = _samples[0];
            var last = _samples[_samples.Count - 1];

            float gemEarly = 0f, gemLate = 0f;
            int half = _samples.Count / 2;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (i < half) { gemEarly += _samples[i].gems; _heapEarly += _samples[i].heap; }
                else { gemLate += _samples[i].gems; _heapLate += _samples[i].heap; }
            }
            gemEarly /= Mathf.Max(1, half);
            gemLate /= Mathf.Max(1, _samples.Count - half);
            _heapEarly /= Mathf.Max(1, half);
            _heapLate /= Mathf.Max(1, _samples.Count - half);

            int peakGems = 0, peakHealth = 0, maxLedgerSkew = 0, maxLedgerTol = 6;
            bool valueOk = true;
            foreach (var s in _samples)
            {
                if (s.gems > peakGems) peakGems = s.gems;
                if (s.healths > peakHealth) peakHealth = s.healths;
                // 账本与场景的差：有生成路径绕过账本时它会被拉开（那种写法下上限形同虚设）
                int skew = Mathf.Abs(s.gems - s.ledgerGems) + Mathf.Abs(s.healths - s.ledgerHealths);
                if (skew > maxLedgerSkew) maxLedgerSkew = skew;
                // 容差按**当时场上的规模**取，不取死数：残差来自"已决定回收、反生成还没处理完"
                // 的那几个对象，它随回收速率缩放。M42 区分力注入 R4 第一次跑时绝对值阈值 4
                // 把一次 8 的正常残差判成了"账本与场景不一致"——而真正的绕账本生成会是几百量级，
                // 按比例取阈值既不会误报，也不会放过。
                int tol = Mathf.Max(6, (int)((s.gems + s.healths) * 0.10f));
                if (tol > maxLedgerTol) maxLedgerTol = tol;
                if (s.wave < 1 || s.kills < 0 || s.heap <= 0) valueOk = false;
            }

            _finalWave = last.wave;
            _peakGems = peakGems;
            _peakHealth = peakHealth;
            _gemEarly = gemEarly;
            _gemLate = gemLate;
            _ledgerGems = last.ledgerGems;
            _ledgerHealths = last.ledgerHealths;
            _gemTrimmed = NetWorld.GemTrimmed;
            _healthTrimmed = NetWorld.HealthTrimmed;

            // 1) 波次确实推进：加速与权威世界演化都没停摆（否则下面所有增长判据都是假绿）
            _waveOk = _finalWave >= 12;
            Check("波次确实推进", _waveOk);

            // 2)/3) 两类各有界。回血球单独再断言一次——两类共用"只进不出"的写法，
            //       只修一半（修了宝石忘了回血球）是最容易发生的漏修。
            _gemBoundOk = peakGems <= PickupTrim.MaxGems + 20;
            Check("宝石有界", _gemBoundOk);
            _healthOk = peakHealth <= PickupTrim.MaxHealth + 10;
            Check("回血球有界", _healthOk);

            // 4) 上限真的生效：回收计数 > 0，证明上限真的在动手。
            //
            //    三态，两个"不适用"的口子，缺一个它就会撒谎：
            //      · **客户端不适用**：账本与回收都是房主权威行为，客户端从不生成也从不回收，
            //        它的计数器恒为 0——在客户端断言只会得到结构性失败，
            //        那是判据写错了，不是被测对象错了。
            //      · **没贴到顶就不适用**：世界压根没堆到上限附近时（掉率本来就低 / 世界没跑起来），
            //        回收从未发生、计数自然是 0——这报的是"世界太小"，
            //        不是"上限没生效"。M42 区分力注入 R1 第一次跑就是这么撞出来的：
            //        摘掉回收调用后它和"宝石有界"一起红，把两条判据的失败面粘成了一坨。
            //    真正该红的只有一种情形：宝石确实堆到了上限附近，却一次都没回收。
            float cap = PickupTrim.MaxGems;
            bool trimAssertable = _isHost && peakGems >= cap * 0.8f;
            if (!trimAssertable)
            {
                _trimOk = true;                       // 不适用
                _trimText = "不适用";
            }
            else
            {
                _trimOk = _gemTrimmed > 0;
                _trimText = Yn(_trimOk);
            }
            Check("上限真的生效", _trimOk);

            // 5) 账本与场景一致：防止有生成路径绕过账本（那种写法下上限形同虚设，
            //    而"宝石有界"仍可能因世界没跑满而绿着）。同上，只在房主侧成立——
            //    客户端的账本从来是空的，场景却有 240 个同步来的实体，差值是必然的。
            if (!_isHost)
            {
                _ledgerOk = true;
                _ledgerText = "不适用";
            }
            else
            {
                _ledgerOk = maxLedgerSkew <= maxLedgerTol;
                _ledgerText = Yn(_ledgerOk);
            }
            Check("账本与场景一致", _ledgerOk);

            // 6) 末段贴顶后不再抬升。
            //
            //    为什么不能用"后半程均值 vs 前半程均值"：开局宝石本来就要从 0 爬到上限，
            //    短局里这段爬坡会占掉大半时长，均值比必然超标——那报的是"世界刚起步"，
            //    不是"物品在堆积"。M41 单机侧没露出来，只因 300s/6x 下爬坡只占前两三段。
            //
            //    改成只问一件事：**够到顶之后，最后一段还在不在继续涨**。
            //      · 有上限：… 241, 241, 241 → 末段增量 ≈ 0
            //      · 无上限：… 480, 700, 887 → 末段增量 187
            //    三态：还没贴到顶（末段 < 上限的 80%）时无从区分"健康的爬坡"与"无上限的爬坡"，
            //    判"不适用"——判"通过"会掩盖问题，判"失败"会误报。
            int lastGems = last.gems;
            int prevGems = _samples.Count >= 2 ? _samples[_samples.Count - 2].gems : lastGems;
            // M44：两段都必须**贴死**上限（≥95%）才断言，不能只看末段。
            //
            // 为什么：宝石数不是钉在上限不动的——玩家吸取快时它会从 240 掉到 190 再慢慢爬回来，
            // 贴顶区内的这种摆动是健康世界的正常呼吸。旧写法只要求末段 ≥80%，
            // 于是把"196 → 239"这一段的正常爬坡判成了"无上限堆积"（M44 全量回归时撞到）。
            // 判据要问的是"够到顶之后还在不在涨"，前提是**两段都已经够到顶**。
            bool saturated = lastGems >= cap * 0.95f && prevGems >= cap * 0.95f;
            if (_finalWave < 12 || !saturated)
            {
                _growOk = true;                       // 不适用
                _growText = "不适用";
            }
            else
            {
                _growOk = (lastGems - prevGems) <= cap * 0.1f;
                _growText = Yn(_growOk);
            }
            Check("末段贴顶后不再抬升", _growOk);

            // 7) 玩家遍历不随掉落物数量增长（M43）。
            //
            //    为什么单独判：M43 把"每个掉落物各自遍历玩家注册表"改成"每帧重建一份快照"。
            //    这个改动**不会**改变任何一条可见行为——掉落物照样被吸、照样被捡，
            //    所有玩法判据在改前改后都一样绿。它只影响成本，而成本没人看着就会退回去：
            //    下一个人写新掉落物/新宠物/新召唤物时顺手调一次 NearestAlive 就白改了。
            //
            //    判据用"快照重建次数 vs 帧数"：正确实现下每帧最多 1 次（比值 ≈1）；
            //    退回逐对象遍历时，重建次数会变成帧数 × 场上掉落物数（240 倍量级），
            //    任何容差都盖不住。三态：世界没堆起来 / 客户端不跑掉落物模拟时"不适用"。
            int builds = NetWorld.PlayerSnapshotBuilds;
            bool snapAssertable = _isHost && peakGems >= 100 && _frames > 120;
            if (!snapAssertable)
            {
                _snapOk = true;
                _snapText = "不适用";
            }
            else
            {
                _snapOk = builds <= _frames * 1.5f;
                _snapText = Yn(_snapOk);
            }
            Check("玩家遍历不随掉落物增长", _snapOk);

            // 8) 长程数值不越界 + 全程零异常
            _valueOk = valueOk;
            Check("长程数值不越界", _valueOk);
            Check("全程零异常", _errors == 0);

            _naCount = 0;
            foreach (var t in new[] { _growText, _trimText, _ledgerText, _snapText })
                if (t == "不适用") _naCount++;

            Debug.Log("[SMOKE] ===== 联机长局稳定性实测结果 =====");
            Line("角色", _isHost ? "房主" : "客户端");
            Line("断点通过", _ok + "/" + _checked + "（其中不适用 " + _naCount + "）");
            Line("波次确实推进", Yn(_waveOk));
            Line("宝石有界", Yn(_gemBoundOk));
            Line("回血球有界", Yn(_healthOk));
            Line("上限真的生效", _trimText);
            Line("账本与场景一致", _ledgerText);
            Line("末段贴顶后不再抬升", _growText);
            Line("玩家遍历不随掉落物增长", _snapText);
            Line("长程数值不越界", Yn(_valueOk));
            Line("全程零异常", Yn(_errors == 0));
            Line("游戏内时长", last.game.ToString("F0") + "s");
            Line("最终波次", _finalWave.ToString());
            Line("最终等级", last.level.ToString());
            Line("本地击杀", last.kills.ToString());
            Line("宝石峰值", _gemEarly.ToString("F0") + " → " + _gemLate.ToString("F0") +
                            "（峰 " + peakGems + "）");
            Line("回血球峰值", peakHealth.ToString());
            Line("末段宝石", prevGems + " → " + lastGems + "（上限 " + cap + "）");
            Line("末次账本", _ledgerGems + " 宝石 / " + _ledgerHealths + " 回血球");
            Line("账本场景最大差", maxLedgerSkew + "（容差 " + maxLedgerTol + "）");
            Line("回收次数", _gemTrimmed + " 宝石 / " + _healthTrimmed + " 回血球");
            Line("托管堆MB", (_heapEarly / 1048576f).ToString("F1") + " → " +
                             (_heapLate / 1048576f).ToString("F1"));
            Line("GC次数", "gen0=" + last.gc0 + " gen1=" + last.gc1 + " gen2=" + last.gc2);
            Line("平均帧率", last.fps.ToString("F0"));
            Line("快照重建/帧数", NetWorld.PlayerSnapshotBuilds + " / " + _frames);
            // M45：多人局里"每端看到几个人"是唯一的成色证据——4 人局每端都该是 4。
            // 少一个说明有人没连上或被踢了，而其余判据（掉落物有界 / 波次推进）
            // 在少人的世界里照样全绿，是典型的假通过。
            Line("场上玩家", PlayerCount() + "（注册表 " + NetPlayerRegistry.Count + "）");
            Line("首个异常", _firstError);
            Line("结论", (_errors == 0 && _fail.Count == 0 && _ok == _checked) ? "通过" : "不通过");
            foreach (var f in _fail) Line("失败项", f);
        }

        // =====================================================================================
        //  计数：场景扫描（不读账本），只数场景里的对象
        // =====================================================================================

        private static bool IsTemplate(Component c) =>
            c != null && c.gameObject.name.EndsWith("Template");

        /// <summary>
        /// 按种类数场上掉落物。两条过滤缺一不可：
        ///   · 预制体资产上的 NetPickup 也会被 FindObjectsOfTypeAll 返回（NetConfig 持引用 → 已加载），
        ///     不按 scene 过滤会把资产算成场上实体；
        ///   · "*Template" 命名兜底（与 M41 探针同一口径）。
        /// </summary>
        private static void CountPickups(out int gems, out int healths)
        {
            gems = 0;
            healths = 0;
            foreach (var p in Resources.FindObjectsOfTypeAll<NetPickup>())
            {
                if (p == null) continue;
                if (!p.gameObject.scene.IsValid()) continue;   // 预制体/资产，不是场上实体
                if (IsTemplate(p)) continue;
                // M43：Fish-Net 的 Despawn 默认是**回池**（对象被停用后留在场景里复用），
                // 只判 null 会把"已反生成"的对象当成"还在场上"——拆完瞬间的残留判据因此
                // 把 0 误报成 200 多。场上 = 激活中的对象，这一判据跟渲染/碰撞的口径一致。
                if (!p.gameObject.activeInHierarchy) continue;
                if (p.Kind == NetPickupKind.Health) healths++;
                else gems++;
            }
        }

        // =====================================================================================
        //  RESTART 用的几个快照（同样走场景扫描，不读被测对象自己的账本）
        // =====================================================================================

        private static int Wave()
        {
            var ws = NetWorldState.Instance;
            return ws != null ? ws.Wave : -1;
        }

        /// <summary>
        /// M51：本端世界是否已拆——**刻意不靠 EnemyManager.Instance 判**。
        ///
        /// 联机世界里 EnemyManager 只建在房主侧（<c>BuildNetworked</c> 的 IsAuthority 分支），
        /// 客户端从头到尾就没它，于是"客户端世界已拆"这条判据**恒绿**：
        /// 哪怕本地世界一点没拆、管理器都还活着，`Instance == null` 依然是 true。
        /// 这正是 M50 §5 记过的那种"永远绿的假判据"——注入组里它一次没红过，
        /// 就是它没在看着任何东西的证据（Q2 期望 5 红、只来 3 红，缺的就是它）。
        ///
        /// 改看 PoolManager：两条建世界路径都是无条件建它（单机 <c>Build</c>、
        /// 联机 <c>BuildNetworked</c>，后者还是 M18 修"联机下没有任何对象池"时补的），
        /// 而 <c>TeardownWorld</c> 会当帧把 Instance 同步清掉（见那里 ClearSingletons 的注释）。
        /// 于是它能把"真的拆了"和"只是 FishNet 把网络对象反生成了"区分开——
        /// 后者正是"点了离开、连是断了、本地残骸全是原样"的形态。
        /// </summary>
        private static bool WorldGone()
        {
            return Core.PoolManager.Instance == null;
        }

        private static int GemCountNow(out int healths)
        {
            int gems;
            CountPickups(out gems, out healths);
            return gems;
        }

        private static int EnemyCount()
        {
            int n = 0;
            foreach (var e in Resources.FindObjectsOfTypeAll<NetEnemy>())
            {
                if (e == null) continue;
                if (!e.gameObject.scene.IsValid()) continue;
                if (IsTemplate(e)) continue;
                if (!e.gameObject.activeInHierarchy) continue;   // 已反生成（回池停用）不算场上
                n++;
            }
            return n;
        }

        /// <summary>
        /// M44：场上网络弹丸数（含探针放的哨兵）。同样是场景扫描 + 三重过滤，
        /// 不读任何账本——弹丸恰恰是**没有账本**的那类实体，这也是它被漏掉的原因。
        /// </summary>
        private static int ProjectileCount()
        {
            int n = 0;
            foreach (var p in Resources.FindObjectsOfTypeAll<NetProjectile>())
            {
                if (p == null) continue;
                if (!p.gameObject.scene.IsValid()) continue;
                if (IsTemplate(p)) continue;
                if (!p.gameObject.activeInHierarchy) continue;   // 已反生成（回池停用）不算在飞
                n++;
            }
            return n;
        }

        /// <summary>
        /// M45：场上网络玩家数（扫场口径，不读 NetPlayerRegistry.Count）。
        ///
        /// 与其余四类同一套三重过滤。玩家跨局存活，所以这个数在重开前后**必须不变**——
        /// 它既是"拆世界有没有误清玩家"的判据，也是多人局是否真的凑齐人的证据
        /// （4 人局里每端都该看到 4 个，少一个就是有人没连上或被踢了）。
        /// </summary>
        private static int PlayerCount()
        {
            int n = 0;
            foreach (var p in Resources.FindObjectsOfTypeAll<NetPlayer>())
            {
                if (p == null) continue;
                if (!p.gameObject.scene.IsValid()) continue;
                if (IsTemplate(p)) continue;
                if (!p.gameObject.activeInHierarchy) continue;   // 已反生成（回池停用）不算在场
                n++;
            }
            return n;
        }

        /// <summary>场上世界状态对象个数。重开后必须恰好 1 个——两个就会互相抢 Instance，波次来回跳。</summary>
        private static int WorldStateCount()
        {
            int n = 0;
            foreach (var w in Resources.FindObjectsOfTypeAll<NetWorldState>())
            {
                if (w == null) continue;
                if (!w.gameObject.scene.IsValid()) continue;
                if (IsTemplate(w)) continue;
                if (!w.gameObject.activeInHierarchy) continue;
                n++;
            }
            return n;
        }

        /// <summary>临时诊断：残留敌人的名字与网络状态（确认是"没登记"还是"IsSpawned 未就绪"）。</summary>
        private static string EnemyLeakDetail()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("注册表=").Append(NetEnemyRegistry.Count).Append(" | ");
            foreach (var e in Resources.FindObjectsOfTypeAll<NetEnemy>())
            {
                if (e == null || !e.gameObject.scene.IsValid()) continue;
                if (!e.gameObject.activeInHierarchy) continue;
                var no = e.GetComponent<FishNet.Object.NetworkObject>();
                sb.Append(e.name).Append("(spawned=").Append(no != null && no.IsSpawned ? 1 : 0).Append(") ");
            }
            return sb.ToString();
        }

        private static int RootCount()
        {
            int n = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
                if (tr != null && tr.parent == null && !IsTemplate(tr)) n++;
            return n;
        }

        // =====================================================================================

        private void Check(string name, bool ok)
        {
            _checked++;
            if (ok) _ok++;
            else _fail.Add(name);
        }

        private void Line(string k, string v) => Debug.Log("[SMOKE] " + k + "              " + v);

        private static string Yn(bool b) => b ? "是" : "否";
    }
}
#endif
