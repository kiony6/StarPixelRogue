using StarPixel.Core;
using StarPixel.UI;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// 设置面板无头探针（M18）：仅当进程带 <c>-uiProbe</c> 时由 <c>GameBuilder</c> 创建。
    ///
    /// 为什么需要它：M13 补了设置页「云存档」开关，而设置面板在无头冒烟里根本不会被创建
    /// （只有从主菜单点「设置」才会），等于这次 UI 改动完全没有验证。
    /// 本探针把它建出来、真的点一下开关、再切一次语言，把三件事变成可断言的事实：
    ///   ① 云存档相关控件都建出来了（名字对得上）；
    ///   ② 点击开关真的写进了 <see cref="SettingsManager"/>（不是只画了个样子）；
    ///   ③ 状态文案在中/英两种语言下都是**成品文案而非本地化键**——
    ///      若 Localization 漏了词条，<c>T()</c> 会原样返回键名，UI 上就会冒出 "cloud.status.off"。
    ///
    /// 第 ③ 条同时顺带修掉一个既存问题：云存档状态原本是硬编码中文，
    /// 切到英文界面会显示中文；现改为存键 + 渲染（见 CloudSaveService.LastStatusText）。
    ///
    /// 正常启动不带该参数时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class UISettingsProbe : MonoBehaviour
    {
        private int _errors;
        private string _firstError = "";
        private bool _done;
        private float _t;

        private bool _stage1, _stage2, _stage3;
        private bool _toggleFound, _statusFound, _syncFound;
        private bool _switchTookEffect;
        private string _zhStatus = "";
        private string _enStatus = "";

        public static void Start()
        {
            var go = new GameObject("UISettingsProbe");
            var p = go.AddComponent<UISettingsProbe>();
            Application.logMessageReceived += p.OnLog;
            // 云存档服务必须先就位：面板的状态文本读的就是它，
            // 否则阶段 1 读到的只是"未启用"这个初始值，而非服务真实的门禁判定结果。
            Core.CloudSave.CloudSaveService.Ensure();
            // 实测环境不污染真实设置：先把云存档开关复位到默认关（不落盘），
            // 探针结束时会再复位一次。
            var s = SettingsManager.Instance;
            if (s != null) s.Current.cloudSave = false;
        }

        /// <summary>
        /// 无头环境必然出现的渲染噪声。NullGfxDevice 下 Canvas 会每帧报同一条错误，
        /// 几秒就能刷出上百条——既不是缺陷，也会把真正的错误淹没，故单列白名单。
        /// </summary>
        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;      // 噪声不计数
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            // 还原语言与云存档开关，避免实测改动留在用户的 settings.json 里
            Localization.SetLanguage(Localization.LangZh, silent: true);
            var s = SettingsManager.Instance;
            if (s != null) s.SetCloudSave(false);
        }

        void Update()
        {
            if (_done) return;
            _t += Time.unscaledDeltaTime;

            if (!_stage1 && _t >= 0.5f) { _stage1 = true; Stage1(); }
            if (!_stage2 && _t >= 1.5f) { _stage2 = true; Stage2(); }
            if (!_stage3 && _t >= 2.5f) { _stage3 = true; Stage3(); }
            if (_t >= 3.5f) Finish();
        }

        /// <summary>阶段 1：建出面板并确认云存档三件套都在。</summary>
        private void Stage1()
        {
            SettingsPanel.Ensure();
            SettingsPanel.Instance?.Show();

            var panel = FindPanel();
            _toggleFound = panel != null && panel.Find("CloudSaveToggle") != null;
            _statusFound = panel != null && panel.Find("CloudStatus") != null;
            _syncFound = panel != null && panel.Find("CloudSyncNow") != null;
        }

        /// <summary>阶段 2：真的点一下开关，确认它写进了设置（而不是只画了个样子）。</summary>
        private void Stage2()
        {
            var panel = FindPanel();
            var toggle = panel != null ? panel.Find("CloudSaveToggle") : null;
            var btn = toggle != null ? toggle.GetComponent<Button>() : null;
            if (btn != null) btn.onClick.Invoke();

            var s = SettingsManager.Instance;
            _switchTookEffect = s != null && s.Current.cloudSave;

            // 后端未配置时，即便开关打开也仍是"未启用"——这正是三重开关该有的行为
            _zhStatus = ReadStatusText();
        }

        /// <summary>阶段 3：切到英文，确认状态文案跟着变（而不是硬编码中文、也不是裸键名）。</summary>
        private void Stage3()
        {
            Localization.SetLanguage(Localization.LangEn);   // 触发 LanguageChanged → 面板重建并 Hide
            SettingsPanel.Instance?.Show();                  // 重建后默认隐藏，这里重新显示
            _enStatus = ReadStatusText();
        }

        private static Transform FindPanel()
        {
            // 必须走 SettingsPanel.PanelRoot 而不是 GameObject.Find("SettingsCanvas")：
            // 语言切换会整体重建面板，而 Destroy 延迟到帧末——Find 很可能命中
            // 尚未销毁的**旧**画布，于是读到的还是重建前（切换前语言）的文案。
            var sp = SettingsPanel.Instance;
            var root = sp != null ? sp.PanelRoot : null;
            return root != null ? root.Find("Panel") : null;
        }

        private static string ReadStatusText()
        {
            var panel = FindPanel();
            var node = panel != null ? panel.Find("CloudStatus") : null;
            var text = node != null ? node.GetComponent<Text>() : null;
            return text != null ? text.text : "";
        }

        private void Finish()
        {
            _done = true;

            // —— 判据 ——
            // 1) 控件齐全
            bool controls = _toggleFound && _statusFound && _syncFound;
            // 2) 开关真的生效
            // 3) 中文文案不是裸键名
            bool zhOk = !string.IsNullOrEmpty(_zhStatus) && !_zhStatus.StartsWith("cloud.status.");
            // 4) 英文文案不是裸键名，且**确实与中文不同**——
            //    若两条相同，说明本地化没生效（回退成了同一份硬编码文案）
            bool enOk = !string.IsNullOrEmpty(_enStatus) && !_enStatus.StartsWith("cloud.status.")
                        && _enStatus != _zhStatus;

            bool pass = controls && _switchTookEffect && zhOk && enOk && _errors == 0;

            Debug.Log("[SMOKE] ===== 设置面板实测结果 =====");
            Debug.Log("[SMOKE] 云存档开关控件  " + (_toggleFound ? "存在" : "缺失"));
            Debug.Log("[SMOKE] 云存档状态控件  " + (_statusFound ? "存在" : "缺失"));
            Debug.Log("[SMOKE] 立即同步控件    " + (_syncFound ? "存在" : "缺失"));
            Debug.Log("[SMOKE] 开关点击生效    " + (_switchTookEffect ? "是" : "否"));
            // 注意：键与值之间必须留 2 个以上空格——解析脚本的 [SMOKE] 正则靠它切分
            Debug.Log("[SMOKE] 中文状态文案    " + _zhStatus);
            Debug.Log("[SMOKE] 英文状态文案    " + _enStatus);
            Debug.Log("[SMOKE] 运行期异常      " + _errors);
            if (_errors > 0)
                Debug.Log("[SMOKE] 首条错误        " + _firstError);
            Debug.Log("[SMOKE] 结论            " + (pass ? "通过" : "未通过"));

            Application.Quit(pass ? 0 : 1);
        }
    }
}
