using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Net
{
    /// <summary>
    /// 联机诊断 HUD（M4.5 实机调优用）：右上角实时显示会话模式 / FPS / 网络实体数。
    /// 不依赖 Fish-Net 类型（编译期隔离），仅在联机模式下由 GameBuilder 创建。
    /// 真机测试时据此判断：客户端是否真的收到了实体同步、帧率是否因同屏实体过多而崩塌。
    /// </summary>
    public class NetStatus : MonoBehaviour
    {
        private Text _text;
        private float _timer;

        void Awake()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("NetStatusCanvas");
            canvasGO.transform.SetParent(transform, false);   // M28：随管理器一起拆世界，防重开后残留画布
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = Core.UIFactory.MenuSortingOrder;   // M51：显式声明层级
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);

            var go = new GameObject("StatusText", typeof(RectTransform));
            go.transform.SetParent(canvasGO.transform, false);
            var t = go.AddComponent<Text>();
            t.font = GetUIFont();
            t.color = new Color(0.75f, 0.9f, 1f, 0.85f);
            t.fontSize = 16;
            t.alignment = TextAnchor.UpperRight;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-16, -16);
            rt.sizeDelta = new Vector2(320, 80);
            _text = t;
        }

        void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = 0.5f;
            if (_text == null) return;

            var sb = new System.Text.StringBuilder();
            sb.Append(NetSession.ModeLabel());
            sb.Append("  FPS ").Append(Mathf.RoundToInt(1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f)));
#if FISHNET_ENABLED
            // M33：这两个标签原本硬编码中文，英文界面下诊断 HUD 会冒中文。
            // 顺带把计数并进占位符，避免"标签本地化了、数字却另拼一段"的割裂。
            sb.Append(Core.Localization.T("net.status_enemies", NetEnemyRegistry.Count));
            sb.Append(Core.Localization.T("net.status_players", NetPlayerRegistry.Count));
#endif
            _text.text = sb.ToString();
        }

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
