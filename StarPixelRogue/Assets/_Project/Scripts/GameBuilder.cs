using UnityEngine;
using UnityEngine.UI;
using StarPixel.Combat;
using StarPixel.Combat.Skill;
using StarPixel.Enemy;
using StarPixel.Pickups;
using StarPixel.Player;
using StarPixel.UI;

namespace StarPixel
{
    /// <summary>
    /// 原型引导器：通过 [RuntimeInitializeOnLoadMethod] 在每次进入 Play 时自动构建整个游戏，
    /// 因此「打开工程 → 按 Play」即可游玩，无需手动搭建场景。
    /// M3 起接入真实像素精灵与音效（Resources 加载，缺失时自动回退占位表现）。
    /// M4 起：检测到 Fish-Net 后端时先进入联机大厅（单人/建房/加入），
    /// 连接就绪后再按 <see cref="NetSession.Mode"/> 构建对应模式的世界。
    /// M5 起：单机路径先进「合规门 → 主菜单」，由玩家点击开始游戏后才 Build 世界。
    /// </summary>
    public static class GameBuilder
    {
        private static bool _built;

        /// <summary>Build 期间创建的世界物体（断线拆世界时统一销毁，保证可再次重建）。</summary>
        private static readonly System.Collections.Generic.List<Object> _created = new System.Collections.Generic.List<Object>();

        private static T Track<T>(T obj) where T : Object
        {
            if (obj == null) return obj;

            // M27：一律登记 **GameObject**，不是组件。
            // 此前 Track(new GameObject("X").AddComponent<Y>()) 传进来的是 AddComponent 的返回值——
            // 组件，而 Object.Destroy(组件) 只把这个组件摘掉，GameObject 本体和**它的所有子物体**
            // 全都留在场景里。于是每重开一局就遗留一整套空壳管理器：
            //   · "DamageNumberManager" 连同它的 DmgCanvas 与飘字模板（一个残留的 Overlay 画布）
            //   · "LevelUpManager"（其 BuildPanel 注释写着"随管理器一起拆世界"，实际从未兑现）
            //   · "HUD" / "PoolManager" / "CombatManager" / "HitstopManager" 等共约 10 个空壳
            // 这些对象一局一局堆下去，永远不会消失。
            _created.Add(obj is Component c ? c.gameObject : obj);
            return obj;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBuild()
        {
            // M18 联机双开实测：自动建房 / 自动加入，绕过大厅 UI（正常启动不受影响）
            if (HasArg("-netHost")) { StartNetSmoke(true); return; }
            if (HasArg("-netClient")) { StartNetSmoke(false); return; }

            // M18 / M13 云存档无头实测：按开关矩阵跑一个场景（正常启动不受影响）
            if (HasArg("-cloudSaveProbe")) { StartCloudSaveProbe(); return; }

            // M18：设置面板无头实测（正常启动不受影响）
            if (HasArg("-uiProbe")) { StartUIProbe(); return; }

            // M19：防沉迷无头实测（正常启动不受影响）
            if (HasArg("-playtimeProbe")) { StartPlaytimeProbe(); return; }

            // M20：持久化层无头实测（正常启动不受影响）
            if (HasArg("-persistenceProbe")) { StartPersistenceProbe(); return; }

            // M21：合规门与同意链路无头实测（正常启动不受影响）
            if (HasArg("-complianceProbe")) { StartComplianceProbe(); return; }

            // M22：设置面板真实交互与落盘往返无头实测（正常启动不受影响）
            if (HasArg("-settingsProbe")) { StartSettingsProbe(); return; }

            // M23：选角 ↔ 开局 与 升级 ↔ 组合技 链路无头实测（正常启动不受影响）
            if (HasArg("-buildProbe")) { StartBuildProbe(); return; }

            // M24：掉落 ↔ 磁吸 ↔ 结算 与 重开局清理 链路无头实测（正常启动不受影响）
            if (HasArg("-pickupProbe")) { StartPickupProbe(); return; }

            // M25：手感层（镜头/顿帧/暂停/震屏/反馈开关）无头实测（正常启动不受影响）
            if (HasArg("-feelProbe")) { StartFeelProbe(); return; }

            // M26：可见层第二批（字体/伤害飘字/受击冲击/前摇警示圈）无头实测（正常启动不受影响）
            if (HasArg("-visualProbe")) { StartVisualProbe(); return; }

            // M27：伤害飘字对象池无头实测（新建数有上限 / 复用实例状态重置 / 爆发不丢字 / 重开无残留）
            if (HasArg("-poolProbe")) { StartPoolProbe(); return; }

            // M28：新建对象归属（链闪电 / Boss / 场景根兜底）无头实测（正常启动不受影响）
            if (HasArg("-orphanProbe")) { StartOrphanProbe(); return; }

            // M29：静态事件总线订阅对称性（反射自检 / 重开累积 / 升级面板重复）无头实测
            if (HasArg("-subProbe")) { StartSubProbe(); return; }

            // M30：运行时 GPU 资源（计数器自检 / 高频特效连放 / 重开累积 / 静态缓存存活）无头实测
            if (HasArg("-gpuProbe")) { StartGpuProbe(); return; }

            // M31：升级选卡生效链路（地基 / 属性 / 武器等级 / 解锁 / 组合技广播）无头实测
            if (HasArg("-upgradeProbe")) { StartUpgradeProbe(); return; }

            // M32：联机本地守卫对称性（翻转手法自检 / 远程零广播 / 本地必须广播 / 全局状态不被污染 / 恢复）
            if (HasArg("-guardProbe")) { StartGuardProbe(); return; }

            // M33：本地化覆盖完整性（扫描器自检 / 中文态基线 / 英文态零中文 / 动态键不回退 / 恢复）
            if (HasArg("-locProbe")) { StartLocProbe(); return; }

            // M35：法务文本双语（扫描器自检 / 中文态基线 / 英文态零中文零回退 / 法务窗上屏 / 就地刷新）
            if (HasArg("-legalProbe")) { StartLegalProbe(); return; }

            // M37：联机升级选卡不丢单（外部供选排队 / 批次表取回 / 顺序不乱 / 本地路径未被堵死）
            if (HasArg("-cardProbe")) { StartCardProbe(); return; }

            // M41：长局稳定性压测（对象增长 / 长程数值曲线 / 长局后重开局是否干净）
            if (HasArg("-longrunProbe")) { StartLongRunProbe(); return; }

            // M17 无头冒烟实测：带 -smokeTest 时跳过合规门与主菜单直接开局（正常启动不受影响）
            if (HasArg("-smokeTest")) { StartSmokeTest(); return; }

            // M5：基础系统先于任何 UI 就绪（顺序确定，不依赖事件时序）
            Core.SettingsManager.Ensure();              // 设置（音量/语言/协议同意态）
            Core.SaveManager.Ensure();                  // 战绩存档
            Core.CloudSave.CloudSaveService.Ensure();   // M13 云存档（未开启/未配置时不联网）
            Core.PlaytimeGuard.Ensure();                // 青少年限时/健康提示
            // M7：背景音乐（DontDestroyOnLoad，不纳入 Track——跨菜单/对局/结算全程存活）
            new GameObject("MusicManager").AddComponent<Core.MusicManager>();   // @owner: app
            Core.Telemetry.Log(Core.Telemetry.EvAppStart,
                "version", Core.VersionInfo.Version,
                "net", Net.NetBackend.Instance != null ? 1 : 0,
                "lang", Core.SettingsManager.Instance.Current.language);

            PrepareStage();                             // 相机 + EventSystem（所有模式共用）

            // M5：联机构建同样先过合规门（协议是产品级要求，与是否联机无关）
            UI.MainMenu.Ensure();                       // 先建好（隐藏），供结算/回退路径使用
            UI.GameOverPanel.Ensure();
            var gate = new GameObject("ComplianceGate").AddComponent<Compliance.ComplianceGate>();
            bool agreed = Core.SettingsManager.Instance != null &&
                Core.SettingsManager.Instance.TermsAgreed(Core.VersionInfo.ComplianceVersion);

            if (Net.NetBackend.Instance != null)
            {
                // 具备联机能力：合规通过后进入大厅，由用户选择单人 / 建房 / 加入
                Compliance.ComplianceGate.OnPassed = OpenLobby;
                if (agreed)
                {
                    OpenLobby();
                    Core.UIFactory.Destroy(gate.gameObject);   // 已同意：拆掉空门
                }
                return;
            }

            // 单机：合规门（首次/协议更新时）→ 主菜单
            Compliance.ComplianceGate.OnPassed = () => UI.MainMenu.Instance.Show();
            if (agreed)
            {
                UI.MainMenu.Instance.Show();
                Core.UIFactory.Destroy(gate.gameObject);       // 已同意：拆掉空门
            }
        }

        /// <summary>M17：命令行是否含指定参数。</summary>
        private static bool HasArg(string flag)
        {
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// M40：命令行强制界面语言（-lang zh|en）。
        ///
        /// 用来做一件单机测不出来的事：**两端语言不同**的联机房间。
        /// 房主中文 + 客户端英文时，若推卡文案跟着房主走，客户端整个选卡面板都会是中文。
        /// 必须在 <c>SettingsManager.Ensure</c> **之后**调用——Ensure 会用存档里的语言覆盖一遍。
        /// 只改运行时语言，不写存档：双开跑完不会把用户的语言设置改掉。
        /// </summary>
        private static void ApplyLanguageArg()
        {
            string v = ArgValue("-lang", "");
            if (string.IsNullOrEmpty(v)) return;
            bool en = v.StartsWith("en", System.StringComparison.OrdinalIgnoreCase);
            Core.Localization.SetLanguage(en ? Core.Localization.LangEn : Core.Localization.LangZh,
                                          silent: true);
            Debug.Log("[M40] 命令行强制语言 → " + Core.Localization.Language);
        }

        /// <summary>M17：读取 -smokeSeconds N（默认 60 秒）。</summary>
        private static float SmokeSeconds()
        {
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-smokeSeconds" && float.TryParse(args[i + 1], out float v))
                        return v;
            }
            catch { }
            return 60f;
        }

        /// <summary>
        /// M41：读取 -smokeSpeed N（默认 1）。>1 时把 <see cref="Core.HitstopManager.BaseScale"/>
        /// 提上去，让"游戏内 30 分钟"能在数分钟真实时间里跑完。
        /// 为什么必须有：长局要测的是**累积**（每波残留、池化是否收敛、数值曲线是否越界），
        /// 而这些只有在波次推到很远之后才暴露；真等 30 分钟既跑不动回归，也没法做四组区分力注入。
        /// 注意它放大的是**模拟步长**（每帧 deltaTime），不是真实负载——真实时长档另跑。
        /// </summary>
        private static float SmokeSpeed()
        {
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-smokeSpeed" && float.TryParse(args[i + 1], out float v))
                        return Mathf.Max(0.01f, v);
            }
            catch { }
            return 1f;
        }

        /// <summary>
        /// M41 长局稳定性压测入口：`-longrunProbe -longrunScene 场景名 [-smokeSeconds N] [-smokeSpeed N]`。
        /// 与短局冒烟的两点区别：玩家保活（否则测到的是死亡流程）、时间按 -smokeSpeed 加速。
        /// 正常启动不会走进这里。
        /// </summary>
        private static void StartLongRunProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Core.PlaytimeGuard.Ensure();
            Core.SettingsManager.Instance.AgreeTerms(Core.VersionInfo.ComplianceVersion);

            Application.targetFrameRate = 60;
            PrepareStage();

            string scene = ArgValue("-longrunScene", "GROWTH");
            float seconds = HasArg("-smokeSeconds") ? SmokeSeconds() : 300f;
            float speed = SmokeSpeed();

            // 探针先建：它要尽早订阅日志以统计运行期异常
            StarPixel.Dev.LongRunProbe.Start(Mathf.Max(6f, seconds - 1f), scene, speed);

            Build();                                       // 单机世界
            Core.HitstopManager.SetBaseScale(speed);        // 世界已建，实例此时在位
            StarPixel.Dev.SmokeTest.Immortal = true;        // 长局不因阵亡终止
            StarPixel.Dev.SmokeTest.Start(seconds);         // 复用已验证的自动驾驶驱动对局
        }

        /// <summary>
        /// M17 无头冒烟实测入口：正常启动要先过合规门、再由人工点主菜单才能开局，
        /// 无人值守环境下跑不起来；本路径直接同意协议并构建单机世界，再挂上 SmokeTest 采集状态。
        /// 不创建 MusicManager——无头环境通常没有音频设备。
        /// </summary>
        private static void StartSmokeTest()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Core.PlaytimeGuard.Ensure();
            // 实测环境无人点击"同意"，直接以当前法律文本版本记同意态（不会改动正常流程）
            Core.SettingsManager.Instance.AgreeTerms(Core.VersionInfo.ComplianceVersion);

            // M18：无头下不锁帧会跑到数千 FPS，把每帧告警放大成 GB 级日志（见 StartNetSmoke 注释）
            Application.targetFrameRate = 60;
            PrepareStage();
            Build();                                  // 单机世界（此时 NetBackend 为 null）
            // M41：-smokeSpeed / -smokeImmortal 对普通冒烟同样有效（长局压测的加速与保活能力复用同一处）
            float speed = SmokeSpeed();
            if (speed != 1f) Core.HitstopManager.SetBaseScale(speed);
            StarPixel.Dev.SmokeTest.Immortal = HasArg("-smokeImmortal");
            StarPixel.Dev.SmokeTest.Start(SmokeSeconds());
        }

        /// <summary>M18：读取 "-key value" 形式的数值参数。</summary>
        private static float ArgFloat(string key, float fallback)
        {
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == key && float.TryParse(args[i + 1], out float v))
                        return v;
            }
            catch { }
            return fallback;
        }

        /// <summary>M18：读取 "-key value" 形式的命令行参数。</summary>
        private static string ArgValue(string key, string fallback)
        {
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == key) return args[i + 1];
            }
            catch { }
            return fallback;
        }

        /// <summary>M18：读取 "-key n" 形式的整数参数。</summary>
        private static int ArgInt(string key, int fallback)
        {
            try
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == key && int.TryParse(args[i + 1], out int v)) return v;
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// M18 / M13 云存档无头实测入口：按命令行给出的开关组合构建一个场景，
        /// 由 <see cref="Dev.CloudSaveProbe"/> 断言「该不该联网」与「玩法有没有被砸」。
        /// 参数（全部可选）：
        ///   -cloudSaveScene  &lt;名&gt;   场景名，仅用于日志标注
        ///   -cloudSaveUrl    &lt;url&gt;  后端地址；不给 = 保持未配置的真实默认态
        ///   -cloudSaveUserOn 0|1      设置里的用户开关（默认 1）
        ///   -cloudSaveAgree  0|1      是否已同意最新版隐私政策（默认 1）
        ///   -cloudSaveTimeout &lt;秒&gt;   单次请求超时（默认 3，加快失败收敛）
        ///   -cloudSaveExpect 0|1      **外部**给定的"是否应当启用"期望值（默认 0）
        /// 期望值刻意由外部脚本给出而非在此推导：若由被测对象自己判断自己该不该启用，
        /// 实现出错时就会自我印证（M11 已踩过一次假通过的坑）。
        /// </summary>
        private static void StartCloudSaveProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Core.PlaytimeGuard.Ensure();

            Application.targetFrameRate = 60;   // 同 StartNetSmoke：不锁帧会把每帧告警刷成 GB 级日志

            string scene = ArgValue("-cloudSaveScene", "未命名");
            string url = ArgValue("-cloudSaveUrl", "");
            bool userOn = ArgInt("-cloudSaveUserOn", 1) != 0;
            bool agreed = ArgInt("-cloudSaveAgree", 1) != 0;
            int timeout = ArgInt("-cloudSaveTimeout", 3);
            bool expect = ArgInt("-cloudSaveExpect", 0) != 0;

            // 未显式给时长时取 20s：云存档场景只需证明"玩法未被砸 + 请求数符合预期"，
            // 跑到 60s 纯属浪费（单机冒烟已有独立入口覆盖长时程）。
            float seconds = HasArg("-smokeSeconds") ? SmokeSeconds() : 20f;

            PrepareStage();

            // 探针先建：它要尽早订阅日志以统计运行期异常；
            // 观察时长比 SmokeTest 短 1s，确保由探针收尾并输出云存档指标。
            StarPixel.Dev.CloudSaveProbe.Start(Mathf.Max(6f, seconds - 1f),
                scene, url, userOn, agreed, timeout, expect);

            Build();                                      // 单机世界
            StarPixel.Dev.SmokeTest.Start(seconds);        // 复用已验证的自动驾驶，负责驱动对局
        }

        /// <summary>
        /// M18 联机双开实测入口：无头自动建房（-netHost）或自动加入（-netClient，
        /// 目标地址用 -netAddress 指定，默认 127.0.0.1），连接就绪后构建世界并挂上 SmokeTest。
        /// 两端日志由 net_smoke.py 交叉核对。正常启动不会走进这里。
        /// </summary>
        private static void StartNetSmoke(bool host)
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Core.PlaytimeGuard.Ensure();
            Core.SettingsManager.Instance.AgreeTerms(Core.VersionInfo.ComplianceVersion);
            ApplyLanguageArg();
            PrepareStage();

            // M18：无头环境必须显式锁帧。不锁时 NullGfxDevice 会跑到数千 FPS，
            // 把「每帧一条」的渲染告警放大成 GB 级日志——本次客户端正是因此刷出 213MB
            // 并触发熔断，真正的问题线索反而被埋掉。锁 60 后日志量回归正常。
            Application.targetFrameRate = 60;

            var backend = Net.NetBackend.Instance;
            if (backend == null)
            {
                Debug.LogError("[NetSmoke] 联机后端不可用（FishNet 未启用），无法双开实测");
                Application.Quit(2);
                return;
            }

            Net.NetSession.RemoteAddress = ArgValue("-netAddress", "127.0.0.1");

            // M14 观战实测：必须在发起连接**之前**置位——Fish-Net 的 PlayerSpawner 会在连接就绪时
            // 立刻为这个连接自动生成玩家对象，事后再置位就晚了（NetPlayer 只能走"先生成再回收"）。
            bool spectator = HasArg("-netSpectator");
            Net.NetSession.SetSpectator(spectator);

            // M11 断线重连实测：>0 时本机跑到该秒数主动模拟一次意外掉线，随后验证自动重连。
            // 房主与客户端是**两条不同的分支**（见 FishNetBackend.OnClientConnectionState）：
            //   房主掉线 → 服务端仍运行、权威世界保留不拆，只重连本地客户端；
            //   客户端掉线 → 先 TeardownWorld 拆本地镜像，重连成功后由 Ready 重建。
            // 故用两个参数分别驱动，才能单独验证其中一条。
            float dropAt = spectator ? 0f
                : (host ? ArgFloat("-netHostDropAt", 0f) : ArgFloat("-netDropAt", 0f));

            bool built = false;

            // M18：连接就绪看门狗。此前客户端在服务端缺失时 backend.Ready 永不触发，
            // 既不建世界也不打印任何 [SMOKE]，只能空转到超时（并把每帧告警刷爆磁盘）。
            // 超时后改为直接产出一份全零报告并退出，把「连不上」变成可判定的失败。
            float readyTimeout = 30f;
            var watchdog = new GameObject("NetReadyWatchdog").AddComponent<NetReadyWatchdog>();
            watchdog.Timeout = readyTimeout;
            watchdog.OnTimeout = () =>
            {
                if (built) return;
                built = true;
                StarPixel.Dev.SmokeTest.ReportUnready(host
                    ? "房主在 " + readyTimeout + "s 内未能启动服务端"
                    : "客户端在 " + readyTimeout + "s 内未能连上 " + Net.NetSession.RemoteAddress);
            };

            backend.Ready += () =>
            {
                if (built) return;
                built = true;
                if (watchdog != null) Object.Destroy(watchdog.gameObject);
                Build();                                       // 联机世界（房主权威）
                StarPixel.Dev.SmokeTest.StartNet(SmokeSeconds(), dropAt);
            };

            if (host) backend.StartHost();
            else backend.StartClient(Net.NetSession.RemoteAddress);
        }

        /// <summary>M18：连接就绪超时计时器（未缩放时间，避免受 timeScale 影响）。</summary>
        private class NetReadyWatchdog : MonoBehaviour
        {
            public float Timeout = 30f;
            public System.Action OnTimeout;
            private float _t;

            void Update()
            {
                if (OnTimeout == null) return;
                _t += Time.unscaledDeltaTime;
                if (_t >= Timeout)
                {
                    var cb = OnTimeout;
                    OnTimeout = null;                 // 只触发一次
                    cb.Invoke();
                }
            }
        }

        /// <summary>
        /// M18 设置面板无头实测入口：只搭舞台 + 建设置面板，不开局。
        /// 设置面板在正常无头冒烟里根本不会被创建（那是单机/联机对局路径），
        /// 于是 UI 改动等于零验证——本入口把它建出来并交给 <see cref="Dev.UISettingsProbe"/> 检查。
        /// </summary>
        private static void StartUIProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();            // 面板会显示版本号/存档派生信息，先就位
            Application.targetFrameRate = 60;
            PrepareStage();                       // uGUI 需要相机与 EventSystem
            StarPixel.Dev.UISettingsProbe.Start();
        }

        /// <summary>
        /// M19 防沉迷无头实测入口：搭好舞台后交给 <see cref="Dev.PlaytimeProbe"/>，
        /// 由它以固定步长推进虚拟时长（真实等待 1 小时去验证限时是不现实的）。
        /// 参数：
        ///   -playtimeScene MINOR|ADULT   青少年模式 / 成人模式（默认 MINOR）
        ///   -playtimeLang  zh|en         提示文案所用的界面语言（默认 zh）
        /// 与云存档实测同理：正常启动不带 -playtimeProbe 时不会走进这里。
        /// </summary>
        private static void StartPlaytimeProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Core.PlaytimeGuard.Ensure();
            Application.targetFrameRate = 60;          // 同其它实测：不锁帧会把每帧告警刷成 GB 级日志
            PrepareStage();
            StarPixel.Dev.PlaytimeProbe.Start(
                ArgValue("-playtimeScene", "MINOR"),
                ArgValue("-playtimeLang", Core.Localization.LangZh));
        }

        /// <summary>
        /// M20 持久化层无头实测入口：交给 <see cref="Dev.PersistenceProbe"/> 验证
        /// SaveManager（战绩/图鉴/往返/损坏回退）与 Telemetry（JSONL 合法性/失败重试/关开关清队列）。
        /// 探针会写真实的 save.json / telemetry 文件——驱动脚本负责跑前备份、跑后恢复。
        /// 参数：-persistenceScene SAVE|TELEMETRY（默认 SAVE）
        /// </summary>
        private static void StartPersistenceProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;          // 同其它实测：不锁帧会把每帧告警刷成 GB 级日志
            PrepareStage();
            StarPixel.Dev.PersistenceProbe.Start(ArgValue("-persistenceScene", "SAVE"));
        }

        /// <summary>
        /// M21 合规门无头实测入口：交给 <see cref="Dev.ComplianceProbe"/> 验证
        /// 「首启弹门 / 已同意放行 / 协议版本升级必须重新征得同意 / 不同意即退出 /
        /// 同意态落盘 / 同意前云存档零请求且同意后补一次拉取」。
        /// 参数：-complianceScene FIRST|AGREED|BUMP|DECLINE|CONSENT_CLOUD|L10N（默认 FIRST）
        ///      -complianceLang zh-CN|en-US（默认 zh-CN）
        /// </summary>
        private static void StartComplianceProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            // 云存档配置必须在 Ensure **之前**覆盖：服务在 Awake 里读配置、Start 里判定 Enabled，
            // 事后再改就晚了（与 CloudSaveProbe 同样的顺序约束）
            var url = ArgValue("-cloudSaveUrl", "");
            if (!string.IsNullOrEmpty(url))
                Core.CloudSave.CloudSaveService.OverrideConfig = new Core.CloudSave.CloudSaveService.Config
                { baseUrl = url, timeoutSeconds = 3 };
            Core.CloudSave.CloudSaveService.Ensure();
            Application.targetFrameRate = 60;          // 同其它实测：不锁帧会把每帧告警刷成 GB 级日志
            PrepareStage();
            StarPixel.Dev.ComplianceProbe.Start(
                ArgValue("-complianceScene", "FIRST"),
                ArgValue("-complianceLang", Core.Localization.LangZh));
        }

        /// <summary>
        /// M22 设置面板无头实测入口：交给 <see cref="Dev.SettingsProbe"/> 验证
        /// 「控件点了真的生效并落盘 / 重启后原样读回 / 设置文件损坏不得静默清零」。
        /// 参数：-settingsScene TOGGLES|ROUNDTRIP|NEGATIVE（默认 TOGGLES）
        /// </summary>
        private static void StartSettingsProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;          // 同其它实测：不锁帧会把每帧告警刷成 GB 级日志
            PrepareStage();
            StarPixel.Dev.SettingsProbe.Start(ArgValue("-settingsScene", "TOGGLES"));
        }

        /// <summary>
        /// M23 选角/成长链路无头实测入口：交给 <see cref="Dev.BuildProbe"/> 验证
        /// 「点选角按钮真的改存档 / 重启后开局用的是选中的角色与数值 / 十把武器都能真的打出伤害 /
        /// 四条组合技各自生效 / 七项属性强化各自生效 / 武器等级封顶」。
        /// 注意本入口**不**在这里开局：APPLY 与 PROGRESSION 场景需要先读存档再走 GameBuilder.Build，
        /// 由探针自己调用，保证"选角 → 存档 → 开局"整条链是同一个真实路径。
        /// 参数：-buildScene SELECT|APPLY|PROGRESSION（默认 SELECT）
        /// </summary>
        private static void StartBuildProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;          // 同其它实测：不锁帧会把每帧告警刷成 GB 级日志
            PrepareStage();
            StarPixel.Dev.BuildProbe.Start(ArgValue("-buildScene", "SELECT"));
        }

        private static void StartPickupProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.PickupProbe.Start(ArgValue("-pickupScene", "MAGNET"));
        }

        /// <summary>
        /// M25 手感层无头实测入口：交给 <see cref="Dev.FeelProbe"/> 验证
        /// 「相机基准缩放不被自己拉塌 / Boss 出场推近生效且回弹 / 顿帧真的压时间且真实时长正确 /
        /// 选卡暂停的四条保证 / 震屏在暂停态也必须衰减完 / 关掉反馈开关后三条通道全关」。
        /// 参数：-feelScene STAGE|ZOOM|HITSTOP|PAUSE|SHAKE|GATE（默认 STAGE）
        /// 这是全项目唯一"输出只在画面上"的一层：前面 24 个里程碑全用 -nographics 验收，
        /// 手感层无论如何实现都能全绿——本入口把"看得见"翻译成可断言的数值。
        /// </summary>
        private static void StartFeelProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.FeelProbe.Start(ArgValue("-feelScene", "STAGE"));
        }

        /// <summary>
        /// M26 无头实测入口：交给 <see cref="Dev.VisualProbe"/> 验证
        /// 「运行时字体真的取得到 / 伤害飘字生成·文案·上浮·淡出·回收 / 受击冲击放大·回落·不叠乘 /
        /// Boss 前摇警示圈生成·有精灵·脉动·时长·自毁」。
        /// 参数：-visualScene FONT|DMG|PUNCH|TELEGRAPH（默认 FONT）
        /// 承接 M25 的教训：无头实测能证明"跑通"，证明不了"看得见"；
        /// 手感层还剩一整批"产出只在画面上"的东西从未被断言过，本入口继续把它们翻译成数值。
        /// </summary>
        private static void StartVisualProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.VisualProbe.Start(ArgValue("-visualScene", "FONT"));
        }

        /// <summary>
        /// M27 无头实测入口：交给 <see cref="Dev.PoolProbe"/> 验证
        /// 「伤害飘字改对象池后：累计新建有上限 / 复用实例的状态被重置 / 单帧爆发一张不丢 / 重开无残留」。
        /// 参数：-poolScene POOL|REUSE|BURST|LEFTOVER（默认 POOL）
        ///
        /// 飘字是全项目每秒触发次数最多的对象（每个敌人每次被打一张），却一直没池化；
        /// 而"改成池"这一步本身才是危险的地方：复用实例时字段初始化器不会重跑，
        /// 漏了重置 _life 的话玩家会看到"数字只出现前几次，之后全消失"——日志一行都不会有。
        /// </summary>
        private static void StartPoolProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.PoolProbe.Start(ArgValue("-poolScene", "POOL"));
        }

        private static void StartOrphanProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.OrphanProbe.Start(ArgValue("-orphanScene", "CHAIN"));
        }

        /// <summary>
        /// M29：静态事件总线订阅对称性实测。
        /// 与前面几轮探针不同，这一轮要盯的不是场景里的对象，而是**委托引用**——
        /// 漏订阅时场景里什么都多不出来，只能靠反射读 EventBus 的委托链才看得见。
        /// </summary>
        private static void StartGpuProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.GpuProbe.Start(ArgValue("-gpuScene", "BASE"));
        }

        private static void StartUpgradeProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.UpgradeProbe.Start(ArgValue("-upgradeScene", "BASE"));
        }

        /// <summary>
        /// M32：联机本地守卫对称性。
        /// 单机下 isLocal 恒为 true，"远程玩家串台"这类缺陷天然测不出来——探针靠翻转
        /// isLocal 把远程分支拉到单机环境里断言，无需真开联机。
        /// </summary>
        private static void StartGuardProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.GuardProbe.Start(ArgValue("-guardScene", "BASE"));
        }

        private static void StartLocProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.LocProbe.Start(ArgValue("-locScene", "BASE"));
        }

        private static void StartLegalProbe()
        {
            // 法务探针只碰 Resources/Legal 与法务窗，不需要搭完整世界
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            StarPixel.Dev.LegalProbe.Start(ArgValue("-legalScene", "BASE"));
        }

        /// <summary>
        /// M37：联机升级选卡不丢单。
        /// 房主推卡是「多次」的，而被推的两处状态都只维护「一份」：客户端面板占用时后来的
        /// 卡被静默丢弃、房主候选表被下一次推卡整体覆盖导致选择错配。两处都在网络层，
        /// 但成因是纯逻辑——抽成 UpgradeOfferBatches + 面板排队后，单机就能把整条链路钉死。
        /// </summary>
        private static void StartCardProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.CardProbe.Start(ArgValue("-cardScene", "BASE"));
        }

        private static void StartSubProbe()
        {
            Core.SettingsManager.Ensure();
            Core.SaveManager.Ensure();
            Application.targetFrameRate = 60;
            PrepareStage();
            StarPixel.Dev.SubProbe.Start(ArgValue("-subScene", "BASE"));
        }

        private static void OpenLobby()
        {
            new GameObject("NetLobby").AddComponent<Net.NetLobby>();   // @owner: stage
        }

        /// <summary>阶段准备：相机与事件系统（幂等，可重复调用）。</summary>
        public static void PrepareStage()
        {
            SetupCamera();
            EnsureCameraShake();   // M14：补齐联机路径缺失的相机跟随/震屏组件
            SetupEventSystem();

            // M16 跨平台：部分移动设备默认锁 30 帧，弹幕玩法下手感会明显变差，故显式锁 60
            if (Core.PlayerInput.TouchPlatform) Application.targetFrameRate = 60;
        }

        /// <summary>
        /// M16 跨平台：触屏平台创建虚拟摇杆。
        /// 本游戏自动开火，移动是唯一需要的玩家操作输入——补上摇杆即让移动端可完整游玩；
        /// 桌面平台不创建，输入仍走原有 Input 轴，行为与之前完全一致。
        /// </summary>
        private static void EnsureTouchJoystick()
        {
            if (!Core.PlayerInput.TouchPlatform) return;
            Track(new GameObject("TouchJoystick").AddComponent<Core.TouchJoystick>());
        }

        /// <summary>
        /// M14：确保主相机挂载 <see cref="Core.CameraShake"/>。
        /// 既存缺陷修复：此前该组件只在单机路径的 <see cref="AttachCameraShake"/> 里被挂载，
        /// 联机（房主/客户端/观战）路径从未创建它——导致联机下相机停在原点不跟随玩家、
        /// 且完全没有震屏表现（NetPlayer 只是"已存在才配置"，从不创建）。
        /// 本方法把所有模式统一补齐；跟随目标仍由各处 Configure() 设置
        /// （单机=玩家，联机=本地玩家，观战=被观察者）。
        /// </summary>
        private static void EnsureCameraShake()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent<Core.CameraShake>() == null)
                cam.gameObject.AddComponent<Core.CameraShake>();
        }

        /// <summary>拆除当前世界（联机断线/返回主菜单/重开一局时调用），使随后可再次 Build。</summary>
        public static void TeardownWorld()
        {
            // M24：先把**池化出来的实体**清干净，再销毁管理器本体。
            // 宝石 / 回血球 / 敌人都是 PoolManager 从模板克隆出来的实例，挂在场景根节点，
            // 既不在 _created 里、也不是任何管理器的子物体 —— 只 Destroy 管理器的话，
            // 上一局的实体会整批漏到新世界：看得见却捡不起来的宝石、围着旧玩家坐标转的幽灵敌人。
            // ResetAll 处理"还在场上的"，DestroyAll 处理"已经归还到池里的"，两者缺一不可。
            Core.PoolManager.Instance?.DestroyAll();
            PickupManager.Instance?.ResetAll();
            EnemyManager.Instance?.ResetAll();
            // M26：临时特效（前摇警示圈 / Nova 爆发环）同样是 new GameObject 建在场景根上的，
            // 不属于任何管理器 —— 漏掉这一行，上一局的圈会整批出现在新世界的开局画面里。
            Effects.TempEffect.DestroyAll();
            // M28：链闪电的电弧段/闪光与警示圈是同一个形态（场景根 + 只靠计时自毁），
            // 由 audit_project.py 第 7 项静态检查扫出来。
            Combat.Skill.ChainBolt.DestroyAll();

            // 再同步清理单例引用：Object.Destroy 的销毁延迟到帧末，
            // 若不清 Instance，紧随其后的 Build() 会被「EnemyManager.Instance != null」守卫错误拦下
            // （PlayerController 的静态 Instance 同理，详见其 ClearInstance 注释）。
            foreach (var o in _created)
            {
                if (o == null) continue;
                ClearSingletons(o);
                Object.Destroy(o);
            }
            _created.Clear();
            _built = false;
            Core.PlaytimeGuard.GameRunning = false;      // 菜单不计游戏时长
            Core.MusicManager.Instance?.PlayMenu();      // M7：拆世界回菜单乐
            if (UI.MainMenu.Instance != null) UI.MainMenu.Instance.RefreshRecords();
        }

        /// <summary>
        /// 销毁一个已登记对象前，先清掉它身上的单例引用。
        ///
        /// M27：Track 改成登记 GameObject 之后，这里不能再只判断"被登记的那个对象是不是单例"——
        /// GameObject 当然不是 ISingletonInstance，于是 EnemyManager / PickupManager /
        /// PlayerController 的静态 Instance 全都留在指向已销毁组件的状态上，
        /// 紧随其后的 Build() 会被「Instance != null」守卫整个拦下：
        /// 玩家拿不到、管理器全缺——正是 M24 修过的那个失败模式。
        /// GameObject 与组件两种登记形态都要走到。
        /// </summary>
        private static void ClearSingletons(Object o)
        {
            var go = o as GameObject;
            if (go != null)
            {
                // 走 MonoBehaviour 再判接口：GetComponents<接口> 的可用性随 Unity 版本而异
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;                       // 已销毁/缺失脚本
                    if (mb is Core.ISingletonInstance s) s.ClearInstance();
                }
                return;
            }
            if (o is Core.ISingletonInstance single) single.ClearInstance();
        }

        public static void Build()
        {
            if (_built) return;

            // M19：青少年模式当日时长耗尽时**不允许再开局**。
            // 旧实现只在跨过上限的那一瞬间把玩家踢回主菜单，而该动作每天只触发一次——
            // 回菜单再点「开始游戏」照样能建世界，限时被完整绕过。
            // 故把判定前移到开局入口：只要当日累计已达上限，一律拦下并提示。
            if (!Core.PlaytimeGuard.CanStartRun())
            {
                if (UI.GameOverPanel.Instance != null) UI.GameOverPanel.Instance.Hide();
                UI.MainMenu.Ensure();
                UI.MainMenu.Instance?.ShowWithNotice(Core.Localization.T("playtime.force_exit"));
                return;
            }

            _built = true;
            Core.PlaytimeGuard.GameRunning = true;       // 对局开始：累计游戏时长
            UI.GameOverPanel.Ensure();                   // 幂等：主菜单/大厅路径可能未创建

            bool net = Net.NetSession.IsNetworked;

            if (!net)
            {
                BuildSinglePlayer();
            }
            else
            {
                BuildNetworked();
            }
            Core.MusicManager.Instance?.PlayBattle();     // M7：对局开始切战斗乐（Boss 出场时自动切换）
        }

        // —— 单机：与 M1–M3.6 完全一致的行为（M5 起由主菜单触发） ——
        private static void BuildSinglePlayer()
        {
            // 玩家与全部模板都纳入 Track：重开一局/回菜单时完整拆除，杜绝残留
            var player = Track(CreatePlayer());
            // M18：PlayerController.isLocal 默认已改为 false（避免联机下未接线完成就自行开火），
            // 单机路径这里显式置回 true，保证移动/开火/HUD 事件行为与之前完全一致。
            var pc0 = player.GetComponent<Player.PlayerController>();
            if (pc0 != null) pc0.isLocal = true;
            Player.CharacterManager.ApplyTo(player, Player.CharacterManager.Selected);   // M6.2 角色属性/起始武器
            var enemyTemplate = Track(CreateEnemyTemplate());
            var projectileTemplate = Track(CreateProjectileTemplate());
            var boomerangTemplate = Track(CreateBoomerangTemplate());   // M6.1
            var homingTemplate = Track(CreateHomingTemplate());         // M6.1
            var mineTemplate = Track(CreateMineTemplate());             // M9
            var gemTemplate = Track(CreateGemTemplate());
            var healthTemplate = Track(CreateHealthTemplate());
            var bossTemplate = Track(CreateBossTemplate());
            var enemyProjectileTemplate = Track(CreateEnemyProjectileTemplate());

            AttachCameraShake(player.transform);
            EnsureTouchJoystick();                      // M16 跨平台：触屏虚拟摇杆

            Track(new GameObject("PoolManager").AddComponent<Core.PoolManager>());
            Track(new GameObject("CombatManager").AddComponent<CombatManager>());
            Track(new GameObject("ComboManager").AddComponent<ComboManager>());
            Track(new GameObject("SfxManager").AddComponent<Core.SfxManager>());

            var em = new GameObject("EnemyManager").AddComponent<EnemyManager>();
            Track(em);
            em.Configure(enemyTemplate, player.transform, player.GetComponent<Damageable>());
            em.ConfigureBoss(bossTemplate, enemyProjectileTemplate);
            em.StartWaves();

            var pm = new GameObject("PickupManager").AddComponent<PickupManager>();
            Track(pm);
            pm.Configure(gemTemplate, healthTemplate, player.GetComponent<PlayerController>());

            Track(new GameObject("DamageNumberManager").AddComponent<DamageNumberManager>());
            Track(new GameObject("LevelUpManager").AddComponent<LevelUpManager>());
            Track(new GameObject("HitstopManager").AddComponent<Core.HitstopManager>());   // M8 手感：命中停顿/选卡暂停时间缩放

            var hud = new GameObject("HUD").AddComponent<HUDController>();
            Track(hud);
            CreateRuntimeHUD(hud);

            player.GetComponent<PlayerController>().SetProjectileTemplate(projectileTemplate);
            player.GetComponent<PlayerController>().SetSpecialTemplates(boomerangTemplate, homingTemplate);
            player.GetComponent<PlayerController>().SetMineTemplate(mineTemplate);   // M9
        }

        // —— 联机：玩家由 Fish-Net PlayerSpawner 生成，本地只搭舞台与管理器 ——
        private static void BuildNetworked()
        {
            // M18 联机实测修复：PoolManager 此前只在单机路径创建，联机路径漏了。
            // 而弹丸/回旋镖/追踪弹/地雷四条开火分支都走 PoolManager.Instance.Get(...)，
            // 房主侧一旦有任一玩家未挂网络弹丸生成器，就会退回到本地池分支并抛 NRE
            // ——实测里房主 60 秒共抛出 14 条 NullReferenceException（客户端 0 条，
            // 因为联机客户端根本不进开火结算，只发请求）。补上与单机一致的对象池。
            Track(new GameObject("PoolManager").AddComponent<Core.PoolManager>());
            Track(new GameObject("CombatManager").AddComponent<CombatManager>());
            Track(new GameObject("ComboManager").AddComponent<ComboManager>());
            Track(new GameObject("SfxManager").AddComponent<Core.SfxManager>());
            Track(new GameObject("DamageNumberManager").AddComponent<DamageNumberManager>());
            Track(new GameObject("LevelUpManager").AddComponent<LevelUpManager>());
            Track(new GameObject("HitstopManager").AddComponent<Core.HitstopManager>());   // M8 手感：命中停顿/选卡暂停时间缩放

            var hud = new GameObject("HUD").AddComponent<HUDController>();
            Track(hud);
            CreateRuntimeHUD(hud);

            // 联机诊断 HUD（M4.5 实机调优：模式/FPS/实体数）
            Track(new GameObject("NetStatus").AddComponent<Net.NetStatus>());

            EnsureTouchJoystick();                      // M16 跨平台：触屏虚拟摇杆

            // M14 观战：观战者没有自己的角色，由本组件接管相机跟随与提示
#if FISHNET_ENABLED
            // SpectatorCamera / NetWatchdog 均为联机专属类型（整文件受 FISHNET_ENABLED 保护），
            // 未装插件时这些引用必须一并编译掉——否则违背「未安装联机插件时保持纯单机可编译」的设计约束。
            if (Net.NetSession.Spectator)
                Track(new GameObject("SpectatorCamera").AddComponent<Net.SpectatorCamera>());
#endif

            var cfg = Net.NetConfig.Instance;
            if (cfg == null || cfg.playerPrefab == null)
            {
                Debug.LogError("[StarPixel] 联机模式需要 NetBootstrap 场景（含 NetConfig 与玩家预制体）。" +
                               "请打开 Assets/Scenes/NetBootstrap.unity，或用菜单 StarPixel/Setup FishNet 重新装配。");
                return;
            }

            // 生成看门狗：6 秒内本地玩家未生成则输出带排查步骤的错误（M4.5）
            // M14：观战者本就没有本地玩家，看门狗会误报，故跳过
#if FISHNET_ENABLED
            if (!Net.NetSession.Spectator)
                Track(new GameObject("NetWatchdog").AddComponent<Net.NetWatchdog>());
#endif

            // 房主：波次权威 + 世界状态同步对象（客户端只读）
            if (Net.NetSession.IsAuthority)
            {
                var em = new GameObject("EnemyManager").AddComponent<EnemyManager>();
                Track(em);
                em.StartWaves();
                Net.NetWorld.SpawnWorldState();
            }
        }

        private static void AttachCameraShake(Transform target)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var shake = cam.GetComponent<Core.CameraShake>();
            if (shake == null) shake = cam.gameObject.AddComponent<Core.CameraShake>();
            shake.Configure(target);
        }

        private static void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("MainCamera");   // @owner: stage
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
            }
            cam.orthographic = true;
            cam.orthographicSize = 8f;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            cam.transform.position = new Vector3(0, 0, -10);
        }

        private static void SetupEventSystem()
        {
            if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;
            var es = new GameObject("EventSystem");   // @owner: stage
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        private static PlayerController CreatePlayer()
        {
            var go = new GameObject("Player");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("player", Color.cyan);
            sr.color = Color.white;                       // 精灵自带颜色
            sr.sortingOrder = 10;
            go.transform.localScale = Vector3.one * 0.5f;

            var dmg = go.AddComponent<Damageable>();
            dmg.Init(100f);

            var ctrl = go.AddComponent<PlayerController>();
            return ctrl;
        }

        private static GameObject CreateEnemyTemplate()
        {
            var go = new GameObject("EnemyTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("enemy_chaser", Color.red);
            sr.color = Color.white;
            sr.sortingOrder = 5;
            go.transform.localScale = Vector3.one * 0.45f;
            // M17 实测修复：Enemy 与命名空间 StarPixel.Enemy 同名，非限定写法会 CS0118
            go.AddComponent<StarPixel.Enemy.Enemy>();
            go.SetActive(false);
            return go;
        }

        private static GameObject CreateProjectileTemplate()
        {
            var go = new GameObject("ProjectileTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("projectile", Color.yellow);
            sr.color = Color.white;
            sr.sortingOrder = 8;
            go.transform.localScale = Vector3.one * 0.25f;
            go.AddComponent<Projectile>();
            go.SetActive(false);
            return go;
        }

        // M6.1：回旋镖模板（自旋刃体，放大以提高可见性）
        private static GameObject CreateBoomerangTemplate()
        {
            var go = new GameObject("BoomerangTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("projectile", Color.yellow);
            sr.color = new Color(0.85f, 0.95f, 1f);
            sr.sortingOrder = 8;
            go.transform.localScale = Vector3.one * 0.45f;
            go.AddComponent<Boomerang>();
            go.SetActive(false);
            return go;
        }

        // M6.1：追踪弹模板（偏红色的小弹体）
        private static GameObject CreateHomingTemplate()
        {
            var go = new GameObject("HomingTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("projectile", Color.yellow);
            sr.color = new Color(1f, 0.6f, 0.3f);
            sr.sortingOrder = 8;
            go.transform.localScale = Vector3.one * 0.22f;
            go.AddComponent<HomingMissile>();
            go.SetActive(false);
            return go;
        }

        // M9：脉冲地雷模板（暗红→亮黄的待引爆小体）
        private static GameObject CreateMineTemplate()
        {
            var go = new GameObject("MineTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("projectile", Color.yellow);
            sr.color = new Color(1f, 0.4f, 0.4f, 0.5f);
            sr.sortingOrder = 8;
            go.transform.localScale = Vector3.one * 0.5f;
            go.AddComponent<StarPixel.Combat.Skill.Mine>();
            go.SetActive(false);
            return go;
        }

        private static GameObject CreateGemTemplate()
        {
            var go = new GameObject("GemTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("gem", Color.blue);
            sr.color = Color.white;
            sr.sortingOrder = 2;
            go.transform.localScale = Vector3.one * 0.2f;
            go.AddComponent<XpGem>();
            go.SetActive(false);
            return go;
        }

        private static GameObject CreateHealthTemplate()
        {
            var go = new GameObject("HealthTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("health", new Color(0.95f, 0.2f, 0.25f));
            sr.color = Color.white;
            sr.sortingOrder = 2;
            go.transform.localScale = Vector3.one * 0.22f;
            go.AddComponent<HealthPickup>();
            go.SetActive(false);
            return go;
        }

        private static GameObject CreateBossTemplate()
        {
            var go = new GameObject("BossTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("boss", new Color(0.8f, 0.2f, 0.8f));
            sr.color = Color.white;
            sr.sortingOrder = 6;
            go.transform.localScale = Vector3.one * 1.6f;
            go.AddComponent<StarPixel.Enemy.Boss>();
            go.SetActive(false);
            return go;
        }

        private static GameObject CreateEnemyProjectileTemplate()
        {
            var go = new GameObject("EnemyProjectileTemplate");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("projectile", Color.yellow);
            sr.color = new Color(1f, 0.35f, 0.35f);       // 敌方弹幕：偏红
            sr.sortingOrder = 7;
            go.transform.localScale = Vector3.one * 0.32f;
            go.AddComponent<EnemyProjectile>();
            go.SetActive(false);
            return go;
        }

        private static void CreateRuntimeHUD(HUDController hud)
        {
            var canvasGO = new GameObject("HUDCanvas");
            Track(canvasGO);                                 // 纳入拆世界范围，防重开后残留旧 HUD 画布
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            canvasGO.AddComponent<GraphicRaycaster>();

            // M7：低血量红晕（首子节点=最底层，不挡射线）
            hud.lowHpOverlay = Core.UIFactory.CreatePanel(canvasGO.transform, "LowHpVignette",
                Vector2.zero, new Vector2(1280, 720), new Color(0.75f, 0.05f, 0.08f, 0f));
            hud.lowHpOverlay.raycastTarget = false;

            hud.levelText = CreateText(canvasGO.transform, Core.Localization.T("hud.level", 1), new Vector2(0, 200), 26);
            hud.waveText = CreateText(canvasGO.transform, Core.Localization.T("hud.wave", 1), new Vector2(0, 170), 22);
            hud.killText = CreateText(canvasGO.transform, Core.Localization.T("hud.kills", 0), new Vector2(0, 140), 22);
            hud.timeText = CreateText(canvasGO.transform, Core.Localization.T("hud.time", "0.0"), new Vector2(0, 110), 22);

            // 联机模式标识（M4）
            hud.modeText = CreateText(canvasGO.transform, Net.NetSession.ModeLabel(), new Vector2(0, 230), 18);
            hud.modeText.color = new Color(0.7f, 0.85f, 1f, 0.9f);
            if (!Net.NetSession.IsNetworked) hud.modeText.gameObject.SetActive(false);

            hud.hpSlider = CreateBar(canvasGO.transform, "HPBar",
                new Vector2(0, 78), new Vector2(320, 18),
                new Color(0.12f, 0.12f, 0.15f, 0.85f), new Color(0.92f, 0.24f, 0.30f), 1f, true);

            hud.xpSlider = CreateBar(canvasGO.transform, "XPBar",
                new Vector2(0, 54), new Vector2(320, 12),
                new Color(0.12f, 0.12f, 0.15f, 0.85f), new Color(0.30f, 0.62f, 1f), 0f, true);

            // M14 观战：没有自己的角色，隐藏玩家专属的血量/经验条（波次/Boss 等全局信息仍显示）
            if (Net.NetSession.Spectator)
            {
                hud.hpSlider.gameObject.SetActive(false);
                hud.xpSlider.gameObject.SetActive(false);
            }

            hud.bossSlider = CreateBar(canvasGO.transform, "BossBar",
                new Vector2(0, 300), new Vector2(540, 22),
                new Color(0.20f, 0.05f, 0.08f, 0.9f), new Color(1f, 0.28f, 0.38f), 1f, false);
            hud.bossBarRoot = hud.bossSlider.gameObject;

            hud.zoneText = CreateText(canvasGO.transform, "", new Vector2(0, 250), 30);
            hud.zoneText.color = new Color(1f, 1f, 1f, 0f);

            // M7：组合技激活提示（区域提示下方，金色高亮后淡出）
            hud.comboToast = CreateText(canvasGO.transform, "", new Vector2(0, 210), 24);
            hud.comboToast.color = new Color(1f, 0.85f, 0.3f, 0f);

            // 武器列表（M3.6 build 反馈），置于左上角
            hud.weaponText = CreateText(canvasGO.transform, "", new Vector2(-460, 300), 18);
            hud.weaponText.alignment = TextAnchor.UpperLeft;
            hud.weaponText.color = new Color(0.85f, 0.92f, 1f, 0.95f);
            var wtRt = (RectTransform)hud.weaponText.transform;
            wtRt.anchorMin = new Vector2(0f, 1f);
            wtRt.anchorMax = new Vector2(0f, 1f);
            wtRt.pivot = new Vector2(0f, 1f);
            wtRt.anchoredPosition = new Vector2(24, -24);
            wtRt.sizeDelta = new Vector2(360, 160);
        }

        /// <summary>创建带背景与填充的进度条（可隐藏），返回 Slider。</summary>
        private static Slider CreateBar(Transform parent, string name, Vector2 pos, Vector2 size,
            Color bgColor, Color fillColor, float value, bool visible)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var bg = root.AddComponent<Image>();
            bg.color = bgColor;

            var slider = root.AddComponent<Slider>();

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            var faRt = (RectTransform)fillArea.transform;
            faRt.anchorMin = new Vector2(0, 0);
            faRt.anchorMax = new Vector2(1, 1);
            faRt.offsetMin = new Vector2(2, 2);
            faRt.offsetMax = new Vector2(-2, -2);

            var fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = new Vector2(0, 0);
            fillRt.anchorMax = new Vector2(1, 1);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = fillColor;

            slider.fillRect = fillRt;
            slider.targetGraphic = fillImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = value;
            slider.transition = Selectable.Transition.None;

            root.SetActive(visible);
            return slider;
        }

        private static Text CreateText(Transform parent, string content, Vector2 anchoredPos, int size)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = GetUIFont();
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = size;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(600, 40);
            return t;
        }

        /// <summary>兼容不同 Unity 版本的内置字体名（LegacyRuntime / Arial）。</summary>
        private static Font GetUIFont() => Core.UIFactory.UIFont;
    }
}
