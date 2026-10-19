using StarPixel.Core;
using StarPixel.Core.CloudSave;
using StarPixel.Enemy;
using StarPixel.Player;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M13 云存档无头实测探针（M18）：仅当进程带 <c>-cloudSaveProbe</c> 时由 <c>GameBuilder</c> 创建。
    ///
    /// 为什么要单独做这个探针：云存档是全局唯一一个「默认关闭、但在真实运行时会联网」的模块，
    /// 而它此前没有任何实测覆盖——包括最该被验证的那条承诺：**未开启/未配置时零网络请求**。
    /// 这条承诺光读代码只能"推断"，无法"证明"，所以它需要一个可观测的计数器（<see cref="ICloudSaveProvider.RequestCount"/>）。
    ///
    /// 实测矩阵（一次进程跑一个场景，由 cloudsave_probe.py 串起来）：
    ///   A 未配置      → 期望禁用、请求 0
    ///   B 非 HTTPS    → 期望因安全校验被禁、请求 0     ← 验证隐私承诺在代码层面成立
    ///   C 用户未开启  → 期望禁用、请求 0
    ///   D 未同意隐私  → 期望禁用、请求 0
    ///   E 全开+不可达 → 期望真的发了请求、但失败静默降级、玩法不受影响
    ///
    /// 判据设计要点（区分力）：
    /// - 「期望是否启用」由外部脚本通过 <c>-cloudSaveExpect</c> 传入，**不**由本探针从被测对象推导。
    ///   若让探针读 CloudSaveService.Enabled 当作期望值，那么实现一旦把 http 误判为已配置，
    ///   探针会跟着判"应该启用"并自我印证——这正是之前 M11 假通过踩过的坑。
    /// - 场景 E 必须断言「请求数 > 0」：只断言"没崩溃"的话，实现压根不联网也能通过。
    /// - 所有场景都断言「玩法仍在推进 + 零异常」：云存档无论开关都必须不砸单机体验。
    ///
    /// 正常启动不带该参数时本组件不会被创建，对游戏零影响。
    /// </summary>
    public class CloudSaveProbe : MonoBehaviour
    {
        private float _limit;
        private float _t;
        private int _kills;
        private int _maxLevel = 1;
        private bool _done;

        /// <summary>运行期 Error/Exception 日志条数（云存档失败只允许静默降级，不允许抛异常）。</summary>
        private int _errors;

        private bool _expectEnabled;
        private string _scene = "";
        private string _url = "";
        private bool _userOn;
        private bool _agreed;

        /// <summary>E 场景：主动触发一次拉取与一次上传，确保两条网络路径都被走到。</summary>
        private bool _pullKicked;
        private bool _pushKicked;

        /// <summary>
        /// 启动一次云存档实测。
        /// 调用顺序很关键：必须先摆好用户开关与后端配置，再 <see cref="CloudSaveService.Ensure"/>——
        /// 服务在 Awake 里读配置、在 Start 里判定 Enabled，事后再改就晚了。
        /// </summary>
        public static void Start(float seconds, string scene, string url, bool userOn, bool agreed,
                                 int timeoutSeconds, bool expectEnabled)
        {
            // ① 用户开关与同意态：直接改内存字段、不落盘，避免污染真实用户的 settings.json
            var s = SettingsManager.Instance;
            if (s == null) { SettingsManager.Ensure(); s = SettingsManager.Instance; }
            if (s != null)
            {
                s.Current.cloudSave = userOn;
                s.Current.agreedTermsVersion = agreed ? VersionInfo.ComplianceVersion : "";
            }

            // ② 后端配置：仅在给出地址时覆盖；不给 = 保持"未配置"的真实默认态
            if (!string.IsNullOrEmpty(url))
            {
                CloudSaveService.OverrideConfig = new CloudSaveService.Config
                {
                    baseUrl = url,
                    timeoutSeconds = timeoutSeconds,
                };
            }
            CloudSaveService.Ensure();

            var go = new GameObject("CloudSaveProbe");
            var p = go.AddComponent<CloudSaveProbe>();
            p._limit = Mathf.Max(5f, seconds);
            p._scene = scene ?? "";
            p._url = url ?? "";
            p._userOn = userOn;
            p._agreed = agreed;
            p._expectEnabled = expectEnabled;

            p.Subscribe();
            Debug.Log("[SMOKE] 云存档实测启动  场景=" + p._scene +
                      " / 时长上限 " + p._limit.ToString("F1") + "s" +
                      " / 期望启用=" + (expectEnabled ? "是" : "否"));
        }

        private void Subscribe()
        {
            EventBus.OnEnemyKilled += _ => _kills++;
            EventBus.OnLevelUp += lv => { if (lv > _maxLevel) _maxLevel = lv; };
            Application.logMessageReceived += OnLog;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _errors++;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        void Update()
        {
            if (_done) return;
            _t += Time.unscaledDeltaTime;

            var svc = CloudSaveService.Instance;

            // E 场景：主动走一次拉取（3s）与一次上传（8s），
            // 保证「拉取失败降级」和「上传失败重试有限」两条路径都被真正执行到，
            // 而不是只靠 Start() 里那一次被动拉取——那样请求数 1 无法区分"降级逻辑"与"根本没联网"。
            if (svc != null)
            {
                if (!_pullKicked && _t >= 3f)
                {
                    _pullKicked = true;
                    svc.SyncNow();
                }
                if (!_pushKicked && _t >= 8f)
                {
                    _pushKicked = true;
                    svc.SchedulePush(true);
                }
            }

            var pc = PlayerController.Instance;
            if (pc != null && pc.level > _maxLevel) _maxLevel = pc.level;

            if (_t >= _limit) Finish();
        }

        private void Finish()
        {
            _done = true;

            var svc = CloudSaveService.Instance;
            var em = EnemyManager.Instance;
            int wave = em != null ? em.CurrentWave : 0;
            int requests = svc != null ? svc.RequestCount : -1;
            bool configured = svc != null && svc.ProviderConfigured;
            bool enabled = svc != null && svc.Enabled;
            // 用 DisabledReasonText（本地化后的成品文案）而非 DisabledReason（本地化键），
            // 保证 [SMOKE] 输出对人或对脚本都可读——cloudsave_probe.py 正是靠它判断
            // 「B 场景的禁用原因是否指向 HTTPS 校验」。
            string reason = svc != null ? svc.DisabledReasonText : "服务缺失";
            if (string.IsNullOrEmpty(reason)) reason = "无（已启用）";

            // —— 判据 ——
            // 1) 云存档无论开关与否，都不能砸单机玩法
            bool playable = _kills > 0 && wave >= 1;
            // 2) 云存档失败只允许静默降级，不允许异常
            bool clean = _errors == 0;
            // 3) 启用与否必须与外部给定的期望一致，且请求数必须与之自洽
            bool gateOk;
            if (_expectEnabled) gateOk = enabled && requests > 0;     // 真启用必须真发请求
            else gateOk = !enabled && requests == 0;                  // 不该启用必须一个请求都不发

            bool pass = playable && clean && gateOk;

            Debug.Log("[SMOKE] ===== 云存档实测结果 =====");
            Debug.Log("[SMOKE] 场景        " + _scene);
            Debug.Log("[SMOKE] 后端地址    " + (string.IsNullOrEmpty(_url) ? "未配置" : _url));
            Debug.Log("[SMOKE] 后端已配置  " + (configured ? "是" : "否"));
            Debug.Log("[SMOKE] 用户开关    " + (_userOn ? "开" : "关"));
            Debug.Log("[SMOKE] 隐私同意    " + (_agreed ? "是" : "否"));
            Debug.Log("[SMOKE] 云存档启用  " + (enabled ? "是" : "否"));
            Debug.Log("[SMOKE] 未启用原因  " + reason);
            // 注意：键与值之间必须留 2 个以上空格——解析脚本的 [SMOKE] 正则靠它切分
            Debug.Log("[SMOKE] 网络请求数  " + requests);
            // 用 LastStatusText（本地化后的成品文案）而非 LastStatus（本地化键），
            // 保证 [SMOKE] 输出对人或对脚本都可读。
            Debug.Log("[SMOKE] 最近状态    " + (svc != null ? svc.LastStatusText : "-"));
            Debug.Log("[SMOKE] 同步成功    " + (svc != null && svc.LastSyncOk ? "是" : "否"));
            Debug.Log("[SMOKE] 击杀        " + _kills);
            Debug.Log("[SMOKE] 波次        " + wave);
            Debug.Log("[SMOKE] 等级        " + _maxLevel);
            Debug.Log("[SMOKE] 运行期异常  " + _errors);
            Debug.Log("[SMOKE] 结论        " + (pass ? "通过" : "未通过"));

            Application.Quit(pass ? 0 : 1);
        }
    }
}
