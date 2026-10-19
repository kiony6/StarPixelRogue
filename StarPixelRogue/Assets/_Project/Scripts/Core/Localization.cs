using System;
using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 轻量本地化（M5）：中/英双语文案，运行时切换，无外部资源依赖。
    /// - <see cref="T"/> 按键取文案，缺失键回退为键名（便于开发期发现漏翻）；
    /// - 切换语言经 <see cref="SetLanguage"/> 广播 <see cref="LanguageChanged"/>，
    ///   HUD/菜单订阅后刷新。**不要假设"每帧重绘就自动生效"**：HUD 只有时间与击杀
    ///   两行是每帧重画的，波次/等级/区域/武器名只在各自事件触发时赋值——
    ///   它们必须显式订阅 LanguageChanged 重画，否则切语言后会停在旧语言
    ///   （M33 实测：切成英文后 HUD 上仍是「波次 1」，要等进下一波才变）；
    /// - 默认语言跟随 <see cref="SettingsManager"/>（无设置时 zh-CN，中国区首发）。
    /// </summary>
    public static class Localization
    {
        public const string LangZh = "zh-CN";
        public const string LangEn = "en-US";

        public static string Language { get; private set; } = LangZh;

        /// <summary>语言切换广播（MainMenu/SettingsPanel 订阅后重建）。</summary>
        public static event Action LanguageChanged;

        private static Dictionary<string, string> _zh;
        private static Dictionary<string, string> _en;
        private static bool _ready;

        /// <summary>切换语言（same 语言也允许触发一次刷新，供初始化调用）。</summary>
        public static void SetLanguage(string lang, bool silent = false)
        {
            if (lang != LangZh && lang != LangEn) lang = LangZh;
            bool changed = Language != lang;
            Language = lang;
            if (changed && !silent) LanguageChanged?.Invoke();
        }

        private static void EnsureReady()
        {
            if (_ready) return;
            _ready = true;
            _zh = new Dictionary<string, string>
            {
                // 标题/主菜单
                ["app.title"] = "星界像素狂潮",
                ["app.subtitle"] = "ROGUELITE SURVIVOR",
                ["menu.start"] = "开始游戏",
                ["menu.settings"] = "设置",
                ["menu.quit"] = "退出游戏",
                ["menu.character"] = "角色",
                ["menu.codex"] = "图鉴",
                ["menu.records"] = "战绩",
                ["menu.best_wave"] = "最佳波次",
                ["menu.best_time"] = "最佳时长",
                ["menu.best_level"] = "最佳等级",
                ["menu.total_runs"] = "总局数",
                ["menu.last_run"] = "上次战绩",
                ["menu.none"] = "—",
                ["menu.records_empty"] = "尚无战绩，开始你的第一局！",
                ["menu.hint_net"] = "检测到联机插件：退出主菜单后将进入联机大厅",
                // HUD
                ["hud.wave"] = "波次 {0}",
                ["hud.level"] = "等级 {0}",
                ["hud.kills"] = "击杀 {0}",
                ["hud.time"] = "时间 {0}",
                ["hud.clear"] = "通关！ {0}",   // M8 方案 A：达标时替换计时文案
                ["hud.zone"] = "— 第 {0} 区 —",
                // M7：组合技
                ["hud.combo"] = "组合技激活！{0}",
                ["combo.fire_storm"] = "烈焰风暴 · 伤害 ×1.5",
                ["combo.ice_earth"] = "冰封大地 · 命中减速",
                ["combo.lightning_fire"] = "雷电爆轰 · 穿透 +2",
                ["combo.holy_fire"] = "圣炎共鸣 · 伤害 ×1.4",
                // 结算
                ["gameover.title"] = "游戏结束",
                ["gameover.victory"] = "通关！",   // M8 方案 A
                ["gameover.restart"] = "重新开始",
                ["gameover.menu"] = "返回主菜单",
                ["gameover.leave"] = "离开对局",
                ["gameover.new_record"] = "新纪录！",
                ["gameover.stats"] = "波次 {0}    等级 {1}    击杀 {2}    时长 {3}",
                // 设置
                ["settings.title"] = "设置",
                ["settings.audio"] = "音频",
                ["settings.master"] = "主音量",
                ["settings.music"] = "音乐音量",
                ["settings.sfx"] = "音效音量",
                ["settings.gameplay"] = "玩法",
                ["settings.quality"] = "画质",
                ["settings.language"] = "语言",
                ["settings.fullscreen"] = "全屏",
                ["settings.shake"] = "屏幕震动",
                ["settings.telemetry"] = "匿名数据统计",
                ["settings.minor_mode"] = "青少年模式",
                ["settings.minor_hint"] = "开启后每日游戏时长受限（框架版，正式版将接入官方实名认证）",
                ["settings.legal"] = "法律文本",
                ["settings.export_log"] = "导出诊断日志",
                ["settings.export_done"] = "日志目录：{0}",
                ["settings.close"] = "关闭",
                // M13 云存档（设置页开关 + 状态展示）
                ["settings.cloud_header"] = "云存档",
                ["settings.cloud_save"] = "云存档同步",
                ["settings.cloud_sync_now"] = "立即同步",
                ["cloud.status.idle"] = "未启用",
                ["cloud.status.off"] = "未启用（可在设置中开启）",
                ["cloud.status.pull_fail"] = "拉取失败（继续使用本地存档）",
                ["cloud.status.empty"] = "云端为空，正在建档",
                ["cloud.status.bad_data"] = "云端数据无法解析，已保留本地存档",
                ["cloud.status.newer_schema"] = "云端存档版本较新，已保留本地存档",
                ["cloud.status.local_not_ready"] = "本地存档未就绪，跳过云端合并",
                ["cloud.status.took_cloud"] = "已采用云端存档（云端较新）",
                ["cloud.status.keep_local"] = "本地较新，保持本地",
                ["cloud.status.pushed"] = "已上传至云端",
                ["cloud.status.push_give_up"] = "上传失败，已停止重试（本地存档不受影响）",
                ["cloud.status.push_retry"] = "上传失败，{0}s 后重试：{1}",
                // 合规门
                ["compliance.title"] = "健康游戏忠告",
                ["compliance.notice1"] = "抵制不良游戏，拒绝盗版游戏。",
                ["compliance.notice2"] = "注意自我保护，谨防受骗上当。",
                ["compliance.notice3"] = "适度游戏益脑，沉迷游戏伤身。合理安排时间，享受健康生活。",
                ["compliance.read"] = "请阅读并同意以下协议后进入游戏：",
                ["compliance.terms"] = "《用户服务协议》",
                ["compliance.privacy"] = "《隐私政策》",
                ["compliance.children"] = "《儿童个人信息保护规则》",
                ["compliance.agree"] = "同意并继续",
                ["compliance.disagree"] = "不同意并退出",
                ["compliance.minor_notice"] = "未成年玩家请在监护人陪同下阅读《儿童个人信息保护规则》",
                // 法律文本浏览
                ["legal.title"] = "法律文本",
                ["legal.close"] = "返回",
                // 通用
                ["common.on"] = "开",
                ["common.off"] = "关",
                ["common.low"] = "低",
                ["common.medium"] = "中",
                ["common.high"] = "高",
                ["common.chinese"] = "中文",
                ["common.english"] = "English",
                // M6.2 角色
                ["char.title"] = "选择角色",
                ["char.ranger"] = "星尘游侠",
                ["char.ranger_desc"] = "均衡的远程射手，适应任何流派build。",
                ["char.ranger_trait"] = "特性：经验获取 +15%",
                ["char.bulwark"] = "重装守卫",
                ["char.bulwark_desc"] = "厚重的近战专家，以范围与伤害压制敌群。",
                ["char.bulwark_trait"] = "特性：生命 +35% / 范围 +20% / 伤害 +10%，移动 -10%",
                ["char.select"] = "选择",
                ["char.selected"] = "已选择",
                ["char.start_weapon"] = "起始武器",
                ["char.stats"] = "生命 {0}%  速度 {1}%  伤害 {2}%  范围 {3}%",
                // M6.4 图鉴
                ["codex.title"] = "图鉴",
                ["codex.tab_weapons"] = "武器",
                ["codex.tab_enemies"] = "敌人",
                ["codex.tab_boss"] = "Boss",
                ["codex.tab_combos"] = "组合技",
                ["codex.tab_stats"] = "统计",
                ["codex.locked"] = "？？？（尚未发现）",
                ["codex.enemy_chaser"] = "蚀能爬行者",
                ["codex.enemy_runner"] = "疾影突进者",
                ["codex.enemy_tank"] = "岩甲巨兵",
                ["codex.enemy_elite"] = "精英变体",
                ["codex.boss_name"] = "虚空之眼",
                ["codex.boss_desc"] = "每 5 波降临的巨型威胁，血量降低将进入更狂暴的阶段。",
                ["codex.boss_progress"] = "已击败：{0} 次",
                ["codex.combo_fire_storm"] = "烈焰风暴：火 + 风暴 → 伤害 ×1.5",
                ["codex.combo_ice_earth"] = "冰封大地：冰 + 土 → 命中减速",
                ["codex.combo_lightning_fire"] = "雷霆烈焰：雷 + 火 → 穿透 +2",
                ["codex.combo_holy_fire"] = "炽热圣印：圣 + 火 → 伤害 ×1.4",
                ["codex.stat_runs"] = "总局数",
                ["codex.stat_clears"] = "通关次数",   // M8 方案 A
                ["codex.stat_kills"] = "总击杀",
                ["codex.stat_time"] = "总时长",
                ["codex.stat_best_wave"] = "最佳波次",
                ["codex.stat_bosses"] = "Boss 击败",
                ["codex.stat_weapons"] = "已发现武器",
                ["codex.stat_enemies"] = "已发现敌种",
                ["codex.stat_combos"] = "已发现组合",
                ["codex.character_runs"] = "{0}：{1} 局",
                // M18 续四：联机大厅（M4 建的界面此前整屏硬编码中文，英文界面会冒中文）
                ["net.lobby_title"] = "星界像素狂潮 · 联机大厅",
                ["net.lobby_subtitle"] = "状态同步 + 半权威房主（ADR-001）",
                ["net.pick_mode"] = "选择模式开始游戏",
                ["net.single"] = "单人游戏",
                ["net.host"] = "创建房间（房主）",
                ["net.join"] = "加入房间",
                ["net.spectate"] = "以观战身份加入（M14）",
                ["net.address"] = "目标地址",
                ["net.err_no_netconfig"] = "当前场景缺少 NetConfig，请打开 Assets/Scenes/NetBootstrap.unity（或先用菜单 StarPixel/Setup FishNet 装配）",
                ["net.err_no_fishnet"] = "未检测到 Fish-Net，请先按 README 安装并联机",
                ["net.creating"] = "正在创建房间…",
                ["net.connecting"] = "正在连接 {0} …",
                ["net.connecting_spectate"] = "正在以观战身份连接 {0} …",
                ["net.disconnected"] = "已断开连接，请重新选择模式",
                // M33：联机运行时浮层（断线遮罩 / 观战横幅 / 诊断 HUD / 模式名）。
                // 此前这些文案直接硬编码在 Net 层代码里——英文界面下整片冒中文，
                // 且这段代码在 #if FISHNET_ENABLED 里、无头单机编译不到，只能靠静态检查兜住。
                ["net.mode_spectator"] = "观战",
                ["net.mode_host"] = "房主",
                ["net.mode_client"] = "客户端",
                ["net.mode_single"] = "单机",
                ["net.reconnecting"] = "连接已断开，正在重连…",
                ["net.reconnect_attempt"] = "连接已断开，正在重连… ({0}/{1})",
                ["net.back_to_lobby"] = "返回大厅",
                ["net.spectating_switch"] = "观战中 · Tab 切换视角",
                ["net.spectating_wait"] = "观战中 · 等待玩家加入…",
                ["net.status_enemies"] = "  敌人 {0}",
                ["net.status_players"] = "  玩家 {0}",
                // M18 续四：升级选卡（核心玩法 UI；武器名与升级文案此前全是硬编码中文）
                ["levelup.title"] = "LEVEL UP!  选择强化",
                ["upgrade.unlock"] = "解锁 · {0}",
                ["upgrade.level"] = "强化 · {0} Lv{1}",
                ["weapon.name.1"] = "星能弹",
                ["weapon.name.2"] = "散射枪",
                ["weapon.name.3"] = "寒霜新星",
                ["weapon.name.4"] = "裂地波",
                ["weapon.name.5"] = "雷环刃",
                ["weapon.name.6"] = "雷霆之链",
                ["weapon.name.7"] = "月刃",
                ["weapon.name.8"] = "蜂群导弹",
                ["weapon.name.9"] = "圣光领域",
                ["weapon.name.10"] = "脉冲地雷",
                ["weapon.desc.projectile"] = "自动锁定最近敌人",
                ["weapon.desc.scatter"] = "一次发射多枚弹体",
                ["weapon.desc.nova"] = "周期性以自身为中心爆发",
                ["weapon.desc.orbit"] = "环绕自身的近战刃",
                ["weapon.desc.chain"] = "在敌人之间跳跃的闪电",
                ["weapon.desc.boomerang"] = "飞出后返回的回旋刃",
                ["weapon.desc.homing"] = "自动追踪敌人的导弹",
                ["weapon.desc.aura"] = "持续伤害身边敌人的领域",
                ["weapon.desc.mine"] = "埋设后自动引爆的区域封锁",
                ["weapon.next.projectile"] = "伤害 +25%",
                ["weapon.next.projectile.3"] = "伤害 +25%，弹体 +1",
                ["weapon.next.projectile.4"] = "伤害 +25%，弹体 +1，穿透 +1",
                ["weapon.next.scatter"] = "弹体 +1，伤害 +10%",
                ["weapon.next.nova"] = "范围 +15%，伤害 +20%",
                ["weapon.next.orbit"] = "刃 +1，伤害 +10%",
                ["weapon.next.chain"] = "跳电目标 +1，伤害 +18%",
                ["weapon.next.boomerang"] = "伤害 +22%",
                ["weapon.next.boomerang.3"] = "弹体 +1，伤害 +22%",
                ["weapon.next.homing"] = "弹体 +1，伤害 +20%",
                ["weapon.next.aura"] = "范围 +12%，伤害 +15%",
                ["weapon.next.mine"] = "伤害 +20%",
                ["weapon.next.mine.3"] = "地雷 +1，伤害 +20%",
                ["upgrade.stat.0"] = "最大生命 +20",
                ["upgrade.stat.desc.0"] = "并回满",
                ["upgrade.stat.1"] = "移动速度 +8%",
                ["upgrade.stat.2"] = "伤害 +15%",
                ["upgrade.stat.3"] = "冷却 -12%",
                ["upgrade.stat.4"] = "弹体 +1",
                ["upgrade.stat.desc.4"] = "直射/散射武器",
                ["upgrade.stat.5"] = "拾取范围 +30%",
                ["upgrade.stat.6"] = "范围 +15%",
                // M18 续四：防沉迷/健康提示（合规相关：英文玩家看不懂 = 提示等于没弹）
                ["playtime.warn"] = "今日游戏时间即将达到上限，请注意休息。",
                ["playtime.health_title"] = "健康游戏提示",
                ["playtime.health"] = "您已连续游戏 45 分钟，适度游戏益脑，沉迷游戏伤身。",
                ["playtime.force_exit"] = "今日游戏时间已达上限（青少年模式），请明天再来。",
                // M18 续四：云存档未启用原因（存键 + 渲染，与 LastStatus 同一套做法）
                ["cloud.reason.no_provider"] = "Provider 缺失",
                ["cloud.reason.not_configured"] = "后端未配置（或地址非 HTTPS）",
                ["cloud.reason.no_settings"] = "设置管理器缺失",
                ["cloud.reason.user_off"] = "用户未开启",
                ["cloud.reason.not_agreed"] = "未同意最新版隐私政策",
                ["cloud.err.not_configured"] = "未配置",
            };
            _en = new Dictionary<string, string>
            {
                ["app.title"] = "Star Pixel Rogue",
                ["app.subtitle"] = "ROGUELITE SURVIVOR",
                ["menu.start"] = "Start Game",
                ["menu.settings"] = "Settings",
                ["menu.quit"] = "Quit",
                ["menu.character"] = "Character",
                ["menu.codex"] = "Codex",
                ["menu.records"] = "Records",
                ["menu.best_wave"] = "Best Wave",
                ["menu.best_time"] = "Best Time",
                ["menu.best_level"] = "Best Level",
                ["menu.total_runs"] = "Total Runs",
                ["menu.last_run"] = "Last Run",
                ["menu.none"] = "-",
                ["menu.records_empty"] = "No records yet - start your first run!",
                ["menu.hint_net"] = "Multiplayer plugin detected: leaving the menu enters the lobby",
                ["hud.wave"] = "Wave {0}",
                ["hud.level"] = "Lv {0}",
                ["hud.kills"] = "Kills {0}",
                ["hud.time"] = "Time {0}",
                ["hud.clear"] = "CLEAR! {0}",   // M8: victory replaces the timer text
                ["hud.zone"] = "- ZONE {0} -",
                ["hud.combo"] = "COMBO ACTIVATED! {0}",
                ["combo.fire_storm"] = "Blazing Storm - DMG x1.5",
                ["combo.ice_earth"] = "Frozen Earth - Slow on Hit",
                ["combo.lightning_fire"] = "Thunderburst - Pierce +2",
                ["combo.holy_fire"] = "Holy Resonance - DMG x1.4",
                ["gameover.title"] = "GAME OVER",
                ["gameover.victory"] = "STAGE CLEAR!",   // M8: plan A
                ["gameover.restart"] = "Restart",
                ["gameover.menu"] = "Main Menu",
                ["gameover.leave"] = "Leave Match",
                ["gameover.new_record"] = "NEW RECORD!",
                ["gameover.stats"] = "Wave {0}    Lv {1}    Kills {2}    Time {3}",
                ["settings.title"] = "Settings",
                ["settings.audio"] = "Audio",
                ["settings.master"] = "Master",
                ["settings.music"] = "Music",
                ["settings.sfx"] = "SFX",
                ["settings.gameplay"] = "Gameplay",
                ["settings.quality"] = "Quality",
                ["settings.language"] = "Language",
                ["settings.fullscreen"] = "Fullscreen",
                ["settings.shake"] = "Screen Shake",
                ["settings.telemetry"] = "Anonymous Analytics",
                ["settings.minor_mode"] = "Minor Mode",
                ["settings.minor_hint"] = "Limits daily play time (framework; official real-name auth will be integrated in release)",
                ["settings.legal"] = "Legal",
                ["settings.export_log"] = "Export Diagnostic Logs",
                ["settings.export_done"] = "Log folder: {0}",
                ["settings.close"] = "Close",
                // M13 云存档（设置页开关 + 状态展示）
                ["settings.cloud_header"] = "Cloud Save",
                ["settings.cloud_save"] = "Cloud Save Sync",
                ["settings.cloud_sync_now"] = "Sync Now",
                ["cloud.status.idle"] = "Not enabled",
                ["cloud.status.off"] = "Not enabled (can be turned on in Settings)",
                ["cloud.status.pull_fail"] = "Pull failed (continuing with local save)",
                ["cloud.status.empty"] = "No cloud save yet, creating one",
                ["cloud.status.bad_data"] = "Cloud data unreadable, kept local save",
                ["cloud.status.newer_schema"] = "Cloud save is newer, kept local save",
                ["cloud.status.local_not_ready"] = "Local save not ready, skipped cloud merge",
                ["cloud.status.took_cloud"] = "Adopted cloud save (cloud is newer)",
                ["cloud.status.keep_local"] = "Local save is newer, kept local",
                ["cloud.status.pushed"] = "Uploaded to cloud",
                ["cloud.status.push_give_up"] = "Upload failed, stopped retrying (local save unaffected)",
                ["cloud.status.push_retry"] = "Upload failed, retrying in {0}s: {1}",
                ["compliance.title"] = "HEALTHY GAMING ADVISORY",
                ["compliance.notice1"] = "Boycott harmful games; refuse pirated games.",
                ["compliance.notice2"] = "Protect yourself; beware of scams.",
                ["compliance.notice3"] = "Gaming in moderation benefits the mind; addiction harms the body. Manage your time and enjoy a healthy life.",
                ["compliance.read"] = "Please read and accept the agreements to continue:",
                ["compliance.terms"] = "Terms of Service",
                ["compliance.privacy"] = "Privacy Policy",
                ["compliance.children"] = "Children's Privacy Rules",
                ["compliance.agree"] = "Accept and Continue",
                ["compliance.disagree"] = "Decline and Quit",
                ["compliance.minor_notice"] = "Minor players: please read the Children's Privacy Rules with a guardian",
                ["legal.title"] = "Legal Documents",
                ["legal.close"] = "Back",
                ["common.on"] = "ON",
                ["common.off"] = "OFF",
                ["common.low"] = "Low",
                ["common.medium"] = "Medium",
                ["common.high"] = "High",
                ["common.chinese"] = "Chinese",
                ["common.english"] = "English",
                // M6.2 Characters
                ["char.title"] = "Select Character",
                ["char.ranger"] = "Stardust Ranger",
                ["char.ranger_desc"] = "Balanced ranged fighter; fits any build.",
                ["char.ranger_trait"] = "Trait: +15% XP gain",
                ["char.bulwark"] = "Bulwark",
                ["char.bulwark_desc"] = "Heavy melee expert who dominates crowds with area and damage.",
                ["char.bulwark_trait"] = "Trait: +35% HP / +20% area / +10% damage, -10% speed",
                ["char.select"] = "Select",
                ["char.selected"] = "Selected",
                ["char.start_weapon"] = "Starting Weapon",
                ["char.stats"] = "HP {0}%  Speed {1}%  Damage {2}%  Area {3}%",
                // M6.4 Codex
                ["codex.title"] = "Codex",
                ["codex.tab_weapons"] = "Weapons",
                ["codex.tab_enemies"] = "Enemies",
                ["codex.tab_boss"] = "Boss",
                ["codex.tab_combos"] = "Combos",
                ["codex.tab_stats"] = "Stats",
                ["codex.locked"] = "??? (not discovered yet)",
                ["codex.enemy_chaser"] = "Corrosion Crawler",
                ["codex.enemy_runner"] = "Blitz Dasher",
                ["codex.enemy_tank"] = "Stoneguard Brute",
                ["codex.enemy_elite"] = "Elite Variant",
                ["codex.boss_name"] = "Void Eye",
                ["codex.boss_desc"] = "A massive threat every 5 waves; grows more frenzied as its HP drops.",
                ["codex.boss_progress"] = "Defeated: {0}",
                ["codex.combo_fire_storm"] = "Firestorm: Fire + Storm -> Damage x1.5",
                ["codex.combo_ice_earth"] = "Frozen Earth: Ice + Earth -> Hit slow",
                ["codex.combo_lightning_fire"] = "Thunderflame: Lightning + Fire -> Pierce +2",
                ["codex.combo_holy_fire"] = "Blazing Seal: Holy + Fire -> Damage x1.4",
                ["codex.stat_runs"] = "Total Runs",
                ["codex.stat_clears"] = "Clears",   // M8: plan A
                ["codex.stat_kills"] = "Total Kills",
                ["codex.stat_time"] = "Total Time",
                ["codex.stat_best_wave"] = "Best Wave",
                ["codex.stat_bosses"] = "Bosses Defeated",
                ["codex.stat_weapons"] = "Weapons Found",
                ["codex.stat_enemies"] = "Enemy Types Found",
                ["codex.stat_combos"] = "Combos Found",
                ["codex.character_runs"] = "{0}: {1} runs",
                // M18 续四: multiplayer lobby (whole screen was hardcoded Chinese before)
                ["net.lobby_title"] = "Star Pixel Rogue - Multiplayer Lobby",
                ["net.lobby_subtitle"] = "State sync + semi-authoritative host (ADR-001)",
                ["net.pick_mode"] = "Pick a mode to start",
                ["net.single"] = "Single Player",
                ["net.host"] = "Create Room (Host)",
                ["net.join"] = "Join Room",
                ["net.spectate"] = "Join as Spectator (M14)",
                ["net.address"] = "Target Address",
                ["net.err_no_netconfig"] = "This scene has no NetConfig; open Assets/Scenes/NetBootstrap.unity (or run menu StarPixel/Setup FishNet first)",
                ["net.err_no_fishnet"] = "Fish-Net not detected; install it per the README first",
                ["net.creating"] = "Creating room...",
                ["net.connecting"] = "Connecting to {0} ...",
                ["net.connecting_spectate"] = "Connecting to {0} as spectator ...",
                ["net.disconnected"] = "Disconnected; please pick a mode again",
                // M33: in-game multiplayer overlays (reconnect / spectator banner / diagnostic HUD / mode label).
                ["net.mode_spectator"] = "Spectator",
                ["net.mode_host"] = "Host",
                ["net.mode_client"] = "Client",
                ["net.mode_single"] = "Single Player",
                ["net.reconnecting"] = "Connection lost, reconnecting...",
                ["net.reconnect_attempt"] = "Connection lost, reconnecting... ({0}/{1})",
                ["net.back_to_lobby"] = "Back to Lobby",
                ["net.spectating_switch"] = "Spectating - Tab to switch view",
                ["net.spectating_wait"] = "Spectating - waiting for players...",
                ["net.status_enemies"] = "  Enemies {0}",
                ["net.status_players"] = "  Players {0}",
                ["levelup.title"] = "LEVEL UP!  Choose an Upgrade",
                ["upgrade.unlock"] = "Unlock - {0}",
                ["upgrade.level"] = "Upgrade - {0} Lv{1}",
                ["weapon.name.1"] = "Star Bolt",
                ["weapon.name.2"] = "Scatter Gun",
                ["weapon.name.3"] = "Frost Nova",
                ["weapon.name.4"] = "Seismic Wave",
                ["weapon.name.5"] = "Thunder Ring Blade",
                ["weapon.name.6"] = "Chain Lightning",
                ["weapon.name.7"] = "Moon Blade",
                ["weapon.name.8"] = "Swarm Missiles",
                ["weapon.name.9"] = "Holy Field",
                ["weapon.name.10"] = "Pulse Mine",
                ["weapon.desc.projectile"] = "Auto-targets the nearest enemy",
                ["weapon.desc.scatter"] = "Fires several projectiles at once",
                ["weapon.desc.nova"] = "Periodic burst centered on you",
                ["weapon.desc.orbit"] = "Melee blades orbiting you",
                ["weapon.desc.chain"] = "Lightning that jumps between enemies",
                ["weapon.desc.boomerang"] = "A blade that flies out and returns",
                ["weapon.desc.homing"] = "Missiles that track enemies",
                ["weapon.desc.aura"] = "A field damaging nearby enemies",
                ["weapon.desc.mine"] = "Area denial: planted, then auto-detonates",
                ["weapon.next.projectile"] = "DMG +25%",
                ["weapon.next.projectile.3"] = "DMG +25%, projectile +1",
                ["weapon.next.projectile.4"] = "DMG +25%, projectile +1, pierce +1",
                ["weapon.next.scatter"] = "Projectile +1, DMG +10%",
                ["weapon.next.nova"] = "Area +15%, DMG +20%",
                ["weapon.next.orbit"] = "Blade +1, DMG +10%",
                ["weapon.next.chain"] = "Chain target +1, DMG +18%",
                ["weapon.next.boomerang"] = "DMG +22%",
                ["weapon.next.boomerang.3"] = "Projectile +1, DMG +22%",
                ["weapon.next.homing"] = "Projectile +1, DMG +20%",
                ["weapon.next.aura"] = "Area +12%, DMG +15%",
                ["weapon.next.mine"] = "DMG +20%",
                ["weapon.next.mine.3"] = "Mine +1, DMG +20%",
                ["upgrade.stat.0"] = "Max HP +20",
                ["upgrade.stat.desc.0"] = "and fully healed",
                ["upgrade.stat.1"] = "Move Speed +8%",
                ["upgrade.stat.2"] = "Damage +15%",
                ["upgrade.stat.3"] = "Cooldown -12%",
                ["upgrade.stat.4"] = "Projectile +1",
                ["upgrade.stat.desc.4"] = "Direct / scatter weapons",
                ["upgrade.stat.5"] = "Pickup Range +30%",
                ["upgrade.stat.6"] = "Area +15%",
                ["playtime.warn"] = "You are close to today's play time limit. Please take a break.",
                ["playtime.health_title"] = "Healthy Gaming Reminder",
                ["playtime.health"] = "You have played for 45 minutes. Gaming in moderation benefits the mind; addiction harms the body.",
                ["playtime.force_exit"] = "Today's play time limit reached (Minor Mode). Please come back tomorrow.",
                ["cloud.reason.no_provider"] = "Provider missing",
                ["cloud.reason.not_configured"] = "Backend not configured (or not HTTPS)",
                ["cloud.reason.no_settings"] = "Settings manager missing",
                ["cloud.reason.user_off"] = "Disabled by user",
                ["cloud.reason.not_agreed"] = "Latest privacy policy not accepted",
                ["cloud.err.not_configured"] = "Not configured",
            };
        }

        /// <summary>取文案；缺失键返回键名本身（开发期可发现漏翻）。</summary>
        public static string T(string key)
        {
            EnsureReady();
            var dict = Language == LangEn ? _en : _zh;
            if (dict.TryGetValue(key, out var v)) return v;
            return key;
        }

        /// <summary>带一个占位符的文案：T("hud.wave", 5) → "波次 5"。</summary>
        public static string T(string key, object arg0)
        {
            return string.Format(T(key), arg0);
        }

        /// <summary>带两个占位符的文案。</summary>
        public static string T(string key, object arg0, object arg1)
        {
            return string.Format(T(key), arg0, arg1);
        }

        /// <summary>带三个占位符的文案（M17 实测修复：char.stats 等键需要，此前缺重载导致编译失败）。</summary>
        public static string T(string key, object arg0, object arg1, object arg2)
        {
            return string.Format(T(key), arg0, arg1, arg2);
        }

        /// <summary>带四个占位符的文案（M17 实测修复：gameover.stats 需要 {0}~{3}）。</summary>
        public static string T(string key, object arg0, object arg1, object arg2, object arg3)
        {
            return string.Format(T(key), arg0, arg1, arg2, arg3);
        }

        /// <summary>语言显示名（设置面板循环按钮用）。</summary>
        public static string LanguageLabel(string lang)
        {
            return lang == LangEn ? T("common.english") : T("common.chinese");
        }
    }
}
