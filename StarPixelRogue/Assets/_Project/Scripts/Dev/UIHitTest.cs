using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M48：UI 命中测试——按**真实点击走的那条路**判断一个按钮此刻点不点得到。
    ///
    /// 为什么要有它：
    ///   此前所有探针"点"按钮都是 <c>btn.onClick.Invoke()</c>——那是**直接调用回调**，
    ///   把 EventSystem 的 raycast 那一层整段绕过去了。于是"一层全屏遮罩盖在按钮上"
    ///   这类缺陷在探针眼里压根不存在：探针照样能把按钮"点"响，判据照样绿。
    ///   M47 记下的遗留第 2 条就是这个——缺陷 B（时长遮罩跨局残留）的**行为后果**
    ///   只被验证了一半：验了"遮罩还在"，没验"玩家因此点不到按钮"。
    ///
    /// 本类补的就是被绕过的那一层：
    ///   从按钮中心发一次 <c>PointerEventData</c>，让场上每个 <c>GraphicRaycaster</c>
    ///   各自 <c>Raycast</c>，再按 EventSystem 的排序规则取最上面那一个
    ///   （sortOrderPriority → renderOrderPriority → sortingLayer → sortingOrder → depth），
    ///   看它是不是按钮自己。不是，就说明有东西挡着，<c>Blocker</c> 给出挡路者的名字。
    ///
    /// 判据怎么用它才不算假绿（这个更重要）：
    ///   "点不到"这件事单独看没有区分力——raycast 全不命中时它也是"点不到"。
    ///   所以每条"应该点不到"的判据都必须配一条"应该点得到"的对照：
    ///     对照（无遮罩）必须点得到，被测（有遮罩）必须点不到，二者同时成立才有意义。
    ///
    /// 仅用于无头实测（Dev 目录），不参与产品逻辑。
    /// </summary>
    public static class UIHitTest
    {
        /// <summary>一次命中测试的结果。</summary>
        public struct Hit
        {
            /// <summary>按钮找着了（存在、激活、带 RectTransform）。找不到时下面各项都没有意义。</summary>
            public bool Found;
            /// <summary>真实点击能不能点到它（raycast 最上面那层就是它）。</summary>
            public bool Reachable;
            /// <summary>最上面那层的对象名：可达时是按钮自己，不可达时是挡路者。</summary>
            public string Blocker;
            /// <summary>最上面那层所属的**根画布**名。判"屏幕上盖着谁"用它，不看对象名。</summary>
            public string TopCanvas;
            /// <summary>参与本次测试的 GraphicRaycaster 数量（排查"压根没人接"用）。</summary>
            public int Raycasters;
            /// <summary>raycast 命中总数（排查"所有画布都没接住"用）。</summary>
            public int Hits;
            /// <summary>按钮中心的屏幕坐标（排查无头下布局塌成一点用）。</summary>
            public string At;
            /// <summary>Screen 尺寸（无头下的画布尺寸并不总是有意义的，先记下来）。</summary>
            public string Screen;
            /// <summary>一句话说明，直接进日志。</summary>
            public string Detail;
        }

        /// <summary>
        /// 对屏幕上一个点做命中测试：返回此刻**盖在这个点最上面**的是谁。
        ///
        /// 与 <see cref="Probe(Button)"/> 的区别：它不针对任何按钮，
        /// 判的是"这块屏幕现在归谁"——比如新一局开局时，屏幕中心盖着的是 HUD
        /// 还是上一局残留的模态遮罩。
        /// </summary>
        public static Hit ProbePoint(Vector2 screenPos)
        {
            var r = new Hit { Screen = Screen.width + "x" + Screen.height, Blocker = "-",
                              At = Mathf.RoundToInt(screenPos.x) + "," + Mathf.RoundToInt(screenPos.y),
                              Found = true, TopCanvas = "-" };

            Canvas.ForceUpdateCanvases();      // 同 Probe：无头下不渲染就不会有 layout
            var data = new PointerEventData(EventSystem.current) { position = screenPos };
            var results = new List<RaycastResult>();
            foreach (var rc in Resources.FindObjectsOfTypeAll<GraphicRaycaster>())
            {
                if (rc == null || !rc.isActiveAndEnabled) continue;
                var rgo = rc.gameObject;
                if (!rgo.scene.IsValid() || !rgo.activeInHierarchy) continue;
                r.Raycasters++;
                var one = new List<RaycastResult>();
                rc.Raycast(data, one);
                results.AddRange(one);
            }
            r.Hits = results.Count;

            if (results.Count == 0)
            {
                r.TopCanvas = "";
                r.Blocker = "(无)";
                r.Detail = "该点无 UI（射线 " + r.Raycasters + "）";
                return r;
            }

            results.Sort(Compare);
            var top = results[0].gameObject;
            r.Blocker = top != null ? top.name : "(null)";
            r.TopCanvas = RootCanvasName(top);
            var order = "";
            for (int i = 0; i < results.Count && i < 3; i++)
                order += (i > 0 ? " > " : "") + Sig(results[i]);
            r.Reachable = true;                // 对"一个点"而言没有可达与否，恒为 true
            r.Detail = "顶层 " + r.Blocker + "（画布 " + (r.TopCanvas == "" ? "-" : r.TopCanvas) +
                       "）｜排序 " + order;
            return r;
        }

        private static string RootCanvasName(GameObject go)
        {
            if (go == null) return "";
            var canvas = go.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            return root != null && root.gameObject != null ? root.gameObject.name : "";
        }

        /// <summary>对指定按钮做一次命中测试。btn 为 null 或不在场景里时 Found = false。</summary>
        public static Hit Probe(Button btn)
        {
            var r = new Hit { Screen = Screen.width + "x" + Screen.height, Blocker = "-",
                              At = "-", TopCanvas = "-" };

            if (btn == null) { r.Detail = "按钮不存在"; return r; }
            var go = btn.gameObject;
            if (!go.scene.IsValid()) { r.Detail = "按钮不在场景里"; return r; }
            if (!go.activeInHierarchy) { r.Detail = "按钮未激活"; return r; }
            var rt = btn.transform as RectTransform;
            if (rt == null) { r.Detail = "按钮没有 RectTransform"; return r; }
            r.Found = true;

            // 按钮中心的屏幕坐标。Overlay 画布下 RectTransform 的世界坐标就是屏幕像素坐标
            // （root canvas 被摆到屏幕中心，localScale 由 CanvasScaler 给出）；
            // 其余两种模式得老老实实过一遍相机。
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 center = (corners[0] + corners[2]) * 0.5f;
            var canvas = btn.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                var cam = canvas.worldCamera;
                if (cam != null) center = RectTransformUtility.WorldToScreenPoint(cam, center);
            }
            r.At = Mathf.RoundToInt(center.x) + "," + Mathf.RoundToInt(center.y);

            // 【无头关键】`-nographics` 下 Unity 不渲染，于是 Canvas 的 layout rebuild
            // 那一趟**根本不会发生**——凡是靠 anchor 拉伸的元素（全屏遮罩正是这种：
            // anchorMin/max = 0/1、offset = 0），RectTransform 的实际矩形会一直停在 0x0，
            // raycast 也就永远打不到它。表现是"遮罩明明在那儿，命中测试却说没挡住"。
            // 固定 sizeDelta 的元素（按钮、文本）不受影响，所以这个坑只会让**遮罩**消失——
            // 恰好是本类唯一要抓的东西。故测试前先手动跑一趟强制布局。
            Canvas.ForceUpdateCanvases();

            var data = new PointerEventData(EventSystem.current) { position = center };
            var results = new List<RaycastResult>();
            foreach (var rc in Resources.FindObjectsOfTypeAll<GraphicRaycaster>())
            {
                if (rc == null || !rc.isActiveAndEnabled) continue;
                var rgo = rc.gameObject;
                if (!rgo.scene.IsValid() || !rgo.activeInHierarchy) continue;
                r.Raycasters++;
                var one = new List<RaycastResult>();
                rc.Raycast(data, one);
                results.AddRange(one);
            }
            r.Hits = results.Count;

            if (results.Count == 0)
            {
                // 一个都没接住：要么按钮被摆到了画布外，要么无头下画布尺寸塌了。
                // 记清楚，别让它伪装成"被遮罩挡住"。
                r.Detail = "raycast 无命中（射线 " + r.Raycasters + "，屏幕 " + r.Screen + "，按钮中心 " + r.At + "）";
                return r;
            }

            results.Sort(Compare);
            var top = results[0].gameObject;
            r.Blocker = top != null ? top.name : "(null)";
            r.TopCanvas = RootCanvasName(top);
            // 命中按钮自身，或命中按钮的子物体（按钮的 targetGraphic 常常挂在子级），都算点得到
            r.Reachable = top == go || top.transform.IsChildOf(btn.transform);
            // 排序依据一起打出来：多层画布叠放时"谁在上"全靠它，
            // 而它在无头下并不可靠（见 Compare 的注释）——不看见这个数就不能信结论。
            var order = "";
            for (int i = 0; i < results.Count && i < 3; i++)
                order += (i > 0 ? " > " : "") + Sig(results[i]);
            r.Detail = (r.Reachable ? "可点到" : "被挡住") + "（顶层 " + r.Blocker + "）｜排序 " + order;
            return r;
        }

        /// <summary>一个命中结果的"层级指纹"：所属根画布 + sortOrder / renderOrder / 场景根序号。</summary>
        private static string Sig(RaycastResult r)
        {
            var go = r.gameObject;
            var canvas = go != null ? go.GetComponentInParent<Canvas>() : null;
            var root = canvas != null ? canvas.rootCanvas : null;
            return (go != null ? go.name : "?") +
                   "[画布 " + (root != null ? root.name : "-") +
                   " so=" + (root != null ? root.sortingOrder.ToString() : "-") +
                   " ro=" + (root != null ? root.renderOrder.ToString() : "-") +
                   " sib=" + RootSibling(r) + "]";
        }

        /// <summary>根画布在场景根上的序号。无头下它是"创建顺序"唯一的可见代理。</summary>
        private static int RootSibling(RaycastResult r)
        {
            var go = r.gameObject;
            if (go == null) return -1;
            var canvas = go.GetComponentInParent<Canvas>();
            if (canvas == null) return -1;
            var root = canvas.rootCanvas;
            return root != null ? root.transform.GetSiblingIndex() : -1;
        }

        /// <summary>
        /// 复刻 <c>EventSystem.RaycastComparer</c>：Unity 那份是 private 的，拿不到。
        /// 排序方向是"越靠上排越前"（与 EventSystem 一致），故每一项都反着比。
        /// </summary>
        private static int Compare(RaycastResult lhs, RaycastResult rhs)
        {
            if (lhs.module != rhs.module)
            {
                var lc = lhs.module != null ? lhs.module.eventCamera : null;
                var rc = rhs.module != null ? rhs.module.eventCamera : null;
                if (lc != null && rc != null && lc.depth != rc.depth)
                    return rc.depth.CompareTo(lc.depth);
                int ls = lhs.module != null ? lhs.module.sortOrderPriority : 0;
                int rs = rhs.module != null ? rhs.module.sortOrderPriority : 0;
                if (ls != rs) return rs.CompareTo(ls);
                int lr = lhs.module != null ? lhs.module.renderOrderPriority : 0;
                int rr = rhs.module != null ? rhs.module.renderOrderPriority : 0;
                if (lr != rr) return rr.CompareTo(lr);

                // 【无头补充】renderOrderPriority 取的是 Canvas.renderOrder，而它只在
                // 真正渲染（WillRenderCanvases）那一趟里被分配——`-nographics` 下恒为 0，
                // 于是上面这一层**退化成了摆设**：两块 overlay 画布谁在上，判不出来。
                // 真实渲染时同 sortOrder 的 overlay 画布按注册顺序叠放（后建的在上），
                // 而"场景根上的序号"是它在无头下唯一可观测的代理，故补在这里。
                // 见 Sig()：判据日志里会打出 so / ro / sib 三个数，ro 若恒为 0 就是这种情况。
                int lsib = RootSibling(lhs), rsib = RootSibling(rhs);
                if (lsib != rsib) return rsib.CompareTo(lsib);
            }
            if (lhs.sortingLayer != rhs.sortingLayer)
                return SortingLayer.GetLayerValueFromID(rhs.sortingLayer)
                    .CompareTo(SortingLayer.GetLayerValueFromID(lhs.sortingLayer));
            if (lhs.sortingOrder != rhs.sortingOrder) return rhs.sortingOrder.CompareTo(lhs.sortingOrder);
            if (lhs.depth != rhs.depth) return rhs.depth.CompareTo(lhs.depth);
            return lhs.distance.CompareTo(rhs.distance);
        }

        /// <summary>日志用的一句话结果。</summary>
        public static string Describe(Hit h)
        {
            if (!h.Found) return "否（" + h.Detail + "）";
            return (h.Reachable ? "是" : "否") + "（" + h.Detail + "）";
        }
    }
}
