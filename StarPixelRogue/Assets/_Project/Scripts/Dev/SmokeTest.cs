using StarPixel.Core;
using StarPixel.Enemy;
using StarPixel.Player;
using StarPixel.UI;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// 无头冒烟实测（M17）：仅当进程带 -smokeTest 命令行参数时，由 <c>GameBuilder</c> 创建。
    ///
    /// 存在的理由：本项目全部内容由 <c>GameBuilder</c> 在运行时构建，正常启动要先过合规门、
    /// 再由人工点主菜单才能开局——无人值守环境下根本跑不起来。本组件让"真实跑一局"变成一条命令。
    ///
    /// 行为：
    /// - 经 <see cref="PlayerInput.Override"/> 注入「远离最近敌人」的自动驾驶（不改动任何玩法代码）；
    /// - 采集关键运行状态：击杀数 / 波次 / 等级 / 是否遇到 Boss / 存活时长 / 是否阵亡；
    /// - 到时或玩家阵亡后以 [SMOKE] 前缀输出结论，并 Application.Quit(0=通过 / 1=失败)。
    ///
    /// 正常构建不带该参数时，本组件不会被创建，对游戏零影响。
    /// </summary>
    public class SmokeTest : MonoBehaviour
    {
        private float _limit = 60f;
        private float _t;
        private int _kills;
        private int _maxLevel = 1;
        private bool _bossSeen;
        private bool _died;
        private float _deathAt = -1f;
        private bool _done;
        private int _cardsPicked;

        /// <summary>
        /// M37：外部供选（联机推卡）的收支。
        /// 之前只统计「点了几次卡」，而统计方式本身是坏的——见 Update 里那条注释。
        /// 光有总数看不出丢没丢，必须把「收到 / 上屏 / 入队 / 丢弃」拆开记：
        /// 收到 ≠ 上屏 的差额就是被吞掉的强化。
        /// </summary>
        private int _extReceived;
        private int _extPresented;
        private int _extQueued;
        private int _extDropped;
        private int _extPending;
        private int _hostOffered;    // 房主侧：为远程玩家推过几批（对端自述的对照）

        /// <summary>
        /// M38：本地镜像 vs 房主权威。两边必须**逐次相等**——不相等就是双端分叉：
        /// 客户端自己先加了成、房主没加（或反过来），而这类分叉不报错、不崩，
        /// 表现出来只是"客户端好像跑得快一点"。
        /// </summary>
        private int _hostApplied;    // 房主权威应用成功次数
        private int _hostRejected;   // 房主拒绝次数
        private int _localApplied;   // 客户端本地镜像真正应用次数（只在收到确认后）
        private int _ackPending;     // 还悬着没等到回执的
        private int _ackRejected;    // 被房主拒的
        private int _ackInvalidated; // 因批次被淘汰而作废的
        private int _ackTimedOut;    // 等回执等到超时的
        private int _localApplyTotal;// 本机**实际**应用过几次（含未在确认路径上的应用）
        // M39：收场通知——四种收场都必须走到玩家眼前
        private int _ntTotal;        // 发生过几次收场
        private int _ntShown;        // 真的上屏了几条
        private int _ntSuppressed;   // 有通知但没出口的（必须为 0）
        private int _extInvalidated; // 面板侧收到作废通知的次数
        private int _hostEvicted;    // 房主侧：被上限淘汰的批次

        /// <summary>
        /// M37：收尾**前** 1 秒的采样。
        ///
        /// 为什么不能只看收尾瞬时值：两个进程的结束时刻是对齐到同一秒的死热，
        /// 谁先退出谁就把对方的注册表打到 1——实测同一份源码跑两次，
        /// 一次是「房主 2 / 客户端 2」，一次是「房主 1 / 客户端 2」。
        /// 那是**测试台的退出竞态**，不是产品属性：用它当判据会把成功的联机判成失败，
        /// 反过来又逼着判据放水。收尾前 1 秒两端必然都还活着，取那一刻才是稳的。
        /// 瞬时值仍然输出（供诊断），只是不再当判据。
        /// </summary>
        private bool _endSampled;
        private int _endPlayers = -1;

        /// <summary>
        /// M18：联机双开模式。单机冒烟里「玩家阵亡即结束」是对的（要采集存活时长），
        /// 但联机下若房主 6 秒就被围死退出，另一端根本来不及连上（本次实测正是如此：
        /// 房主 6.3s 退出，客户端 10s 才启动，全程无服务端可连）。故联机模式改为：
        /// 阵亡只记录、不终止，并持续保活，让两端都能跑满预定时长再做交叉核对。
        /// </summary>
        private bool _netMode;

        /// <summary>
        /// M18：联机期间见过的最大玩家数。
        /// 为什么不能只看收尾瞬时值：双开时两端时长不同步（客户端 50s、房主 60s），
        /// 短的那端会先退出并断线，房主收尾时注册表已掉回 1——实测里就出现过
        /// 「客户端看到 2、房主看到 1」的假失败。峰值才是"客户端是否成功加入"的判据。
        /// </summary>
        private int _maxPlayers;

        /// <summary>
        /// M18 / M11 断线重连：>0 表示本机跑到该秒数时主动模拟一次「意外掉线」，
        /// 随后验证 M11 自动重连是否把波次与玩家进度接回。0 = 不触发。
        /// </summary>
        private float _dropAt;
        private bool _dropped;
        private float _dropTime;
        private bool _reconnected;
        private float _reconnectTime;
        private float _postSampleAt = -1f;
        private int _preWave = -1, _preLevel = -1, _preKills = -1;
        private int _postWave = -1, _postLevel = -1, _postKills = -1;

        /// <summary>
        /// M11：掉线**期间**的采样（重连就绪之前）。
        /// 这是区分房主分支与客户端分支的关键证据——房主掉线时权威世界保留不拆，
        /// 断线期间场上敌人/波次应当照常存在并推进；客户端掉线时本地镜像已被拆掉，两者都会归零。
        /// </summary>
        private bool _midSampled;
        private int _midWave = -1, _midEnemies = -1, _midKills = -1;

        /// <summary>
        /// M11：掉线期间「权威世界管理器」是否仍存活。
        /// 为什么不能用「场上敌人」判断世界是否保留：房主本地客户端断开时，Fish-Net 会清空
        /// **本地网络视图**（NetEnemy 对象被销毁），AliveEnemyCount 因此归零——但服务端仍在跑、
        /// 权威世界并未被拆。真正能证明"没调用 TeardownWorld"的是 EnemyManager 是否还在：
        /// TeardownWorld 会销毁并清空它，而房主分支本就不该拆世界。
        /// </summary>
        private bool _midWorldAlive;

        /// <summary>M18 / M14 观战：本端是否以观战身份加入（不生成可操作角色）。</summary>
        private bool _spectator;
        private bool _ownerAliveNow;

        /// <summary>
        /// M18 / M11：房主视角下是否观测到「玩家数 2 → 1 → 2」。
        /// 这是房主侧对"对端掉线又重连"的直接证据——不依赖对端自述。
        /// </summary>
        private bool _peerDropSeen;
        private bool _peerRejoinSeen;

        /// <summary>由 GameBuilder 调用，开始一轮实测。</summary>
        public static void Start(float seconds)
        {
            var go = new GameObject("SmokeTest");
            var st = go.AddComponent<SmokeTest>();
            st._limit = Mathf.Max(5f, seconds);
            st.Subscribe();
            Debug.Log("[SMOKE] 冒烟实测启动，时长上限 " + st._limit + "s");
        }

        /// <summary>
        /// M18：联机双开实测入口。与 <see cref="Start"/> 的区别是阵亡不终止 + 本地玩家保活，
        /// 保证房主能撑满时长等待客户端加入（详见 <see cref="_netMode"/> 注释）。
        /// </summary>
        /// <param name="dropAt">M11：>0 时在该秒数模拟一次意外掉线并验证自动重连。</param>
        public static void StartNet(float seconds, float dropAt = 0f)
        {
            var go = new GameObject("SmokeTest");
            var st = go.AddComponent<SmokeTest>();
            st._limit = Mathf.Max(5f, seconds);
            st._netMode = true;
            st._dropAt = dropAt;
            st._spectator = StarPixel.Net.NetSession.Spectator;
            st.Subscribe();
            st.SubscribeNet();
            Debug.Log("[SMOKE] 冒烟实测启动（联机" + (st._spectator ? "·观战" : "") +
                      (dropAt > 0f ? "·掉线@" + dropAt + "s" : "") + "），时长上限 " + st._limit + "s");
        }

        /// <summary>M11：订阅后端 Ready，用于判定「重连是否已就绪」。</summary>
        private void SubscribeNet()
        {
#if FISHNET_ENABLED
            var backend = StarPixel.Net.NetBackend.Instance;
            if (backend != null) backend.Ready += OnBackendReady;
#endif
        }

        private void UnsubscribeNet()
        {
#if FISHNET_ENABLED
            var backend = StarPixel.Net.NetBackend.Instance;
            if (backend != null) backend.Ready -= OnBackendReady;
#endif
        }

        /// <summary>
        /// M11：掉线后再次收到 Ready 即视为重连成功。
        /// 注意此刻 <c>NetReconnect.OnReconnectReady</c> 的 Build() 可能还没跑完、玩家对象也还没重新生成，
        /// 因此不在这里采样，只记时刻，实际采样延后 2s（见 Update 中的 _postSampleAt）。
        /// </summary>
        private void OnBackendReady()
        {
            if (!_dropped || _reconnected) return;
            _reconnected = true;
            _reconnectTime = _t;
            _postSampleAt = _t + 2.5f;
            Debug.Log("[SMOKE] 重连就绪  " + _t.ToString("F1") + "s（掉线持续 " +
                      (_t - _dropTime).ToString("F1") + "s）");
        }

        /// <summary>
        /// M18：连接始终未就绪时也要产出一份完整的 [SMOKE] 报告。
        /// 为什么需要：此前客户端连不上房主时 backend.Ready 永不触发，既不建世界也不打印，
        /// 只能空转到超时，net_smoke.py 拿不到任何指标、只能报「无法核对」，
        /// 而空转期间又把每帧告警刷成 200MB 日志。这里改为直接输出全零报告并退出，
        /// 让脚本能明确判定「未连上」而不是「结果缺失」。
        /// </summary>
        public static void ReportUnready(string reason)
        {
            var go = new GameObject("SmokeTest");
            var st = go.AddComponent<SmokeTest>();
            st._netMode = true;
            st.Subscribe();
            Debug.Log("[SMOKE] 冒烟实测启动（联机），时长上限 0s");
            Debug.LogError("[SMOKE] 连接未就绪：" + reason);
            st.Finish();
        }

        private void Subscribe()
        {
            EventBus.OnEnemyKilled += _ => _kills++;
            EventBus.OnLevelUp += lv => { if (lv > _maxLevel) _maxLevel = lv; };
            EventBus.OnBossSpawned += (_, __) => _bossSeen = true;
            EventBus.OnGameOver += () =>
            {
                if (_died) return;
                _died = true;
                _deathAt = _t;
                Debug.Log("[SMOKE] 玩家阵亡，存活 " + _t.ToString("F1") + "s");
            };
        }

        void OnDestroy()
        {
            PlayerInput.Override = null;
            UnsubscribeNet();
        }

        void Update()
        {
            if (_done) return;
            _t += Time.unscaledDeltaTime;   // 选卡暂停时时间缩放为 0，用未缩放时间才不会停摆

            // M11：到点模拟一次意外掉线（走真实自动重连路径，不走 Stop() 的"主动离开"分支）
            if (_dropAt > 0f && !_dropped && _t >= _dropAt)
            {
                _dropped = true;
                _dropTime = _t;
                _preWave = SyncedWave();
                _preLevel = AuthoritativeLevel();
                _preKills = SyncedKills();
#if FISHNET_ENABLED
                // 房主与客户端都走同一入口，分支由后端按 _intentHost 区分（见 GameBuilder 的注释）
                (StarPixel.Net.NetBackend.Instance as StarPixel.Net.FishNetBackend)?.SimulateDrop();
#endif
                Debug.Log("[SMOKE] 模拟掉线  " + _t.ToString("F1") + "s（波次 " + _preWave +
                          " / 等级 " + _preLevel + " / 击杀 " + _preKills + "）");
            }

            // M11：掉线 1s 后、且尚未重连时采样一次，用于判断「掉线期间世界是否保留」
            if (_dropped && !_reconnected && !_midSampled && _t >= _dropTime + 1.0f)
            {
                _midSampled = true;
                _midWave = SyncedWave();
                _midEnemies = StarPixel.Combat.CombatTargets.AliveEnemyCount;
                _midKills = SyncedKills();
                _midWorldAlive = EnemyManager.Instance != null;
                Debug.Log("[SMOKE] 掉线期间采样  波次 " + _midWave + " / 场上敌人 " + _midEnemies +
                          " / 击杀 " + _midKills + " / 权威世界存活=" + (_midWorldAlive ? "是" : "否"));
            }

            // M11：重连就绪 2s 后再采样——此时世界已重建、玩家对象已重新生成并回放完进度
            if (_reconnected && _postWave < 0 && _t >= _postSampleAt)
            {
                _postWave = SyncedWave();
                _postLevel = AuthoritativeLevel();
                _postKills = SyncedKills();
                Debug.Log("[SMOKE] 重连后采样  波次 " + _postWave + " / 等级 " + _postLevel +
                          " / 击杀 " + _postKills);
            }

            // M17：升级选卡会把 timeScale 归零等待玩家点击，无头环境无人点击将永远卡住——代玩家选第一张。
            //
            // M37：这里原本写的是 `if (Time.timeScale <= 0f)`——用"游戏被暂停"来推断"正在选卡"。
            // 而外部供选（联机推卡）是**刻意不暂停**的（PresentExternal 传 pause=false，
            // 免得冻结网络心跳），于是客户端的选卡一次都统计不到：实测 60s 双开
            // 「房主选卡 5 次 / 客户端 0 次」，看起来像是客户端没升级，其实是探针瞎了。
            // 真正的缺陷（面板占用期间推来的卡被静默丢弃）就藏在这个盲区里，藏了很久。
            // 改法：别猜，直接问面板自己——AutoChooseFirst 未处于选卡态时返回 false。
            if (!_died)
            {
                var lum = LevelUpManager.Instance;
                if (lum != null && lum.AutoChooseFirst()) _cardsPicked++;
            }

            DrivePlayer();

            var pc = PlayerController.Instance;
            if (pc != null && pc.level > _maxLevel) _maxLevel = pc.level;

            // M18：联机模式保活。房主一旦阵亡，敌人不再被击杀、波次停止推进，
            // 两端同步数值会同时冻结，交叉核对就失去意义——故持续把血补满。
            if (_netMode && pc != null && pc.damageable != null && pc.damageable.maxHp > 0f)
            {
                if (pc.damageable.currentHp < pc.damageable.maxHp * 0.6f)
                    pc.Heal(pc.damageable.maxHp);
            }
#if FISHNET_ENABLED
            // 房主权威侧顺便给所有玩家保活：血量由房主结算，客户端自己补血无效，
            // 放任不管的话客户端角色会在中途阵亡（实测 103.9s 阵亡），后续同步样本失真。
            if (_netMode && StarPixel.Net.NetSession.IsHost)
            {
                StarPixel.Net.NetPlayerRegistry.ForEachAlive(p =>
                {
                    var d = p != null ? p.Damageable : null;
                    var c = p != null ? p.Controller : null;
                    if (d == null || c == null || d.maxHp <= 0f) return;
                    if (d.currentHp < d.maxHp * 0.6f) c.Heal(d.maxHp);
                });
            }
#endif

#if FISHNET_ENABLED
            // 峰值玩家数：收尾瞬时值会因对端先退出而失真，详见 _maxPlayers 注释
            int players = StarPixel.Net.NetPlayerRegistry.Count;
            if (players > _maxPlayers) _maxPlayers = players;

            // M11：房主视角的「2 → 1 → 2」观测（对端掉线又重连的直接证据）
            if (_maxPlayers >= 2 && players < 2) _peerDropSeen = true;
            else if (_peerDropSeen && players >= 2) _peerRejoinSeen = true;

            // M37：收尾前 1 秒采一次（详见 _endPlayers 注释）。
            // 房主的「推了几批」也必须在这里采——它同样依赖对端玩家对象还在注册表里，
            // 收尾瞬时采会拿到 0（对端已退出），与客户端自述的「收到 4」对不上。
            if (!_endSampled && _t >= _limit - 1.0f)
            {
                _endSampled = true;
                _endPlayers = StarPixel.Net.NetPlayerRegistry.Count;
                StarPixel.Net.NetPlayerRegistry.ForEachAlive(p =>
                {
                    if (p == null) return;
                    _hostOffered += p.CardsOffered;
                    _hostEvicted += p.CardsEvicted;
                    // M38：房主权威应用/拒绝——与客户端自述的「本地已应用」对账
                    _hostApplied += p.CardsApplied;
                    _hostRejected += p.CardsRejected;
                });
                // M38：客户端自述。同样必须**同刻**采——收尾瞬时采时房主的玩家对象
                // 可能已随对端退出而被回收，两侧又对不上了（M36/M37 已经栽过两次）。
                var self = StarPixel.Net.NetPlayer.Local;
                if (self != null)
                {
                    _localApplied = self.LocalAppliedCount;
                    // 这一条是**实际**应用次数（ApplyKind/ApplyStat 全算，含闭包路径）。
                    // 只有把它和上面的"确认次数"分开统计，才抓得住"确认之外又加了一次"的先斩后奏。
                    _localApplyTotal = StarPixel.Combat.UpgradeCatalog.ApplyCount;
                    _ackPending = self.AckPendingCount;
                    _ackRejected = self.AckRejectedCount;
                    _ackInvalidated = self.AckInvalidatedCount;
                    _ackTimedOut = self.AckTimedOutCount;
                }
                // M39：四种收场（确认/拒绝/作废/超时）都必须有玩家看得见的出口。
                // 通知是全局的（属于玩家，不属于某个 NetPlayer），故不依赖 self。
                _ntTotal = StarPixel.Combat.UpgradeNotice.Total;
                _ntShown = StarPixel.Combat.UpgradeNotice.ShownCount;
                _ntSuppressed = StarPixel.Combat.UpgradeNotice.SuppressedCount;
            }

            // M14：观战者是否仍持有一个属于自己的（IsOwner）玩家对象。
            // 观战链路正确时，房主应在收到 RequestSpectatorServerRpc 后把该对象反生成，这里应为 false。
            if (_spectator)
            {
                bool ownerAlive = false;
                StarPixel.Net.NetPlayerRegistry.ForEachAlive(p =>
                {
                    if (p != null && p.IsLocalOwned) ownerAlive = true;
                });
                _ownerAliveNow = ownerAlive;
            }
#endif

            // 联机模式下阵亡只记录不终止（否则房主早退会让客户端永远连不上）
            if (_t >= _limit || (_died && !_netMode)) Finish();
        }

        /// <summary>
        /// 自动驾驶：与最近的敌人保持「能打到、但不被打到」的距离带（武器射程约 7）。
        /// 太近后撤、太远靠近、带内站桩输出。
        ///
        /// 初版写成「无脑远离最近敌人」，实测 45s 仅 7 杀、卡在 Wave 1、一次没升级——
        /// 玩家全程逃窜导致武器够不着。那是测试脚本自身的缺陷，不是游戏问题，故改为距离带策略。
        /// </summary>
        private void DrivePlayer()
        {
            var pc = PlayerController.Instance;
            if (pc == null) { PlayerInput.Override = null; return; }

            Vector2 me = pc.transform.position;

            // M18：改用统一索敌入口。此前读 EnemyManager.ActiveEnemies，而联机敌人是
            // NetEnemy、根本不在那份列表里 → 判定为"没有敌人"→ 玩家全程站桩，
            // 只能打死自己撞上来的怪（150s 实测仅 10 杀、卡在第 1 波）。
            var nearest = StarPixel.Combat.CombatTargets.NearestAny(me);
            if (nearest == null) { PlayerInput.Override = Vector2.zero; return; }

            float dist = Vector2.Distance(me, nearest.position);

            Vector2 toEnemy = (Vector2)nearest.position - me;
            Vector2 away = -toEnemy;
            Vector2 orbit = new Vector2(-toEnemy.y, toEnemy.x);   // 垂直方向：绕圈游走

            if (dist < 4f)
            {
                // 太近：一边后撤一边绕圈，避免被贴脸围死
                Vector2 mv = (away.sqrMagnitude > 0.0001f ? away.normalized : Vector2.zero)
                           + (orbit.sqrMagnitude > 0.0001f ? orbit.normalized * 0.6f : Vector2.zero);
                PlayerInput.Override = mv.sqrMagnitude > 0.0001f ? mv.normalized : Vector2.zero;
            }
            else if (dist > 6f)
            {
                // 太远：靠近（否则武器够不着，测试会退化成"干瞪眼"）
                PlayerInput.Override = toEnemy.sqrMagnitude > 0.0001f ? toEnemy.normalized : Vector2.zero;
            }
            else
            {
                // 距离带内：持续绕圈走位。
                // 注意不能站桩——第二版在带内站桩输出，结果 6.1s 就被围死，
                // 那是测试脚本的缺陷，真人玩家会一直移动。
                PlayerInput.Override = orbit.sqrMagnitude > 0.0001f ? orbit.normalized : Vector2.zero;
            }
        }

        /// <summary>联机下读房主权威波次（两端口径一致）；单机回退本地 EnemyManager。</summary>
        private int SyncedWave()
        {
#if FISHNET_ENABLED
            var ws = StarPixel.Net.NetWorldState.Instance;
            if (ws != null) return ws.Wave;
#endif
            var em = EnemyManager.Instance;
            return em != null ? em.CurrentWave : 0;
        }

        private int SyncedKills()
        {
#if FISHNET_ENABLED
            var ws = StarPixel.Net.NetWorldState.Instance;
            if (ws != null) return ws.Kills;
#endif
            return _kills;
        }

        /// <summary>
        /// 房主权威等级。
        /// 注意不能读 <c>PlayerController.level</c>：客户端本地控制器不参与 XP 结算、恒为初始值，
        /// 只有 NetPlayer 的等级 SyncVar 才是房主对该玩家的权威值。
        /// </summary>
        private int AuthoritativeLevel()
        {
#if FISHNET_ENABLED
            // 联机下只认 NetPlayer 的等级 SyncVar。
            // 绝不能回退到 PlayerController.level：它不参与 XP 结算、恒为初始值 1，
            // 一旦本地玩家对象缺失就会静默返回 1，把「进度重放失败」伪装成「等级本来就是 1」
            // （本次实测正是这样被假通过糊弄过去的）。
            //
            // M36 补一条：分流要看**运行时是否真的联网**，不能只看编译期有没有 FishNet。
            // FishNet 一直都在（内置包），所以 #if 分支恒真；而单机冒烟根本不建 NetPlayer，
            // NetPlayerRegistry.Local 为 null——按编译期宏分流会让单机的等级恒为 -1
            // （实测单机冒烟从「等级 8」变成「等级 -1」，就是这么踩到的）。
            if (_netMode)
            {
                var lp = StarPixel.Net.NetPlayerRegistry.Local;
                return lp != null ? lp.SyncedLevel : -1;
            }
#endif
            var pc = PlayerController.Instance;
            return pc != null ? pc.level : -1;
        }

        private void Finish()
        {
            _done = true;
            PlayerInput.Override = null;

            var em = EnemyManager.Instance;
            int wave = em != null ? em.CurrentWave : 0;
#if FISHNET_ENABLED
            var ws = StarPixel.Net.NetWorldState.Instance;
            // 联机下纯客户端没有 EnemyManager（波次由房主权威同步下来），
            // 若沿用本地 Wave 会恒为 0，把一局成功的实测判成"未通过"——改用同步值。
            if (_netMode && ws != null && wave <= 0) wave = ws.Wave;
#endif
            // M36：这里原本读 PlayerController.level，联机客户端下它不参与 XP 结算、恒为初始值 1
            // ——实测客户端真实等级 3 却被输出成 1，与几行之上 AuthoritativeLevel() 的注释
            // 「绝不能回退到 PlayerController.level」自相矛盾（那处是 M11 踩坑后加的，这处漏改了）。
            // 统一改走权威来源：联机读 NetPlayer 的等级 SyncVar，单机读 PlayerController。
            int level = AuthoritativeLevel();
            // M18：联机下的敌人是 NetEnemy，不会进 EnemyManager.ActiveEnemies（那是单机 Enemy 的列表），
            // 直接读它会恒为 0——实测里出现过「击杀 45、波次 3，却报场上敌人 0」的自相矛盾输出。
            // 改用与 EnemyManager 波次推进同一个统一入口，两种模式口径一致。
            int aliveEnemies = StarPixel.Combat.CombatTargets.AliveEnemyCount;

#if FISHNET_ENABLED
            // M37：联机下把「推卡收支」采出来。来源刻意分开——客户端读自己的 LevelUpManager
            // （自述），房主读 NetPlayer 的批次表（对端视角），两边对不上就说明中间丢包或错配。
            var lum = LevelUpManager.Instance;
            if (lum != null)
            {
                _extReceived = lum.ExternalReceived;
                _extPresented = lum.ExternalPresented;
                _extQueued = lum.ExternalQueued;
                _extDropped = lum.ExternalDropped;
                _extPending = lum.ExternalPendingCount;
                _extInvalidated = lum.ExternalInvalidated;
            }
            // 上面已在收尾前 1 秒采过；这里只补一次（那次没采到时至少不是空的）
            if (!_endSampled)
                StarPixel.Net.NetPlayerRegistry.ForEachAlive(p =>
                {
                    if (p == null) return;
                    _hostOffered += p.CardsOffered;
                    _hostEvicted += p.CardsEvicted;
                    _hostApplied += p.CardsApplied;
                    _hostRejected += p.CardsRejected;
                });
#endif

            // 判定门槛刻意保守：只要"能刷怪、能打死怪、波次在推进"即视为核心循环成立。
            // 等级/Boss 属于期望项（信息输出），不作为通过条件，避免把数值平衡问题误判为功能故障。
            bool pass = _kills > 0 && wave >= 1;

            // M14 观战：核心判据是"没有自己的可操作角色"——若房主没回收，说明观战 ServerRpc 链路断了
            if (_spectator) pass = pass && !_ownerAliveNow;

            // M11 断线重连：必须真的重连成功，且波次/等级/击杀三项进度没有回退
            if (_dropAt > 0f)
                pass = pass && _reconnected && _postWave >= _preWave
                       && _postLevel >= _preLevel && _postKills >= _preKills;

            Debug.Log("[SMOKE] ===== 实测结果 =====");
            Debug.Log("[SMOKE] 时长      " + _t.ToString("F1") + "s");
            Debug.Log("[SMOKE] 击杀      " + _kills);
            Debug.Log("[SMOKE] 波次      " + wave);
            Debug.Log("[SMOKE] 等级      " + level);
#if FISHNET_ENABLED
            // M36 诊断：联机下「等级」一度恒为初始值 1，需要区分是"真的没升级"还是
            // "读错了来源"。两个来源并列输出，谁对谁错一眼可见。
            if (_netMode)
                Debug.Log("[SMOKE] 权威等级    " + AuthoritativeLevel());
#endif
            Debug.Log("[SMOKE] 场上敌人  " + aliveEnemies);
            Debug.Log("[SMOKE] 升级选卡  " + _cardsPicked + " 次");
#if FISHNET_ENABLED
            if (_netMode)
            {
                // 注意：键与值之间必须留 2 个以上空格——net_smoke.py 的 [SMOKE] 解析正则靠它切分
                Debug.Log("[SMOKE] 推卡收到    " + _extReceived);
                Debug.Log("[SMOKE] 推卡上屏    " + _extPresented);
                Debug.Log("[SMOKE] 推卡入队    " + _extQueued);
                Debug.Log("[SMOKE] 推卡丢弃    " + _extDropped);
                Debug.Log("[SMOKE] 队列残余    " + _extPending);
                Debug.Log("[SMOKE] 房主推卡批次  " + _hostOffered);
                Debug.Log("[SMOKE] 批次淘汰    " + _hostEvicted);
                Debug.Log("[SMOKE] 面板作废    " + _extInvalidated);
                // M38：双端对账——本地镜像已应用 vs 房主权威已应用，必须相等
                Debug.Log("[SMOKE] 房主已应用  " + _hostApplied);
                Debug.Log("[SMOKE] 本地已应用  " + _localApplied);
                Debug.Log("[SMOKE] 本地强化次数  " + _localApplyTotal);
                Debug.Log("[SMOKE] 房主已拒绝  " + _hostRejected);
                Debug.Log("[SMOKE] 本地被拒    " + _ackRejected);
                Debug.Log("[SMOKE] 本地作废    " + _ackInvalidated);
                Debug.Log("[SMOKE] 待确认残留  " + _ackPending);
                Debug.Log("[SMOKE] 回执超时    " + _ackTimedOut);
                // M39：收场通知——静默的收场等于没发生，一条都不许被吞
                Debug.Log("[SMOKE] 收场总数    " + _ntTotal);
                Debug.Log("[SMOKE] 通知上屏    " + _ntShown);
                Debug.Log("[SMOKE] 通知被吞    " + _ntSuppressed);
            }
            Debug.Log("[SMOKE] 会话角色    " + (_spectator ? "观战"
                : StarPixel.Net.NetSession.IsHost ? "房主"
                : StarPixel.Net.NetSession.IsClient ? "客户端" : "单机"));
            Debug.Log("[SMOKE] 网络玩家数  " + StarPixel.Net.NetPlayerRegistry.Count);
            // M37：判据用这一条（收尾前 1 秒，两端必然都还活着），
            // 上面那条瞬时值只作诊断——它由两个进程的退出竞态决定，不是产品属性。
            Debug.Log("[SMOKE] 收尾前玩家数  " + _endPlayers);
            // 注意：键与值之间必须留 2 个以上空格——net_smoke.py 的 [SMOKE] 解析正则靠它切分
            Debug.Log("[SMOKE] 网络玩家峰值  " + _maxPlayers);
            Debug.Log("[SMOKE] 同步波次    " + (ws != null ? ws.Wave : -1));
            Debug.Log("[SMOKE] 同步击杀    " + (ws != null ? ws.Kills : -1));
            // M11：房主视角的掉线/重连观测（不依赖对端自述）
            Debug.Log("[SMOKE] 对端掉线    " + (_peerDropSeen ? "是" : "否"));
            Debug.Log("[SMOKE] 对端重连    " + (_peerRejoinSeen ? "是" : "否"));
            // M14：观战者不该还留着自己的玩家对象
            if (_spectator)
                Debug.Log("[SMOKE] 自身角色已回收  " + (_ownerAliveNow ? "否" : "是"));
            if (_dropAt > 0f)
            {
                Debug.Log("[SMOKE] 断线时刻    " + (_dropped ? _dropTime.ToString("F1") + "s" : "未触发"));
                Debug.Log("[SMOKE] 断线前波次  " + _preWave);
                Debug.Log("[SMOKE] 断线前等级  " + _preLevel);
                Debug.Log("[SMOKE] 断线前击杀  " + _preKills);
                Debug.Log("[SMOKE] 断线期间波次  " + _midWave);
                Debug.Log("[SMOKE] 断线期间敌人  " + _midEnemies);
                Debug.Log("[SMOKE] 断线期间击杀  " + _midKills);
                Debug.Log("[SMOKE] 断线期间世界存活  " + (_midWorldAlive ? "是" : "否"));
                Debug.Log("[SMOKE] 重连完成    " + (_reconnected ? "是" : "否"));
                Debug.Log("[SMOKE] 重连耗时    " +
                          (_reconnected ? (_reconnectTime - _dropTime).ToString("F1") + "s" : "-"));
                Debug.Log("[SMOKE] 重连后波次  " + _postWave);
                Debug.Log("[SMOKE] 重连后等级  " + _postLevel);
                Debug.Log("[SMOKE] 重连后击杀  " + _postKills);
            }
#endif
            Debug.Log("[SMOKE] 遇到Boss  " + (_bossSeen ? "是" : "否"));
            Debug.Log("[SMOKE] 玩家阵亡  " + (_died ? "是（" + _deathAt.ToString("F1") + "s）" : "否"));
            Debug.Log("[SMOKE] 结论      " + (pass ? "通过" : "未通过"));

            Application.Quit(pass ? 0 : 1);
        }
    }
}
