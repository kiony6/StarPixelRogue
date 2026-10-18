using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 跳字伤害管理器：监听 OnDamaged 事件，在受击位置生成上浮数字（对应 TDD M2 打击感）。
    /// </summary>
    public class DamageNumberManager : Core.MonoSingleton<DamageNumberManager>
    {
        private Canvas _canvas;

        protected override void Awake()
        {
            base.Awake();
            var go = new GameObject("DmgCanvas");
            go.transform.SetParent(transform, false);   // M5：随管理器一起拆世界，防重开后残留画布
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();
            Core.EventBus.OnDamaged += Show;
        }

        void OnDestroy()
        {
            // M4.5：断线拆世界后重建时避免旧实例继续响应（画布已销毁会报错）
            Core.EventBus.OnDamaged -= Show;
        }

        private void Show(float amount, Vector3 worldPos)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(worldPos);

            var go = new GameObject("Dmg");
            go.transform.SetParent(_canvas.transform);
            var t = go.AddComponent<Text>();
            t.text = Mathf.RoundToInt(amount).ToString();
            t.font = GetUIFont();
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = 22;
            t.color = new Color(1f, 0.85f, 0.2f);

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(80, 40);
            var ft = go.AddComponent<FloatingText>();
            ft.Init(sp);
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
