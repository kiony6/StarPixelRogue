using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 界面星空背景（M53）：一层缓慢漂移、明暗交替的星点。
    ///
    /// 挂在某个 Canvas 之下作为它的**第一个子节点**——这样它自然落在同层最底，
    /// 不需要为此新建一个 Canvas。刻意不新建：又一个 Overlay 就是又一次
    /// "谁压谁"的题（M51 抓的就是两块同层 Overlay 谁先挡住 Metro），
    /// 而把背景放进现有画布里，层级关系在代码里是看得见的。
    ///
    /// 用锚点而不是绝对坐标落位：这样一来无论窗口什么比例，星点始终铺满整屏，
    /// 不需要知道当前分辨率——直写 1280×720 在带鱼屏上会留下一整条没有星的空边。
    /// </summary>
    public class StarfieldUI : MonoBehaviour
    {
        private class Dot
        {
            public RectTransform rt;
            public Image img;
            public float speed;        // 每秒下落像素
            public float twinkleRate;
            public float phase;
            public float baseAlpha;
            public float driftX;
        }

        private readonly List<Dot> _dots = new List<Dot>();
        private RectTransform _canvasRect;
        private float _boundsY = 720f;
        private float _resync;

        /// <summary>
        /// 生成星点。
        /// </summary>
        /// <param name="count">星点数量。</param>
        /// <param name="tint">基色（每颗的亮度在它上面再随一次）。</param>
        public void Build(int count, Color tint)
        {
            _canvasRect = transform.parent as RectTransform;
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Star", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                var rt = (RectTransform)go.transform;

                // 归一化锚点：位置按比例，不受窗口尺寸变化影响
                float ax = Random.Range(0f, 1f);
                float ay = Random.Range(0f, 1f);
                rt.anchorMin = new Vector2(ax, ay);
                rt.anchorMax = new Vector2(ax, ay);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;

                // 三档大小：近处的星大且亮，远处的小而暗——这是 depth 的唯一线索
                float depth = Random.value;
                float size = depth > 0.9f ? Random.Range(3.4f, 5.0f)
                           : depth > 0.6f ? Random.Range(2.0f, 3.2f)
                                          : Random.Range(1.2f, 1.8f);
                rt.sizeDelta = new Vector2(size, size);

                var img = go.AddComponent<Image>();
                img.sprite = Core.SpriteFactory.MakeRoundPanel();
                img.type = Image.Type.Sliced;
                float bright = Mathf.Lerp(0.35f, 1f, depth);
                img.color = new Color(tint.r * bright, tint.g * bright, tint.b * bright,
                    Random.Range(0.25f, 0.85f));
                img.raycastTarget = false;      // 背景不许参与点击判定

                _dots.Add(new Dot
                {
                    rt = rt,
                    img = img,
                    speed = size * Random.Range(1.2f, 3.0f),          // 越大的星掉得越快 = 越近
                    twinkleRate = Random.Range(0.5f, 2.2f),
                    phase = Random.Range(0f, Mathf.PI * 2f),
                    baseAlpha = img.color.a,
                    driftX = Random.Range(-4f, 4f),
                });
            }
        }

        void Update()
        {
            if (_dots.Count == 0) return;

            // 画布高度偶发重取：窗口被拉伸时星点会漂出边界再也回不来。
            // 每 0.5s 采一次就够了——这是个长周期量，不必每帧都读一次 rect。
            _resync -= Time.unscaledDeltaTime;
            if (_resync <= 0f)
            {
                _resync = 0.5f;
                if (_canvasRect != null && _canvasRect.rect.height > 1f)
                    _boundsY = _canvasRect.rect.height;
            }

            float half = _boundsY * 0.5f;
            for (int i = 0; i < _dots.Count; i++)
            {
                var d = _dots[i];
                if (d.rt == null) continue;
                var pos = d.rt.anchoredPosition;
                pos.y -= d.speed * Time.unscaledDeltaTime;
                pos.x += d.driftX * Time.unscaledDeltaTime;
                // 用 unscaledTime：主菜单不会暂停，但**结算面板弹起时 timeScale 会被压到 0**，
                // 若走 scaledTime，背景会当场冻住，跟"整块界面死机了"没有区别。
                if (pos.y < -half) pos.y += _boundsY;
                if (pos.y > half) pos.y -= _boundsY;
                if (pos.x < -half) pos.x = half;
                else if (pos.x > half) pos.x = -half;
                d.rt.anchoredPosition = pos;

                if (d.img != null)
                {
                    float tw = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * d.twinkleRate + d.phase);
                    var c = d.img.color;
                    c.a = Mathf.Clamp01(d.baseAlpha * tw);
                    d.img.color = c;
                }
            }
        }
    }
}
