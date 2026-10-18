# M11 / M14 无头实测报告

联机双开脚本 `net_smoke.py` 此前只验证了 M10/M11/M14 的**共同运行时底座**
（房主权威同步链路），M11（断线重连）与 M14（观战）本体从未真正跑过。本轮补齐实测入口并验证。

## 一、新增实测入口

| 入口 | 位置 | 说明 |
| --- | --- | --- |
| `-netSpectator` | `GameBuilder.StartNetSmoke` | 在 `backend.StartClient()` **之前**调 `NetSession.SetSpectator(true)`。必须在连接前置位——Fish-Net 的 PlayerSpawner 会在连接就绪时立刻生成玩家对象，事后置位来不及（只能走「先生成再回收」） |
| `-netDropAt N` | `GameBuilder` → `SmokeTest.StartNet(seconds, dropAt)` | 客户端跑到第 N 秒主动模拟一次意外掉线 |
| `FishNetBackend.SimulateDrop()` | 后端 | 只停本地客户端且**不置** `_intentionalStop`，从而走真实 M11 路径：`Stopped → TeardownWorld → NetReconnect → RetryLastConnection → Ready → Build` |
| `SmokeTest` 采样 | 采集器 | 掉线前 / 重连后各采一次波次·等级·击杀；房主侧记录玩家数「2→1→2」；观战侧记录自身玩家对象是否被回收 |

> 为什么不能用现成的 `Stop()`：它表示「主动离开」，会置 `intentionalStop` 并退订连接状态事件，
> 重连分支根本不会进入——那样测的是「退出房间」，不是 M11。

## 二、判据设计

### M14 观战
观战者**没有自己的可操作角色**，所以不能沿用「玩家数 ≥2」。判据改为：

1. `自身角色已回收 == 是` —— 房主收到 `RequestSpectatorServerRpc` 后执行 Despawn，
   观战端注册表里不再有 `IsOwner` 的玩家对象。这一条同时验证了「客户端 → 房主」的 ServerRpc 链路。
2. 仍能收到房主权威世界同步：同步波次 ≥1、同步击杀 >0（观战者不参战，靠 SyncVar 观察对局）。
3. 玩家数期望相应放宽为 ≥1。

### M11 断线重连（三层证据，缺一不可）
1. 客户端自述 `重连完成 == 是`；
2. 掉线前后 **波次 / 等级 / 击杀** 三项进度均未回退（证明世界续上 + 进度快照真的重放了）；
3. 房主侧观测到玩家数 **2 → 1 → 2**（不依赖对端自述）。

只看 1 会被「根本没掉线也算通过」糊弄；只看 3 则无法确认进度重放。

## 三、发现并修复的真实缺陷（缺陷 9，M11）

**现象**：客户端掉线重连后，**进度被重置回 1 级**（断线前 3 级）。

**根因**：`NetPlayer.OnStartClient` 里直发的 `RequestReconnectServerRpc` —— **首连能到、重连必丢**。
客户端日志显示「已上报令牌」，房主侧从头到尾没有对应的受理日志，`NetReconnectStore` 匹配不上快照，
新玩家对象只能从初始状态开始。

**修法**：改为「首连仍直发（延迟最低）+ `Update` 每 0.4s 重发 + 房主 `ConfirmHandshakeTargetRpc`
回执后置 `_handshakeDone` 停止重发」。与 M14 既有的 `_spectatorDespawnPending`（延后一帧执行）
是同一类时序问题的同一类解法。

**修复前后日志对照**：

```
修复前：快照入库 token=a7293145 level=3   ← 房主存下了
        （重连后）再无「收到重连上报」      ← 令牌没送到，重放从未发生
        快照未入库：token=空               ← 新对象最终也没有令牌

修复后：快照入库 token=a7293145 level=2
        收到重连上报 token=a7293145 命中快照=是
        进度重放完成 level=2 syncLevel=2
```

## 四、踩坑：判据必须自证有区分力

第一轮 M11 判为**通过**，但等级是 `1 → 1` —— 该轮掉线时客户端只有 1 级，
「重连后 ≥ 断线前」**平凡成立**；而 `grep -c "进度重放完成"` = **0**，重放一次都没发生。

补了两道防线：

1. **采样口径**：只认 `NetPlayer.SyncedLevel`（房主权威等级 SyncVar），**禁止**回退到
   `PlayerController.level` —— 后者不参与 XP 结算、恒为 1，会把「重放失败」伪装成「本来就是 1 级」；
   本地玩家对象缺失时返回 `-1` 显式暴露。
2. **区分力守卫**：`net_smoke.py` 在掉线时等级 ≤1 时直接判为无效样本，并提示调大 `--drop-at`。

> **「通过」不等于「验证过」。** 若一条断言在故障情况下也能成立，它就是无效断言——
> 必须让采样值先离开默认值区间，判据才有区分力。

## 五、实测结果（全部运行时异常 0 条）

| 模式 | 关键结果 |
| --- | --- |
| 普通联机 60s（回归） | 波次 7 / 5，击杀 108 / 73，Boss 是 / 是 |
| 观战 60s（M14） | 会话角色「观战」，自身角色已回收 **是**，同步波次 7 / 6，击杀 118 / 84 |
| 断线重连 90s（M11） | 掉线 @50.0s，重连耗时 1.6s，波次 6→7，**等级 6→6**，击杀 105→113，房主观测对端掉线 / 重连 **是 / 是** |
| 单机回归 45s | 击杀 72，波次 4，等级 7，EXIT=0 |

M11 那轮等级为 6，判据区分力充足（若重放失败会退回 1）。

## 六、命令

```bash
# 普通联机
python net_smoke.py --seconds 60 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe

# M14 观战
python net_smoke.py --spectator --seconds 60 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe

# M11 断线重连（建议 --drop-at 足够大，让客户端先升到 3 级以上，判据才有区分力）
python net_smoke.py --drop-at 50 --seconds 90 --start-delay 10 --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe
```

前置：先构建联机包
`Unity.exe -batchmode -nographics -quit -projectPath StarPixelRogue -executeMethod StarPixel.EditorTools.BuildScript.HeadlessNetBuild`
