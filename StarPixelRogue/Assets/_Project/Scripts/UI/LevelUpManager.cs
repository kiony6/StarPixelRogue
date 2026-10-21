using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using StarPixel.Combat;
using StarPixel.Player;

namespace StarPixel.UI
{
    /// <summary>
    /// 升级选卡：监听 OnLevelUp，暂停游戏并弹出 3 选 1 强化卡（对应 TDD M2 升级循环）。
    /// 选完后恢复时间缩放。依赖 UI 事件系统（GameBuilder 已创建 EventSystem）。
    /// </summary>
    public class LevelUpManager : Core.MonoSingleton<LevelUpManager>
    {
        /// <summary>M37：待弹外部供选的排队上限。超过后丢弃最旧的一批并计入 ExternalDropped。</summary>
        public const int MaxPendingExternal = 16;

        private bool _active;
        private int _pending;
        private bool _externalCards;   // M4.5：联机客户端为 true——选卡一律由房主经 PresentExternal 推送

        /// <summary>
        /// M37：外部供选的排队区。
        ///
        /// 原先 <see cref="PresentExternal"/> 开头就是 `if (_active) return;`——面板正开着时
        /// 推来的卡**直接丢掉**。而本地路径是有排队的：OnLevelUp 累加 _pending，选完一张
        /// 再 TryPresent 补弹下一张。两条路不一致的后果：联机客户端连升两级时，第二张
        /// 及以后的升级卡永久消失（房主侧也永远收不到选择回传，等于这次升级没发生）。
        /// 实测 60s 双开：房主选卡 5 次、客户端 0 次——客户端全程只上屏过第一组，之后全丢。
        /// </summary>
        private readonly List<PendingExternal> _pendingExternal = new List<PendingExternal>();

        private struct PendingExternal
        {
            /// <summary>M38：房主给这一批的批次 id——房主淘汰某批时要能按 id 把它撤掉。</summary>
            public int batchId;
            public List<UpgradeOffer> offers;
            public System.Action<int> onChosen;
        }

        /// <summary>M38：当前正在展示的那一批的批次 id（0 = 无）。作废通知按它比对。</summary>
        private int _currentBatchId;

        // —— M37 计数：全部只增不减，供 CardProbe / SmokeTest 断言「一次都不许丢」——
        /// <summary>收到的外部推卡次数（空候选不计）。</summary>
        public int ExternalReceived { get; private set; }
        /// <summary>真正上屏展示过的推卡次数。</summary>
        public int ExternalPresented { get; private set; }
        /// <summary>因面板正开着而入队等待的次数（排队不算丢）。</summary>
        public int ExternalQueued { get; private set; }
        /// <summary>被丢弃的次数（修复后应恒为 0；>0 说明排队溢出或调用方发了空候选）。</summary>
        public int ExternalDropped { get; private set; }
        /// <summary>当前队列里还压着几批。</summary>
        public int ExternalPendingCount { get { return _pendingExternal.Count; } }
        /// <summary>M38：被房主通知作废的批次数（>0 说明玩家长时间不选卡，或回传链路断了）。</summary>
        public int ExternalInvalidated { get; private set; }
        /// <summary>M38：当前正在展示的批次 id（0 = 没在展示）。</summary>
        public int CurrentBatchId { get { return _active ? _currentBatchId : 0; } }
        /// <summary>面板是否正在展示（供探针判定"是否在选卡态"，不许再靠 timeScale 推断）。</summary>
        public bool IsShowing { get { return _active; } }

        /// <summary>
        /// M40：最近一次**真正上屏**的卡片文本（标题 + 描述，" | " 拼接）。
        /// 联机冒烟据此核对"英文客户端眼前写的到底是不是英文"——
        /// 只核对 kind 有没有传过来证明不了这件事（房主可以传 kind 却在客户端拼中文）。
        /// </summary>
        public string LastPresentedText { get; private set; }
        /// <summary>当前面板（供探针读取**真实上屏文本**，而不是内部列表——M35 的教训）。</summary>
        public Transform CurrentPanel { get { return transform.Find("LevelUpCanvas"); } }

        /// <summary>
        /// M29：订阅必须**存成字段**再挂上去。
        ///
        /// 原来的写法是 `OnLevelUp += _ => { _pending++; TryPresent(); };`——匿名 lambda，
        /// 而 EventBus 是**静态**的：本管理器由 GameBuilder 每局 Track 新建、
        /// 拆世界时销毁，委托却一直攥着旧实例。重开三局后链上就攒了三份：
        ///   · 升一级弹三次面板（玩家要连点三次才能继续）；
        ///   · 更糟的是旧实例先执行，TryPresent → BuildPanel 里访问已销毁的 transform
        ///     直接抛 NullReferenceException，**中断整条委托链**——排在后面的新实例
        ///     压根收不到事件，于是面板一个都不弹，而 ShowPanel 已经把游戏暂停了：
        ///     升级时画面停住、面板不出来，只能杀进程。
        /// 存成字段后 OnDestroy 里 `-=` 同一实例即可精确退订。
        /// </summary>
        private System.Action<int> _onLevelUp;

        protected override void Awake()
        {
            base.Awake();
            _onLevelUp = _ => { _pending++; TryPresent(); };
            Core.EventBus.OnLevelUp += _onLevelUp;
        }

        void OnDestroy()
        {
            // M29：静态事件必须退订——否则销毁后委托链仍攥着本实例（详见 _onLevelUp 注释）
            if (_onLevelUp != null)
            {
                Core.EventBus.OnLevelUp -= _onLevelUp;
                _onLevelUp = null;
            }
            // M37：拆世界时队列里未弹的卡必须一起清掉——留着会让下一局
            // 收到第一张推卡时被旧批次插队（玩家拿到的是上一局的卡）
            _pendingExternal.Clear();
            _currentBatchId = 0;                  // M38：批次游标跟着队列一起清
            // M4.5：断线拆世界时防止时间缩放卡在 0（选卡面板打开中被销毁）
            // M41：兜底分支此前写死 1f，与 HitstopManager 里的"正常速度"是两份事实。
            // 加速档下会在选卡面板被销毁的瞬间把倍率拽回 1，长局压测因此只能跑真实时长。
            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
            else Time.timeScale = Core.HitstopManager.BaseScale;
        }

        /// <summary>M4.5：切换「外部供卡」模式。联机客户端开启后，本地不再自动建卡
        /// （客户端武器库/状态未同步，本地 BuildOffers 会弹错面板并污染本地状态）。</summary>
        public void SetExternalCards(bool external)
        {
            _externalCards = external;
        }

        /// <summary>
        /// M51：外力**无选择地**收面板（游戏结束时用）。
        ///
        /// 与 <see cref="Choose"/> 的区别：Choose 要应用所选强化并接着弹下一批；
        /// 这里什么都不给，直接把面板连同队列一起抹掉。
        /// 触发场景：玩家死在选卡面板开着的瞬间（联机里很常见——服务端把血打空时，
        /// 客户端往往正停在 3 选 1 上）。此前没人管这件小事的后果是：
        /// 结算面板被 LevelUpCanvas 的 BG 挡住，「离开对局」按钮点不到，
        /// 玩家拿不到任何出口，只能杀进程。
        ///
        /// 幂等：面板没开时返回 false，什么也不做（尤其不碰 timeScale）。
        /// </summary>
        /// <returns>是否确实收掉了一张面板。</returns>
        public bool DismissPanel()
        {
            if (!_active) return false;

            var panel = transform.Find("LevelUpCanvas");
            if (panel != null) Destroy(panel.gameObject);

            _active = false;
            _chooser = null;
            _currentBatchId = 0;
            // 队列里还压着的批次属于已经结束的这一局，绝不能再弹出来：
            // 那时世界已经拆了，给一张卡等于给一次空指针。
            _pendingExternal.Clear();
            _pending = 0;

            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
            else Time.timeScale = Core.HitstopManager.BaseScale;
            return true;
        }

        /// <summary>
        /// M51：结算面板出场前调用——收掉可能还开着的选卡面板。
        /// 静态是刻意的：GameOverPanel 不该为了一件"可能没有"的事去碰 LevelUpManager 的实例。
        /// </summary>
        public static void DismissForRunEnd()
        {
            var m = Instance;
            if (m == null) return;
            if (m.DismissPanel())
                Debug.Log("[M51] 结算前收掉仍开着的选卡面板（世界已结束，不再选卡）");
        }

        /// <summary>
        /// M17 无头实测：自动选择第一张升级卡。
        /// 正常游戏里选卡由玩家点击触发，且选卡期间会把 timeScale 归零暂停；
        /// 无头环境无人点击会一直卡在暂停态，导致实测死锁（M17 首轮就卡在这里）。
        /// 本入口仅供 SmokeTest 调用；正常构建永不调用，对游戏零影响。
        /// </summary>
        /// <returns>是否确实选掉了一张卡（未处于选卡态时返回 false）。</returns>
        public bool AutoChooseFirst()
        {
            if (!_active) return false;
            var panel = transform.Find("LevelUpCanvas");
            if (panel == null) return false;
            Choose(0, panel.gameObject);
            return true;
        }

        private void TryPresent()
        {
            if (_active || _pending <= 0 || _externalCards) return;
            var offers = UpgradeCatalog.BuildOffers(3, PlayerController.Instance);
            ShowPanel(offers, idx => offers[idx].apply?.Invoke(), true);
        }

        /// <summary>
        /// 外部供选入口（联机模式）：房主生成候选后经 TargetRpc 送到客户端，
        /// 客户端展示面板并把选择序号回传（apply 为空，由房主应用）。
        /// </summary>
        public void PresentExternal(List<UpgradeOffer> offers, System.Action<int> onChosen)
        {
            PresentExternal(0, offers, onChosen);
        }

        /// <summary>
        /// 外部供选入口（联机模式），带<b>批次 id</b>。
        ///
        /// M38：批次 id 不是记账用的，是**撤销**用的——房主那边的批次表有上限，
        /// 玩家长时间不选卡时最旧的一批会被淘汰。没有 id，房主就没法告诉客户端
        /// "你眼前那张卡已经作废了"，玩家点了之后什么也不会发生（M38 实测缺陷）。
        /// </summary>
        public void PresentExternal(int batchId, List<UpgradeOffer> offers, System.Action<int> onChosen)
        {
            if (offers == null || offers.Count == 0) return;
            ExternalReceived++;

            // M37：面板正开着时**排队**而不是丢弃（详见 _pendingExternal 注释）。
            // 联机客户端连升两级是常态（早期 XP 涨得快），丢一张就是永久少一个强化。
            if (_active)
            {
                if (_pendingExternal.Count >= MaxPendingExternal)
                {
                    // 队列溢出：玩家长时间不选卡，或回传链路断了。宁可丢最旧的一批
                    // （它已经过期）也不让队列无限涨——但要计数，探针据此报警。
                    ExternalDropped++;
                    _pendingExternal.RemoveAt(0);
                }
                _pendingExternal.Add(new PendingExternal
                {
                    batchId = batchId,
                    offers = offers,
                    onChosen = onChosen
                });
                ExternalQueued++;
                return;
            }
            _currentBatchId = batchId;
            ShowPanel(offers, onChosen, false);   // 联机下不暂停（避免冻结网络心跳）
            ExternalPresented++;
        }

        /// <summary>
        /// M38：房主通知某一批作废——把队列里的它撤掉；若正在展示的就是它，直接关面板。
        ///
        /// 不作废的后果：那张卡还摆在玩家眼前，玩家点了，客户端把选择发上去，
        /// 房主查不到这一批、拒绝，什么也没发生——玩家看到的是"点了没反应"。
        /// 撤掉至少让界面和事实一致。
        /// </summary>
        /// <returns>是否真的撤到了（false = 已经选掉或早已不在）。</returns>
        public bool InvalidateExternal(int batchId)
        {
            if (batchId <= 0) return false;

            if (_active && _currentBatchId == batchId)
            {
                ExternalInvalidated++;
                CloseShownPanelWithoutChoosing();
                return true;
            }

            for (int i = 0; i < _pendingExternal.Count; i++)
            {
                if (_pendingExternal[i].batchId != batchId) continue;
                _pendingExternal.RemoveAt(i);
                ExternalInvalidated++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// M38：关掉当前面板但**不算作选择**（不触发 onChosen）。
        /// 与 <see cref="Choose"/> 的区别只在这里：作废不是"玩家选了"，不能走回调。
        /// </summary>
        private void CloseShownPanelWithoutChoosing()
        {
            var panel = transform.Find("LevelUpCanvas");
            if (panel != null) Destroy(panel.gameObject);
            _active = false;
            _chooser = null;
            _currentBatchId = 0;

            // 后面还压着别的批次就继续弹——玩家还有别的卡要选
            if (_pendingExternal.Count > 0)
            {
                var next = _pendingExternal[0];
                _pendingExternal.RemoveAt(0);
                _active = true;
                _currentBatchId = next.batchId;
                StartCoroutine(PresentQueuedExternal(next.batchId, next.offers, next.onChosen));
            }
        }

        /// <summary>
        /// M37：选完一张后把队列里的下一批弹出来。
        /// 必须**延后**到帧末之后：Destroy(panel) 到帧末才真正生效，立刻新建的话
        /// transform.Find("LevelUpCanvas") 会先找到刚被标记销毁的那一块。
        /// </summary>
        private System.Collections.IEnumerator PresentQueuedExternal(int batchId,
                                                                     List<UpgradeOffer> offers,
                                                                     System.Action<int> onChosen)
        {
            yield return null;
            yield return null;
            if (this == null) yield break;        // 期间可能被拆世界
            _chooser = onChosen;
            _currentBatchId = batchId;            // M38：补弹的这一批也要能被撤销
            BuildPanel(offers);
            ExternalPresented++;
        }

        private void ShowPanel(List<UpgradeOffer> offers, System.Action<int> choose, bool pause)
        {
            _active = true;
            _chooser = choose;
            // 暂停改由 HitstopManager 统一掌管（避免与 hitstop 顿帧互相覆盖 timeScale）
            if (pause)
            {
                if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(true);
                else Time.timeScale = 0f;
            }
            BuildPanel(offers);
        }

        private System.Action<int> _chooser;

        private void BuildPanel(List<UpgradeOffer> offers)
        {
            // M40：记下**这一组真正上屏的文字**（含描述），供冒烟脚本核对语言。
            // 放在 BuildPanel 而不是 PresentExternal：本地路径、联机路径、队列补弹
            // 三条路最后都汇到这里，记一处就全盖住了。
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < offers.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(offers[i].title);
                if (!string.IsNullOrEmpty(offers[i].desc)) sb.Append(" — ").Append(offers[i].desc);
            }
            LastPresentedText = sb.ToString();

            var canvasGO = new GameObject("LevelUpCanvas");
            canvasGO.transform.SetParent(transform, false);  // M5：随管理器一起拆世界
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // M51：选卡面板必须压得住 HUD。此前它与 HUDCanvas 同为 sortingOrder 0，
            // 谁在上面不由代码决定——CARD 场景实测里排出来的结果是 HUD 在上，
            // 卡片按钮恰好落在血条的填充图上：看得见，点不到。
            canvas.sortingOrder = Core.UIFactory.ModalSortingOrder;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // 半透明遮罩（初始全透明，入场淡入）
            var bg = new GameObject("BG");
            bg.transform.SetParent(canvasGO.transform);
            var img = bg.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            var brt = bg.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero;
            brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero;
            brt.offsetMax = Vector2.zero;

            var title = CreateText(canvasGO.transform, Core.Localization.T("levelup.title"), new Vector2(0, 200), 32);

            var cards = new List<RectTransform>();
            for (int i = 0; i < offers.Count; i++)
            {
                var o = offers[i];
                var idx = i;
                var btnGO = new GameObject("Offer" + i);
                btnGO.transform.SetParent(canvasGO.transform);
                var btn = btnGO.AddComponent<Button>();
                var bimg = btnGO.AddComponent<Image>();
                bimg.color = new Color(0.2f, 0.4f, 0.7f);
                var rt = btnGO.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0, 90 - i * 95);
                rt.sizeDelta = new Vector2(440, 80);

                CreateText(btnGO.transform, o.title + "\n" + o.desc, Vector2.zero, 20);

                btn.onClick.AddListener(() => Choose(idx, canvasGO));
                cards.Add(rt);
            }

            // M8 手感：遮罩淡入 + 标题弹入 + 卡片依次缩放弹入（用 unscaledDeltaTime，暂停态也能播）
            StartCoroutine(AnimateIn(cards, img, title != null ? title.rectTransform : null));
        }

        /// <summary>选卡面板入场演出：遮罩淡入、标题放大弹入、卡片错峰缩放弹入。</summary>
        private IEnumerator AnimateIn(List<RectTransform> cards, Image bgImg, RectTransform titleRt)
        {
            const float dur = 0.34f;
            const float stagger = 0.07f;
            Color bgStart = bgImg != null ? bgImg.color : Color.clear;
            Color bgEnd = new Color(0f, 0f, 0f, 0.6f);
            if (titleRt != null) titleRt.localScale = Vector3.zero;
            foreach (var c in cards) c.localScale = Vector3.zero;

            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                float e = 1f - (1f - k) * (1f - k);   // easeOutQuad
                if (bgImg != null) bgImg.color = Color.Lerp(bgStart, bgEnd, e);
                if (titleRt != null) titleRt.localScale = Vector3.one * e;
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i] == null) continue;    // 面板可能已被销毁（用户提前点选）
                    float ck = Mathf.Clamp01((t - i * stagger) / (dur - stagger));
                    float ce = 1f - (1f - ck) * (1f - ck);
                    cards[i].localScale = Vector3.one * ce;
                }
                yield return null;
            }
            if (bgImg != null) bgImg.color = bgEnd;
            if (titleRt != null) titleRt.localScale = Vector3.one;
            foreach (var c in cards) if (c != null) c.localScale = Vector3.one;
        }

        private void Choose(int idx, GameObject panel)
        {
            _chooser?.Invoke(idx);
            Destroy(panel);
            _active = false;
            _chooser = null;
            _currentBatchId = 0;                  // M38：这一批已经选掉，别再被作废通知误伤
            _pending = Mathf.Max(0, _pending - 1);

            // M37：先消化外部队列（联机客户端的全部选卡都从这里来），再回到本地待弹计数。
            // 立刻把 _active 置回 true 占位，避免延后的这两帧里被新推卡插队。
            if (_pendingExternal.Count > 0)
            {
                var next = _pendingExternal[0];
                _pendingExternal.RemoveAt(0);
                _active = true;
                _currentBatchId = next.batchId;
                StartCoroutine(PresentQueuedExternal(next.batchId, next.offers, next.onChosen));
                return;
            }

            if (_pending > 0) TryPresent();
            else
            {
                // M41：这一处是面板"选完了、恢复世界"的出口——与 OnDestroy 里的兜底同一类，
                // 写死 1f 会在加速档下把倍率拽回 1（第 19 项静态检查抓到的就是它）
                if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
                else Time.timeScale = Core.HitstopManager.BaseScale;
            }
        }

        private static Text CreateText(Transform parent, string content, Vector2 pos, int size)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent);
            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = GetUIFont();
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = size;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(420, 72);
            return t;
        }

        /// <summary>兼容不同 Unity 版本的内置字体名（LegacyRuntime / Arial）。</summary>
        private static Font GetUIFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null)
            {
                try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            return f;
        }
    }
}
