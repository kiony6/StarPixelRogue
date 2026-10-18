#if FISHNET_ENABLED
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using StarPixel.Combat;
using StarPixel.Core;
using StarPixel.Player;
using StarPixel.UI;
using UnityEngine;

namespace StarPixel.Net
{
    /// <summary>
    /// 网络玩家（联机模式，对应 TDD ADR-001 半权威房主 + 客户端本地预测）。
    ///
    /// 职责划分：
    /// - 房主：持有权威 <see cref="PlayerController"/>，负责所有玩家的 XP/等级/伤害/升级应用；
    ///   远程玩家的升级以 TargetRpc 推卡、ServerRpc 收选择；敌人 AI 从 <see cref="NetPlayerRegistry"/> 索敌。
    /// - 客户端：本地玩家为「客户端权威移动」（NetworkTransform 默认客户端权威），
    ///   武器不本地结算，只发开火请求；HUD 数据全部来自 SyncVar 回调转 EventBus。
    ///
    /// M4.5 修正记录：
    /// 1) SyncVar 回调统一加 asServer/IsServerInitialized 守卫——房主会同时收到
    ///    服务端(asServer=true)与本地客户端(asServer=false)两次回调，不守会导致击杀双计/跳字双份；
    /// 2) 武器库存经 SyncList 同步（房主权威）：客户端此前永远只有默认武器，
    ///    既不发开火请求也不显示 HUD，等于联机下升级全部失效；
    /// 3) 升级推卡携带 kind 编码，客户端本地镜像应用（非权威表现），
    ///    否则客户端移速/生命等属性强化不生效（这些是客户端权威/本地数据）。
    /// </summary>
    public class NetPlayer : NetworkBehaviour
    {
        private PlayerController _ctrl;
        private Damageable _dmg;

        // —— 同步状态 ——
        private readonly SyncVar<float> _hpRatio = new SyncVar<float>(1f);
        private readonly SyncVar<int> _level = new SyncVar<int>(1);
        private readonly SyncVar<float> _xpRatio = new SyncVar<float>(0f);
        private readonly SyncVar<float> _lastHit = new SyncVar<float>(0f);
        private readonly SyncVar<int> _hitSeq = new SyncVar<int>(0);

        // 武器库存（房主权威）：id 与等级一一对应，客户端据此重建本地武器
        private readonly SyncList<int> _netWeaponIds = new SyncList<int>();
        private readonly SyncList<int> _netWeaponLevels = new SyncList<int>();

        // 房主侧：为远程玩家缓存的升级供选（客户端选择后按 index 应用）
        private readonly List<UpgradeOffer> _cachedOffers = new List<UpgradeOffer>();
        private int _lastKnownLevel = 1;

        /// <summary>M11 断线重连：本玩家对应的重连令牌（由客户端首连时上报，房主据此暂存/匹配进度）。</summary>
        private string _reconnectToken = "";

        /// <summary>
        /// M18 实测修复：重连令牌/角色握手是否已得到房主回执。
        /// 为什么需要重发：在 <c>OnStartClient</c> 里直发 ServerRpc，**首连能到、重连会丢**——
        /// 实测重连后房主侧从未收到该 RPC，导致进度快照匹配不上、玩家进度被重置回 1 级
        /// （客户端自述"已上报"、房主"从没收到"，两条日志对不上才暴露出来）。
        /// 故改为：首连照旧直发（延迟最低），之后每 0.4s 重发一次，直到房主回执 <c>_handshakeDone</c>。
        /// </summary>
        private bool _handshakeDone;
        private float _handshakeTimer;
        private bool _tokenLogged;   // 日志收敛：重发时不重复打印

        /// <summary>M14 观战：房主侧待回收标记——观战者被自动生成的玩家对象需反生成。</summary>
        private bool _spectatorDespawnPending;

        /// <summary>本机本地玩家（房主视角为房主自己的角色，客户端视角为自己的角色）。</summary>
        public static NetPlayer Local { get; private set; }

        public PlayerController Controller => _ctrl;
        public Damageable Damageable => _dmg;

        /// <summary>
        /// M18 实测：房主权威等级（SyncVar 当前值）。
        /// 为什么不能读 <c>PlayerController.level</c>：客户端本地控制器不参与 XP 结算，
        /// 其 level 恒为初始值，只有本 SyncVar 才是房主对该玩家的权威等级——
        /// 核对 M11「断线重连后进度是否重放」必须读这里。
        /// </summary>
        public int SyncedLevel => _level.Value;

        /// <summary>本机是否持有该玩家对象（观战回收校验用）。</summary>
        public bool IsLocalOwned => IsOwner;

        public override void OnStartClient()
        {
            base.OnStartClient();
            _ctrl = GetComponent<PlayerController>();
            _dmg = GetComponent<Damageable>();
            NetPlayerRegistry.Register(this);

            if (IsOwner)
            {
                // M14 观战：观战者不持有可操作角色，跳过全部本地接线，并请求房主回收本对象。
                // （Fish-Net 会为每个连接自动生成玩家预制体，无法在生成前拦截，只能先生成再回收。）
                if (NetSession.Spectator)
                {
                    _ctrl.isLocal = false;
                    RequestSpectatorServerRpc();
                }
                else
                {
                    Local = this;
                    // 本地玩家：移动/相机/开火输入在本地运行（开火改为发请求，见 PlayerController）
                    _ctrl.isLocal = true;
                    _ctrl.SetProjectileSpawner(ServerSpawnProjectile);
                    _ctrl.SetSpecialSpawner(ServerSpawnSpecialProjectile);
                    PlayerController.Instance = _ctrl;

                    var shake = Camera.main != null ? Camera.main.GetComponent<Core.CameraShake>() : null;
                    if (shake != null) shake.Configure(transform);

                    // 纯客户端：升级选卡一律走房主推卡（TargetRpc），禁止本地 BuildOffers——
                    // 客户端武器库未同步前本地建卡会弹错面板，且 apply 会在未同步的本地状态上生效。
                    if (!IsServerInitialized)
                        LevelUpManager.Instance?.SetExternalCards(true);

                    // M6.2：应用本地选择的角色（客户端为本地预测；房主侧经 ServerRpc 权威应用）
                    var charDef = CharacterManager.ById(CharacterManager.SelectedId);
                    _ctrl.ApplyCharacter(charDef);
                    if (!IsServerInitialized)
                        RequestSetCharacterServerRpc(CharacterManager.SelectedId);

                    // M11 断线重连：上报重连令牌，使房主能在断连时暂存、重连时重放本玩家进度。
                    // 首连直发即可；重连时这条可能被丢弃，由 Update 里的重发兜底（见 _handshakeDone 注释）。
                    RequestReconnectServerRpc(NetSession.ReconnectToken, CharacterManager.SelectedId);
                }
            }
            else
            {
                // 远程玩家：不在本地跑 AI/开火/事件，仅作为同步表现与房主权威数据容器
                _ctrl.isLocal = false;
                _ctrl.SetProjectileSpawner(ServerSpawnProjectile);
                _ctrl.SetSpecialSpawner(ServerSpawnSpecialProjectile);
            }

            _hpRatio.OnChange += OnHpRatioChanged;
            _level.OnChange += OnLevelChanged;
            _xpRatio.OnChange += OnXpRatioChanged;
            _hitSeq.OnChange += OnHitSeqChanged;

            // 本地玩家血量由 SyncVar 驱动 HUD（伤害在房主侧结算）
            if (_dmg != null) _dmg.OnHpChanged += OnDamageableHpChanged;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (_dmg != null) _dmg.OnHpChanged -= OnDamageableHpChanged;
            NetPlayerRegistry.Unregister(this);
            if (IsOwner && Local == this) Local = null;
        }

        /// <summary>
        /// M11 断线重连：房主视角下，远程玩家掉线 → 其网络对象在房主侧反生成，此处暂存进度快照。
        /// 以重连令牌为键，待该玩家重连时（RequestReconnectServerRpc）取出并重放。
        /// </summary>
        public override void OnStopServer()
        {
            base.OnStopServer();
            if (!string.IsNullOrEmpty(_reconnectToken) && _ctrl != null)
            {
                var snap = _ctrl.CaptureProgression();
                NetReconnectStore.Put(_reconnectToken, snap);
                Debug.Log("[M11] 快照入库 token=" + _reconnectToken.Substring(0, 8) +
                          " level=" + snap.level + " 武器=" + snap.weaponIds.Length);
            }
            else
            {
                Debug.Log("[M11] 快照未入库：token=" +
                          (string.IsNullOrEmpty(_reconnectToken) ? "空" : _reconnectToken.Substring(0, 8)) +
                          " ctrl=" + (_ctrl == null ? "null" : "ok"));
            }
        }

        /// <summary>Damageable 血量变化 → 房主侧写入权威 SyncVar（联机下唯一血量来源）。</summary>
        private void OnDamageableHpChanged(float ratio)
        {
            if (IsServerInitialized) _hpRatio.Value = ratio;
        }

        /// <summary>弹丸生成委托：房主持有权威时才真正生成网络弹丸。</summary>
        private void ServerSpawnProjectile(Vector3 pos, Vector2 dir, float dmg, float speed,
            float pierce, bool applySlow, float slowFactor)
        {
            if (!IsServerInitialized) return;
            NetWorld.SpawnProjectile(pos, dir, dmg, speed, pierce, applySlow, false);
        }

        /// <summary>M6.1：特殊弹丸生成委托（回旋镖/追踪弹），仅房主权威。</summary>
        private void ServerSpawnSpecialProjectile(Vector3 pos, Vector2 dir, float dmg, float speed,
            float range, int behavior, bool applySlow)
        {
            if (!IsServerInitialized) return;
            NetWorld.SpawnProjectileEx(pos, dir, dmg, speed, range, behavior, applySlow);
        }

        // —— 房主：检测远程玩家升级并推卡；客户端：拉取武器库存 ——
        void Update()
        {
            if (_ctrl == null) return;
            if (IsServerInitialized)
            {
                // M14 观战：回收观战者被自动生成的玩家对象。
                // 延后到下一帧执行——在 OnStartServer/OnStartClient 期间直接 Despawn 容易撞上生成时序。
                if (_spectatorDespawnPending)
                {
                    _spectatorDespawnPending = false;
                    if (NetworkObject != null && NetworkObject.IsSpawned) NetworkObject.Despawn();
                    return;
                }

                // M18 联机实测修复：等级/经验此前只在断线重连重放快照时写入过，
                // 平时从不更新——客户端 HUD 的等级永远停在 1、经验条一动不动
                // （实测：房主 16 级，客户端恒为 1 级）。这里补齐房主侧的常规写入。
                if (_ctrl.level != _level.Value) _level.Value = _ctrl.level;
                float xr = _ctrl.XpRatio;
                if (Mathf.Abs(xr - _xpRatio.Value) > 0.001f) _xpRatio.Value = xr;

                if (IsOwner) return;                     // 房主自己的升级走本地 EventBus 流程
                while (_ctrl.level > _lastKnownLevel)
                {
                    _lastKnownLevel++;
                    OfferCards();
                }
                PushWeaponInventory();                   // 武器库存 → SyncList（客户端重建用）
            }
            else if (IsClientInitialized && IsOwner)
            {
                ApplyWeaponInventory();                  // SyncList → 本地武器（表现/开火请求用）

                // M11：握手重发兜底（详见 _handshakeDone 注释）。
                // 重连场景下 OnStartClient 里那条 ServerRpc 会被丢弃，没有这里就永远匹配不上进度快照。
                if (!_handshakeDone)
                {
                    _handshakeTimer -= Time.unscaledDeltaTime;
                    if (_handshakeTimer <= 0f)
                    {
                        _handshakeTimer = 0.4f;
                        RequestReconnectServerRpc(NetSession.ReconnectToken, CharacterManager.SelectedId);
                    }
                }
            }
        }

        // —— 武器库存同步（房主权威）——

        /// <summary>房主侧：本地武器库与 SyncList 不一致时整体重写（变化低频，可接受全量）。</summary>
        private void PushWeaponInventory()
        {
            var weapons = _ctrl.Weapons;
            if (weapons == null) return;
            bool changed = _netWeaponIds.Count != weapons.Count;
            if (!changed)
            {
                for (int i = 0; i < weapons.Count; i++)
                {
                    var w = weapons[i];
                    if (w == null || w.def == null) { changed = true; break; }
                    if (_netWeaponIds[i] != w.def.id || _netWeaponLevels[i] != w.level) { changed = true; break; }
                }
            }
            if (!changed) return;

            _netWeaponIds.Clear();
            _netWeaponLevels.Clear();
            foreach (var w in weapons)
            {
                if (w == null || w.def == null) continue;
                _netWeaponIds.Add(w.def.id);
                _netWeaponLevels.Add(w.level);
            }
        }

        /// <summary>客户端侧：按 SyncList 补齐/升级本地武器（HUD 显示 + 开火请求 + 属性预测）。</summary>
        private void ApplyWeaponInventory()
        {
            var weapons = _ctrl.Weapons;
            if (weapons == null) return;
            for (int i = 0; i < _netWeaponIds.Count; i++)
            {
                int id = _netWeaponIds[i];
                int lv = i < _netWeaponLevels.Count ? _netWeaponLevels[i] : 1;
                var w = weapons.Find(x => x != null && x.def != null && x.def.id == id);
                if (w == null)
                {
                    var def = UpgradeCatalog.CreateById(id);
                    if (def != null) _ctrl.AddSkill(def);   // 内部按 id 去重，并刷新 HUD
                }
                else
                {
                    while (w.level < lv) _ctrl.LevelUpWeapon(id);
                }
            }
        }

        // —— 房主：为远程玩家生成候选并推卡 ——
        private void OfferCards()
        {
            _cachedOffers.Clear();
            _cachedOffers.AddRange(UpgradeCatalog.BuildOffers(3, _ctrl));

            var titles = new string[_cachedOffers.Count];
            var descs = new string[_cachedOffers.Count];
            var kinds = new int[_cachedOffers.Count];
            for (int i = 0; i < _cachedOffers.Count; i++)
            {
                titles[i] = _cachedOffers[i].title;
                descs[i] = _cachedOffers[i].desc;
                kinds[i] = _cachedOffers[i].kind;
            }
            ShowCardsTargetRpc(Owner, titles, descs, kinds);
        }

        [TargetRpc]
        private void ShowCardsTargetRpc(NetworkConnection conn, string[] titles, string[] descs, int[] kinds)
        {
            var offers = new List<UpgradeOffer>();
            for (int i = 0; i < titles.Length; i++)
                offers.Add(new UpgradeOffer
                {
                    title = titles[i],
                    desc = i < descs.Length ? descs[i] : "",
                    apply = null,
                    kind = i < kinds.Length ? kinds[i] : 0
                });

            LevelUpManager.Instance?.PresentExternal(offers, index =>
            {
                RequestChooseUpgradeServerRpc(index);                    // 权威：房主应用
                if (index >= 0 && index < kinds.Length)
                    UpgradeCatalog.ApplyKind(kinds[index], _ctrl);       // 本地镜像：客户端权威数据（移速/磁吸范围/生命上限）
            });
        }

        // RequireOwnership 保持默认 true：只有持有该玩家对象的客户端能回应自己的选卡，
        // 避免恶意客户端代替他人应用强化。
        [ServerRpc]
        private void RequestChooseUpgradeServerRpc(int index)
        {
            if (!IsServerInitialized || _ctrl == null) return;
            if (index < 0 || index >= _cachedOffers.Count) return;
            _cachedOffers[index].apply?.Invoke();
        }

        /// <summary>M6.2：客户端上报角色选择，房主权威应用（属性/起始武器）。</summary>
        [ServerRpc]
        private void RequestSetCharacterServerRpc(int id)
        {
            if (!IsServerInitialized || _ctrl == null) return;
            _ctrl.ApplyCharacter(CharacterManager.ById(id));
        }

        /// <summary>
        /// M11 断线重连：客户端上报重连令牌（首连也会上报，但此时快照表为空，等价于普通连接）。
        /// 房主侧：记录令牌，并按令牌取出断线前的进度快照重放——重建武器/等级/经验/血量，
        /// 同时把房主侧升级发牌计数对齐到当前等级，避免重连瞬间一次性弹出多张升级卡。
        /// </summary>
        [ServerRpc]
        private void RequestReconnectServerRpc(string token, int characterId)
        {
            if (!IsServerInitialized || _ctrl == null) return;
            _reconnectToken = token ?? "";
            _ctrl.ApplyCharacter(CharacterManager.ById(characterId));
            bool hit = NetReconnectStore.TryGet(_reconnectToken, out var snap);
            // 只在「首次受理」或「命中快照」时打日志：重发可能每 0.4s 一次，无脑打印会刷屏
            if (!_tokenLogged || hit)
            {
                _tokenLogged = true;
                Debug.Log("[M11] 收到重连上报 token=" +
                          (_reconnectToken.Length >= 8 ? _reconnectToken.Substring(0, 8) : _reconnectToken) +
                          " 命中快照=" + (hit ? "是" : "否"));
            }
            if (hit)
            {
                _ctrl.ReapplyProgression(snap);
                _lastKnownLevel = System.Math.Max(_lastKnownLevel, snap.level);
                _level.Value = snap.level;
                _xpRatio.Value = _ctrl.XpRatio;
                NetReconnectStore.Consume(_reconnectToken);
                Debug.Log("[M11] 进度重放完成 level=" + _ctrl.level + " syncLevel=" + _level.Value);
            }
            ConfirmHandshakeTargetRpc(Owner);   // 回执：客户端据此停止重发
        }

        /// <summary>M11：房主回执——令牌上报已受理，客户端停止重发。</summary>
        [TargetRpc]
        private void ConfirmHandshakeTargetRpc(NetworkConnection conn)
        {
            _handshakeDone = true;
        }

        /// <summary>
        /// M14 观战：客户端声明观战身份，请求房主回收刚为它自动生成的玩家对象。
        /// 观战者不参与战斗——不结算伤害、不被敌人索敌、不占用玩家名额，只接收世界同步用于观察。
        /// </summary>
        [ServerRpc]
        private void RequestSpectatorServerRpc()
        {
            if (!IsServerInitialized) return;
            _spectatorDespawnPending = true;   // 实际回收延后到下一帧 Update
        }

        /// <summary>客户端开火请求（由 PlayerController 在联机客户端模式下调用）。</summary>
        [ServerRpc]
        public void RequestFire(int weaponId)
        {
            if (!IsServerInitialized || _ctrl == null) return;
            _ctrl.ServerFire(weaponId);
        }

        /// <summary>房主侧给指定玩家发放经验（NetPickup 收集时调用）。</summary>
        [Server]
        public void ServerGrantXp(float amount)
        {
            if (!IsServerInitialized || _ctrl == null) return;
            _ctrl.AddXp(amount);
        }

        /// <summary>房主侧给指定玩家回血。</summary>
        [Server]
        public void ServerGrantHeal(float amount)
        {
            if (!IsServerInitialized || _ctrl == null) return;
            _ctrl.Heal(amount);
        }

        // —— 客户端 HUD 驱动（守卫见各类注释：房主侧这些数据已由本地路径驱动）——
        private void OnHpRatioChanged(float prev, float next, bool asServer)
        {
            if (asServer || IsServerInitialized || !IsOwner) return;
            EventBus.RaisePlayerHealthRatio(next);
            if (next < prev - 0.0001f)
            {
                EventBus.RaisePlayerHurt();
                EventBus.RaiseShake(0.14f, 0.14f);
            }
            if (next <= 0f) EventBus.RaiseGameOver();
        }

        private void OnLevelChanged(int prev, int next, bool asServer)
        {
            if (asServer || IsServerInitialized || !IsOwner) return;
            EventBus.RaiseLevelUp(next);
        }

        private void OnXpRatioChanged(float prev, float next, bool asServer)
        {
            if (asServer || IsServerInitialized || !IsOwner) return;
            EventBus.RaiseXpChanged(next);
        }

        private void OnHitSeqChanged(int prev, int next, bool asServer)
        {
            if (asServer || IsServerInitialized || !IsOwner || next <= prev) return;
            EventBus.RaiseDamaged(_lastHit.Value, transform.position);
        }

        /// <summary>本地玩家受击记录（房主侧写入，供客户端跳字）。</summary>
        [Server]
        public void ServerRecordHit(float amount)
        {
            if (!IsServerInitialized) return;
            _hitSeq.Value++;
            _lastHit.Value = amount;
        }
    }
}
#endif
