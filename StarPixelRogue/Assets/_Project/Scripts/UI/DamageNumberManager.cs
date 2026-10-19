using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 跳字伤害管理器：监听 OnDamaged 事件，在受击位置生成上浮数字（对应 TDD M2 打击感）。
    ///
    /// M27：改为对象池。此前每次伤害都是 new GameObject + AddComponent&lt;Text&gt;
    /// + 两次 Resources.GetBuiltinResource（字体回退链），重战况下每秒数百次分配，
    /// 与项目「对象池抗 GC」的原则相悖，而敌人/弹丸/宝石早就都池化了。
    /// 现在 Awake 时建一个**不活跃**的模板，之后一律走 PoolManager.Get / Release 复用：
    /// 字体只在建模板时查一次（UIFactory 自带缓存），运行时零分配。
    ///
    /// 归属：模板挂在自己的画布下（画布又是本管理器的子物体），克隆体由 PoolManager 登记，
    /// 拆世界时两边都能清掉（M24/M26 的所有权教训）。
    /// </summary>
    public class DamageNumberManager : Core.MonoSingleton<DamageNumberManager>
    {
        private Canvas _canvas;
        private GameObject _template;

        protected override void Awake()
        {
            base.Awake();
            var go = new GameObject("DmgCanvas");
            go.transform.SetParent(transform, false);   // M5：随管理器一起拆世界，防重开后残留画布
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();
            BuildTemplate();
            Core.EventBus.OnDamaged += Show;
        }

        void OnDestroy()
        {
            // M4.5：断线拆世界后重建时避免旧实例继续响应（画布已销毁会报错）
            Core.EventBus.OnDamaged -= Show;
        }

        /// <summary>
        /// 建池模板。命名以 Template 结尾，便于实测脚本把「模板」和「场上在用的」区分开。
        /// </summary>
        private void BuildTemplate()
        {
            var t = new GameObject("DmgTemplate");
            t.SetActive(false);          // AddComponent 会同步跑 Awake/OnEnable：先关掉，装配完再不管
            t.transform.SetParent(_canvas.transform, false);

            var text = t.AddComponent<Text>();
            // M26 前这里是每次伤害现查一遍内置字体；UIFactory.UIFont 自带缓存且与其余 UI 同一实例。
            text.font = Core.UIFactory.UIFont;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 22;
            text.color = new Color(1f, 0.85f, 0.2f);

            var rt = t.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(80, 40);
            t.AddComponent<FloatingText>();
            _template = t;
        }

        private void Show(float amount, Vector3 worldPos)
        {
            var cam = Camera.main;
            if (cam == null || _template == null) return;
            Vector3 sp = cam.WorldToScreenPoint(worldPos);

            var pool = Core.PoolManager.Instance;
            GameObject go;
            if (pool != null)
            {
                go = pool.Get(_template);
                // Object.Instantiate 会把克隆体落在**场景根节点**上（和 M24 的宝石/敌人一样）；
                // 不挂回画布的话 uGUI 的 Text 根本不会被渲染，玩家什么都看不到。
                if (go.transform.parent != _canvas.transform)
                    go.transform.SetParent(_canvas.transform, false);
            }
            else
            {
                // 池还没建（理论上不会发生）：退回原路径，至少保证数字还能显示
                go = Instantiate(_template);
                go.transform.SetParent(_canvas.transform, false);
                go.SetActive(true);
            }

            var t = go.GetComponent<Text>();
            if (t != null) t.text = Mathf.RoundToInt(amount).ToString();

            var ft = go.GetComponent<FloatingText>();
            if (ft != null) ft.Init(sp, pool, _template);
        }

        /// <summary>池模板（实测脚本用它确认「模板不算场上在用的」）。</summary>
        public GameObject Template => _template;
    }
}
