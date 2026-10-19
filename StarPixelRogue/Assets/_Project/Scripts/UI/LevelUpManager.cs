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
        private bool _active;
        private int _pending;
        private bool _externalCards;   // M4.5：联机客户端为 true——选卡一律由房主经 PresentExternal 推送

        protected override void Awake()
        {
            base.Awake();
            Core.EventBus.OnLevelUp += _ => { _pending++; TryPresent(); };
        }

        void OnDestroy()
        {
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
            if (_active) return;
            if (offers == null || offers.Count == 0) return;
            ShowPanel(offers, onChosen, false);   // 联机下不暂停（避免冻结网络心跳）
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
