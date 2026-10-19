using System.Collections;
using System.Collections.Generic;
using StarPixel.Core;
using StarPixel.Enemy;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M25「手感层」无头实测：镜头基准缩放 / Boss 出场推近 / 命中顿帧 / 选卡暂停 / 震屏衰减 / 反馈开关。
    ///
    /// 为什么必须测：
    ///   这一层是**唯一一个"跑得通 ≠ 看得见"的模块**。前面 24 个里程碑全部用 -nographics 的无头产物验收，
    ///   而手感层的输出全是"画面上发生了什么"——相机正交尺寸、顿帧把 timeScale 压到多少、震屏偏了多少。
    ///   于是下面这些实现都可以在既有实测里全绿：
    ///     1) 相机正交尺寸被持续拉向 0（玩家看到的是糊成几个像素的画面）；
    ///     2) 顿帧计时误用被缩放的 deltaTime，一次顿帧变成十几秒慢动作；
    ///     3) 震屏在暂停态永不衰减（选卡面板背后镜头一直抖）；
    ///     4) 关掉「屏幕震动」开关后 Boss 出场推近照旧。
    ///   本探针把"看得见"翻译成可断言的数值，让这一层第一次真正被判分。
    ///
    /// 六个场景：
    ///   STAGE   —— 真实启动路径：PrepareStage → 菜单停留 1.5s → Build。相机正交尺寸必须始终在基准 8 附近。
    ///   ZOOM    —— Boss 出场推近必须生效、必须回弹到基准；推近进行中被 Configure 不得把基准改小。
    ///   HITSTOP —— 顿帧必须真的压 timeScale，且真实时长≈请求时长（证明计时走 unscaledDeltaTime）。
    ///   PAUSE   —— 选卡暂停必须归零、暂停中的顿帧必须被忽略、放开必须回到 1、暂停必须清掉在飞的顿帧。
    ///   SHAKE   —— 震屏必须偏得出去、静置必须衰减完、**暂停中也必须在真实时间里衰减完**。
    ///   GATE    —— 关掉「屏幕震动」后，顿帧 / 震屏 / 推近三者都必须完全不发生。
    ///
    /// 判据的区分力：
    /// 1) 相机缩放一律读 <see cref="Camera.orthographicSize"/> 的**实测值**，不读组件内部字段——
    ///    用被测对象自己的账本判自己等于没判（M21 DECLINE 假通过同类教训）。
    /// 2) 顿帧时长用**墙钟**度量（自己累加 unscaledDeltaTime），并且故意挑 duration=0.30 / scale=0.04 这组参数：
    ///    计时若误用缩放后的 deltaTime，一次顿帧会被自己的 timeScale 拖成 ~7.5 秒，判据立刻爆红。
    /// 3) 震屏幅度用**采样窗口内的最大偏移**，不是瞬时值——随机偏移每帧都换，取瞬时值会时红时绿。
    /// 4) 暂停相关的等待一律走 <see cref="WaitReal"/>（unscaledDeltaTime）：
    ///    WaitForSeconds 会被 timeScale=0 拖成永不返回，整个场景会挂死。
    /// 5) 每个场景都同时断言"必须发生"和"必须结束"两侧：
    ///    只测单边的话，"永远推近"和"从不推近"各能骗过一半。
    /// </summary>
    public class FeelProbe : MonoBehaviour
    {
        private string _scene = "STAGE";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— STAGE ——
        private int _stageChecked;
        private int _stageOk;
        private float _stageMenuOrtho = -1f;
        private float _stageBuildOrtho = -1f;
        private bool _stageMenuHeld;
        private bool _stageBuildHeld;

        // —— ZOOM ——
        private int _zoomChecked;
        private int _zoomOk;
        private float _zoomBase = -1f;
        private float _zoomPunched = -1f;
        private float _zoomBack = -1f;
        private float _zoomAfterCfg = -1f;
        private bool _zoomPunchWorks;
        private bool _zoomBackOk;
        private bool _zoomCfgOk;

        // —— HITSTOP ——
        private int _hsChecked;
        private int _hsOk;
        private float _hsEventScale = -1f;
        private float _hsPulseScale = -1f;
        private float _hsReal = -1f;
        private float _hsFinal = -1f;
        private bool _hsEventOk;
        private bool _hsPressOk;
        private bool _hsDurOk;
        private bool _hsBackOk;

        // —— PAUSE ——
        private int _pauseChecked;
        private int _pauseOk;
        private float _pauseZero = -1f;
        private float _pauseDuring = -1f;
        private float _pauseBack = -1f;
        private float _pauseStack = -1f;
        private bool _pauseZeroOk;
        private bool _pauseIgnoreOk;
        private bool _pauseBackOk;
        private bool _pauseStackOk;

        // —— SHAKE ——
        private int _shakeChecked;
        private int _shakeOk;
        private float _shakeOn = -1f;
        private float _shakeIdle = -1f;
        private float _shakePaused = -1f;
        private float _shakeAfter = -1f;
        private bool _shakeOnOk;
        private bool _shakeIdleOk;
        private bool _shakePausedOk;
        private bool _shakeAfterOk;

        // —— GATE ——
        private int _gateChecked;
        private int _gateOk;
        private float _gateOrthoBefore = -1f;
        private float _gateScale = -1f;
        private float _gateDev = -1f;
        private float _gateOrtho = -1f;
        private bool _gateHsOk;
        private bool _gateShakeOk;
        private bool _gateZoomOk;

        private Transform _marker;
        private Core.CameraShake _shakeComp;
        private float _sampled;

        private const float BaseOrtho = 8f;   // GameBuilder.SetupCamera 里写死的正交尺寸

        private static readonly string[] Noise =
        {
            "screen position out of view frustum",
            "shader",
            "gpu",
            "memorysetup",
        };

        public static void Start(string scene)
        {
            // AddComponent 会同步执行 Awake（M21–M24 同一条坑）：先停用 → 挂载赋值 → 再激活。
            var go = new GameObject("FeelProbe");
            go.SetActive(false);
            var p = go.AddComponent<FeelProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Awake()
        {
            Application.logMessageReceived += OnLog;
            Localization.SetLanguage(Localization.LangZh, silent: true);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var low = (condition ?? "").ToLowerInvariant();
            foreach (var n in Noise)
                if (low.Contains(n)) return;
            _errors++;
            if (string.IsNullOrEmpty(_firstError)) _firstError = condition ?? "";
        }

        private IEnumerator Start()
        {
            yield return null;

            switch (_scene)
            {
                case "ZOOM": yield return Zoom(); break;
                case "HITSTOP": yield return Hitstop(); break;
                case "PAUSE": yield return Pause(); break;
                case "SHAKE": yield return Shake(); break;
                case "GATE": yield return Gate(); break;
                default: yield return Stage(); break;
            }

            Report();
            yield return new WaitForSeconds(0.2f);
            Application.Quit(0);
        }

        // =====================================================================================
        //  公共 helper
        // =====================================================================================

        /// <summary>
        /// 搭一个只有"手感层"的最小舞台：主相机 + CameraShake（已 Configure 到原点标记）+ HitstopManager。
        /// 不调用 GameBuilder.Build()——那会带进玩家/敌人/波次，把镜头与顿帧的观测搅浑。
        /// </summary>
        private IEnumerator EnsureStage()
        {
            if (Camera.main == null) GameBuilder.PrepareStage();
            yield return null;

            var cam = Camera.main;
            if (cam == null) { Fail("主相机缺失"); yield break; }

            var shake = cam.GetComponent<Core.CameraShake>();
            if (shake == null) shake = cam.gameObject.AddComponent<Core.CameraShake>();
            if (Core.HitstopManager.Instance == null)
                new GameObject("HitstopManager").AddComponent<Core.HitstopManager>();

            if (_marker == null) _marker = new GameObject("FeelMarker").transform;
            _marker.position = Vector3.zero;
            shake.Configure(_marker);
            _shakeComp = shake;
            yield return null;
        }

        /// <summary>真实时间等待。WaitForSeconds 会被 timeScale=0 拖成永不返回，这里必须自己累加。</summary>
        private static IEnumerator WaitReal(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>等到 timeScale 回到 1（带真实时间上限，避免被测行为异常时挂死）。</summary>
        private static IEnumerator WaitScale1(float timeout)
        {
            float t = 0f;
            while (Time.timeScale < 0.999f && t < timeout)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>在一段真实时间里采样"相机偏离跟随点"的最大幅度，结果写入 <see cref="_sampled"/>。</summary>
        private IEnumerator SampleDev(float seconds)
        {
            float max = 0f;
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                var cam = Camera.main;
                if (cam != null)
                {
                    float d = Vector2.Distance(
                        new Vector2(cam.transform.position.x, cam.transform.position.y), Vector2.zero);
                    if (d > max) max = d;
                }
                yield return null;
            }
            _sampled = max;
        }

        private static bool NearBase(float v) => Mathf.Abs(v - BaseOrtho) <= 0.5f;

        private static void ClearEnemiesNow()
        {
            var em = EnemyManager.Instance;
            if (em == null) return;
            var snapshot = new List<Enemy.Enemy>(em.ActiveEnemies);
            foreach (var e in snapshot)
            {
                if (e == null) continue;
                em.RemoveEnemy(e);
                Destroy(e.gameObject);
            }
            em.ActiveEnemies.Clear();
        }

        // =====================================================================================
        //  STAGE：真实启动路径下，相机基准缩放不得被自己拉塌
        // =====================================================================================

        private IEnumerator Stage()
        {
            if (Camera.main == null) GameBuilder.PrepareStage();
            yield return null;

            // 阶段一：菜单停留。真实启动是 PrepareStage → 合规门 → 主菜单 → 玩家点「开始游戏」，
            // 中间必然有若干秒；这段时间里 CameraShake 已经挂上但 Configure 还没人调用。
            yield return WaitReal(1.50f);
            var cam = Camera.main;
            _stageMenuOrtho = cam != null ? cam.orthographicSize : -1f;
            _stageChecked++;
            if (NearBase(_stageMenuOrtho)) { _stageOk++; _stageMenuHeld = true; }
            else Fail("菜单停留 1.5s 后正交尺寸 " + N5(_stageMenuOrtho) + "（基准 8，已被拉向 0）");

            // 阶段二：真实开局路径（Build → AttachCameraShake → Configure）。
            // Configure 此刻读到的就是阶段一被压过的值——会被当成基准记下来。
            GameBuilder.Build();
            var em = EnemyManager.Instance;
            if (em != null) em.FreezeWaves = true;
            ClearEnemiesNow();
            yield return WaitReal(0.50f);

            cam = Camera.main;
            _stageBuildOrtho = cam != null ? cam.orthographicSize : -1f;
            _stageChecked++;
            if (NearBase(_stageBuildOrtho)) { _stageOk++; _stageBuildHeld = true; }
            else Fail("开局后正交尺寸 " + N5(_stageBuildOrtho) + "（Configure 记下了被压过的基准）");
        }

        // =====================================================================================
        //  ZOOM：Boss 出场推近必须生效、必须回弹，且不得被中途 Configure 改小基准
        // =====================================================================================

        private IEnumerator Zoom()
        {
            yield return EnsureStage();
            var cam = Camera.main;
            var shake = _shakeComp;
            if (cam == null || shake == null) { Fail("相机/震屏组件缺失"); yield break; }

            yield return WaitReal(0.30f);
            _zoomBase = cam.orthographicSize;
            _zoomChecked++;
            if (NearBase(_zoomBase)) _zoomOk++;
            else Fail("静置基准缩放 " + N5(_zoomBase) + "（期望≈" + N5(BaseOrtho) + "）");

            // 推近必须生效
            shake.ZoomPulse(0.72f, 0.55f);
            yield return WaitReal(0.25f);
            _zoomPunched = cam.orthographicSize;
            _zoomChecked++;
            if (_zoomPunched < _zoomBase * 0.95f) { _zoomOk++; _zoomPunchWorks = true; }
            else Fail("推近未生效 " + N5(_zoomPunched) + "（基准 " + N5(_zoomBase) + "）");

            // 推近必须回弹
            yield return WaitReal(1.50f);
            _zoomBack = cam.orthographicSize;
            _zoomChecked++;
            if (Mathf.Abs(_zoomBack - _zoomBase) <= 0.20f) { _zoomOk++; _zoomBackOk = true; }
            else Fail("推近后未回弹 " + N5(_zoomBack) + "（基准 " + N5(_zoomBase) + "）");

            // 推近进行中 Configure：Boss 出场那 0.55s 内若有玩家加入/重连就会命中。
            // 此刻的 orthographicSize 是被压过的临时值，把它记成基准会让镜头永久推近且逐波叠乘。
            shake.ZoomPulse(0.72f, 0.55f);
            yield return WaitReal(0.20f);
            shake.Configure(_marker);
            yield return WaitReal(1.50f);
            _zoomAfterCfg = cam.orthographicSize;
            _zoomChecked++;
            if (Mathf.Abs(_zoomAfterCfg - _zoomBase) <= 0.20f) { _zoomOk++; _zoomCfgOk = true; }
            else Fail("推近中途 Configure 把基准改小了 " + N5(_zoomAfterCfg) + "（基准 " + N5(_zoomBase) + "）");
        }

        // =====================================================================================
        //  HITSTOP：顿帧必须真的压时间，且真实时长≈请求时长
        // =====================================================================================

        private IEnumerator Hitstop()
        {
            yield return EnsureStage();
            var hs = Core.HitstopManager.Instance;
            if (hs == null) { Fail("顿帧管理器缺失"); yield break; }

            // 1) 真实事件链路：玩家受伤 → Pulse(0.05, 0.18)
            Core.EventBus.RaisePlayerHurt();
            _hsEventScale = Time.timeScale;   // Pulse → Apply 是同步的，立刻可读
            _hsChecked++;
            if (_hsEventScale > 0.10f && _hsEventScale < 0.30f) { _hsOk++; _hsEventOk = true; }
            else Fail("受伤事件未压时间 timeScale=" + N3(_hsEventScale));
            yield return WaitScale1(3f);

            // 2) 自定义长顿帧：0.30s / 0.04。若计时误用缩放后的 deltaTime，
            //    真实时长会被自己的 timeScale 拖成 0.30 / 0.04 ≈ 7.5s。
            hs.Pulse(0.30f, 0.04f);
            _hsPulseScale = Time.timeScale;
            _hsChecked++;
            if (_hsPulseScale > 0f && _hsPulseScale < 0.10f) { _hsOk++; _hsPressOk = true; }
            else Fail("顿帧未压到 0.04，实测 " + N3(_hsPulseScale));

            float t = 0f;
            while (Time.timeScale < 0.999f && t < 12f)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            _hsReal = t;
            _hsChecked++;
            if (_hsReal >= 0.20f && _hsReal <= 0.60f) { _hsOk++; _hsDurOk = true; }
            else Fail("顿帧真实时长 " + N3(_hsReal) + "s（期望≈0.300s）");

            yield return null;
            _hsFinal = Time.timeScale;
            _hsChecked++;
            if (Mathf.Abs(_hsFinal - 1f) < 0.001f) { _hsOk++; _hsBackOk = true; }
            else Fail("顿帧结束后 timeScale=" + N3(_hsFinal));
        }

        // =====================================================================================
        //  PAUSE：选卡暂停的四条保证
        // =====================================================================================

        private IEnumerator Pause()
        {
            yield return EnsureStage();
            var hs = Core.HitstopManager.Instance;
            if (hs == null) { Fail("顿帧管理器缺失"); yield break; }

            hs.SetPaused(true);
            _pauseZero = Time.timeScale;
            _pauseChecked++;
            if (Mathf.Abs(_pauseZero) < 0.001f) { _pauseOk++; _pauseZeroOk = true; }
            else Fail("暂停未归零 timeScale=" + N3(_pauseZero));

            // 暂停期间的顿帧必须被完全忽略：不能把 timeScale 从 0 抬到 0.18
            hs.Pulse(0.10f, 0.18f);
            _pauseDuring = Time.timeScale;
            _pauseChecked++;
            if (Mathf.Abs(_pauseDuring) < 0.001f) { _pauseOk++; _pauseIgnoreOk = true; }
            else Fail("暂停中的顿帧没被忽略 timeScale=" + N3(_pauseDuring));

            hs.SetPaused(false);
            _pauseBack = Time.timeScale;
            _pauseChecked++;
            if (Mathf.Abs(_pauseBack - 1f) < 0.001f) { _pauseOk++; _pauseBackOk = true; }
            else Fail("放开暂停后 timeScale=" + N3(_pauseBack));

            // 顿帧在飞的时候被暂停：放开必须立刻是 1，不能残留顿帧的 0.04
            hs.Pulse(0.20f, 0.04f);
            yield return null;
            hs.SetPaused(true);
            yield return WaitReal(0.05f);
            hs.SetPaused(false);
            _pauseStack = Time.timeScale;
            _pauseChecked++;
            if (Mathf.Abs(_pauseStack - 1f) < 0.001f) { _pauseOk++; _pauseStackOk = true; }
            else Fail("暂停没能清掉在飞的顿帧，放开后 timeScale=" + N3(_pauseStack));

            yield return WaitReal(0.60f);
            if (Mathf.Abs(Time.timeScale - 1f) >= 0.001f)
                Fail("放开 0.6s 后仍未回到 1，timeScale=" + N3(Time.timeScale));
        }

        // =====================================================================================
        //  SHAKE：震屏必须偏得出去，也必须衰减得完（含暂停态）
        // =====================================================================================

        private IEnumerator Shake()
        {
            yield return EnsureStage();
            var hs = Core.HitstopManager.Instance;
            if (hs == null) { Fail("顿帧管理器缺失"); yield break; }

            // 1) 震屏必须真的把相机推离跟随点
            Core.EventBus.RaiseShake(0.5f, 0.30f);
            yield return SampleDev(0.25f);
            _shakeOn = _sampled;
            _shakeChecked++;
            if (_shakeOn > 0.05f) { _shakeOk++; _shakeOnOk = true; }
            else Fail("震屏未生效，最大偏移 " + N5(_shakeOn));

            // 2) 静置必须衰减完
            yield return WaitReal(0.60f);
            yield return SampleDev(0.30f);
            _shakeIdle = _sampled;
            _shakeChecked++;
            if (_shakeIdle < 0.02f) { _shakeOk++; _shakeIdleOk = true; }
            else Fail("震屏静置后未衰减完，最大偏移 " + N5(_shakeIdle));

            // 3) 暂停中同样必须在真实时间里衰减完：
            //    选卡/结算面板背后如果镜头还在抖，玩家看到的是面板后面一直抽搐的世界。
            Core.EventBus.RaiseShake(0.5f, 0.30f);
            hs.SetPaused(true);
            yield return WaitReal(1.00f);
            yield return SampleDev(0.30f);
            _shakePaused = _sampled;
            _shakeChecked++;
            if (_shakePaused < 0.02f) { _shakeOk++; _shakePausedOk = true; }
            else Fail("暂停 1s 后震屏仍在抖，最大偏移 " + N5(_shakePaused));

            // 4) 放开后必须归位
            hs.SetPaused(false);
            yield return WaitReal(0.40f);
            yield return SampleDev(0.30f);
            _shakeAfter = _sampled;
            _shakeChecked++;
            if (_shakeAfter < 0.02f) { _shakeOk++; _shakeAfterOk = true; }
            else Fail("放开暂停后震屏未归位，最大偏移 " + N5(_shakeAfter));
        }

        // =====================================================================================
        //  GATE：关掉「屏幕震动」后，三条反馈通道必须全部关闭
        // =====================================================================================

        private IEnumerator Gate()
        {
            yield return EnsureStage();
            var hs = Core.HitstopManager.Instance;
            var cam = Camera.main;
            var shake = _shakeComp;
            if (hs == null || cam == null || shake == null) { Fail("手感层组件缺失"); yield break; }

            var s = Core.SettingsManager.Instance;
            if (s == null) { Fail("设置管理器缺失"); yield break; }
            s.Current.screenShake = false;

            // 顿帧
            Core.EventBus.RaisePlayerHurt();
            _gateScale = Time.timeScale;
            _gateChecked++;
            if (Mathf.Abs(_gateScale - 1f) < 0.001f) { _gateOk++; _gateHsOk = true; }
            else Fail("关掉开关后顿帧仍然生效 timeScale=" + N3(_gateScale));

            // 震屏
            Core.EventBus.RaiseShake(0.5f, 0.30f);
            yield return SampleDev(0.30f);
            _gateDev = _sampled;
            _gateChecked++;
            if (_gateDev < 0.02f) { _gateOk++; _gateShakeOk = true; }
            else Fail("关掉开关后震屏仍然生效，最大偏移 " + N5(_gateDev));

            // 推近（Boss 出场镜头）——与震屏同属"打击反馈"，受同一个开关管辖
            _gateOrthoBefore = cam.orthographicSize;
            shake.ZoomPulse(0.72f, 0.55f);
            yield return WaitReal(0.30f);
            _gateOrtho = cam.orthographicSize;
            _gateChecked++;
            if (Mathf.Abs(_gateOrtho - _gateOrthoBefore) < 0.20f) { _gateOk++; _gateZoomOk = true; }
            else Fail("关掉开关后推近仍然生效 " + N5(_gateOrthoBefore) + " → " + N5(_gateOrtho));
        }

        // =====================================================================================

        private void Fail(string msg) { _fail.Add(msg); }

        private static string Yn(bool b) => b ? "是" : "否";

        private static string N3(float v) { return v.ToString("0.000"); }

        private static string N5(float v) { return v.ToString("0.00000"); }

        /// <summary>SMOKE 行的键宽对齐：中日韩字符按两列算，保证键值之间至少两个空格（驱动脚本靠它切分）。</summary>
        private static string Pad(string key)
        {
            int w = 0;
            foreach (var ch in key) w += ch > '\u2E80' ? 2 : 1;
            int pad = 18 - w;
            if (pad < 2) pad = 2;
            return key + new string(' ', pad);
        }

        private static void Line(string key, string value)
        {
            Debug.Log("[SMOKE] " + Pad(key) + value);
        }

        private void Report()
        {
            bool pass = _errors == 0 && _fail.Count == 0;
            if (_scene == "STAGE") pass = pass && _stageChecked == 2 && _stageOk == 2;
            if (_scene == "ZOOM") pass = pass && _zoomChecked == 4 && _zoomOk == 4;
            if (_scene == "HITSTOP") pass = pass && _hsChecked == 4 && _hsOk == 4;
            if (_scene == "PAUSE") pass = pass && _pauseChecked == 4 && _pauseOk == 4;
            if (_scene == "SHAKE") pass = pass && _shakeChecked == 4 && _shakeOk == 4;
            if (_scene == "GATE") pass = pass && _gateChecked == 3 && _gateOk == 3;

            Debug.Log("[SMOKE] ===== 手感层实测结果 =====");
            Line("场景", _scene);
            switch (_scene)
            {
                case "STAGE":
                    Line("断点通过", _stageOk + "/" + _stageChecked);
                    Line("菜单期缩放", N5(_stageMenuOrtho));
                    Line("开局后缩放", N5(_stageBuildOrtho));
                    Line("菜单期保持", Yn(_stageMenuHeld));
                    Line("开局后正确", Yn(_stageBuildHeld));
                    break;
                case "ZOOM":
                    Line("断点通过", _zoomOk + "/" + _zoomChecked);
                    Line("静置基准", N5(_zoomBase));
                    Line("推近中", N5(_zoomPunched));
                    Line("回弹后", N5(_zoomBack));
                    Line("中途Configure后", N5(_zoomAfterCfg));
                    Line("推近生效", Yn(_zoomPunchWorks));
                    Line("回弹基准", Yn(_zoomBackOk));
                    Line("基准不被改小", Yn(_zoomCfgOk));
                    break;
                case "HITSTOP":
                    Line("断点通过", _hsOk + "/" + _hsChecked);
                    Line("事件链路压到", N3(_hsEventScale));
                    Line("顿帧压到", N3(_hsPulseScale));
                    Line("真实时长", N3(_hsReal) + "s");
                    Line("结束后", N3(_hsFinal));
                    Line("事件链路通", Yn(_hsEventOk));
                    Line("时长正确", Yn(_hsDurOk));
                    Line("结束后归1", Yn(_hsBackOk));
                    break;
                case "PAUSE":
                    Line("断点通过", _pauseOk + "/" + _pauseChecked);
                    Line("暂停时", N3(_pauseZero));
                    Line("暂停中顿帧", N3(_pauseDuring));
                    Line("放开后", N3(_pauseBack));
                    Line("顿帧被清掉", N3(_pauseStack));
                    Line("暂停归零", Yn(_pauseZeroOk));
                    Line("顿帧被忽略", Yn(_pauseIgnoreOk));
                    Line("放开归1", Yn(_pauseBackOk));
                    Line("不残留顿帧", Yn(_pauseStackOk));
                    break;
                case "SHAKE":
                    Line("断点通过", _shakeOk + "/" + _shakeChecked);
                    Line("震屏幅度", N5(_shakeOn));
                    Line("静置后", N5(_shakeIdle));
                    Line("暂停中", N5(_shakePaused));
                    Line("放开后", N5(_shakeAfter));
                    Line("震屏生效", Yn(_shakeOnOk));
                    Line("静置衰减", Yn(_shakeIdleOk));
                    Line("暂停中衰减", Yn(_shakePausedOk));
                    Line("放开后归位", Yn(_shakeAfterOk));
                    break;
                default:
                    Line("断点通过", _gateOk + "/" + _gateChecked);
                    Line("开关后顿帧", N3(_gateScale));
                    Line("开关后震屏", N5(_gateDev));
                    Line("开关后缩放", N5(_gateOrthoBefore) + " → " + N5(_gateOrtho));
                    Line("顿帧被关", Yn(_gateHsOk));
                    Line("震屏被关", Yn(_gateShakeOk));
                    Line("推近被关", Yn(_gateZoomOk));
                    break;
            }
            Line("失败项", _fail.Count == 0 ? "无" : string.Join(" | ", _fail.ToArray()));
            Line("运行期异常", _errors.ToString());
            if (_errors > 0) Line("首条错误", _firstError);
            Line("结论", pass ? "通过" : "不通过");
        }
    }
}
