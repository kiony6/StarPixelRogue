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
            public List<UpgradeOffer> offers;
            public System.Action<int> onChosen;
        }

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
        /// <summary>面板是否正在展示（供探针判定"是否在选卡态"，不许再靠 timeScale 推断）。</summary>
        public bool IsShowing { get { return _active; } }
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
            // M4.5：断线拆世界时防止时间缩放卡在 0（选卡面板打开中被销毁）
            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
            else Time.timeScale = 1f;
        }

        /// <summary>M4.5：切换「外部供卡」模式。联机客户端开启后，本地不再自动建卡
        /// （客户端武器库/状态未同步，本地 BuildOffers 会弹错面板并污染本地状态）。</summary>
        public void SetExternalCards(bool external)
        {
            _externalCards = external;
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
                _pendingExternal.Add(new PendingExternal { offers = offers, onChosen = onChosen });
                ExternalQueued++;
                return;
            }
            ShowPanel(offers, onChosen, false);   // 联机下不暂停（避免冻结网络心跳）
            ExternalPresented++;
        }

        /// <summary>
        /// M37：选完一张后把队列里的下一批弹出来。
        /// 必须**延后**到帧末之后：Destroy(panel) 到帧末才真正生效，立刻新建的话
        /// transform.Find("LevelUpCanvas") 会先找到刚被标记销毁的那一块。
        /// </summary>
        private System.Collections.IEnumerator PresentQueuedExternal(List<UpgradeOffer> offers,
                                                                     System.Action<int> onChosen)
        {
            yield return null;
            yield return null;
            if (this == null) yield break;        // 期间可能被拆世界
            _chooser = onChosen;
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
            var canvasGO = new GameObject("LevelUpCanvas");
            canvasGO.transform.SetParent(transform, false);  // M5：随管理器一起拆世界
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
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
            _pending = Mathf.Max(0, _pending - 1);

            // M37：先消化外部队列（联机客户端的全部选卡都从这里来），再回到本地待弹计数。
            // 立刻把 _active 置回 true 占位，避免延后的这两帧里被新推卡插队。
            if (_pendingExternal.Count > 0)
            {
                var next = _pendingExternal[0];
                _pendingExternal.RemoveAt(0);
                _active = true;
                StartCoroutine(PresentQueuedExternal(next.offers, next.onChosen));
                return;
            }

            if (_pending > 0) TryPresent();
            else
            {
                if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
                else Time.timeScale = 1f;
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
