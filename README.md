# 《星界像素狂潮》— Unity 工程原型（M1 – M28）

> **仓库导航**（本文件与 `StarPixelRogue/README.md` 内容一致；置于仓库根便于 GitHub 首页直接展示）
>
> | 路径 | 内容 |
> | --- | --- |
> | `StarPixelRogue/` | Unity 2022.3 工程本体——完整源码 / 资源 / 配置 / 技术文档 |
> | `net_smoke.py` | M18 联机双开实测脚本：普通联机 / `--spectator` 观战(M14) / `--drop-at`·`--host-drop-at` 断线重连(M11) |
> | `net_smoke_m11_m14.md` | M11 断线重连 / M14 观战的无头实测报告（含判据设计与缺陷修复） |
> | `cloudsave_probe.py` | M13 云存档无头实测脚本：5 个开关场景（双向断言「未启用即零网络请求」）+ UI 探针 `--ui`（设置面板 / 联机大厅 / 升级选卡） |
> | `cloudsave_m13.md` | M13 云存档无头实测报告（请求计数器 / 判据区分力 / 实测矩阵 / 设置页 UI） |
> | `playtime_m19.md` | M19 防沉迷无头实测报告（三处"限时形同虚设"的绕过缺陷 / 时间注入 / 区分力验证） |
> | `playtime_probe.py` | M19 防沉迷无头实测脚本：`MINOR`（限时 / 强制退出 / 二次开局拦截 / 跨天重置）与 `ADULT`（健康提示 / 计时不被弹窗冻结 / 时长持久化），虚拟时长由 60s 步长推进 |
> | `persistence_probe.py` | M20 持久化层无头实测脚本：`SAVE`（战绩 / 图鉴去重 / 真实读档往返 / 损坏回退）与 `TELEMETRY`（JSONL 合法性与特殊字符精确往返 / 落盘失败回队列重试 / 关开关即丢弃队列） |
> | `persistence_m20.md` | M20 持久化层无头实测报告（三处"静默作废"的数据缺陷 / 判据区分力 / 审计工具误报修复） |
> | `compliance_probe.py` | M21 合规门无头实测脚本：6 场景（首启弹门 / 已同意放行 / `BUMP` 协议升级必须重新征得同意 / `DECLINE` 不同意即退出 / `CONSENT_CLOUD` 同意前零请求且同意后补拉取 / `L10N` 英文界面无中文残留） |
> | `compliance_m21.md` | M21 合规门无头实测报告（同意后云存档"只推不拉"导致本地档覆盖云端 / 判据区分力 / 假通过与 Awake 时序踩坑） |
> | `settings_probe.py` | M22 设置面板无头实测脚本：3 场景（`TOGGLES` 逐个点击 10 次同时断言生效/文字/落盘 / `ROUNDTRIP` 11 字段写入-读回并查引擎侧 / `NEGATIVE` 设置文件损坏必须回退默认值且归档） |
> | `settings_m22.md` | M22 设置面板无头实测报告（损坏设置被静默清零 / 切语言被踢出面板 / 语言文字不跟随 / `var` 快照假通过踩坑） |
> | `build_probe.py` | M23 选角/成长链路无头实测脚本：3 场景（`SELECT` 一次点击的四处一致性 / `APPLY` 跨进程读档后 7 乘区+起始武器+幂等+换角色不叠乘 / `PROGRESSION` 10 武器开火 + 4 组合技 + 7 属性 + 等级封顶） |
> | `build_m23.md` | M23 选角/成长链路无头实测报告（换角色数值叠乘 / 联机冒烟跑错产物 / `GetInstanceID` 定位陷阱 / 探针站位陷阱） |
> | `pickup_probe.py` | M24 掉落/磁吸/结算与重开局清理无头实测脚本：5 场景（`MAGNET` 半径两态对比 / `CREDIT` 经验倍率与回血封顶 / `LEFTOVER` 重开局实体残留 / `RESTART` 重开后能否拿到新玩家 / `REPEAT` 反复重开是否累积） |
> | `pickup_m24.md` | M24 掉落/重开局链路无头实测报告（重开一局拿不到新玩家 / 上一局实体整批残留 / 「写了却零 caller」的清理方法 / `Destroy` 延迟导致的计数陷阱） |
> | `feel_probe.py` | M25 手感层无头实测脚本：6 场景（`STAGE` 相机基准不被自己拉塌 / `ZOOM` 推近生效且回弹、中途 Configure 不改小基准 / `HITSTOP` 顿帧真的压时间且真实时长≈请求时长 / `PAUSE` 选卡暂停四条保证 / `SHAKE` 震屏在暂停态也衰减完 / `GATE` 关掉开关后三条反馈通道全关） |
> | `feel_m25.md` | M25 手感层无头实测报告（相机基准被自己拉塌、整局镜头无限推近 / 推近中途 Configure 叠乘 / 震屏误用缩放时间导致暂停态永不衰减 / 推近不看反馈开关） |
> | `visual_probe.py` | M26 可见层第二批无头实测脚本：5 场景（`FONT` 运行时字体非空且两处一致 / `DMG` 飘字生成·文案·上浮·淡出·回收 / `PUNCH` 受击冲击放大·回落·不叠乘 / `TELEGRAPH` 前摇警示圈画得出·会脉动·按时自毁 / `LEFTOVER` 重开局不得残留上一局特效） |
> | `visual_m26.md` | M26 可见层第二批无头实测报告（前 4 个模块本来就对、零缺陷 / 上一局的前摇警示圈与爆发环漏进新世界 / 四次注入的区分力验证） |
> | `pool_probe.py` | M27 伤害飘字对象池无头实测脚本：4 场景（`POOL` 累计新建有上限且复用过半 / `REUSE` 复用实例必须重置寿命与透明度 / `BURST` 单帧爆发一张不丢 / `LEFTOVER` 重开局不得残留空壳管理器与上一局飘字） |
> | `pool_m27.md` | M27 报告（飘字池化 / `Track(组件)` 导致每次重开遗留一整套空壳管理器 / 我自己引入的 `ClearInstance` 回归被 M24 判据当场抓住） |
> | `orphan_probe.py` | M28 新建对象归属无头实测脚本：3 场景（`CHAIN` 链闪电电弧/闪光不得残留 / `BOSS` 上一局活着的 Boss 不得被带进新世界 / `ROOT` 通用兜底：重开后场景根对象不得多出任何一个） |
> | `audit_project.py --selftest` | 归属检查自检：10 个最小夹具反向验证「该红的必须红、不该红的必须不红」 |
> | `orphan_m28.md` | M28 报告（把连续三轮靠人肉复查才找到的泄漏做成静态检查第 7 项 / 链闪电与 M26 同形未修 / 上一局的 Boss 被带进新世界 / 注解是封闭词汇且逐项验证） |
> | `audit_project.py` | 工程静态审计（退出码 0 = 无问题） |
> | `balance_sim.py` + `balance_*.md` / `balance_*.txt` | 数值平衡模拟器与实验报告 |
> | `gen_assets.py` | 像素精灵 / 音效资源生成脚本 |
>
> 正文中的相对路径（如 `Assets/...`、`Docs/...`、`../balance_sim.py`）均相对于 `StarPixelRogue/` 工程目录。

# 《星界像素狂潮》— Unity 工程原型（M1 – M28）

> 像素肉鸽割草（吸血鬼幸存者类）· 单机即开即玩，联机（Fish-Net 状态同步）为可选模块。
> 本目录为可运行原型源码，对应《联机肉鸽技术设计文档》的 M1–M4 里程碑。
> M3 起自带真实像素精灵与音效（纯 Resources 加载，缺失自动回退占位）。
> **M3.6 起全部战斗数值经过离线蒙特卡洛模拟验证**（见 `../balance_sim.py`）。
> **M4 起支持 2–4 人联机合作**：状态同步 + 半权威房主（ADR-001），未装 Fish-Net 时自动退回纯单机。

## 如何运行

### 单机（零配置）

1. 用 **Unity Hub** 打开本目录 `StarPixelRogue` 作为项目（建议 Unity 2022 LTS）。
2. 首次打开 Unity 会自动生成 `ProjectSettings / Packages / Library` 等工程文件。
3. 直接按 **Play**（无需任何美术资源 / 预制体 / 手动搭场景）：`GameBuilder` 会在进入运行时自动构建整个游戏，并自动创建 EventSystem、HUD 与全部管理器。
4. **M5 启动流**：首次启动先弹「健康游戏忠告 + 用户协议/隐私政策/儿童个人信息保护规则」合规门（同意后不再弹，协议版本更新后重新弹），随后进入**主菜单**（开始游戏 / 设置 / 退出），主菜单展示本地战绩。设置面板含音量/画质/全屏/震屏/匿名统计/青少年模式/中英语言切换/导出诊断日志。

### 联机（可选，三步开启）

1. **安装 Fish-Net**：Unity 打开 `Window > Package Manager > + > Add package from git URL`，填入
   `https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet`
   （本仓库 `Packages/manifest.json` 已声明该依赖；若安装失败可删除该行，工程自动退回纯单机）。
   已验证目标版本：Fish-Net V4.x（`com.firstgeargames.fishnet`，要求 Unity 2021.3+）。
2. **定义脚本符号**：`Project Settings > Player > Other Settings > Scripting Define Symbols` 增加 `FISHNET_ENABLED`。
   所有联机代码都在 `#if FISHNET_ENABLED` 内——**不装插件、不加符号时，联机代码整段不编译，单机行为零影响**。
3. **一键装配**：菜单 `StarPixel > Setup FishNet 联机装配`，自动生成
   `Assets/Net/Prefabs/`（7 个网络预制体）、`Assets/Net/DefaultPrefabObjects.asset`（可生成预制体表）
   与 `Assets/Scenes/NetBootstrap.unity`（NetworkManager + Tugboat + PlayerSpawner + NetConfig），
   并自动注册 SpawnablePrefabs、指定玩家预制体、配置各预制体 NetworkTransform 权威模式。
   若个别 Fish-Net 版本成员名不同导致自动注册失败，Console 会给出警告，此时按检视面板手动拖拽即可（预制体都已生成）。
4. **开玩**：打开 `NetBootstrap` 场景按 Play → 大厅选 **创建房间（房主）**；另一位玩家（可同机双开/另一台电脑）选 **加入房间** 并填入房主 IP（同局域网为房主内网 IP，同机测试用 `127.0.0.1`）。

## 操作方式

- **移动**：WASD 或 方向键（联机下为客户端权威 + 本地预测，手感无延迟）
- **攻击**：全自动——武器按各自冷却自动锁定最近敌人开火（无需按键）
- **升级**：经验满后弹出 3 选 1 强化卡，点击选择后继续（联机下由房主生成候选、客户端回选）
- **目标**：尽可能久地存活、清空一波又一波敌人、冲击更高等级与组合技

HUD 显示：等级（Lv）、HP 条、经验条（XP）、波次（Wave）、击杀（Kills）、存活时间（Time）；进入 Boss 波时顶部出现 **Boss 血条**，每 5 波切换 **区域** 时中央短暂提示 `ZONE N` 并改变背景色。玩家阵亡即 Game Over。联机时顶部另有模式标识（单机/房主/客户端）。
## 资源（自动加载，无需手动导入）

- `Assets/Resources/Sprites/`：`player / enemy_chaser / enemy_runner / enemy_tank / enemy_elite / boss / gem / health / projectile`（32×32 或 48×48 像素精灵）。
- `Assets/Resources/Audio/`：`shoot / hit / pickup / levelup / boss / hurt / gameover / combo`（WAV 音效）与 `bgm_menu / bgm_battle / bgm_boss`（循环 BGM，22050Hz 单声道）。
- 由 `gen_assets.py`（工作区根目录）以**纯 Python 标准库**生成；可随时重跑覆盖，替换为自己的美术/音效即可。

## 已实现

### M1 核心循环
| 系统 | 实现 | 对应 TDD |
| --- | --- | --- |
| 底层框架 | MonoSingleton / EventBus / PoolManager / SpriteFactory | 3.2 / 4.2 / 7 |
| 玩家 | WASD 移动、自动索敌开火、受伤/死亡 | M1 |
| 战斗 | 统一伤害管线 `CombatManager.CalcDamage`、可受伤实体 `Damageable` | 4.1 |
| 技能（数据驱动） | `SkillDef`(ScriptableObject) + 弹道 `Projectile` | 4.1 |
| 敌人 | 对象池复用、追击 AI、接触伤害 | 4.2 |
| 波次 | 波次递增难度（数量 + 血量 + 速度） | 4.2 |
| HUD | 运行时自动创建 HP/波次/击杀/计时 | M1 |

### M2 升级与差异化（本次新增）
| 系统 | 实现 | 对应 TDD |
| --- | --- | --- |
| 经验/升级 | 击杀掉宝石 → 磁吸拾取 → 升级曲线（指数增长） | M2 |
| 升级选卡 | `LevelUpManager` 暂停 3 选 1：武器解锁 / 属性强化 | M2 |
| 多武器 | 数据驱动 `WeaponSlot`，5 种武器：星能弹 / 散射枪 / 寒霜新星 / 裂地波 / 雷环刃 | 4.1 |
| 组合技 | `ComboManager` 基于 `SkillDef.tags`：火+风=伤害×1.5、冰+土=命中减速、雷+火=穿透+2 | 4.1 |
| 敌人多样化 | 追击 / 疾行 / 重甲 三类，按波次权重出现 | M2 |
| 打击感 | 跳字伤害 `DamageNumberManager`、受击闪白、命中减速表现 | M2 |

### M3 完成度与 Boss 战（本次新增）
| 系统 | 实现 | 对应 TDD |
| --- | --- | --- |
| 真实像素美术 | `SpriteFactory` 从 Resources 加载像素精灵（PPU=32），缺失回退纯色方块 | M3 |
| Boss 战 | 每 5 波 Boss：径向弹幕、召唤小怪、血量过半狂暴、专属血条 | M3 |
| 敌方弹幕 | `EnemyProjectile` 对象池、命中玩家扣血 | 4.2 |
| 分区主题 | 每 5 波切换区域：背景色 + `ZONE N` 提示 | M3 |
| 音效反馈 | `SfxManager`：射击/命中/拾取/升级/受伤/Boss/结束 | M3 |

### M3.5 手感打磨（本次新增）
| 系统 | 实现 | 对应 TDD |
| --- | --- | --- |
| 屏幕震动 | `CameraShake` 挂在主相机，LateUpdate 在跟随位置上叠加随机偏移；受击/Boss 出场/弹幕释放/精英死亡均触发 | M3.5 |
| 精英怪 | 第 3 波起按概率出现（上限约 26%）：金色装甲专属外观、约 3 倍血量、5 倍分数、常驻呼吸脉冲、死亡掉 3 宝石 + 必掉回血球 | 4.2 |
| 回血掉落 | `HealthPickup` 红色心形：普通怪 5% 概率 / 精英必掉 / Boss 大奖励（40 HP），磁吸拾取回复生命 | 4.2 |
| Boss 前摇预警 | 弹幕与召唤前 0.55s 显示急促闪烁的脉冲警示圈，给玩家反应窗口 | 4.4 |

> 震屏统一走 `EventBus.OnShakeRequest`（强度 × 时长取更强者，防止弱震覆盖强震）；所有新表现都由事件驱动，与系统解耦。

### M3.6 数值平衡与 build 深化（本次新增）
| 项目 | 内容 |
| --- | --- |
| 离线平衡模拟器 | `../balance_sim.py`：把刷怪/索敌/弹道/接触伤害/升级抽卡/组合技/回血球/Boss 全部镜像为可执行模型，60 局蒙特卡洛输出存活波次分布与失衡告警 |
| 武器等级系统 | 每把武器 1–4 级（`SkillDef.EffectiveDamage/Count/Pierce/AoeRadius`），升级池从 11 项扩展到 23 项，消除"build 提前建完"的成长断崖 |
| 武器等级成长 | 直射：伤害 +25%/级，Lv3/Lv4 各 +1 弹体，Lv4 +1 穿透；散射：弹体 +1/级；爆发：范围 +15%/级、伤害 +20%/级；环绕：刃 +1/级 |
| HUD build 反馈 | 左上角实时显示「武器名 Lv」列表（`OnWeaponsChanged` 事件驱动） |
| 数值重调 | 见下方「已验证数值」 |

**已验证数值（模拟结论：中位存活约 15 分钟 / 第 14 波，死亡波次 5–17 波健康扩散）**

| 参数 | 原值 | 新值 | 原因 |
| --- | --- | --- | --- |
| 玩家生命 / 移速 | 100 / 5.0 | 110 / 5.6 | 提高生存与走位空间 |
| 经验曲线 | ×1.35 +3 | ×1.25 +2 | 原曲线 Lv15 需 2466 XP（≈第28波），玩家根本达不到 |
| 难度递增 | +15%/波 | +10%/波 | 原曲线第 30 波敌人血量 5.35 倍，玩家跟不上 |
| 星能弹 | 12 伤 / 0.4s | 18 伤 / 0.35s | 原 30 DPS 每秒仅杀 1 怪，人口堆积导致被围死 |
| 散射枪 | 8 伤 / 0.6s | 10 伤 / 0.55s | 同上 |
| 寒霜新星 / 裂地波 | 18 / 2.2s，26 / 3.0s | 22 / 1.8s，32 / 2.6s | AoE 武器需撑起清场职责 |
| 雷环刃 | 14 伤 / 0.25s | 12 伤 / 0.30s | 原 56 DPS 一家独大，需与其他武器平衡 |
| 疾行怪速度 | 4.0 + 0.15/波 | 3.8 + 0.08/波 | 原第 7 波即超过玩家移速，风筝策略过早失效 |
| 接触伤害 | 全员 8 | 追击 6 / 疾行 9 / 重甲 11，精英 ×1.6 | 按威胁分级，而非一刀切 |
| 刷怪间隔 | 固定 0.15s | 0.15s → 0.07s 随波次加快 | 制造人口压力，形成"被围住"的真实威胁 |
| Boss 血量 | 550 + 130/波 | 400 + 90/波 | 原 Wave10 Boss 1860 血过于肉 |

> 修 Bug：旧「弹体 +1」强化只改 `def.projectileCount`，而直射武器开火写死 `count = 1`，导致**星能弹永远吃不到该强化**。现改为独立的 `stats.projectileBonus`，直射/散射均生效。

### M4 联机合作（本次新增，ADR-001：状态同步 + 半权威房主）

| 项目 | 实现 |
| --- | --- |
| 编译隔离 | 全部联机代码位于 `Scripts/Net/` 与 `#if FISHNET_ENABLED` 内；未装 Fish-Net 时整段不编译，单机行为零变化 |
| 大厅 | `NetLobby`：单人 / 创建房间 / 加入房间（IP 输入）；断线自动回大厅 |
| 权威模型 | 房主持有权威模拟：敌人/波次/Boss/掉落/伤害/经验/升级全部在房主结算；客户端只发输入与请求 |
| 移动 | 客户端权威 + 本地预测（NetworkTransform 同步），手感无延迟 |
| 开火 | 客户端发 `RequestFire` ServerRpc → 房主做权威冷却校验并生成网络弹丸（`NetProjectile`） |
| 敌人同步 | `NetEnemy`：位置走 NetworkTransform，kind/精英/Boss/血量走 SyncVar；AI 与掉落只在房主运行 |
| 掉落与拾取 | 网络宝石/回血球（`NetPickup`）：磁吸与拾取判定全部在房主权威模拟（避免客户端本地位移与 NetworkTransform 拉扯），反生成广播即时反馈 |
| 升级选卡 | 房主检测远程玩家升级 → TargetRpc 推 3 张卡（含 kind 编码）→ 客户端选择序号回传 ServerRpc → 房主应用 + 客户端本地镜像 |
| 武器库存 | `NetPlayer` SyncList 同步武器 id/等级（房主权威）：客户端据此生成本地武器（HUD/开火请求/属性预测） |
| 世界状态 | `NetWorldState` 同步波次/击杀/区域（SyncVar 回调转 EventBus，HUD 两种模式复用同一套代码） |
| 联机修复 | `ComboManager` 加成计算改为纯函数 `Compute(tags)`——原全局单例状态在房主持有多个玩家时会互相覆盖 |

**已知限制（M4 范围内接受，后续里程碑处理）**：环绕刃（雷环刃）的刃体视觉在联机下不同步（伤害正常）；Nova 爆发环特效仅权威侧可见；客户端存活时间为本地时钟；房主暂停不再适用（联机不冻结时间缩放，避免卡住网络心跳）。

### M4.5 联机实机验证与静态修复（本次新增）

本地无 Unity 环境，无法真机编译，因此本轮按「首编译成功率最高」标准做了一次**全链路静态验证**：
通读 43 个 .cs + 对齐 Fish-Net V4.7.4 上游源码（NetworkObject/ServerManager/SyncVar/NetworkTransform/
PlayerSpawner/Transport 的逐个签名核实），共发现并修复 **14 处问题**，其中 5 处为不修复则联机完全不可用：

| # | 问题 | 严重度 | 修复 |
| - | --- | --- | --- |
| 1 | `ComboManager.Combos` 用 `Action<Bonus>` 却按 `ref` 调用（编译错误），且 lambda 值捕获使组合技加成静默失效 | P0 编译 | 改为匿名方法 `delegate (ref Bonus b)` |
| 2 | `NetBackend` 用 `InstanceFinder` 但缺 `using FishNet;`（编译错误） | P0 编译 | 补 using（#if 内） |
| 3 | `NetSpawner` 调用 V4 已不存在的 `Spawn(position, NetworkManager)` 重载 | P0 编译 | 改 `ServerManager.Spawn(go, owner)` |
| 4 | `ClientManager.OnClientConnectionState` 订阅签名与 V4 的 `Action<ClientConnectionStateArgs>` 不符 | P0 编译 | 改签名 + `args.ConnectionState` |
| 5 | `manifest.json` Fish-Net git 路径 `/FishNet` 错误（包根本解析不了） | P0 工程 | 改为 `?path=Assets/FishNet` |
| 6 | **玩家对象没有任何代码生成**（联机进了房间但场上没有角色） | P0 功能 | 装配脚本添加 Fish-Net `PlayerSpawner` 组件并指定玩家预制体（V4 标准玩家自动生成方案） |
| 7 | **客户端武器库存不同步**：升级全在房主应用，客户端永远只有默认武器 → 不开火、HUD 无 build、属性强化（移速等）不生效 | P0 功能 | `NetPlayer` 增加武器 SyncList（房主权威推送）+ 客户端按 kind 编码本地镜像应用 |
| 8 | 客户端 `LevelUpManager` 同时走「本地建卡」与「房主推卡」两条路径，会弹双面板且在未同步状态上 apply | P0 功能 | 新增 `SetExternalCards` 外部供卡模式，纯客户端只收房主推卡 |
| 9 | `NetPickup` 客户端本地磁吸与 NetworkTransform 服务端权威互相拉扯（抖动）+ 多余 ServerRpc 往返 | P1 | 磁吸/拾取全部改房主权威模拟 |
| 10 | 房主同时收到 SyncVar 的 asServer/asClient 两次回调 → 击杀双计、跳字双份、波次提示双触发 | P1 | 回调统一加 `asServer \|\| IsServerInitialized` 守卫（Boss 血条例外：房主侧唯一数据源） |
| 11 | SyncVar 初始值不触发客户端 OnChange + `ServerInit` 在 `Spawn()` 之后调用 → 客户端初始精灵/类型错误、Boss 血条不显示 | P1 | `NetSpawner` 增加 beforeSpawn 回调（先注入权威值再网络生成）+ OnStartClient 显式应用 |
| 12 | 联机 Boss 波不停常规刷怪（`_bossActive` 未置位）、Boss 血量写死 6600 不随波次成长 | P1 | 置位 `_bossActive`；血量与单机一致 `400+90×wave` |
| 13 | `FishNetBackend.Stop()` 退订后不清引用 → 二次建房收不到 Ready；断线后选「单人游戏」不重建世界 | P1 | Stop 后 `_nm=null`；新增 `GameBuilder.TeardownWorld()` 断线拆世界 |
| 14 | `LevelUpManager` 选卡面板字体写死 `Arial.ttf`（Unity 2022 内置字体已改名，面板文字不显示） | P2 | 与 HUD 一致用 LegacyRuntime/Arial 双候选 |

另新增两个实机调优组件：**`NetStatus`**（右上角 模式/FPS/敌人数/玩家数 诊断 HUD）与
**`NetWatchdog`**（进入联机 6 秒内本地玩家未生成时，Console 输出带 4 步排查清单的明确错误）。

**首编译排错（若 Unity 报错，按此顺序查）**

1. 报错集中在 `Scripts/Net/`：确认已定义 `FISHNET_ENABLED` 且 Package Manager 里 Fish-Net 解析成功（manifest 的 git URL 需要 Git ≥ 2.14）。
2. 报 `Spawn` 相关签名错误：Fish-Net 又改了 API——只需改 `Net/NetBackend.cs` 的 `NetSpawner`（唯一收口）。
3. 报 `OnClientConnectionState` 签名错误：同样只改 `Net/FishNetBackend.cs`（唯一收口）。
4. Console 出现「未生成本地玩家」：按 NetWatchdog 的 4 步清单检查 NetBootstrap 场景与 PlayerSpawner 配置。
5. 联机代码报错但想先跑单机：临时删掉 `FISHNET_ENABLED` 符号即可，单机路径零依赖。

**双端测试清单（Unity 内验证联机，建议 30 分钟）**

1. 装配后打开 `NetBootstrap`，Play → 创建房间；应看到大厅、场景中有自己的角色、右上角 NetStatus 显示「房主」。
2. 同机再开一个 Unity 或 Build 一份，加入 `127.0.0.1`；双方应看到彼此角色移动（客户端权威移动，无拉扯感）。
3. 客户端攻击：敌人掉血/死亡/掉宝石，客户端 HUD 击杀数与波次与房主一致（约 1 个网络 tick 内）。
4. 客户端升级：弹出 3 张卡（由房主生成），选择后房主侧玩家变强；客户端左上角武器列表同步更新。
5. 到第 5 波：Boss 出场，双方出现 Boss 血条；Boss 弹幕/召唤在双方可见；Boss 死亡后掉落正常拾取。
6. 房主 Stop（或客户端强退）：另一端自动回大厅且显示「已断开连接」，之后可正常选「单人游戏」重开。
7. 性能：同屏 60+ 敌人时观察 NetStatus 的 FPS；客户端掉帧严重时优先降低 `NetProtocol.InterestRadius` 之外的实体数或调 Fish-Net `TimeManager.TickRate`。

### M5 商业化与上线准备（本次新增）

M5 把工程从「可玩原型」推进到「可发行形态」：**国内合规必备三件套 + 主菜单/设置/存档/防沉迷 + 构建自动化 + 上线材料文档**。启动流变为：

```
Play → 合规门（首次/协议更新）→ 主菜单 → 开始游戏 → HUD → 死亡 → 结算面板（保存纪录）→ 重开 / 回菜单
```

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 首启合规门 | `Scripts/Compliance/ComplianceGate.cs` | 健康游戏忠告（国标三行）+ 三份协议；不同意则退出；协议版本变更后自动重新弹出 |
| 法律文本 | `Resources/Legal/{terms,privacy,children}.txt` | 游戏内展示源（离线可读）；`Docs/` 下有同内容格式化版（同步维护） |
| 文本浏览器 | `Scripts/Compliance/LegalViewer.cs` | 可滚动长文本模态（滚轮/拖拽） |
| 主菜单 | `Scripts/UI/MainMenu.cs` | 开始游戏/设置/退出 + 战绩面板；语言切换整体重建 |
| 设置 | `Scripts/UI/SettingsPanel.cs` | 音量×3/画质/全屏/震屏/匿名统计/青少年模式/中英语言/导出日志，全部实时生效 |
| 设置存储 | `Scripts/Core/SettingsManager.cs` | JSON 原子写（tmp+replace），非法值 clamp，加载即应用 |
| 本地化 | `Scripts/Core/Localization.cs` | 中/英约 60 键，运行时切换（HUD/菜单/设置/结算全覆盖） |
| 存档 | `Scripts/Core/SaveManager.cs` | 最佳波次/时长/等级、总局数、上次战绩；仅本机、无个人信息 |
| 时长守护 | `Scripts/Core/PlaytimeGuard.cs` | 青少年模式每日限时（框架 60 分钟，到点强制回菜单）+ 45 分钟健康提示；`VerifyMinor()` 为官方实名认证接入缝 |
| 埋点骨架 | `Scripts/Core/Telemetry.cs` | 12 类事件、本地 JSONL、可关；网络上报 TODO（隐私政策同意前禁止） |
| 版本信息 | `Scripts/Core/VersionInfo.cs` | 构建号/时间/Unity 版本，来自 `StreamingAssets/BuildInfo.json` |
| 构建脚本 | `Assets/Editor/BuildScript.cs` | 版本号单一数据源 `Assets/_Project/version.json`；PlayerSettings 注入；自动生成并登记空 Bootstrap 场景；Mono/IL2CPP 两种构建 |
| 结算面板 | `Scripts/UI/GameOverPanel.cs` | 成绩展示、新纪录标记、保存、重开/回菜单；联机提供「离开对局」 |
| 拆世界 | `GameBuilder.TeardownWorld` | 全量拆除（玩家/模板/画布/单例引用同步清理），支持无限次重开 |

**构建出包**：菜单 `StarPixel > 构建 Windows (Mono 快速验证)` / `StarPixel > 构建 Windows (IL2CPP 正式包)`；
产物在 `Build/Windows/StarPixelRogue.exe`。版本号递增：`StarPixel > 递增版本号 (patch)`。

**合规注意（正式上线前必读）**：

1. 防沉迷必须接入**国家新闻出版署实名认证系统**并补齐「未成年人仅周五六日及节假日 20:00–21:00、每日 1 小时」的时段规则——当前 `PlaytimeGuard` 是只限总时长的本地框架。
2. 三份法律文本（`Docs/` 与 `Resources/Legal/` 双份）为模板，须经法务审核并替换占位邮箱/主体；改文本后必须递增 `VersionInfo.ComplianceVersion`。
3. 遥测网络上报为 TODO 接入点，开启前须更新隐私政策并取得用户单独同意。
4. 版号（出版审批）周期通常 4–8 个月，需经出版社报送；材料与流程见 `Docs/M5-商业化与上线清单.md`。

**已知限制（M5）**：联机大厅（NetLobby）文案暂未本地化（仅中文）；音乐音量滑条已持久化但暂无 BGM 系统（M6）；青少年模式时段规则未实现（见上）。

### M6 内容扩充（本次新增）

M6 把武器/角色/Boss/收集四个内容维度一次性拉开：**10 种武器、2 名角色、三模式 Boss、图鉴系统**（M9 在第 9 把基础上补第 10 把「脉冲地雷」，并为 Boss 追加第二套模式「风暴领主」；M12 再追加第三套「虚空编织者」，合计三种变体）。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 新武器族 ×4 | `Scripts/Combat/Skill/SkillDef.cs` + `Boomerang/HomingMissile/ChainBolt.cs` | 雷霆之链（跳电，每跳衰减 20%）、月刃（回旋镖，来回各伤一次）、蜂群导弹（追踪）、圣光领域（光环持续伤害）；全部接入等级公式与升级选卡 |
| 联机弹道行为 | `Scripts/Net/NetProjectile.cs` + `NetWorld.SpawnProjectileEx` | 回旋镖/追踪弹的飞行与命中同样只在房主运行（客户端同步位置），单机/联机表现一致 |
| 第二角色 | `Scripts/Player/CharacterDef.cs` + `CharacterManager.cs` + `UI/CharacterSelectPanel.cs` | 星尘游侠（均衡，经验 +15%）/ 重装守卫（生命 +35%、范围 +20%、伤害 +10%、移动 -10%，起始武器裂地波）；选择持久化，开局应用属性乘区与起始武器 |
| 联机角色同步 | `Scripts/Net/NetPlayer.cs` | 客户端本地预测应用 + ServerRpc 上报，房主权威应用（权威数值以房主为准） |
| Boss 三阶段 | `Scripts/Enemy/BossBrain.cs` | 血量 66%/33% 触发 P2/P3：移速/弹幕密度/召唤频率/接触伤害逐级提升，阶段转换有震屏+音效+警示圈演出；**单机与联机共用同一状态机**（`NetSession.IsAuthority` 守卫）。M9 追加变体 1「风暴领主」：旋转螺旋弹幕 + 周期性突进、不召唤小怪；M12 追加变体 2「虚空编织者」：瞄准式狙击散射 + 扩散环、不召唤不突进；与变体 0「守望者」（径向弹幕+召唤）三选一 |
| 联机 Boss 补齐 | `Scripts/Net/NetEnemy.cs` | 此前联机 Boss 只有简单弹幕/召唤；现由 BossBrain 驱动，弹幕/召唤经 `NetWorld` 网络生成（客户端可见），阶段经 SyncVar 广播染色 |
| 图鉴系统 | `Scripts/UI/CodexPanel.cs` + `Core/SaveManager.cs` | 武器/敌人/Boss/组合技/统计五个标签页；发现记录持久化（武器解锁、敌种遭遇、组合激活、Boss 击败），未发现显示问号 |
| 新组合技 | `Scripts/Combat/ComboManager.cs` | 圣（holy）+ 火 → 伤害 ×1.4「炽热圣印」；图鉴同步记录激活组合 |

**武器/角色平衡说明**：新武器数值与现有 5 种武器同曲线（DPS 目标区间一致，经 M3.6 蒙特卡洛框架外推验证）；
重装守卫以「移动速度换生存与范围」，与游侠形成明确取舍。真机验证时若某武器 DPS 偏离，只需调 `UpgradeCatalog` 中对应 `Create*()` 的数值（单一数据源）。

**已知限制（M6）**：联机下远程玩家的光环/环绕刃视觉仅各自客户端可见（伤害正常，属既有表现限制）；
Boss 前摇警示圈在纯客户端侧不可见（弹幕本身经网络同步可见）；图鉴的「敌人」页不区分具体波次变体。

### M7 打磨（本次新增）

M7 补上「氛围与反馈」最后一块：**BGM、组合技提示、低血量警示**。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 背景音乐 | `Scripts/Core/MusicManager.cs` | 三轨循环 BGM（菜单/战斗/Boss）双 AudioSource 交叉淡入淡出（1.2s）；Boss 出场自动切 Boss 乐、阵亡回战斗乐、结算/回菜单回菜单乐；音量接入设置「音乐音量」，实时生效；`DontDestroyOnLoad` 不纳入拆世界范围，断线重连不中断 |
| 程序化 BGM | `gen_assets.py`（`gen_bgm()`） | 纯 Python 标准库合成三段整数小节循环（菜单 A 小调琶音 84BPM / 战斗 D 小调 132BPM / Boss E 小调 152BPM，含鼓组），22050Hz 单声道 WAV 共约 2.1MB；换自有音乐只需把同名 `bgm_menu/bgm_battle/bgm_boss` 放进 `Resources/Audio`，**缺失时代码静默跳过** |
| 组合技提示 | `Core/EventBus.cs` + `Player/PlayerController.cs` + `UI/HUDController.cs` | 武器池满足两个流派 tag 的瞬间触发：金色高亮 `组合技激活！烈焰风暴 · 伤害 ×1.5`（2.5s 淡出）+ 上行琶音提示音；按本玩家武器池差分检测，每组合每局只提示一次，联机远程玩家不重复弹 |
| 低血量红晕 | `UI/HUDController.cs` + `GameBuilder.CreateRuntimeHUD` | HP &lt; 30% 时全屏红色脉冲警示（正弦呼吸），回升后淡出，阵亡/结算即消；位于 HUD 最底层不挡交互 |

**已知限制（M7）**：BGM 为程序化合成的占位音乐（纯音色，无真实乐器），正式版替换为成品音频即可（接口不变）；
联机下客户端的组合技提示按本地预测武器池触发，与房主权威结算若有毫秒级先后，仅影响提示时机、不影响数值。

### M8 联机表现补齐与平衡复验（本次新增）

M8 修掉 M6 遗留的两个联机表现缺口，并把 M6 新增内容全部纳入离线平衡模拟器复验。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 环绕刃/光环远程可见 | `Scripts/Player/PlayerController.cs` | 视觉生成去掉 `isLocal` 门控——联机下所有玩家的环绕刃/光环都渲染（纯表现，位置由 NetworkTransform 同步，转向用本地时钟近似）；**伤害仍仅权威侧结算**（`NetSession.IsAuthority` 守卫不变）。视觉对象改为 `OnDestroy` 显式清理，远程玩家反生成/拆世界重建不再残留漂浮刃片 |
| Boss 前摇警示圈广播 | `Scripts/Enemy/BossBrain.cs` + `Scripts/Net/NetEnemy.cs` | 大脑新增 `TelegraphRequested` 事件（权威侧照旧本地生成，单机行为零变化）；联机房主把事件转成 `_telegraphSeq/_telegraphKind` 两个 SyncVar 广播，客户端按 kind 还原三个前摇点（弹幕 1.3 红圈 / 召唤 2.4 黄圈 / 阶段转换 3.2 染色圈）——客户端现在也能读前摇走位 |
| 平衡模拟器扩展 | `../balance_sim.py` | 补齐 M6 全部内容：4 种新武器的真实机制（跳电逐跳 ×0.8 衰减 / 回旋镖来回各伤一次 / 追踪导弹 0.15s 重锁+限速转向 / 光环 0.5s 整圆）、9 武器解锁池、双角色乘区；组合规则按 C# 数组顺序**后写覆盖**（fire+storm+holy 同存最终 ×1.4）；输出分角色报告 + 武器采用率 + 角色对比 |
| 900s 通关条件（方案 A） | `UI/HUDController.cs` + `Core/EventBus.cs` + `UI/GameOverPanel.cs` + `Core/SaveManager.cs` | 存活满 `ClearTime=900s` 即通关：HUD 本地计时达标后走与死亡同一结算出口（`OnRunEnded` 增加 `victory` 参数），面板标题切「通关！/STAGE CLEAR!」、埋点 reason=victory、存档计 `totalClears`（图鉴统计页可见）。难度数值不动——模拟显示游侠在任何难度候选下 900s 内 0 死亡，与其调难度不如把 15 分钟定义为通关 |
| 分段难度曲线（方案 B） | `Enemy/EnemyManager.cs` `NextWave()` | Wave ≤30 维持 0.10/波，30 后提至 0.18/波：XP 约 Wave 62 停滞、build 停止成长，后段加压让难度重新追上（模拟：游侠 P90 波 61→59，中位 57→56） |
| 疾行怪速度封顶（方案 B） | `Enemy/EnemyManager.cs` `SpawnOne()` | Runner 速度 `Mathf.Min(3.8+0.08w, 5.2)`：原公式 Wave 23 起超玩家基础移速 5.6，无移速强化/减速联组时无法风筝（阶跃变平峰） |
| 守卫冷却 Buff（方案 B） | `Player/CharacterDef.cs` `CreateBulwark()` | `cooldownMult 0.92→0.88`：模拟中唯一把守卫 900s 幸存率 82%→100% 的杠杆（游侠同种子胜率仍 ~92%，角色差距主要来自 XP×1.15+零移速惩罚，未动） |
| 命中停顿 / 统一时间缩放 | `Core/HitstopManager.cs`（新）+ `UI/LevelUpManager.cs` + `UI/GameOverPanel.cs` | 新增 `HitstopManager` 作为 `Time.timeScale` **唯一写者**，消除选卡暂停与顿帧互相覆盖的隐患。玩家受击 / 精英死亡 / Boss 死亡 触发不同强度"顿帧"（unscaledDeltaTime 计时）；选卡与结算冻结统一改走 `SetPaused`。联机下不压时间（避免影响心跳），并复用「屏幕震动」开关作为整体打击反馈开关 |
| 升级三选一入场动画 | `UI/LevelUpManager.cs` `AnimateIn()` | 遮罩淡入 + 标题弹入 + 卡片错峰缩放弹入（easeOutQuad，unscaledDeltaTime 驱动，暂停态也能播）；防止面板在收尾前被销毁时访问已销毁对象做了空引用保护 |
| Boss 出场镜头推近 | `Core/CameraShake.cs` `ZoomPulse()` + `Enemy/Boss.cs` `Init()` | 正交相机尺寸脉冲：Boss 出生瞬间把尺寸压到基准 72% 再平滑回弹（约 0.55s），配合原有出场震屏强化"登场仪式感"；围绕 `Configure` 记录的基准尺寸回弹，联机无 CameraShake 时自动跳过 |

**平衡复验结论（60 局 × 2 角色蒙特卡洛 + 4 候选 × 40 种子对比实验，完整数据见 `../balance_report_m8.txt` / `../balance_experiments_plan_ab.txt`）**：
- 游侠在任何难度候选（含难度+60%/敌数+25%/刷怪 0.06s）下 900s 观测窗内 **0 死亡**——加敌数只拖慢推进不制造死亡，根因是 XP 指数需求导致约 Wave 62 后 build 停滞；
- 角色同种子胜率全候选稳定 88–92%，差距是结构性的（XP 与移速乘区），与难度无关；
- 据此落地方案 A（900s=通关）+ 方案 B（分段难度/疾行怪封顶/守卫 CD），新基线模拟：游侠中位 56 波 100% 通关、守卫中位 49 波 95% 通关。
- 手感演出（juice）：新增 `HitstopManager` 统一时间缩放（顿帧 + 选卡/结算冻结），升级三选一卡片入场弹入动画，Boss 出场镜头推近——均不碰平衡数值，纯提升打击感与仪式感。

**已知限制（M8）**：模拟器的 Boss 仍只镜像 P1 参数（弹幕 2.3s/召唤 9s/接触 12），三阶段递进未纳入——
M3.6 基线与 M8 复验同口径，横向可比，但 Boss 战后期压力会被低估；联机下远程玩家的刃/光环角度偏差已在 M10 修复（改用房主经 `NetWorldState` 广播的同步相位旋转，各客户端角度与房主一致）；
追击怪速度未封顶（Wave 57 起才超玩家移速，晚于 900s 窗口中位波次，暂观察）；联机通关按各客户端本地计时判定（与死亡同步不冲突，但理论上客户端间有几秒误差）。

### M9 内容扩展：第 10 把武器 + 第二种 Boss 变体（本次新增）

M9 把 M6 遗留的内容扩展做完整——新增第 10 把武器「脉冲地雷」，以及 Boss 的第二套战斗模式「风暴领主」。两者均先经 `balance_sim.py` 复验，确认不砸既有曲线。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 第 10 把武器：脉冲地雷 | `Combat/Skill/SkillDef.cs`（新枚举 `Mine` + 字段 `mineArmTime/mineTrigger/mineLife`）+ 新 `Combat/Skill/Mine.cs` + `Player/PlayerController.cs` `FireMine` + `Combat/UpgradeCatalog.cs` `CreateMine`（id=10）+ `GameBuilder.cs` 模板 | 区域封锁型武器：埋设于玩家当前位置，激活延迟后进入待引爆，敌人进入触发半径即爆，超时也自动爆；范围伤害（覆盖敌人+ Boss）。与既有「即时/持续」武器形成差异（主动布雷、延迟清场）。`earth` 标签与寒霜新星构成 `ice+earth` 减速组合。`Lv3/Lv4` 各 +1 枚、伤害 +20%/级、范围 +12%/级 |
| 第二种 Boss 变体：风暴领主 | `Enemy/BossBrain.cs`（新 `variant` 分支）+ `Enemy/Boss.cs` `Init(variant)` + `Enemy/EnemyManager.cs` `SpawnBoss` | 变体 0「守望者」（径向弹幕+召唤）沿用；变体 1「风暴领主」改为**旋转螺旋弹幕 + 周期性向玩家突进**、不召唤小怪，更强调纯走位/输出节奏。三阶段递进、接触伤害、前摇预警、染色全部沿用；`PhaseTint` 按变体走青蓝调以区分。单机 `SpawnBoss` 随机选取变体 |
| 模拟器同步 | `../balance_sim.py` | 武器表补 `id=10`（type=mine），`UNLOCKABLE_IDS` 含 10；`weapon_stats` / `weapon_dps` / 开火步进三处新增 `mine` 分支（区域封锁近似为玩家处周期 AoE）；Boss 变体为纯模式变化、不影响 DPS 生存曲线，故不单独建模 |

**复验结论（蒙特卡洛 + 模拟器，完整数据见 `../balance_report_m9.txt`）**：新增地雷纳入解锁池后约 99% 对局会采用，但作为低频高伤区域武器，模拟近似下对全角色 900s 通关率/中位波次**无显著影响**（不会取代既有的游侠 100% 通关格局）；Boss 变体只改战斗模式、不改玩家 DPS 与血量公式，生存曲线不受影响。

**已知限制（M9）**：脉冲地雷为单机完整特性——联机下房主权威侧布防/引爆正常，但远程玩家客户端看不到地雷视觉（与既有光环/环绕刃表现限制同源），且 `_mineTemplate` 仅在单机 `BuildWorld` 注入，联机远程玩家布雷请求会被 `_mineTemplate==null` 安全跳过（不崩溃、只是不出雷）；联机 Boss 走 `NetWorld` 简化镜像、无 `BossBrain` 模式，故**联机无 Boss 变体**（守望者/风暴领主/虚空编织者三种均仅单机生效）。

### M10 联机同步修正：刃/光环角度偏差（本次新增）

M10 修掉 M4/M6 遗留的一条「纯视觉」已知限制——联机下远程玩家的环绕刃（雷环刃）与光环（圣光领域）旋转角度与房主不一致。根因：刃/光环位置已由 `NetworkTransform` 同步，但旋转相位此前取各机器本地 `Time.time`，不同客户端时钟起点不同 → 同一玩家的刃在房主与各个客户端呈现不同角度。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 同步相位源 | `Net/NetWorldState.cs` | 新增 `SyncVar<float> _orbitPhase`，房主每帧写入 `Time.time` 并经网络广播（复用现有世界状态同步单例，零额外带宽成本）；客户端经 `OrbitPhase` 读取 |
| 时间源收口 | `Net/NetProtocol.cs`（`NetSession`） | 新增 `SyncedOrbitTime()`：联机且 `NetWorldState` 存在时返回同步相位，否则回退本机 `Time.time`（单机/断网零影响） |
| 旋转改用同步时间 | `Player/PlayerController.cs` | `UpdateOrbit` 与 `UpdateAura` 的旋转/呼吸相位由 `Time.time` 改为 `NetSession.SyncedOrbitTime()` |

**效果**：所有机器（房主 + 各客户端）共用房主同步相位旋转，远程玩家刃/光环角度与房主完全一致；单机与断网路径行为不变。伤害结算仍仅权威侧（未改动），纯表现修复。

**校验**：3 个改动 .cs 括号全平衡；`SyncedOrbitTime` / `OrbitPhase` 引用点（定义 + 两处调用）交叉核对通过；`NetSession` 不引入 Fish-Net 依赖（同步引用收口在 `#if FISHNET_ENABLED` 内）。无 Unity 编译器，仅静态校验——真机建议验：2 人以上联机时观察任一玩家切到远程视角，其环绕刃/光环旋转角度与房主一致。

### M11 断线重连（本次新增，M6 遗留清单收官）

M11 完成 M6 遗留清单的最后一项——联网掉线后不再直接回大厅，而是**自动重连回同一局**，并把断线前的玩家进度（等级/武器/经验/血量）还原，而不是从 1 级重新开始。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 连接生命周期 | `Net/FishNetBackend.cs` | 区分「主动离开」与「意外掉线」：主动离开走旧回大厅路径；意外掉线进入重连状态机（房主本地客户端掉线时**保留权威世界**，纯客户端掉线先拆本地镜像待重建） |
| 重连驱动器+遮罩 | `Net/NetReconnect.cs` | 常驻 MonoBehaviour：`「连接已断开，正在重连…(n/5)」` 遮罩 + 1.5s 延迟重试 + 「返回大厅」兜底；连接就绪（Ready）时客户端重建本地世界、房主无需重建；最多 5 次后放弃回大厅 |
| 重连令牌 | `Net/NetProtocol.cs`（`NetSession`） | 客户端每次开新局生成 `ReconnectToken` 并持久化于静态类，跨「掉线→重连」保持同一值——房主据此把进度匹配回同一玩家（不含任何个人信息） |
| 主机进度快照 | `Net/NetReconnectStore.cs` | 房主侧 `Dictionary<token, 快照>`：远程玩家掉线时（`NetPlayer.OnStopServer`）按令牌暂存进度，重连时取出；开新房 `ClearAll` 清空上一局残留 |
| 进度捕获/重放 | `Player/PlayerController.cs` | 新增 `PlayerReconnectState` 结构 + `CaptureProgression()`（房主权威侧捕获）/ `ReapplyProgression()`（重放武器/等级/经验曲线/血量并刷新组合技与事件）；`XpRatio` 供同步 |
| 血量比例写入 | `Combat/Damageable.cs` | 新增 `SetHpRatio()`：按比例直接设置血量并使死亡状态复位，供重连后继续作战 |
| 玩家对象钩子 | `Net/NetPlayer.cs` | 首连即上报 `RequestReconnectServerRpc(token, 角色id)`；`OnStopServer` 断连时暂存快照；重连时取出快照重放，并把房主侧升级发牌计数对齐到当前等级（避免重连瞬间一次弹出多张升级卡） |

**效果**：
- 房主本地客户端掉线：服务端仍在运行，仅重连本地客户端，**敌人/波次/Boss 进度完全保留**，本地玩家进度（含房主自身）也按令牌还原。
- 纯客户端掉线：显示遮罩并自动重试，重连成功后重建本地世界重新订阅房主同步；玩家回到断线前的等级/武器/血量，而非 1 级。

**校验**：8 个改动/新增 .cs 括号全平衡；`ReconnectToken` / `NetReconnectStore` / `PlayerReconnectState` / `CaptureProgression` / `ReapplyProgression` / `SetHpRatio` / `RequestReconnectServerRpc` / `OnStopServer` / `BeginReconnect` / `RetryLastConnection` 等全部引用点交叉核对通过。无 Unity 编译器，仅静态校验——真机建议验：① 联机对局中拔掉客户端网线/断开，确认出现重连遮罩并在数秒内恢复同一局（房主世界未重置）；② 断线前升到若干级、拿若干武器，重连后确认等级/武器/血量被正确还原；③ 连续多次断线仍能重连；④ 重连失败（如房主已关）时点击「返回大厅」可正常回大厅。

**已知限制（M11）**：重连基于「同一房主进程仍在」的前提——若房主本身退出/崩溃，则无法重连（需重建对局），符合半权威房主架构；令牌为区分同房间玩家的轻量标识，未做跨进程身份鉴权（原型阶段可接受）；重连瞬间的敌人接触伤害/弹幕以房主权威状态为准，客户端重连后由同步自然追平。

### M12 第三种 Boss 变体「虚空编织者」（本次新增）

M12 在 M9 的第二种 Boss 变体基础上，再补第三种战斗模式——让单机 Boss 战出现三种随机变体（三选一），提升重复可玩性。沿用在 M9 建立的 `BossBrain` 变体分支机制，纯增量、不碰武器/角色数值，因此**不影响已验证的 DPS 生存曲线**（详见 M9 复验结论）。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 变体路由 + 行为 | `Enemy/BossBrain.cs` | `Config.variant` 新增取值 2；`Update` 分出 `UpdateVoid` 分支；新增 `AimedVolley`（瞄准式锥形狙击散射，带 0.4s 前摇预警圈，速度 8.0 逼迫走位）与 `RingBurst`（周期性扩散环，区域封锁，频率低于守望者的径向爆发）；不召唤、不突进 |
| 变体染色 | `Enemy/BossBrain.cs`（`PhaseTint`） | 变体 2 走紫罗兰调（P1 浅紫 / P2 紫 / P3 品红），与守望者（红白）、风暴领主（青蓝）视觉明确区分；`Boss.cs` 每帧读取 `PhaseTint` 上色，无需额外接线 |
| 变体随机 | `Enemy/EnemyManager.cs`（`SpawnBoss`） | 单机路径 `Random.Range(0, 3)` 三选一（原 0/1 二选一） |

**三变体设计对比**：
- 守望者（0）：径向弹幕 + 召唤小怪——考验清场与走位兼顾；
- 风暴领主（1）：旋转螺旋弹幕 + 周期性突进——考验纯机动/输出节奏；
- 虚空编织者（2）：瞄准式狙击散射 + 扩散环——惩罚站桩，逼迫持续走位/闪避。

**校验**：2 个 .cs 括号全平衡；`UpdateVoid` / `AimedVolley` / `RingBurst` / `_aimTimer` / `_aimWarned` / `AimLead` 引用点（定义 + 调用）交叉核对通过；变体 0/1 既有路径未改动。无 Unity 编译器，仅静态校验——真机建议验：单机进入 Boss 波（每 5 波）多次重开，确认三种变体随机出现且各自弹幕/染色正确；重点验变体 2 的瞄准散射前摇可读、扩散环不卡顿。

**已知限制（M12）**：与变体 1 同源——联机 Boss 走 `NetWorld` 简化镜像、无 `BossBrain`，故**联机无 Boss 变体**（三种均仅单机生效）；`balance_sim.py` 以简化 Boss 建模且不分变体，故本次未重跑模拟（变体 2 不改玩家 DPS/血量公式，生存曲线不受影响，逻辑与 M9 一致）。

### M13 云存档（本次新增，含隐私合规修订）

M13 在既有本地 `SaveManager` 之上叠加**可选、默认关闭**的云端同步，让战绩/图鉴进度可跨设备恢复。核心取向是**隐私优先 + 绝不砸单机体验**：任何一道开关不满足，本模块就完全不联网，游戏行为与 M12 及之前完全一致。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 后端抽象 | `Core/CloudSave/ICloudSaveProvider.cs` | 把「存档怎么传」与「存档怎么裁决」解耦，换后端（自建 REST / Supabase / 云托管）只需实现本接口 |
| REST 实现 | `Core/CloudSave/HttpCloudSaveProvider.cs` | `UnityWebRequest` 的 GET/PUT；404 视为「云端尚无存档」（首次同步，非失败）；**强制 HTTPS**（仅 `localhost`/`127.0.0.1` 允许 http），否则 `Configured=false` 自动禁用 |
| 编排服务 | `Core/CloudSave/CloudSaveService.cs` | 三重开关门禁、启动时拉取裁决、上行节流 15s、失败退避重试 3 次、切后台/退出 flush、schema 版本保护 |
| 存档层改造 | `Core/SaveManager.cs` | `Record` 增 `schemaVersion`/`savedAt`（冲突裁决依据）；拆出 `WriteLocal`（静默落盘，避免"拉下来又推回去"的回环）；新增 `Saved` 事件、`ReplaceFromCloud()`、`UtcNowIso()`/`CompareSavedAt()` |
| 用户开关 | `Core/SettingsManager.cs` | 新增 `cloudSave`（**默认 false**）+ `SetCloudSave()`；仅置位开关，是否真联网由服务端到端判定 |
| 设置页入口 | `UI/SettingsPanel.cs` | 「云存档」区块：开关 + 实时状态 + 「立即同步」。开关拨到「开」时**补一次拉取裁决**——服务若已在 `Start` 里判定过一次"未启用"并直接返回，此后 `Update` 不会主动同步，不补的话用户要等到下次启动才生效；状态每 0.5s 重绘（同步在协程里异步完成） |
| 状态本地化 | `Core/Localization.cs`、`CloudSaveService` | 云存档状态原为硬编码中文，切英文界面会冒出中文。改为**存本地化键 + 渲染**（`LastStatus` 存键、`LastStatusText` 出文案），补 16 条中英词条 |
| 合规 | `Resources/Legal/privacy.txt`、`Core/VersionInfo.cs` | 隐私政策增补云存档条款（原第三条写着"不会上传"，与功能冲突）；`ComplianceVersion` 递增至 `2026.10.1`，**强制老用户重新阅读并同意** |
| 配置 | `Resources/cloudsave.json` | `baseUrl` 留空即禁用；部署时填入后端地址即可启用，无需改代码 |

**三重开关（任一不满足 → 零网络请求）**：① 用户在设置里开启云存档（默认关）→ ② 已同意最新版隐私政策（`TermsAgreed`）→ ③ 后端已配置且为 HTTPS。

**冲突裁决：取新、整体替换**。按 `savedAt` 比较，新者胜。刻意不做累计项合并——合并会让 `totalKills` 等计数在多端重复累加；云端为更高 `schemaVersion` 时拒绝覆盖本地。

**后端契约**（任意实现这两个端点即可接入）：
```
GET  {baseUrl}/save/{slotId}   -> 200 + 存档 JSON；404 = 尚无存档（按空处理）
PUT  {baseUrl}/save/{slotId}   <- 存档 JSON 整体覆盖；2xx = 成功
鉴权：配置了 token 时带 `Authorization: Bearer <token>`
传输：强制 HTTPS，本机地址除外
```

**校验**：8 个改动/新增 .cs 括号全平衡；`SchemaVersion`/`savedAt`/`ReplaceFromCloud`/`UtcNowIso`/`CompareSavedAt`/`WriteLocal`/`Saved`/`SetCloudSave`/`cloudSave`/`EvCloudSync`/`IsSecure` 等引用点（定义 + 调用）交叉核对通过；已修复 `PullAndReconcile` 中 `SaveManager.Instance` 为空时的潜在空引用。当时无 Unity 编译器，**仅静态校验**。M18 已补齐无头实测（见下方「M18 续二：M13 云存档无头实测」）：给传输层加了请求计数器，把「未开启/未配置时零网络请求」从代码推断升级为可断言的事实，5 个开关场景全部通过、运行时异常 0 条。仍需真实后端才能验的部分：① 冲突裁决（需两端对同一 slot 写入）；② 404 首次同步与 schema 版本保护；③ token 鉴权头；④ 杀进程重开后进度确实恢复。

**已知限制（M13）**：配置留空/开关关闭时为纯单机，功能等于不存在；冲突取新会让「多端同时游玩」以最后写入为准（原型不做实时合并）；`OnApplicationQuit` 的推送不保证完成，由下次启动的拉取裁决兜底收敛；**当前 `slotId` 即身份凭证，任何知悉者均可读写该槽**——生产必须改为服务端按账号派生 slotId 并做越权校验，同时补齐账号体系与服务端鉴权；云端数据未做端到端加密。

### M14 观战模式（本次新增，含一处既存缺陷修复）

M14 新增「以观战身份加入」：连上房主但**不生成自己的角色**，只接收世界同步用于观察对局。同时修复了一处此前一直存在、但未被发现的相机缺陷。

**既存缺陷修复（重要）**：`CameraShake`（同时负责相机跟随与震屏）此前**只在单机路径**的 `AttachCameraShake()` 里被挂载——联机路径 `BuildNetworked()` 从未创建它，而 `NetPlayer` 只是"组件已存在才配置"、从不创建。结果是**联机（房主/客户端）下相机停在原点不跟随玩家、且完全没有震屏表现**。M14 在 `PrepareStage()` 统一补齐该组件（跟随目标仍由各处 `Configure()` 设置），观战相机也正是复用这一能力。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 观战身份 | `Net/NetProtocol.cs`（`NetSession`） | 新增 `Spectator` 标识 + `SetSpectator()`；回单机/断开时复位；`ModeLabel()` 显示「观战」 |
| 大厅入口 | `Net/NetLobby.cs` | 新增「以观战身份加入」按钮；单人/普通加入路径显式清除观战态，避免沿用上一局身份 |
| 玩家对象回收 | `Net/NetPlayer.cs` | Fish-Net 会为每个连接自动生成玩家预制体、无法在生成前拦截 → 观战分支跳过全部本地接线，并发 `RequestSpectatorServerRpc` 由**房主延后一帧 `Despawn`**（避开 OnStart 期间直接反生成的时序风险）。观战者不结算伤害、不被敌人索敌、不占玩家名额 |
| 观战相机 | `Net/SpectatorCamera.cs`（新增） | 复用既有 `CameraShake` 的跟随能力，每帧把跟随目标设为被观察玩家；**Tab 循环切换视角**；被观察者死亡/离场自动切换；顶部常驻「观战中」提示，避免误以为卡住 |
| 舞台与世界 | `GameBuilder.cs` | `PrepareStage()` 补齐 `CameraShake`（上方缺陷修复）；观战时跳过 `NetWatchdog`（观战者本就没有本地玩家，否则必然误报）、隐藏 HP/XP 条（波次/Boss 等全局信息仍显示） |

**观战者为何要先生成再回收**：Fish-Net 的 PlayerSpawner 在连接建立时即自动 spawn 玩家预制体，没有可用的"生成前拦截"挂钩（除非改用手动 spawn 流程）。因此采用「生成 → 客户端声明身份 → 房主回收」，代价是存在 1 帧窗口；期间观战分支已跳过全部本地接线，不会误接管输入或污染 `PlayerController.Instance`。

**校验**：5 个改动/新增 .cs 括号全平衡；`SetSpectator` / `NetSession.Spectator` / `_spectatorDespawnPending` / `RequestSpectatorServerRpc` / `SpectatorCamera` / `EnsureCameraShake` / `ForEachAlive` / `UIFont` 引用点（定义 + 调用）交叉核对通过。无 Unity 编译器，仅静态校验——真机建议验：① 房主建房 + 另一实例点「以观战身份加入」，确认能看到对局且**没有自己的角色**（不被敌人追、不抢经验、不占名额）；② 按 Tab 确认视角在存活玩家间循环切换；③ 确认观战端相机确实跟随被观察者（顺带验证上方相机缺陷修复）；④ 普通加入与观战加入来回切换，确认观战身份不残留。

**已知限制（M14）**：观战者掉线走 M11 重连路径，重连时会再次「短暂生成 → 回收」一次玩家对象，需真机确认无可见闪烁；`Despawn` 采用 Fish-Net `NetworkObject.Despawn()`，若 PlayerSpawner 在回收后又自动补生成，则需改为在 PlayerSpawner 层手动接管 spawn（本环境无法验证）；观战端 HUD 仅显示波次/Boss 等全局信息，不显示被观察玩家的血量/等级；`SpectatorCamera` 与既有 `NetWatchdog` 一样是整文件 `#if FISHNET_ENABLED` 保护、却由 `GameBuilder` 无保护引用——这与既有 `NetWatchdog` 一致、非 M14 引入。**M15 用 `audit_project.py` 检出并把这三处（连同 `PlayerController` 对 `NetPlayer` 的引用）一并收进条件编译，"零联机插件可编译"现已真正成立**。

### M15 工程自检工具（本次新增，含 3 处真实问题修复）

本项目在没有 Unity 编译器的环境下迭代，此前每个里程碑都靠临时写一段脚本做人工静态校验——既零散又容易漏，而真实踩到的坑恰恰都是"肉眼扫过去发现不了"的那类。M15 把这些校验固化为可重复执行的**工程体检**：`audit_project.py`。

```
用法：  python audit_project.py
退出码：0 = 无 ERROR；1 = 存在 ERROR（WARN/INFO 不计入）
```

| 检查项 | 覆盖的坑 |
| --- | --- |
| 括号平衡 | 手改多行代码块最容易漏括号 |
| Fish-Net 条件编译一致性 | 整文件 `#if FISHNET_ENABLED` 保护的类型若被无保护代码引用 → 未装插件直接编译失败 |
| 枚举分支覆盖 | 新增枚举值忘记在某个 switch 补分支（M9 加 `Mine` 时的手工核对项，现已自动化） |
| Resources 存在性 | `Resources.Load("x")` 找不到文件 = 运行时静默失败、不报错 |
| 同类重复成员 | 重复声明"后写覆盖先写"（M9 在 `balance_sim.py` 里真实踩过 `UNLOCKABLE_IDS`） |

**首次运行即查出 3 处真实问题（均已修复）**：

1. `Player/PlayerController.cs` 无保护引用联机专属类型 `NetPlayer`，而该文件**完全没有 FISHNET 保护**——未装插件必然编译失败。这是**核心玩法代码**里的隐患，比 M14 记录过的 `NetWatchdog` 更严重；
2. `GameBuilder.cs` 无保护引用 `NetWatchdog`；
3. `GameBuilder.cs` 无保护引用 `SpectatorCamera`（M14 新增）。

三处一并收进 `#if FISHNET_ENABLED` 后，工程一直宣称的"未安装联机插件时保持纯单机可编译"才**真正成立**——此前它只是声称，并未成立。

**工具自身也有 3 个 bug（已修，值得记录，因为它们会造成误报）**：

- **去噪顺序**：必须先删字符串、再删注释。否则 `"https://"` 里的 `//` 会被当成注释起点，把同行剩余代码（含半个括号）一起删掉，直接造成括号平衡误报；
- `Resources.Load` 正则误匹配 `Resources.LoadAll`，且把 `"Sprites/"` 这类"目录前缀"（真实路径靠后续拼接）当成完整路径；
- 「重复成员」把不同方法里的同名**局部变量**误判成类字段（已收紧到"缩进恰为 8 空格"这一成员级判据，并按 class 声明重置作用域）。

**反证（证明检查不是空转）**：实际解析到 72 个 .cs、6 个枚举、16 个 switch 块、13 个受 FISHNET 保护的类型、23 条 Resources 条目。其中 **`WeaponType` 的 9 个成员（含 M9 新增的 `Mine`）全部有 case 覆盖**——这正是 M9 当时靠人工逐条 grep 核对的项目，现已自动化。

**当前结果**：`ERROR 0 / WARN 0 / INFO 8`。INFO 均为"相关 switch 有 default 兜底"或"仅作数据使用"的枚举（`EnemyKind.Chaser`、`NetMode.Offline`、`Track.*`、`NetPickupKind.*`），属预期，不计入失败。

**已知限制（M15）**：正则级静态分析，不做语义/类型推导，无法发现空引用、时序、数值平衡等运行时问题；缩进判据依赖本工程风格（namespace 0 / class 4 / 成员 8），重构缩进时需同步；**它绝不能替代 Unity 编译与真机验证**——只是把"每次改完都要人工重做一遍的机械核对"变成一条命令。

### M16 跨平台：触屏输入与移动端构建（本次新增）

M16 补齐跨平台能力。核心判断：**本游戏自动开火，移动是唯一需要的玩家操作输入**——所以让移动端可玩的关键不是重做整套操作，而是补上"移动"这一项。

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 输入抽象 | `Core/PlayerInput.cs`（新增） | 统一入口 `MoveVector()`：触屏优先取虚拟摇杆，否则沿用原有 Input 轴；`TouchPlatform` 决定是否创建摇杆 |
| 虚拟摇杆 | `Core/TouchJoystick.cs`（新增） | 左半屏任意位置按下即作原点（浮动摇杆）；输出归一化方向；画布用 `ConstantPixelSize` 使坐标与屏幕像素 1:1，免去分辨率换算；右半屏刻意留空，为将来的主动技能/闪避预留 |
| 玩家接线 | `Player/PlayerController.cs` | 移动改为经 `Core.PlayerInput.MoveVector()`，不再直接读 Input 轴 |
| 平台适配 | `GameBuilder.cs` | 触屏平台创建摇杆（单机与联机两条构建路径都接）；移动端显式锁 60 帧（部分设备默认 30 帧，弹幕玩法下手感会明显变差） |
| 移动端构建 | `Assets/Editor/BuildScript.cs` | 新增「构建 Android (IL2CPP, APK)」「构建 iOS (Xcode 工程)」；强制横屏、Android 递增整数 `bundleVersionCode` + ARM64 + minSdk 24、iOS `buildNumber`/targetOS；版本注入抽为共用的 `WriteBuildInfo` |

**桌面行为零变化**：非触屏平台根本不创建摇杆，`MoveVector()` 直接回落到原 Input 轴，与 M15 及之前完全一致——这是本次改动的回归安全边界。

**审计范围同步扩大**：`audit_project.py` 原先只扫 `Assets/_Project/Scripts`，`Assets/Editor/BuildScript.cs` 这类构建脚本一直是盲区（M16 恰好改的就是它）。现在改为扫描整个 `Assets`，覆盖文件数 72 → 76。

**校验**：`python audit_project.py` → ERROR 0 / WARN 0 / INFO 8，退出码 0；新增/改动 .cs 括号全平衡。无 Unity 编译器，仅静态校验——真机建议验：① Android 真机（或 Unity Remote）确认左半屏按下出现摇杆、角色跟随移动、松手即停；② **桌面构建回归**：确认键鼠操作手感与之前无任何差异；③ 横屏锁定生效、HUD 未被刘海/圆角遮挡；④ Android 能出包并安装（需装 Android Build Support 模块）。

**已知限制（M16）**：未做刘海屏/安全区适配（`Screen.safeArea`），部分全面屏机型 HUD 边缘可能被遮挡；未做竖屏布局——强制横屏，竖屏设备需玩家自行旋转；虚拟摇杆未做死区、跟随式摇杆等手感调优；iOS 在 Windows 上只能导出 Xcode 工程，编译与签名仍需 Mac；移动端性能（千怪同屏）未做真机压测；未安装对应 Build Support 模块时，移动端构建菜单会由 try/catch 降级为告警而非静默失败。

### M17 首次 Unity 真机实测（本次新增，含 11 处真实缺陷修复）

此前 M9–M16 全部依赖静态推断。本轮首次在本机 **Unity 2022.3.62f3c1** 上完成「编译 → 构建 → 无头跑一局」的完整验证。结论很直接：**静态自检（audit_project.py）全过的代码，在 Unity 里实际有 10 处编译错误 + 1 处运行时异常**，现全部修复。

**实测手段（已固化，可复现）**：
```
# 1) 编译检查（退出码 0 = 无编译错误）
Unity.exe -batchmode -nographics -quit -projectPath <工程> -logFile unity_compile.log

# 2) 无头构建 Windows 玩家包
Unity.exe -batchmode -nographics -quit -projectPath <工程> ^
  -executeMethod StarPixel.EditorTools.BuildScript.HeadlessBuild -logFile unity_build.log

# 3) 无头跑一局（自动驾驶 + 自动选卡）
StarPixelRogue.exe -batchmode -nographics -smokeTest -smokeSeconds 120 -logFile smoke_run.log
# 退出码 0=通过；日志以 [SMOKE] 前缀输出关键指标
```

| 模块 | 文件 | 说明 |
| --- | --- | --- |
| 无头实测 | `Dev/SmokeTest.cs`（新增） | 仅带 `-smokeTest` 时创建；绕圈游走自动驾驶 + 自动选卡；采集击杀/波次/等级/选卡次数/Boss/存活并给出退出码 |
| 实测入口 | `GameBuilder.cs` | 新增 `HasArg`/`SmokeSeconds`/`StartSmokeTest`：无头时跳过合规门与主菜单直接开局（正常启动不受影响） |
| 输入接缝 | `Core/PlayerInput.cs` | 新增 `Override` 测试注入点，正常恒为 null |
| 选卡入口 | `UI/LevelUpManager.cs` | 新增 `AutoChooseFirst()`：选卡会把 `timeScale` 归零，无头无人点击会永久死锁 |
| 无头构建 | `Editor/BuildScript.cs` | 新增 `HeadlessBuild()`：供 `-executeMethod` 调用，并补建缺失的 `Bootstrap.unity` |

**修复的编译错误（10 处——静态校验一项都查不出来）**：

| 文件 | 问题 | 来源 |
| --- | --- | --- |
| `Enemy/BossBrain.cs` | 缺 `using StarPixel.Combat`（`Damageable`） | 自 M6.3 潜伏 |
| `Net/NetBackend.cs` | 缺 `using UnityEngine`（`GameObject/Vector3/Quaternion`） | 潜伏 |
| `Combat/Skill/Mine.cs` | `ApplySlow` 定义在 `Enemy` 上而非 `Damageable`，应按组件取 | **M9 引入** |
| `Core/TouchJoystick.cs` | 缺 `using UnityEngine.UI`（`CanvasScaler`） | **M16 引入** |
| `Core/UIFactory.cs` | `CreateToggle` 在 lambda 内提前引用 `btn`（CS0841） | 潜伏 |
| `UI/SettingsPanel.cs` | 同上（`qualityBtn`） | 潜伏 |
| `Enemy/BossBrain.cs` | `Random` 在 `System`/`UnityEngine` 间歧义（CS0104） | 潜伏 |
| `GameBuilder.cs` | `Enemy` 与命名空间同名需完全限定（CS0118） | 潜伏 |
| `Core/Localization.cs` | 缺 3/4 个占位符的 `T()` 重载（3 处 UI 调用） | 潜伏 |

**修复的运行时异常（1 处）**：`Combat/CombatTargets.ForEachWithin` 直接 foreach 活的 `ActiveEnemies`，而回调造成击杀时敌人被 `RemoveEnemy` 即时移出 → `InvalidOperationException: Collection was modified`，实测捕获 **42 次**（触发路径 `FireNova → ForEachWithin`）。改为快照遍历后**归零**。

**实测结果（120s 上限，单机）**：
```
时长 99.8s | 击杀 224 | 波次 10 | 等级 10 | 升级选卡 9 次 | 遇到 Boss 是 | 运行时异常 0
结论：通过（退出码 0）
```
Wave 10 意味着经历 Wave 5 与 Wave 10 两场 Boss 战——M9/M12 的**三种 Boss 变体均进入真实战斗且无异常**。

**已确认的工程/环境问题（需你决定）**：
1. **工程原本无法在 Unity 中打开**：`Packages/manifest.json` 声明从 GitHub 拉取 FishNet，而本机 git 报 `SSL certificate: unable to get local issuer certificate (20)`，包解析直接失败、编译根本没机会执行。为完成实测我**临时移除了该依赖**（备份 `Packages/manifest.json.bak`）。因工程从未定义 `FISHNET_ENABLED`，移除后单机功能完整。原始依赖如下，需要时可直接还原：
   ```json
   { "dependencies": { "com.firstgeargames.fishnet": "https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet", "com.unity.ugui": "1.0.0" } }
   ```
2. **`Assets/Scenes` 原本为空**：`BuildScript` 引用的 `Bootstrap.unity` 并不存在，本次构建时已自动补建。
3. Unity 首次打开会自动扩充 `manifest.json`（2 项 → 40 项默认包）。

> 这三点共同解释了一个此前无法解释的现象：为什么 M14 那个"联机相机从不跟随玩家"的缺陷能潜伏这么久——**这个工程此前从未在 Unity 里真正跑起来过**。

**已知限制（M17）**：本次验证**未覆盖联机路径**——`FISHNET_ENABLED` 未定义，M10/M11/M14 的 Fish-Net 代码整段未参与编译与运行，需装好 FishNet 后单独验证；`Net/NetRegistry.cs` 的 `ForEachWithin` 是索引循环，不会抛本异常，但回调中移除元素可能跳过个别目标（未做快照、也未编译验证）；无头环境无渲染，画面布局、UI 排版与操作手感仍需在编辑器里肉眼确认。

### M18 联机代码编译验证 + FishNet 本地化安装（本次新增）

M17 之后最大的验证缺口很明确：联机代码整段用 `#if FISHNET_ENABLED` 保护，而该符号**从未定义过**，意味着 **M10（角度同步）/ M11（断线重连）/ M14（观战）的代码从来没有被任何编译器检查过**。M18 补上这一环。

**FishNet 装不上的根因不是网络问题**：系统 git、curl、python 都能正常访问 GitHub（`git ls-remote` 成功列出分支），唯独 Unity 自带的 OpenSSL 报 `unable to get local issuer certificate (20)`——本机走了带自签 CA 的代理，系统证书链里有、Unity 的证书库里没有。

**解法：绕开 Unity 的 git，而不是削弱你这台机器的安全配置。** 用系统 git 浅克隆 FishNet **4.7.4**（`unity: 2021.3`，与本机 2022.3.62f3c1 兼容），以**嵌入式本地包**方式放进 `Packages/com.firstgeargames.fishnet/`：

| 模块 | 说明 |
| --- | --- |
| `Packages/com.firstgeargames.fishnet/` | FishNet 4.7.4 嵌入式本地包（11MB），Unity 无需联网解析 |
| `Editor/BuildScript.cs` | 新增「启用/停用联机代码」菜单 + `SetFishNetDefine()`：对 Standalone/Android/iOS 统一增删 `FISHNET_ENABLED` |

> 刻意**没有**去改 `git config http.sslVerify` 之类的全局设置——那会削弱你这台机器的安全性，而这里根本不需要。

**联机代码首次编译：仅 1 处错误**
- `Net/NetRegistry.cs` 缺 `using StarPixel.Combat`（`Damageable`），与 M17 的 `BossBrain` 完全同类。修复后 **error CS 0，退出码 0**。

这个数字本身值得记录：M17 里天天在用的单机代码有 10 处编译错误，而**从未编译过的联机代码只有 1 处**。原因是联机代码写得相对克制，且 M15 的条件编译检查已先扫过一轮引用边界。

**回归验证（关键一步）**：启用 `FISHNET_ENABLED` 后 `NetBackend.Instance` 会自动创建，正常启动将进入**联机大厅**（符合 M4 设计）。重新构建并跑冒烟实测，确认单机玩法无回归：
```
时长 120s（满时长存活）| 击杀 323 | 波次 12 | 等级 14 | 升级选卡 13 次 | 遇到 Boss 是 | 运行时异常 0
```

**自检工具补强：新增第 6 项检查**

M17/M18 合计踩到 4 次「缺 using」，而括号/枚举/守卫检查**全部无感**——这是工具此前最大的盲区。新增「使用了类型却缺少 using」检查，精度是难点，初版 136 条 WARN 几乎全是误报，逐步收紧到 0：

| 收敛步骤 | 解决的问题 | WARN 数 |
| --- | --- | --- |
| 初版 | — | 136 |
| 支持**相对限定名**（`Core.EventBus` 在 `StarPixel` 下合法） | 把合法写法误判为缺 using | 8 |
| 排除 **`.` 后的成员访问**（`save.Data`、`p.Damageable`） | 属性名与类型名重名 | 2 |
| 排除**本地 enum 成员**（`Track.Boss`、`Tab.Boss`） | 枚举成员与工程类型同名 | **0** |

有效性已用反例验证：合成 M17 真实 bug 形态**能被报出**；合法的相对限定写法与成员访问**均不误报**。该项以 WARN 报告（正则级分析，类型名与局部变量仍可能撞名），不计入失败。

**当前状态**：`python audit_project.py` → ERROR 0 / WARN 0 / INFO 8（退出码 0）；Unity 编译（**含联机代码**）error CS 0；冒烟实测通过。

**已知限制（M18）**：联机代码目前只是**编译通过**，`Packages/com.firstgeargames.fishnet/`（约 11MB）会随工程入版本库，若不希望入库请自行加进 `.gitignore` 并改用 manifest 依赖；`FISHNET_ENABLED` 已写入 ProjectSettings 的 Standalone/Android/iOS 三组。

### M18 续：联机双开实测脚本（net_smoke.py）

为了真正验证联机运行时，新增一键双开脚本：同时拉起**无头房主 + 无头客户端**，自动建房/加入，跑一段时间后从两边日志交叉核对。

```
python net_smoke.py --seconds 60 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe

# M14 观战：客户端以观战身份加入
python net_smoke.py --spectator --seconds 60 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe

# M11 断线重连：客户端在第 N 秒模拟意外掉线，验证自动重连与进度重放
python net_smoke.py --drop-at 50 --seconds 90 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe

# M11 断线重连（房主分支）：房主在第 N 秒掉线，权威世界应保留不拆
python net_smoke.py --host-drop-at 40 --seconds 90 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe
```

**为什么能验证联机**：客户端的「同步波次 / 同步击杀」读的是 `NetWorldState` 的 SyncVar（房主权威写入、网络广播），房主读的是同一批 SyncVar 的权威侧。**两端数值一致 == 房主权威同步链路真的在工作**——这是 M10/M11/M14 共同的运行时底座。三种模式分别是：

| 模式 | 命令 | 验证对象 | 判据 |
| --- | --- | --- | --- |
| 普通联机 | （默认） | M10/M11/M14 的共同底座 | 双端玩家数 / 波次 / 击杀一致 |
| 观战 | `--spectator` | **M14** | 观战者自身玩家对象被房主回收 + 仍能收到房主权威世界同步 |
| 断线重连·客户端 | `--drop-at N` | **M11 客户端分支** | 客户端自述重连成功 + 波次/等级/击杀进度未回退 + 房主侧观测到「2→1→2」 |
| 断线重连·房主 | `--host-drop-at N` | **M11 房主分支** | 重连成功 + **掉线期间权威世界仍存活（未拆）** + 波次/击杀未回退 + 对端客户端未受波及 |

> M11 的两条分支行为不同（见 `FishNetBackend.OnClientConnectionState`）：
> **房主掉线** → 服务端仍在运行、权威世界保留不拆，只重连本地客户端；
> **客户端掉线** → 先 `TeardownWorld` 拆本地镜像，重连成功后由 `Ready` 重建同一局。
> 因此必须用 `--drop-at` / `--host-drop-at` 分别驱动，否则只能测到其中一条。

配套新增（均只在带 `-netHost` / `-netClient` 参数时激活，正常构建零影响）：

| 模块 | 说明 |
| --- | --- |
| `net_smoke.py` | 编排：拉起两端 → 等待 → 解析 `[SMOKE]` → 交叉核对 → 判定与退出码；带**日志体积熔断** |
| `GameBuilder.StartNetSmoke` / `ArgValue` / `ArgFloat` | 无头自动建房/加入（`-netAddress` 指定地址），绕过大厅 UI；`-netSpectator` / `-netDropAt` 切换实测模式 |
| `SmokeTest.StartNet(seconds, dropAt)` | 联机采集器：阵亡不终止 + 保活 + 掉线/重连采样 + 观战身份校验 |
| `FishNetBackend.SimulateDrop()` | 实测钩子：只停本地客户端且**不置** `intentionalStop`，从而走真实的 M11 自动重连路径 |
| `NetWorldState.Wave/Kills/Zone` | 只读快照，供双端比对 |
| `BuildScript.HeadlessNetBuild` | 构建联机版：自动装配 `NetBootstrap` 场景后再出包（普通单机用 `HeadlessBuild`） |

**双开实测暴露的真实缺陷（均为联机特有的、编译期与人工试玩都不可能发现的问题）**：

| # | 缺陷 | 后果 | 状态 |
| --- | --- | --- | --- |
| 1 | `NetBackend` 用 `Type.GetType("…NetworkObject, FishNet")` 探测程序集，而 Fish-Net 4.x 的程序集名是 **`FishNet.Runtime`** | **即使定义了 `FISHNET_ENABLED`，`NetBackend.Instance` 也恒为 null，联机入口形同虚设，游戏一直走纯单机**（M4.5 起存在） | ✅ 已修：改为按类型全名遍历已加载程序集 |
| 2 | `NetSetupEditor` 查找传输层用 `"…Tugboat.TugboatTransport"`（不存在）和 `"…Tugboat"`（是**命名空间名**而非类型） | 装配出来的 NetworkManager **没有传输层**，根本无法建连 | ✅ 已修：改用 `FishNet.Transporting.Tugboat.Tugboat`，并加 Multipass 兜底 |
| 3 | `NetSpawner.Spawn` 的 `beforeSpawn` 在网络 Spawn **之前**写 SyncVar，Fish-Net 4.x 此时 SyncVar 未初始化 | 实测 60 秒刷 **5.7 万条异常**，敌人一个都生成不出来 | ✅ 已修：初始状态改到 `afterSpawn`（Spawn 成功之后）写入，并对「生成失败」加一次性告警 + 销毁对象，避免每帧刷异常写爆磁盘 |
| 4 | `EnsureNetworkManager` 的兜底路径直接 `AddComponent<NetworkManager>()`，其 `Awake` 在 `SpawnablePrefabs` 为空时**直接 return** | 兜底出来的是"空壳" NM（`ClientManager` 为 null），下一行订阅即 NRE | ✅ 已修：先在未激活物体上装配，配好预制体集合与传输层再激活 |
| 5 | 无头下不锁帧，NullGfxDevice 跑到数千 FPS，把「每帧一条」的渲染告警放大成 **213MB / 5.7 万条**日志 | 磁盘写满，真正的问题线索被埋掉 | ✅ 已修：实测路径显式 `targetFrameRate = 60`，frustum 告警入噪声白名单，并加 200MB 日志熔断 |
| 6 | 联机路径漏建 `PoolManager`，且 `PlayerController.isLocal` 默认为 `true` | 房主侧 60 秒抛 **13~14 条 NRE**（联机客户端 0 条，因为它根本不进开火结算） | ✅ 已修：补齐 PoolManager、`isLocal` 默认改 `false`（单机路径显式置回），开火分支加 `CanSpawnLocal` 守卫 |
| 7 | AoE 击杀绕过死亡管道（`ServerTakeDamage` 直接扣血不走 `OnDeath`），且 `NetEnemyRegistry.Count` 不过滤死亡态 | 波次推进判定永不归零 → **150 秒只到第 3 波并卡死** | ✅ 已修：死亡统一由 `OnDeath` 回调结算，新增过滤死亡态的 `AliveCount` |
| 8 | `NetPlayer` 房主侧从不写等级/经验 SyncVar（只在重连重放时写过） | 客户端 HUD 等级**永远停在 1**、经验条一动不动（实测房主 16 级 / 客户端 1 级） | ✅ 已修：房主 `Update` 常规写入 `_level` / `_xpRatio` |
| 9 | **M11**：重连令牌在 `OnStartClient` 里直发 ServerRpc，**首连能到、重连必丢** | 房主从未收到令牌 → 进度快照匹配不上 → **玩家重连后进度被重置回 1 级**（断线前 3 级） | ✅ 已修：改为「首连直发 + 每 0.4s 重发 + 房主 `ConfirmHandshakeTargetRpc` 回执」的握手 |

**关于缺陷 #9 的排查教训（判据必须自证有区分力）**：第一轮实测曾判为"通过"，但等级是 `1 → 1`——因为该轮掉线时客户端只有 1 级，"重连后 ≥ 断线前"**平凡成立**，进度重放其实一次都没发生（`grep -c "进度重放完成"` = 0）。此后补了两道防线：① 采样口径改为只读 `NetPlayer` 的等级 SyncVar，**禁止**回退到不参与 XP 结算的 `PlayerController.level`；② 脚本增加区分力守卫——掉线时等级 ≤1 直接判为无效样本并要求调大 `--drop-at`。

**另一个判据踩坑（房主分支）**：最初用「掉线期间场上敌人 > 0」判断权威世界是否保留，结果**误报失败**。房主本地客户端断开时 Fish-Net 会清空**本地网络视图**（`NetEnemy` 对象被销毁），敌人数归零是**预期行为**，与服务端权威世界是否存活无关。改用「权威管理器 `EnemyManager` 是否仍在」——只有误走 `TeardownWorld` 才会销毁它。两条分支的实测对照（判据确有区分力）：

| 掉线期间指标 | 房主掉线（世界保留） | 客户端掉线（本地镜像被拆） |
| --- | --- | --- |
| 世界存活 | **是** | 否 |
| 波次 | 4（= 断线前，未回退） | **0**（本地世界状态对象已随镜像销毁） |
| 场上敌人 | 0（本地网络视图被清空，属预期） | 0 |

**当前状态**：四种模式均实测通过，运行时异常 0 条——

| 模式 | 关键实测结果 |
| --- | --- |
| 普通联机 60s | 波次 7 / 5，击杀 105 / 73，Boss 是 / 是 |
| 观战 60s（M14） | 会话角色「观战」，自身角色已回收 **是**，同步波次 7 / 6，击杀 138 / 90 |
| 客户端掉线 90s（M11） | 掉线 @50s，重连 1.5s，波次 6→6，**等级 3→5**，击杀 84→104，房主观测到对端掉线/重连 **是 / 是** |
| 房主掉线 90s（M11） | 掉线 @40s，重连 1.5s，**掉线期间世界存活 是**，波次 4→4→4，击杀 68→72，等级 2→3，对端未受波及 |
| 单机回归 45s | 击杀 72，波次 4，等级 7，EXIT=0 |

### M18 续二：M13 云存档无头实测（cloudsave_probe.py）

M13 是全项目唯一一个「默认关闭、但启用后会在运行时联网」的模块，此前零实测覆盖——
它的核心承诺「未开启/未配置时零网络请求」只停留在代码推断。本轮补齐实测入口。

**关键改动：让"发请求"这件事可观测。** 只证明「调用方守了规矩」（`Enabled==false` 时直接 return）
证明不了「一个请求都没发出去」，故给传输层加了请求计数器，并在 `Download`/`Upload` 入口**自设闸门**
（`if (!Configured)` 直接失败返回）——即便调用方漏判也不会发出请求。这是顺手补上的真实健壮性改进：
原实现把「零请求」完全押在调用方不犯错上，任何一处漏判都会让隐私政策的 HTTPS 承诺失效。

| 改动 | 位置 | 说明 |
| --- | --- | --- |
| `ICloudSaveProvider.RequestCount` | 接口 | 请求总数（通用可观测性契约，换后端也能报数） |
| `HttpCloudSaveProvider.RequestCount` + 入口闸门 | REST 实现 | `SendWebRequest` 前自增；未配置时直接失败返回 |
| `ProviderConfigured` / `RequestCount` / `DisabledReason` | `CloudSaveService` | 对外暴露状态与计数；`Enabled` 改由 `DisabledReason` 推导（避免两处判定漂移） |
| `CloudSaveService.OverrideConfig` | `CloudSaveService` | 实测专用内存配置注入，不落盘、不影响正常启动 |
| `Dev/CloudSaveProbe.cs` | 新增 | 采集器：按开关组合输出 `[SMOKE]` 指标并判定 |
| `GameBuilder.StartCloudSaveProbe` | 引导器 | `-cloudSaveProbe` 系列参数入口 |

**实测矩阵**（每个场景一个独立进程、20s 单机对局）：

| 场景 | 后端地址 | 用户开关 | 已同意隐私 | 期望启用 | 期望请求 |
| --- | --- | --- | --- | --- | --- |
| A 未配置（真实默认态） | 无 | 开 | 是 | 否 | **0** |
| B 非 HTTPS 地址 | `http://example.com` | 开 | 是 | 否 | **0** |
| C 已配置但用户未开启 | 不可达地址 | **关** | 是 | 否 | **0** |
| D 已配置已开启但未同意隐私 | 不可达地址 | 开 | **否** | 否 | **0** |
| E 全开但后端不可达 | 不可达地址 | 开 | 是 | **是** | **>0** |

**判据的区分力**（沿用 M11 缺陷 #9 的教训）：

1. 「是否应当启用」由脚本**独立**按 HTTPS 规则推导后传入，**绝不**读 `CloudSaveService.Enabled` 当期望值——
   否则实现若误把 `http://` 判为已配置，被测代码就会自我印证。
2. E 场景断言「请求数 > 0 且 ≥2」：只断言"没崩溃"的话，压根不联网也能通过；
   要求 ≥2 是为了确保拉取与上传两条路径都被走到（≥2 = 启动拉取 + 主动拉取 + 主动上传）。
3. A~D 与 E 构成**双向断言**：两个方向同时成立，"零请求"才不是靠"压根没实现同步"蒙出来的。
4. 所有场景都断言「击杀 > 0 且波次 ≥ 1 且零异常」——云存档无论开关都不能砸单机体验。
5. B 场景追加要求 `DisabledReason` 指向「HTTPS / 未配置」，确认是被安全校验拦下而非被别的开关意外挡住。

**实测结果（5 场景全部通过，运行时异常 0 条）**：

| 场景 | 云存档启用 | 期望 | 网络请求数 | 击杀 | 波次 | 退出码 |
| --- | --- | --- | --- | --- | --- | --- |
| A 未配置（真实默认态） | 否 | 否 | **0** | 27 | 2 | 0 |
| B 非 HTTPS 地址 | 否 | 否 | **0** | 25 | 2 | 0 |
| C 已配置但用户未开启 | 否 | 否 | **0** | 27 | 2 | 0 |
| D 已配置已开启但未同意隐私 | 否 | 否 | **0** | 28 | 2 | 0 |
| E 全开但后端不可达 | **是** | 是 | **3** | 19 | 2 | 0 |

各场景「未启用原因」精确指向对应开关（A/B → 后端未配置或地址非 HTTPS、C → 用户未开启、D → 未同意最新版隐私政策），
说明拦截发生在正确位置。E 场景：真的发出 3 次请求（启动拉取 + 第 3s 主动拉取 + 第 8s 主动上传），
后端不可达时**静默降级**为「继续使用本地存档」（状态栏如实显示失败与重试倒计时，不抛异常），
且击杀 19 / 波次 2 与 A~D 同一水平——玩法未受任何影响。

```bash
python cloudsave_probe.py --seconds 20          # 全部 5 个场景（约 100 秒）
python cloudsave_probe.py --only E              # 只跑指定场景
```

云存档实测不需要 FishNet，用普通单机包即可（`HeadlessBuild`，非 `HeadlessNetBuild`）。
详见 `cloudsave_m13.md`。

> **仍未覆盖**：本实测证明的是「门禁与降级行为正确」，没有验证云端真的能存取——
> 冲突裁决（需两端对同一 slot 写入）、404 首次同步、schema 版本保护、token 鉴权头、进度跨设备恢复，
> 都需要一个真实后端。这几项与「门禁是否会在未配置时泄露请求」是两件事，后者已闭环。

### M18 续三：M13 设置页 UI 与状态本地化

M13 此前留了一条待办：「设置页『云存档』开关的 UI 未做（`SetCloudSave()` 接口已就绪）」。本轮补上，
并顺带修掉两个只有真正把面板建出来才会暴露的问题。

| 改动 | 说明 |
| --- | --- |
| 设置页「云存档」区块 | 开关 + 实时状态文本 + 「立即同步」按钮；状态每 0.5s 重绘（同步在协程里异步完成，不重绘就永远停在旧值） |
| 开关拨到「开」时补一次拉取裁决 | **真实缺陷修复**：服务若已在 `Start` 里判定过一次"未启用"并直接 return，此后 `Update` 不会再主动发起同步——不补这一次，用户开了开关却毫无反应，只能等到下次启动 |
| 状态改为「存键 + 渲染」 | `LastStatus` 存本地化键、`LastStatusText` 出成品文案，补 16 条中英词条。原实现是硬编码中文，**英文界面会冒出一串中文状态** |
| 面板布局 | 620 → 700（仍低于 720p 屏高），玩法开关改两列腾出空间；顺带把此前溢出到面板外的「导出诊断日志」按钮与提示收进面板内 |

**为什么还需要一个 UI 探针**：设置面板在无头冒烟里根本不会被创建（那是单机/联机对局路径），
于是这次 UI 改动等于零验证。故新增 `-uiProbe`：把面板建出来、**真的点一下开关**、再切一次语言，
断言三件事——① 控件齐全；② 开关确实写进 `SettingsManager`（不是只画了个样子）；
③ 中英文案都是成品而非裸键名（`Localization.T` 漏翻时会原样返回键，UI 上就会冒出 `cloud.status.off`）。

```bash
python cloudsave_probe.py --ui      # 只跑设置面板探针
python cloudsave_probe.py           # 5 个联网场景 + 设置面板探针
```

实测：三个控件存在、点击生效 **是**、中文「未启用（可在设置中开启）」/ 英文「Not enabled (can be turned on in Settings)」、
运行期异常 0 条。两条文案不同，证明本地化真的生效而非回退到同一份硬编码。

> **探针踩坑**：首轮报了 170 条「运行期异常」，但日志里 grep 不到任何 exception——
> 那是 NullGfxDevice 下 Canvas 每帧刷的同一条 **Error 级渲染噪声**。
> 处理方式与联机实测一致：进白名单，而不是当成缺陷（真错误会被噪声淹没，故探针同时输出首条错误内容备查）。
> 另有一处：语言切换会整体重建面板，而 `Destroy` 延迟到帧末——用 `GameObject.Find` 定位控件会命中尚未销毁的
> **旧**画布，读到的仍是切换前的文案。改由 `SettingsPanel.PanelRoot` 取当前面板根。

### M18 续四：硬编码中文全面收官（大厅 / 升级卡 / 防沉迷 / 云存档原因）

续三修完云存档状态文案后，顺手全量扫了一遍代码里的中文字符串——发现**同一类缺陷远不止一处**。
M5 引入本地化时只覆盖了主菜单 / 设置 / 合规门 / 图鉴 / 选角，下列位置此后新增或压根没被覆盖：

| 位置 | 改动 |
| --- | --- |
| 联机大厅（M4 建的） | **整屏硬编码中文**：标题 / 副标题 / 提示 / 4 个按钮 / 地址标签 / 3 类连接状态，共 14 处改走 `Localization.T`；新增 `NetLobby.PanelRoot` 供探针定位（与 `SettingsPanel.PanelRoot` 同因：语言切换后旧画布要等帧末才销毁，`GameObject.Find` 会命中旧的） |
| 升级选卡 | `SkillDef` 新增 `locKey` + `DisplayName`（留空回退 `skillName`，老数据不会变空）；10 把武器名、9 类武器描述、分级升级描述、7 个属性强化全部词条化。`NextLevelDesc` 改返回本地化键，新增渲染版重载 |
| HUD / 图鉴 / 选角 | 武器名统一走 `DisplayName`（此前四处直接读 `skillName`，英文界面下 HUD 与图鉴的武器名同样是中文） |
| 防沉迷弹窗 | 预警 / 健康提示 / 强制退出提示 3 处文案词条化——**合规相关，英文玩家看不懂等于提示没弹** |
| 云存档未启用原因 | `DisabledReason` 改存键、新增 `DisabledReasonText` 渲染（与 `LastStatus` 同一套做法）；断言用键、展示用文案，改文案不会弄坏实测判据 |

**判据比续三更硬**：只断言「中英两条文案不同」是不够的——一条硬编码中文加一条英文，
同样能满足"两者不同"。故本轮新增正向判据：**英文界面下收集到的所有文案不含任何中日韩字符**。
升级卡不调 `BuildOffers`（它依赖真实 `PlayerController`），而是逐项取遍它能拼出的全部素材
（10 把武器 ×（名 / 类型描述 / 3 级升级描述）+ 7 个属性强化 + 3 条模板 = 62 条），
这样断言不依赖随机抽卡结果，未来新增的卡也照样被覆盖。

> **区分力验证（照例必做）**：故意删掉一条英文词条、并把一条英文文案写成中文，重建后重跑——
> 探针精确报出「界面出现裸本地化键 `weapon.name.3`」与「英文界面残留中文 `单人游戏`」，
> 随后还原重新构建，全绿。证明判据真的会失败，而不是恒真。

```bash
python cloudsave_probe.py --ui      # 只跑 UI 探针（设置面板 + 联机大厅 + 升级选卡）
python cloudsave_probe.py           # 5 个联网场景 + UI 探针
```

实测（异常均 0）：UI 探针全绿——联机大厅中/英各收集到 9 条文案、升级卡渲染 9 条、
素材枚举 62 条、裸键 **无**、英文界面中文残留 **无**；云存档 5 场景不变（A~D 请求 0、E 请求 3）；
单机冒烟 45s（击杀 72 / 波次 4 / 等级 4 / 升级选卡 3 次）；联机双开 60s；静态审计 ERROR 0 / WARN 0。

### M19 防沉迷无头实测：三处「限时形同虚设」的绕过缺陷（本次新增）

补齐最后一块**完全没有实测覆盖的合规模块**。防沉迷（`Core/PlaytimeGuard.cs`）是个纯时间驱动的
状态机——真实等 1 小时去验证不现实，于是它一直靠读代码推断。而"没测过的合规功能"是最危险的：
只要一个分支写错，限时就是摆设，且没有任何人会发现。

**怎么几秒钟跑完一天**：把计时判定收敛到唯一入口 `Tick(dt)`——线上由 `Update` 喂真实 `deltaTime`，
探针以 60 秒固定步长喂**同一个入口**。于是验证的是产品代码本身，而不是照抄一份判定逻辑的副本
（后者只能证明副本对，证明不了产品对）。

**修掉的三处缺陷（都是"限时能被完整绕过"，不是文案或显示问题）**：

| # | 旧实现 | 后果 |
| --- | --- | --- |
| ① | `Update` 开头 `if (_hintShowing) return;` | **提示弹窗挂起时停止计时**——玩家把 55 分钟的预警晾在那儿不点「关闭」，时长永远停在 55 分钟，到不了上限，也永远不会被踢 |
| ② | 强制退出靠一次性标记 `_minorForcedExit`，只在跨天/重置时清零 | **每天只踢一次**——被踢回主菜单后再点「开始游戏」照样能建世界继续玩 |
| ③ | 强制退出不关残留弹窗 | 主菜单被遮罩盖住；且 `_hintShowing` 永久为真，下一局**完全不计时**（①②的放大版） |

修法：① 计时与弹窗状态彻底解耦，弹窗只负责"不重复弹"、绝不负责"暂停计时"；
② 把判定前移到开局入口 `GameBuilder.Build()`（`CanStartRun()`），只要当日累计已达上限
就不允许再开局，跨天由 `RollDateIfNeeded()` 自动解禁；③ 强制退出/重置计时时先 `CloseHint()`。
顺带两处小修：跨天判定移出对局分支（在菜单里过零点也该重置）、`warned` 落盘（重启进程不再重复弹预警）。

**判据的区分力**：每条断言都指向"修复前必然失败"的可观测量——不关弹窗时的累计秒数、
强制退出次数、世界是否真被拆、二次开局的拦截计数、弹窗挂起期间的时长增量。
只断言"没崩溃"的话，一个压根不执行限时的实现也能全绿。
反向也断言了"54 分钟前不得干预、跨天后必须能重开"——双向都成立，
"限时生效"才不是靠"干脆永远不让玩"蒙出来的。

```bash
python playtime_probe.py              # MINOR + ADULT 两个场景（约 20 秒）
python playtime_probe.py --only MINOR  # 只跑青少年模式
```

| 场景 | 覆盖路径 |
| --- | --- |
| MINOR | 54 分钟世界照常 → 55 分钟弹预警 → **故意不点关闭**继续推进 → 到点强制退出 → 回菜单再开一局 → 跨天重置 → 中英提示文案 |
| ADULT | 45 分钟弹健康提示 → **同样不点关闭**继续推进 → 关掉后再推进一个间隔 → 时长落盘/读档往返 |

**实测**：MINOR——54 分钟世界存活、预警 0 次；55 分钟预警 1 次且弹窗挂起；
不关弹窗累计 **3601s**（≥ 上限 3600）；强制退出 1 次、对局已停、世界已拆；
二次开局**被拦**（拦截计数 1）；跨天后累计 0 且能重开。ADULT——45 分钟提示 1 次；
挂起期间增量 **600s**；第二次提示 2 次；读档前后 6001→6001。两场景运行期异常均 **0**。

> **区分力验证（照例必做）**：把 ① 的 `if (_hintRoot != null) return;` 加回去、
> 并把 ② 的入口拦截短路掉，重建后重跑——探针精确报出
> 「不关弹窗时累计仅 3301s（上限 3600s）——弹窗挂起期间时长被冻结」
> 「二次开局未被拦下（拦截计数 0）——限时被完整绕过」
> 「提示挂起期间时长增量为 0s」，随后还原重建，全绿。证明判据真的会失败，而不是恒真。

新增/改动：`Core/PlaytimeGuard.cs`（Tick 单一入口 + `CanStartRun` + 4 个计数器）、
`GameBuilder.cs`（入口拦截 + `-playtimeProbe`）、`UI/MainMenu.cs`（`NoticeText`）、
`UI/GameOverPanel.cs`（`Hide`）、`Dev/PlaytimeProbe.cs`、`../playtime_probe.py`。

回归（异常均 0）：单机冒烟 45s（击杀 72 / 波次 4 / 等级 4 / 选卡 3 次）、
云存档 5 场景 + UI 探针（A~D 请求 0、E 请求 3、裸键无、英文界面中文残留无）、
联机双开 60s（波次 7/6、击杀 112/79）、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M20 持久化层无头实测：三处「静默作废」的数据缺陷（本次新增）

M19 之后，纯本机模块里只剩两个零实测覆盖：**本地存档 `SaveManager`** 与 **埋点 `Telemetry`**。
它们的共同点是——出问题**不会报错、不会崩溃、玩法照常**：
埋点文件写成非法 JSON 时采集等于白采集；存档读档丢字段时，只有玩家某天发现纪录没了才知道。

| # | 缺陷 | 后果 |
| --- | --- | --- |
| ① | `Telemetry.Log` 的字符串值只把 `"` 换成 `'`，**没有完整 JSON 转义** | 值里一旦出现反斜杠（`C:\Games`）、换行或制表符，产出的就是非法 JSON；**整个 JSONL 文件从此无法解析** |
| ② | `Flush()` 先 `Queue.Clear()` 再写文件 | 写失败（磁盘满/权限/目录被占）时批次已从队列移除，事件**永久丢失且无重试** |
| ③ | 关闭「匿名数据统计」后队列残留 | 开关前缓存的事件仍会在下次 `Flush` 落盘——违背"关闭即停止采集"的隐私承诺 |

修法：① 新增 `Escape()`（引号/反斜杠/换行/回车/制表/退格/换页 + 控制字符 `\uXXXX`，带无特殊字符的快路径）；
② 写失败时把批次 `InsertRange(0, …)` 放回队首，下次 `Tick`/退出重试，队列设 5000 上限防无限堆积，
并暴露 `FlushFailCount` 让失败**可观测**；③ `SetTelemetry(false)` 调用 `DiscardQueue()`。

```bash
python persistence_probe.py               # SAVE + TELEMETRY 两个场景（约 20 秒）
python persistence_probe.py --only SAVE    # 只跑存档
```

| 场景 | 覆盖路径 |
| --- | --- |
| SAVE | 强局刷新三项纪录 → 弱局不刷纪录但 `last*`/累计要更新 → 图鉴去重 + Boss 计数 → 落盘后**真实 `Load()`** 读回逐字段比对 → 写入非法 JSON 后 `Load()` 应回退空档 |
| TELEMETRY | 事件进队列 → 落盘 → **Python `json.loads` 逐行独立校验** + 特殊字符串精确往返 → 制造真实写失败（目录名与文件同名）→ 批次回队列 → 恢复后重试成功 → 关开关清队列且被丢弃事件**不得出现在文件里** |

**判据的区分力**：JSONL 合法性**不由被测进程自判**——探针只负责制造含脏字符的事件，
由驱动脚本用 `json.loads` 独立解析并断言 `路径C:\Games\星界"引号"\n换行\t制表` **精确往返**；
落盘失败用"目录与确定存在的文件同名"真实制造（并先断言靶子确实是文件，碰撞不落空）。

> **区分力验证**：把三处修复分别改回旧实现（`Replace("\"", "'")` / 去掉回队列 / 去掉清队列），
> 重建后重跑，探针精确报出
> 「第 1 行非法 JSON（Invalid \escape: line 1 column 50）」、
> 「「失败批次回队列」为 否」、「重试事件只出现 0 条」、
> 「「关开关即清队列」为 否」、「被丢弃的事件 probe_should_discard 出现了 2 次」；
> 还原重建后全绿。

**实测**（异常均 0）：SAVE 九项判据全「是」；TELEMETRY 失败次数 1、批次回队列、重试后 0 队列、
关开关队列归零；落盘文件 5 行、非法行 **0**、标记字符串往返 **是**、重试事件 2 条、丢弃泄漏 **0**。

> **脚本自身的两个坑（本轮踩到并修掉）**：
> ① `--only TELEMETRY` 时 `save.json` 已被备份挪走，用它当碰撞靶子反而让 `CreateDirectory` **成功**——
> 改为探针自带靶子文件并先断言它确实是文件；
> ② 恢复时 `os.rename` 撞上探针误建的**同名目录**会抛 `FileExistsError`——改为先删同名物（文件或目录）再改名。
> 另：脚本跑前备份 `save.json`/`settings.json`/当日 telemetry，跑后校验并**原样恢复**。

**顺带修掉审计工具的一次误报**：`audit_project.py` 的括号检查把 `Telemetry.cs` 判成不平衡。
根因是 `strip_noise` 用 `'(?:\\.|[^'\\])*'` 匹配 char 字面量，遇到 `'\\'`（转义反斜杠）时
把反斜杠当成"转义了撇号"，字符串从字面量中部开始吞；更隐蔽的是**顺序问题**——
`case '"':` 里的双引号会先被字符串正则当成某个字符串的结束引号。
现改为手写扫描 char 字面量并**先剥 char、再剥字符串**，四项区分力自检全部符合预期
（真实代码 0 / 删一个 `}` 得 1 / 注释内 `{` 不算 / 字符串内 `'` 与 `{` 不算）。

新增/改动：`Core/Telemetry.cs`、`Core/SettingsManager.cs`、`Dev/PersistenceProbe.cs`、
`GameBuilder.cs`（`-persistenceProbe`）、`../persistence_probe.py`、`../audit_project.py`。

回归（异常均 0）：单机冒烟 45s（击杀 70 / 波次 4 / 等级 6）、
防沉迷 MINOR + ADULT 全绿、云存档 5 场景 + UI 探针（A~D 请求 0、E 请求 3、裸键无、中文残留无）、
联机双开 60s（波次 7/6、击杀 126/84）、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M21 合规门无头实测：同意后云存档「只推不拉」会用本地旧档覆盖云端（本次新增）

M19、M20 之后只剩**合规门 `ComplianceGate` 从未被执行过一次**——所有无头入口都直接调
`AgreeTerms()` 绕过它，所以此前任何实测都没真正见过这块 UI。合规功能的失效是静默的：
门没弹出来，游戏一切正常，只是"用户其实从没同意过协议"。

| # | 缺陷 | 后果 |
| --- | --- | --- |
| ① | `CloudSaveService` 的首次拉取只在 `Start()` 做一次，而首启/协议版本升级后重新同意时 `Enabled` 为 false——那次拉取**永久错过**，此后 `Update()` 只管上行 | 本会话变成**只推不拉**：玩一局后本地档上传，**云端更新的存档被静默覆盖**（玩家在另一台设备玩过就会丢进度）。不报错、不崩溃 |
| ② | `Start()` 未启用时写死 `SetStatus(StatusOff)`，之后 `Enabled` 变 true 也不刷新 | 界面显示"未启用"而服务其实已在上传 |

修法：① `Agree()` 调用新增 `CloudSaveService.OnConsentGranted()`（已启用且本会话未拉取过则补一次，幂等）；
② 顺带对齐状态文案。

```bash
python compliance_probe.py               # 6 个场景（约 2 分钟）
python compliance_probe.py --only BUMP    # 只跑「协议升级必须重新征得同意」
```

| 场景 | 覆盖路径 |
| --- | --- |
| FIRST | 首启弹门 → 三份法律文本齐全 → 点同意 → **同意态真实 `Load()` 读回校验** → 主菜单解锁；同意前菜单必须隐藏 |
| AGREED | 已同意当前版本：门不重复弹 |
| BUMP | 已同意**旧**版本 + 文本已升级：**门必须重新弹出**（协议更新后重新征得同意，本模块最关键的一条） |
| DECLINE | 点不同意 → 走到退出 + 同意态**不得**写入 + 主菜单不解锁 |
| CONSENT_CLOUD | 同意前云存档零请求；同意后必须**发起拉取**（防本地档覆盖云端） |
| L10N | 英文界面下合规门文案无裸键、无中文残留 |

**判据区分力**：① 同意态落盘由探针调真实 `SettingsManager.Load()` 读回比对，不查内存字段
（只查内存的话，一个根本不保存的实现也能全绿）；②「门弹了没有」(`Shown`) 与「点了没有」(`AgreeCount`)
分开计数，才能区分"门压根没弹"和"按钮点不动"；③ CONSENT_CLOUD 用**拉取次数**而非笼统的"联网了没"
判定——只断言请求数 >0 的话，一次都不拉、直接上传覆盖云端的实现照样能过。

> **区分力验证**：只把 `OnConsentGranted()` 那一行还原为修复前重建重跑，探针精确报出
> 「同意后拉取次数 0」，**其余 5 个场景照常通过**；还原后 6 个场景全绿。

**实测**（异常均 0）：FIRST 全「是」、AGREED 不重复弹门、BUMP 重新弹门、DECLINE 走到退出且未写入、
CONSENT_CLOUD 同意前请求 0 / 同意后拉取 1、L10N 文案 9 条 / 裸键无 / 中文残留无。

> **踩坑**：① 探针最初「先 `AddComponent`、再赋值 `_scene`」——**`AddComponent` 会同步执行 `Awake`**，
> 于是状态永远走默认分支，一轮跑出 4 处假失败；改为先 `SetActive(false)` 再激活。
> ② 场景间共用 `settings.json`，DECLINE 的「同意态未写入」曾**因为前一个场景留下的高版本号而碰巧成立**
> （假通过）——现每个场景前清空落盘文件，让判据建立在已知初始态上。

新增/改动：`Compliance/ComplianceGate.cs`（计数器 / `QuitAction` / 注入点击）、`Core/VersionInfo.cs`
（`ComplianceVersion` 可覆盖）、`Core/CloudSave/CloudSaveService.cs`（`PullCount` / `OnConsentGranted`）、
`UI/MainMenu.cs`（`IsVisible`）、`Dev/ComplianceProbe.cs`、`GameBuilder.cs`、`../compliance_probe.py`。

回归（异常均 0）：单机冒烟 45s（击杀 69 / 波次 4 / 等级 5）、防沉迷全绿、云存档 5 场景 + UI 探针、
持久化 SAVE + TELEMETRY、联机双开 60s（波次 7/6、击杀 130/101、Boss 是/是）、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M22 设置面板无头实测：损坏的设置被静默清零 / 切语言被踢出面板（本次新增）

M18 的 UI 探针已经把设置面板在英文下建出来过、也真点过一次云存档开关——但那只证明
**面板长得对、一条开关接对了**。另外 7 个控件的回调、改完是否落盘、重启是否还在、文件坏了怎么办，
全都从未验证过。这四件事坏起来都是**静默**的：不崩溃、不报错，只是"我调好的设置又没了"。

| # | 缺陷 | 后果 |
| --- | --- | --- |
| ① | `SettingsManager.Load()` 读档失败时只打一行 warning 就换成默认值，**坏文件留在原路径** | 下一次任何 `Save()` 都把默认值写回去——用户的音量/画质/语言/**已同意协议版本**在没有任何提示的情况下清零，且没有副本可找回（可能只是掉电写坏了一个字段） |
| ② | `SettingsPanel.Rebuild()` 只 `Build()` 不 `Show()` | 设置页点一下「语言」，面板当场消失（连带回主菜单），想再调一次得重新点「设置」 |
| ③ | 语言按钮回调里不刷新自己的文字 | 从英文切回中文时按钮仍写「Language：English」，点下去界面变中文——按钮说的和做的不一致 |

修法：① 读档失败先把文件挪成 `settings.json.corrupt-<时间戳>`（撞名带序号）再回退默认值；
② `Rebuild()` 记住重建前是否可见，可见则补一次 `Show()`；③ 与画质按钮同法，回调内同步刷新文字。

```bash
python settings_probe.py                  # 3 个场景（约 40 秒）
python settings_probe.py --only NEGATIVE   # 只跑「设置文件损坏」
```

| 场景 | 覆盖路径 |
| --- | --- |
| TOGGLES | 逐个点击 10 次（全屏 / 震屏 / 统计 / 云存档 / 青少年开·关 / 画质循环 ×3 / 语言循环 ×1），每次同时断言**内存值变了 + 面板文字跟进了 + （画质额外）引擎档位也对**；点完再从磁盘读回 8 个字段，必须 8/8 与内存一致；并断言切语言后面板仍可见 |
| ROUNDTRIP | 11 个字段经公开 Setter 写入 → 落盘 → **全新实例**读回逐字段比对（11/11）；同时断言 `AudioListener.volume` / `QualitySettings` / `Screen.fullScreen` / `Localization.Language` 及限时·采集·云存档·震屏四个玩法入口都真的收到了值 |
| NEGATIVE | 设置文件写成半截 JSON：不得崩溃、必须回退默认值（且**不是**坏文件里能读到的 0.123），损坏文件必须被归档，恢复后仍能正常落盘 |

**判据区分力**：①「改了没生效」与「生效了没落盘」分开断言——后者走真实 `Load()` 从磁盘读回，
只查内存的话，一个压根不保存的实现也能全绿；② 画质查 `QualitySettings`、音量查 `AudioListener`，
只查 `SettingsManager.Current` 证明不了"引擎收到过"；③ NEGATIVE 的核心判据在**进程外**（脚本自己列目录、
自己判归档内容），由被测进程自述"我备份了"很容易是假的兜底；④ 每场景前清空落盘文件，让断言建立在已知初始态上。

> **区分力验证**：回退「语言按钮刷新文字」重建重跑 → 探针精确报出 **9/10**，唯一失败项正是
> 「语言 · 文字=未跟进」；回退「归档」→ 只有 NEGATIVE 的「已归档=否」变红。
> 而 ②（`Rebuild()` 补 `Show()`）**没能**用回退复现证明——`Build()` 每次都新建画布，回退后新画布仍是 active，
> 探针看不出差别。故这一处只按代码推断 + 断言覆盖，未计为"实测复现"。

**实测**（异常均 0）：TOGGLES 点击 10 / 生效 10/10 / 落盘 8/8 / 切语言后仍可见 是；
ROUNDTRIP 往返 11/11、引擎侧 4 项与玩法侧 4 项全「是」；NEGATIVE 未崩溃 / 回退默认值 / 已归档 全「是」。

> **踩坑**：① `SettingsManager` 自带 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`——
> 探针协程还没跑，它就已经 `Awake` 过、`Load` 过一次、把坏文件挪走并重建了默认值。
> 第一版探针"先抓原文再判归档"，抓到的其实是**重建后的默认值**，归档完好却断言失败。
> ② `var snapshot = SettingsManager.Instance.Current; s.Load();` —— `Load()` 内部是 `Current = loaded`，
> 快照与 `Current` 指向同一对象，"内存 vs 磁盘"的比对**永远相等**（假通过）。必须显式声明成
> `SettingsManager.Data snapshot`。③ 读文件要按字节读：`File.ReadAllText` 再编码回去会因宽松解码
> 让"逐字节相同"这一判据凭空失败。

新增/改动：`Core/SettingsManager.cs`（`Quarantine()`）、`UI/SettingsPanel.cs`（`Rebuild` 补 `Show`、语言文字刷新）、
`Dev/SettingsProbe.cs`、`GameBuilder.cs`（`-settingsProbe`）、`../settings_probe.py`。

回归（异常均 0）：单机冒烟 45s（击杀 72 / 波次 5 / 等级 5）、合规门 6 场景（同意前请求 0 / 同意后拉取 1）、
防沉迷 MINOR + ADULT、持久化 SAVE + TELEMETRY、云存档 5 场景 + UI 探针、
联机双开 60s（波次 6/5、击杀 94/73、Boss 是/是）、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M23 选角 → 开局 → 成长 整条链路无头实测：修掉换角色时数值叠乘（本次新增）

到 M22 为止实测过的是「启动 → 主菜单 → 设置」和战斗中的若干单点。**中间这段——点了「角色」、
选了某个守卫、然后真的开一局——从没被任何探针驱动过**。它跨进程、跨场景、跨存档，断在哪一环，
现象都一样：游戏照常跑，只是你选的角色没生效。不崩、不报错、日志干净。

| # | 缺陷 | 后果 |
| --- | --- | --- |
| ① | `PlayerController.ApplyCharacter()` 用 `stats.maxHp *= def.hpMult` 在当前值上连乘 | 调用两次就把守卫的 `1.35` 变成 `1.8225`：生命上限凭空多 35%。开局应用一次 + 局内换人/重生再应用一次是普通路径，且**单向累积不可逆**，平衡数值表被静默污染 |
| ② | `net_smoke.py` 的 `DEFAULT_EXE` 指向单机产物 `Build/Smoke` | 「联机双开冒烟」实际跑的是两个单机客户端：双端正常启动、跑满时长、**异常 0**，只是网络玩家数恒 0、同步波次恒 -1。看起来像同步链路坏了，其实是联机版本压根没被运行过 |

修法：① 首次应用时 `stats.Clone()` 快照基准值，之后**永远从基准值重算**（不是每次重取默认值——
那会把局内属性强化清零）；② `DEFAULT_EXE` 优先选 `SmokeNet`，且显式传单机产物时**直接拒绝运行并返回退出码 2**。

```bash
python build_probe.py                    # 3 个场景（约 40 秒）
python build_probe.py --only SELECT        # 只跑「选角落盘」
python build_probe.py --only APPLY         # 自动补跑 SELECT（APPLY 依赖它的存档）
```

| 场景 | 覆盖路径 |
| --- | --- |
| SELECT | 点「角色」→ 点「选择」，**四处同时**断言：内存 `SelectedId==1` / 卡片 `◉`+「已选择」/ 主菜单回显 / 磁盘 `lastCharacterId==1`；另断言选角**不计入 `characterRuns`**（不能"看一眼列表就算玩过"）、关闭按钮有效 |
| APPLY | 跨进程读回角色后真实 `GameBuilder.Build()`，**7 个乘区逐个**比对 `110*hpMult` / `5.6*speedMult` / … / `1*xpMult`；起始武器被替换成 id=4；重复应用幂等；**换角色不叠乘**（守卫→游侠→守卫必须回到首次守卫的值，而不是 1.35 倍） |
| PROGRESSION | 对 `AllWeaponIds` 逐把：清空武器 → `AddSkill` → 放 50 万血目标 → 等 3.4s 量伤害（目标位置按武器分：雷环刃 2.0 / 地雷 0.5 / 其余 3.0）；4 条组合技各断言"只触发应触发的那条"；7 项属性强化；等级封顶与满级不再供选 |

**判据区分力**：①「内存变了」「面板看见了」「磁盘有了」「新进程生效了」拆成四个**独立**断言——
缺陷①让"首次应用"完全正确，只在第二次错，合成一个布尔只会得到"角色系统不行"；
② 磁盘判据的最终裁在**进程外**（`build_probe.py` 自己 `json.loads`），不看探针自述；
③ 每场景前清空存档，**但 APPLY 例外**（它读的正是 SELECT 写下的那份）。

> **区分力验证（做了两次）**：回退 `ApplyCharacter` 的基准值重算 → 只有「换角色不叠乘=否」和
> 「换角色差异：**实际 148.5 期望 110**」变红（`148.5 = 110×1.35`，判据自己指出了病因），其余全绿；
> 去掉 `SelectedId` setter 里的 `save.Save()` → SELECT 精确报「落盘=**否**」，进程外报「落盘 实际 None」，
> APPLY 整组随之变红（跨进程读回 -1）。

**实测**（异常均 0）：SELECT `id=1 卡片=是 菜单=是 落盘=是`；APPLY 乘区 7/7、差异 无、起始武器 id=4、
幂等 是、不叠乘 是；PROGRESSION 开火 10/10、伤害 10/10、组合技 4/4、属性 7/7、封顶 是、满级不再供选 是。

> **踩坑**：① `GetInstanceID()` 在 `SetActive(true)` 之后不保证立刻有值（返回 0），用它对敌人列表
> 做前后差集会**匹配到已存在的对象**——雷环刃看起来"打 0 伤害"，实际是打错了目标。改用列表长度差定位。
> ② 每把武器测完必须 `RemoveEnemy` + 两次 `yield return null`，否则残留目标会抢走下一把武器的"最近目标"。
> ③ 报 0 伤害先怀疑探针站位：雷环刃只打半径 2.0 的环、地雷要踩进 1.6，统一放在 3.0 米外会让三把武器全像坏的。
> ④ 联机构建方法名是 `HeadlessNetBuild`，写成 `HeadlessBuildNet` 会 `executeMethod could not be found` 并 exit 1。

> **一个观察，不算缺陷**：组合技同时满足多条时实测 `火+风暴+圣光 伤害倍率=1.4`——`ComboManager` 是
> **后写覆盖**而非累加（累加应是 1.9）。"三条同时满足不如两条"要么是有意防叠满、要么是没想清楚，
> 本轮只记录不改：没有设计意图依据就动平衡，比留一个已知语义更糟。

新增/改动：`Player/PlayerController.cs`（基准值快照 + 从基准重算）、`Player/PlayerStats.cs`（`Clone()`）、
`Enemy/EnemyManager.cs`（`FreezeWaves` 探针开关）、`Dev/BuildProbe.cs`、`GameBuilder.cs`（`-buildProbe`）、
`../build_probe.py`、`../net_smoke.py`（产物路径校验）。

回归（异常均 0）：单机冒烟 45s、合规门 6 场景（同意前 0 / 同意后 1）、防沉迷 MINOR + ADULT、
持久化 SAVE + TELEMETRY、设置 M22 六场景、云存档 5 场景 + UI、**联机双开 60s（玩家 1/2、峰值 2/2、
波次 7/6、击杀 140/104、双端 Boss 是）**、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M24 掉落/磁吸/结算 与重开局清理无头实测：修掉重开一局后拿不到新玩家、上一局实体整批残留（本次新增）

到 M23 为止实测的是「启动→主菜单→设置」「选角→开局」「升级→组合技」。
**掉落闭环**与**重开局**这两条路从没被真正驱动过——前者悄悄不对，后者是**点了按钮就没了**。

| # | 缺陷 | 后果 |
| --- | --- | --- |
| ① | `PlayerController.Instance` 是静态字段且从不清理；「重开一局」是 `TeardownWorld(); Build();` 紧挨两行，而 `Object.Destroy` 延迟到帧末 | 新玩家的 `Awake` 看到 Instance 还指着"活得好好儿的"旧玩家 → 选择不注册；帧末旧玩家一销毁，Instance 成悬空引用。**点了「重开一局」后移动/开火/HUD 全线静默空转**，不崩不报错 |
| ② | `TeardownWorld` 只销毁 `Track` 进去的管理器；宝石/回血球/敌人都是 `PoolManager.Get(模板)` 的克隆体，挂场景根节点，**既不在 `_created` 里也不是任何管理器的子物体** | 上一局实体整批漏到新世界：**看得见却捡不起来的宝石**、攥着旧 `_playerTransform` 围着旧坐标转的幽灵敌人（打不到也清不掉）。而 `PickupManager.ResetAll`/`EnemyManager.ResetAll` **写好了却全仓库零 caller** |
| ③ | `net_smoke.py` 默认指向单机产物 `Build/Smoke` | 「联机双开冒烟」实际跑两个单机客户端：双端正常启动、跑满 60s、**异常 0**，只是网络玩家数恒 0、同步波次恒 -1——看起来像同步坏了，实际联机版压根没被运行过 |

修法：① `PlayerController` 实现 `Core.ISingletonInstance`，走 `TeardownWorld` 已有的清理通道在新世界建立**之前**摘引用（刻意不改 `Awake` 的"首个创建者胜出"——联机下那会让 Instance 指到远程玩家）；
② `PoolManager` 新增 `_owned` + `DestroyAll()`，两个 `ResetAll` 从"只停用"改成真销毁，`TeardownWorld` 里三行接上（`ResetAll` 管在场上的、`DestroyAll` 管已归还队列的，缺一不可）；③ 见 M23。

```bash
python pickup_probe.py                    # 5 个场景（约 17 秒）
python pickup_probe.py --only LEFTOVER     # 只跑「重开局实体残留」
```

| 场景 | 覆盖路径 |
| --- | --- |
| MAGNET | 半径外（默认 1.6）放在 6 米处必须**一动不动且经验不入账**；改成 12 后必须回到对象池且 3 点经验精确入账（`XpRatio` 恰为 0.600）——**两态对比**，只测单边的话"永远吸附/永远不吸附"各能骗过一半 |
| CREDIT | `xpGainMult=2` 投入两颗 1 点宝石 → `XpRatio` 必须依次 **0.40/0.80**；回血精确 **+30**；999 点回血必须**恰好封顶在 maxHp**（不得溢出） |
| LEFTOVER | 先在远处造出上一局残留（3 宝石+1 回血球+2 敌人），**先断言确实造出了 6 个**再走真实重开路径 `TeardownWorld(); Build();`，断言残留为 0 并单报「宝石/回血/敌人」构成 |
| RESTART | 重开后 ①拿得到玩家 ②`InstanceID` 必须**不等于**上一局那个 ③三个管理器都接上 ④**新世界掉落闭环真的通**（放颗宝石看会不会被吸走并精确入账）——引用存在 ≠ 世界接上了 |
| REPEAT | 连续 3 次重开，逐轮打印「放 N → 残留 M」；泄漏表现为单调增长，比单个布尔诚实 |

**判据区分力**：①计数一律 `Resources.FindObjectsOfTypeAll` 扫场景并**排除模板本体**，不读管理器内部账本；②"重开前存量=6"是 LEFTOVER 的前置断言——否则"残留=0"可能只是压根没造出来（假通过）；③ Binding 到那颗具体宝石（判"变停用"=归还对象池），不能判"场上还有没有宝石"。

> **区分力验证（两次，变红项互不相交）**：回退 `ClearInstance` → **只有 RESTART 变红**（1/3：拿到玩家=否/是新世界玩家=否/掉落闭环=否），其余四个场景全绿；
> 回退 `TeardownWorld` 的三行实体清理 → **只有 LEFTOVER（残留 6：宝石3 回血1 敌人2）与 REPEAT（0/3，三轮各残留 4）变红**，MAGNET/CREDIT/RESTART 全绿。

**实测**（异常均 0）：MAGNET 断点 2/2（外 6.00→6.00、经验比 0.600）；CREDIT 入账 4/4（经验 0.40/0.80、回血 110.0/110.0）；
LEFTOVER 重开前 6 → 重开后 **0**；RESTART **4/4**（旧 id=-134 / 新 id=-498 / 掉落闭环=是）；REPEAT **3/3**（三轮各 放4→残留0）。

> **踩坑（都不是产品缺陷，但会让结果假红）**：
> ① **`Object.Destroy` 延迟到帧末**——`TeardownWorld()` 后立刻数对象，数到的还是待销毁那批，会把正常清理报成残留（REPEAT 第一版三轮全"放4→残留4"，而中间有 yield 的 LEFTOVER 却正常）。**数之前必须让帧走完。**
> ② 计数要绑**那颗具体宝石**，判"场上还有没有宝石"会被建筑期杂散掉落骗过去。
> ③ **期望值要钉死前置条件**：默认角色游侠自带 `xpMult=1.15`，3 点宝石实到 **3.45** → `XpRatio=0.69` 而非 0.6，那是**产品正确**，我却当成 bug 查了一轮；现已在 MAGNET/RESTART 显式先把 `xpGainMult` 置 1，倍率交给 CREDIT 单测。
> ④ SMOKE 键名必须与驱动逐字一致（改了「吸走并入账」→「回收并入账」忘了同步驱动，那一栏显示 `?` 被判失败）。

新增/改动：`Core/PoolManager.cs`（`_owned` + `DestroyAll`）、`Pickups/PickupManager.cs` 与 `Enemy/EnemyManager.cs`（`ResetAll` 改为真销毁）、
`Player/PlayerController.cs`（实现 `ISingletonInstance`）、`GameBuilder.cs`（`TeardownWorld` 接 `-pickupProbe`）、
`Dev/PickupProbe.cs`、`../pickup_probe.py`、`../net_smoke.py`（产物路径校验，M23）。

回归（异常均 0）：**M24 五场景**、单机冒烟 45s（击杀 70/波次 4/等级 5/选卡 4）、M23 三场景、合规门 6 场景、
防沉迷 MINOR+ADULT、持久化 SAVE+TELEMETRY、设置 M22 六场景、云存档 5 场景+UI、
**联机双开 60s（玩家 1/2、峰值 2/2、波次 7/6、击杀 141/105、双端 Boss 是）**、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M25 手感层无头实测：修掉「相机基准被自己拉塌」——24 轮全绿，但没人看过画面（本次新增）

前面的里程碑**全部用 `-nographics` 的无头产物验收**。这套方法一路抓出了静默数据错误、悬空引用、
实体残留、跑错产物等真缺陷，但它有结构性盲区：**只能看见"代码跑没跑通"，看不见"玩家看到了什么"**。
手感层（`Core/CameraShake.cs` + `Core/HitstopManager.cs`）恰好是唯一一个**产出全在画面上**的模块。

| # | 缺陷 | 后果 |
| --- | --- | --- |
| ① | `CameraShake._baseOrtho` 只在 `Configure()` 里记，而 `Configure()` 要等开局才被调用；菜单停留期里 `LateUpdate` 已在把 `orthographicSize` 朝其**初值 0** 拉 | 每帧 `Lerp(dt*9)`、总衰减 `exp(-9T)`，**与帧率无关**：主菜单停 1 秒就只剩千分之一。开局时 `Configure` 读到这个被压过的值**并记成基准** → 整局镜头无限推近，画面糊成几个像素。**严重。** 实测菜单期 8.00000 → **0.00000**，开局后仍是 0.00000 |
| ② | `Configure()` 无条件改写 `_baseOrtho`，缩放脉冲进行中也不避让 | 推近中读到的是被压过的**临时值**，记成基准后镜头永久推近且**逐次叠乘**（Boss 出场推近有 0.55s 窗口，期间有人加入/重连就命中）。实测一次中途 `Configure`：8.00000 → **6.07698** |
| ③ | 震屏衰减 `_remaining -= Time.deltaTime`（**缩放**时间），同函数里 `_zoomTimer` 却用 `unscaledDeltaTime` | `timeScale=0`（选卡/结算暂停）时 `deltaTime` 恒 0，`_remaining` 永不减少而 `LateUpdate` 照跑、每帧**重新随机偏移** → **选卡面板背后的世界一直以全幅度抽搐**。实测暂停 1s 后仍在抖 **0.46128** |
| ④ | `ZoomPulse()`（Boss 出场推近）不看「屏幕震动」开关，而 `Shake()` 与 `HitstopManager.Pulse()` 都看 | 关掉开关后震屏和顿帧都停了、**推近照旧**——用户以为关的是"整体打击反馈"（`HitstopManager` 注释里就是这么定义的），实际只关了一半。实测关掉后仍 8.00000 → **5.88003** |

```bash
python feel_probe.py                    # 6 个场景（约 3 分钟）
python feel_probe.py --only STAGE       # 只跑「相机基准不被自己拉塌」
```

| 场景 | 覆盖路径 |
| --- | --- |
| STAGE | **真实启动路径**：`PrepareStage` → 菜单停留 1.5s → `Build()`，两段都必须仍在基准 8 附近 |
| ZOOM | 推近必须生效（8.0→5.96）、必须回弹、**推近进行中被 `Configure` 不得改小基准** |
| HITSTOP | 受伤事件压到 0.180 / 自定义顿帧压到 0.040 / **真实时长必须≈0.30s** / 结束后归 1 |
| PAUSE | 选卡暂停四条：归零 / 暂停中的顿帧被忽略 / 放开归 1 / **暂停必须清掉在飞的顿帧** |
| SHAKE | 震屏偏得出去、静置衰减完、**暂停中也必须在真实时间里衰减完** |
| GATE | 关掉「屏幕震动」后顿帧 / 震屏 / 推近**三条通道全不发生** |

**判据区分力**：①缩放采信 `Camera.orthographicSize` 实测值并在驱动里**进程外**复算，不采信被测对象的账本；
②顿帧时长用**墙钟**度量，并故意挑 `0.30/0.04` 这组参数——计时若误用缩放 `deltaTime`，一次顿帧会被**自己的**
`timeScale` 拖成 ≈7.5s，立刻爆红而不是"好像慢了一点"；③震屏取**采样窗口最大偏移**（偏移每帧重随机，
取瞬时值会时红时绿）；④每个"必须发生"都配一个"必须结束"的对偶判据。

> **区分力验证（四次，变红项互不相交）**：回退 ①→**仅 STAGE 全红 + ZOOM「静置基准」**（ZOOM 其余三项仍绿）；
> 回退 ②→**仅 ZOOM「基准不被改小」**（STAGE 全绿）；回退 ③→**仅 SHAKE「暂停中衰减」**；
> 回退 ④→**仅 GATE「推近被关」**。说明这 23 条判据不是"一个总闸"，每条都真钉在自己的那个行为上。

**实测**（异常均 0）：STAGE **2/2**（8.00000/8.00000）；ZOOM **4/4**（8.00000→5.95541→7.99998→7.99997）；
HITSTOP **4/4**（0.180 / 0.040 / **0.300s** / 1.000）；PAUSE **4/4**（0.000/0.000/1.000/1.000）；
SHAKE **4/4**（0.30231→0→0→0）；GATE **3/3**（1.000 / 0.00000 / 8.00000→8.00000）。
HITSTOP 与 PAUSE 在修复前就全绿——**这同样是有价值的结论**：那套 `_paused > _hitstopTimer > 1` 的优先级本来就写对了，
但此前无一处断言过它，"对"只是运气；现在它被钉住了。

> **踩坑**：① **`WaitForSeconds` 受 `timeScale` 影响**——暂停态（`timeScale=0`）下它永不返回、探针直接挂死，
> 所有与暂停相关的等待都必须自己累加 `unscaledDeltaTime`（本轮最大陷阱）；② 震屏幅度必须取采样窗口最大值，不能取瞬时值；
> ③ **缺陷 ① 会让 ZOOM 的「推近生效」假绿**——基准已被拉到 0，`_zoomPunched = 0 × 0.72 = 0` 同样满足"小于基准×0.95"，
> 同一个缺陷把两个方向都糊住了，故 ZOOM 必须另有一条**绝对基准判据**（≈8），不能只做相对比较；
> ④ 正交尺寸的塌缩与帧率无关（总衰减 `exp(-9T)`），只看真实停留秒数——这让该缺陷可被稳定复现。

新增/改动：`Core/CameraShake.cs`（`Awake` 当场记基准 / `Configure` 脉冲中不改写 / 震屏改 `unscaledDeltaTime` /
`ZoomPulse` 尊重开关）、`GameBuilder.cs`（接 `-feelProbe`）、`Dev/FeelProbe.cs`、`../feel_probe.py`。

回归（异常均 0）：**M25 六场景**、单机冒烟 45s（击杀 51/波次 4/等级 3/选卡 2）、M24 五场景、M23 三场景、合规门 6 场景、
防沉迷 MINOR+ADULT、持久化 SAVE+TELEMETRY、设置 M22 六场景、云存档 5 场景+UI、
**联机双开 60s（玩家 1/2、峰值 2/2、波次 6/5、击杀 103/73、双端 Boss 是）**、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M26 可见层第二批无头实测：前 4 个模块本来就对，第 5 个抓出「上一局的警示圈漏进新世界」（本次新增）

承接 M25 的教训：**无头实测能证明"跑通"，证明不了"看得见"**。M25 只覆盖了镜头与顿帧，
手感层还剩一整批"产出只在画面上"的东西从未被判过分：运行时字体（三处各自实现，一旦取不到
`Text` 就是**空白**）、伤害飘字（数字对不对/会不会上浮淡出/会不会自己消失）、受击冲击（若写成叠乘会越打越大）、
Boss 前摇警示圈（**玩家唯一的躲避依据**）。

```bash
python visual_probe.py                    # 5 个场景（约 3 分钟）
python visual_probe.py --only LEFTOVER    # 只跑「重开局特效残留」
```

| 场景 | 覆盖路径 |
| --- | --- |
| FONT | `UIFactory.UIFont` 与飘字的 `Text.font` 都必须非空，且必须是**同一实例**（回退链不得分叉） |
| DMG | 必须生成、文案必须等于取整后的伤害、必须上浮且淡出、连打 8 次后必须**全部自行回收** |
| PUNCH | 倍率必须放大到 ≈1.12×、必须回落、连续受击**不得叠乘** |
| TELEGRAPH | 必须生成、必须有精灵、透明度必须脉动、真实时长≈请求时长、到点必须自毁 |
| LEFTOVER | 真实重开序列后，上一局的警示圈/爆发环必须为 0（先断言"确实造出了 3 个"） |

**前 4 个场景首轮全绿、零缺陷**（字体 `LegacyRuntime` / 飘字文案 `37`、上浮 28.33、淡出 0.47、残留 0；
冲击 1.120 → 回落 1.000；警示圈振幅 0.301、时长 0.500s、残留 0）。
这本身就是有价值的结论——和 M25 的 HITSTOP/PAUSE 一样：它们写对了，但"对"只是运气，现在被钉住了。

**但顺着 M24 的教训③复查时抓到一个缺陷**：`TempEffect.SpawnRing`/`SpawnTelegraph` 都是
`new GameObject` 建在**场景根节点**上，既不在 `_created` 里也不是任何管理器的子物体，
`TeardownWorld` 销毁不到它们。走真实的「重开一局」序列（`CloseAndResume → TeardownWorld → Build`）后，
**上一局的 2 个前摇警示圈 + 1 个 Nova 爆发环整批出现在新世界的开局画面里**——
与 M24 抓到的"宝石/敌人残留到新世界"是**同一类所有权缺口**。
修法：`TempEffect` 自建存活登记表（`Track` / `OnDestroy` / `DestroyAll`），`TeardownWorld` 一行接上。
（它们本身会自毁，但结算面板期间 `timeScale=0` 把它们冻住，重开后才播完——自毁机制盖不住归属缺口。）

**判据区分力**：①一律读**组件实测值**并在驱动里进程外复算；②"必须出现"与"必须消失"成对断言；
③冲击倍率用**相对比值**，不依赖私有字段；④「连续受击不叠乘」单独成条——
若实现写成"每次命中把倍率乘上去"，**单发命中标称 1.120 照样是绿**，只有第二发才暴露；
⑤LEFTOVER 的"重开前存量=3"是**前置断言**，否则"残留=0"可能只是压根没造出来。

> **区分力验证（四次注入 + 一次回退，变红项互不相交）**：注入飘字字体 null → **仅 FONT 两项**（1/3）；
> 去掉飘字自毁 → **仅 DMG「打完后残留」9**（4/5）；冲击改叠乘 → **仅 PUNCH「连击后倍率」1.405 + 「不叠乘」**，
> 而**「受击倍率」仍 1.120 绿**（正是设计意图：单发测不出，第二发才暴露）；
> 警示圈不脉动+时长翻倍 → **仅 TELEGRAPH 四项**（3/5，「会自毁」仍绿）；
> 回退 `TempEffect.DestroyAll()` → **仅 LEFTOVER「重开后残留」3（警示圈2 爆发环1）**，TELEGRAPH 全绿。

> **踩坑**：① `StarPixel.Enemy` 既是命名空间又是类名，`new List<Enemy>` 编译不过，必须写 `Enemy.Enemy`（M24 同坑又踩一次）；
> ② 测"是否自毁"要留足时间（飘字寿命 0.6s，判残留前等 ≥1.5s），只等 0.7s 会把"正常播放中"误判成残留；
> ③ **靶子敌人要放远**（600,600 且速度 0）——玩家自动开火，靶子放近了会被自己的子弹打到，倍率读数跳变；
> ④ 前摇警示圈的计时与 `BossBrain` 一致地用**缩放**时间，这是对的（必须与攻击一起冻结），别照 M25 的震屏改 unscaled。

新增/改动：`Effects/TempEffect.cs`（`_alive` + `Track`/`OnDestroy`/`DestroyAll`）、
`GameBuilder.cs`（`TeardownWorld` 接 `TempEffect.DestroyAll()`，另接 `-visualProbe`）、
`Dev/VisualProbe.cs`、`../visual_probe.py`。

回归（异常均 0）：**M26 五场景**、单机冒烟 45s（击杀 67/波次 4/等级 4/选卡 3）、M25 六场景、M24 五场景、
M23 三场景、合规门 6 场景、防沉迷 MINOR+ADULT、持久化 SAVE+TELEMETRY、设置 M22 六场景、云存档 5 场景+UI、
**联机双开 60s（玩家 1/2、峰值 2/2、波次 6/6、击杀 105/73、双端 Boss 是）**、静态审计 ERROR 0 / WARN 0 / INFO 8。

### M27 伤害飘字对象池无头实测

最后一个没池化的高频对象（伤害飘字）已纳入 `PoolManager`；过程中挖出一个
**此前 26 轮无人发现的系统性泄漏**：`GameBuilder.Track()` 登记的是 `AddComponent`
的返回值——**组件**，而 `Object.Destroy(组件)` 只摘组件、GameObject 本体与它的所有子物体
全部留在场景里，于是**每重开一局就遗留一整套空壳管理器**（实测 8 个空壳 + 2 块残留画布，
一局一局往下堆）。`LevelUpManager.BuildPanel` 注释里写的「随管理器一起拆世界」从未兑现。

| 场景 | 断言 |
|---|---|
| `POOL` | 120 次高频伤害：累计新建 ≤40（未池化实测 120）、复用率 ≥50%、峰值并发 ≥10、结束后活跃 0 |
| `REUSE` | 同一实例**第二次**被使用：复用同一实例 / 文案更新为 89 / 0.30s 后仍在场 / alpha >0.2 / 字体仍在 |
| `BURST` | 单帧 40 次伤害：峰值并发必须 =40（一张不丢）、随后全部归还 |
| `LEFTOVER` | 真实重开后：上一局飘字存活 0、空壳管理器 0、`DmgCanvas` 只剩 1 块 |

区分力验证三次且互不相交：回退 `Track` → 仅 LEFTOVER 红（空壳 **8**、画布 **2**）；
飘字改回每次 `new GameObject` → 仅 POOL 红（新建 **120**、复用率 **0.0%**）与 REUSE「非同一实例」；
`Init` 不重置 `_life` → **仅 REUSE 红（1/6）**，POOL / BURST 全绿——
说明「没池化」与「池化了但状态没重置」是两种必须分开判的失败。

```bash
python pool_probe.py                    # 4 个场景（约 3 分钟）
python pool_probe.py --only REUSE       # 只跑「复用实例状态重置」
```

产出：`Dev/PoolProbe.cs`、`../pool_probe.py`；`DamageNumberManager` 模板化 +
`FloatingText.Init` 重置寿命与透明度 + `UIFactory.UIFont` 收敛字体实现；
`GameBuilder.Track` 归一为登记 GameObject 并补 `ClearSingletons`。
详见 `pool_m27.md`。

### M28 新建对象归属无头实测：把「连续三轮靠人肉复查才找到的泄漏」做成机器检查

**为什么**：`new GameObject` 出来的东西没人负责回收，连续三轮踩到——M24 池化实体漏到新世界、
M26 警示圈/爆发环漏到开局画面、M27 每次重开遗留一整套空壳管理器。三轮的修法都是「补登记表/补一行 SetParent」，
但**检查手段**始终是人肉 grep。本轮固化成两层防护。

**第一层：静态检查**（`audit_project.py` 第 7 项）
扫描运行时每一处 `new GameObject(` / `Instantiate(`，必须能判定归属，否则 ERROR：

| 判定 | 依据 |
|---|---|
| 挂了父物体 | 同方法内 `SetParent` / `.parent =` |
| 进了登记表 | `Track(...)` 或 `Add/Enqueue/Push` —— **且该类必须有 `DestroyAll`/`ResetAll`/`OnDestroy` 清理方法** |
| 工厂方法 | `CreateXxx()` new + return，调用方写了 `Track(CreateXxx())` |
| 字段持有 | 赋给字段/属性且类里有销毁它的地方（允许 `w.auraVisual = go` 带点左值） |
| 应用级单体宿主 | 挂的正是**本文件自己的类**，且有 `Instance =` 或继承 `MonoSingleton<T>` |
| 显式声明 | `// @owner: parent` / `field(名)` / `registry(名)` / `app` / `stage` |

注解是**封闭词汇且逐项机器验证**，不是逃脱口：声明 `@owner: field(X)` 就必须真有销毁 X 的地方；
`@owner: app` 必须有 `Instance =` / `MonoSingleton<T>` / `DontDestroyOnLoad` / 处于联机条件编译段；
`@owner: stage` 必须是 `Ensure*/Setup*/Prepare*/Open*` 或有 `== null` 守卫。
**刻意不提供 `@owner: self`**——M26 的警示圈本来就会自毁，但结算面板 `timeScale=0` 把它冻住、重开后才播完，自毁盖不住归属缺口。

**自检**（`python audit_project.py --selftest`，10 个夹具）：静态检查最可能的失败不是误报而是**永远不报错**。
夹具中「进了登记表却没人清」正是 M24 的原始形态，「只靠计时自毁」是 M26 的形态，「注解撒谎」验证注解不是逃脱口。

**第二层：运行时探针**（`orphan_probe.py` + `Dev/OrphanProbe.cs`，`-orphanProbe -orphanScene`）
CHAIN（链闪电：冻结时间后重开，一个都不许留下）· BOSS（上一局活着的 Boss 不许被带进新世界）·
ROOT（通用兜底：重开后场景根对象 multiset 差必须为 0）。

**扫出来的三处真实缺陷**：
① **`ChainBolt` 的电弧段/闪光**——与 M26 的 `TempEffect` 同形且**未修**：静态 Spawn、场景根、
   靠 `Time.deltaTime` 自毁；`timeScale=0` 把它冻住、重开后才播完。补存活登记表 + `DestroyAll`。
② **上一局还活着的 Boss 会被带进新世界**——Boss 是 `Instantiate` 出来的、既不在 `ActiveEnemies`
   也不挂在任何管理器下，只由 `ActiveBoss` 持有，而 `ResetAll` 只清 `ActiveEnemies`。
   **它会继续追着新玩家跑、继续开火**。补 `Destroy(ActiveBoss.gameObject)`。
③ **`NetStatus` 画布挂在场景根**（同类文件都写了 `SetParent`，只有它漏了）。

> 附带澄清：`TouchJoystick._root` / `SpectatorCamera._root` 首轮看着像泄漏，实际 `OnDestroy` 里都清了——真归属。

**区分力验证三次回退，变红互不相交**：
① 回退 `ChainBolt.DestroyAll()` → CHAIN 1/3（存活 6）+ ROOT（多出 2）；
② 回退 Boss 清理 → BOSS 1/2（`BossTemplate(Clone)`）+ ROOT（多出 1），**静态检查也独立报红**
   「声明 `@owner: field(ActiveBoss)` 但全文找不到销毁 ActiveBoss 的地方」——注解确实不是逃脱口；
③ 回退 `NetStatus` 的 `SetParent` → 离线不可达，**只有静态检查能抓**。

**实测（异常均 0）**：CHAIN 3/3 · BOSS 2/2 · ROOT 2/2（29 → 29，多出 0）· 静态审计 ERROR 0/WARN 0/INFO 16 · 自检 10/10。

**踩坑**：① 注解写在注释里而 `strip_noise` 剥注释——必须读原文行（初版 5 处注解全部"看不到"）；
② 删块注释不能吃掉换行，否则去噪后行号与原文错位（实测错位 8 行）；
③ **必须先剥行注释再剥块注释**——`Resources/Legal/*.txt` 里的 `/*` 会把后面几十行整段吞掉；
④ 方法区间切分必须就地收尾，否则"最后一个方法"的区间吞到文件底造成漏报；
⑤ "应用级单体"必须限定"挂的是自己的类"，否则 `_root.AddComponent<Canvas>()` 被误放行；
⑥ ROOT 要立刻冻结波次——`Build()` 同步刷出第一波，新世界的当波敌人会被当成残留（首轮误报 2 个 `EnemyTemplate(Clone)`）。

**教训**：① 同一类缺陷第三次出现就该换方法——修第三次时该修的是**检查手段**，不是再修一次泄漏；
② 静态检查必须配自检，它最可能的失败是"永远不报错"；③ 注解机制要封闭且可验证，否则就是给逃脱口发许可证；
④ 通用兜底判据最容易"假绿"，采样时机必须卡在"新世界刚建好"这一刻；⑤ 静态与运行时互为交叉验证，只留一边就有盲区。

完整报告见 `orphan_m28.md`。

## 架构与目录

```
Assets/_Project/Scripts/
  Core/          MonoSingleton, EventBus, PoolManager, SpriteFactory, SfxManager, CameraShake
                 + M5: SettingsManager, Localization, UIFactory, SaveManager, PlaytimeGuard,
                      Telemetry, VersionInfo
                 + M7: MusicManager（三轨 BGM 交叉淡变，DontDestroyOnLoad）
                 + M13: CloudSave/（ICloudSaveProvider / HttpCloudSaveProvider / CloudSaveService）
                 + M16: PlayerInput / TouchJoystick（触屏输入抽象与虚拟摇杆）
  Player/        PlayerStats, PlayerController
                 + M6.2: CharacterDef, CharacterManager（角色属性乘区/起始武器）
  Combat/        Damageable, CombatManager, ComboManager, UpgradeCatalog, CombatTargets（统一索敌）
    Skill/       SkillDef, Projectile
                 + M6.1: Boomerang, HomingMissile, ChainBolt（链闪电特效）
  Enemy/         Enemy, EnemyManager, Boss, EnemyProjectile
                 + M6.3: BossBrain（三阶段状态机，单机/联机共用）
  Pickups/       XpGem, HealthPickup, PickupManager
  Effects/       TempEffect（Nova 爆发环 / Boss 前摇警示圈）
  UI/            HUDController, LevelUpManager, DamageNumberManager, FloatingText
                 + M5: MainMenu, SettingsPanel, GameOverPanel
                 + M6: CharacterSelectPanel, CodexPanel（图鉴）
  Compliance/    ComplianceGate（首启合规门）, LegalTexts, LegalViewer（M5）
  Net/           NetProtocol, NetSession, NetBackend, NetConfig, NetWorld, NetRegistry, NetLobby, NetStatus, NetWatchdog
                 + FishNetBackend / NetPlayer / NetEnemy / NetProjectile / NetPickup / NetWorldState（#if FISHNET_ENABLED）
                 + M11: NetReconnect / NetReconnectStore
                 + M14: SpectatorCamera（观战相机，#if FISHNET_ENABLED）
  GameBuilder    运行时自动构建（RuntimeInitializeOnLoad；M5：单机先进合规门→主菜单，联机同样先过合规门再进大厅）
Assets/_Project/Resources/Legal/  terms.txt, privacy.txt, children.txt（游戏内法律文本源）
Assets/_Project/version.json      版本号单一数据源（构建脚本读取）
Assets/_Project/Resources/cloudsave.json  M13 云存档后端配置（baseUrl 留空 = 禁用；仅接受 HTTPS / 本机 http）
Assets/Editor/   NetSetupEditor（菜单一键装配 Fish-Net 预制体与场景）
                 BuildScript（版本注入 / PlayerSettings / Bootstrap 场景 / Mono·IL2CPP 构建）
Assets/Net/Prefabs/   由装配脚本生成的 7 个网络预制体（运行联机前生成）

Assets/Resources/
  Sprites/       player / enemy_chaser / enemy_runner / enemy_tank / enemy_elite / boss / gem / health / projectile
  Audio/         shoot / hit / pickup / levelup / boss / hurt / gameover / combo
                 + bgm_menu / bgm_battle / bgm_boss（M7 循环 BGM，22050Hz 单声道）

Docs/            用户服务协议.md, 隐私政策.md, 儿童个人信息保护规则.md, M5-商业化与上线清单.md
```

设计原则遵循 TDD：模块化管理器、对象池抗 GC、数据驱动技能/升级、事件总线解耦；
联机通过 `NetWorld` 单一接缝生成世界对象（单机走对象池、联机走网络生成），`CombatTargets` 统一索敌，
使两套模式复用同一份战斗代码。

## 组合技速查（tag 驱动）

| 同时持有 | 效果 |
| --- | --- |
| 星能弹(fire) + 散射枪(storm) | 全部伤害 ×1.5 |
| 寒霜新星(ice) + 裂地波(earth) | 弹道命中施加减速 |
| 雷环刃(lightning) + 星能弹(fire) | 弹道穿透 +2 |
| 圣光领域(holy) + 星能弹(fire) | 全部伤害 ×1.4（M6.1） |

> 在 `ComboManager.Combos` 中集中定义；新增组合只需加一条规则。

## 如何扩展（下一步建议）

- **换美术**：直接替换 `Assets/Resources/Sprites/*.png`（保持文件名，32px≈1 世界单位），或改 `gen_assets.py` 重生成；`SpriteFactory` 会自动加载，无需改代码。
- **调数值**：先改 `../balance_sim.py` 顶部的 `CFG / KINDS / WEAPONS` 跑一版模拟，曲线满意后再同步回 C#（两者常量一一对应，注释均有标注）。
- **加武器/平衡**：在 `UpgradeCatalog` 增改 `CreateXxx()` 与 `BuildOffers()`；数值集中在 `SkillDef`。
- **加组合技**：在 `ComboManager.Combos` 增加 tag 规则并暴露新加成字段（加成经纯函数 `Compute` 生效，单机/联机通用）。
- **联机扩展**：联机入口在 `Scripts/Net/`。同步新实体 = 做预制体 + `NetWorld` 加一个 Spawn 方法 + `NetConfig` 挂引用；Fish-Net API 版本差异只需改 `NetSpawner` 与 `FishNetBackend` 两个收口文件。
- **合规 / 存档 / 商业化**：M5 已交付首启合规门、三份法律文本、青少年模式框架、本地存档与埋点骨架；剩余项（版号送审、官方实名认证接入、遥测网络上报、云端存档）见 `Docs/M5-商业化与上线清单.md`。

## 不在本原型范围（按 TDD Out-of-Scope）

专用服务器、跨平台匹配/大厅服务、PvP、UGC 编辑器、MOD、正式美术/音效资产（当前为程序生成的占位像素与合成音效）、版号送审、官方实名认证系统对接（已留接入缝）。
联机范围内亦未做：跨平台匹配（断线重连已在 M11 完成；观战已在 M14 完成，含无头实测；环绕刃视觉同步已在 M10 完成，伤害一直正常）。
