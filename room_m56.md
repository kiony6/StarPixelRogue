# M56 · 传输层那一层

## 1. 起因

M55 交付之后，组队码还是只能在**同一个局域网**里用。跨公网这条路走的是"借平台中继"（EOS / Steam），
而平台凭据还在你手上——那段我推进不了。

但也不是完全无事可做。我去翻了 FishyEOS 的源码，翻出一件必须先处理掉的事：

```csharp
/// <summary>EOS Not Used</summary>
public override void SetClientAddress(string address) { _ = address; }

/// <summary>EOS Not Used</summary>
public override void SetPort(ushort port) { _ = port; }
```

**参数被直接扔掉。** 而项目此前正是靠 `SetClientAddress` 告诉传输层"连谁"——
`FishNetBackend.StartClient` 里就是这么写的。也就是说：凭据一到、把 Tugboat 换成 FishyEOS，
它会**立刻静默失效**。不报错、不打日志、点了"加入"没反应，看起来和"网络本来就不通"完全一样。

这不是"预留抽象"，是换传输层之前必须拆掉的一颗雷。所以这一轮（M56）做的是：
**把"换传输层"这件事本身变成一个可以被验证的操作**，顺手拔掉这颗雷。

## 2. 这一轮做了什么

### 2.1 一张说明书（`Net/NetTransport.cs`）

传输层之间的差异，此前散在三处硬编码里。现在收成一张表：

| 种类 | 类型全名 | 能跨公网 | 要登录 | 有端口 | 怎么设目标 |
|---|---|---|---|---|---|
| `Tugboat` | `FishNet.Transporting.Tugboat.Tugboat` | ✗ | ✗ | ✓ | `SetClientAddress` |
| `Eos` | `FishNet.Transporting.FishyEOSPlugin.FishyEOS` | ✓ | ✓ | ✗ | 组件上的 `RemoteProductUserId` |
| `Steam` | `FishNet.Transporting.FishySteamworks.FishySteamworks` | ✓ | ✓ | ✗ | 未接入（不猜） |

**换传输层 = 改 `NetTransport.Default` 一行。**

表里每一列都有代价，不是摆设：

- **能跨公网** —— UI 用它决定要不要对玩家说"仅限同一网络"。
- **要登录** —— EOS 必须先做 EOS Connect 登录拿到 ProductUserId，Tugboat 什么都不用。
  这一步目前还没接（见 §5），但位置留好了。
- **有端口** —— Tugboat 的端口是真实的；EOS 的 `SetPort` 被吞掉，端口概念不存在。
- **怎么设目标** —— 就是上面那颗雷。EOS 必须设 `RemoteProductUserId`；用反射设，
  所以**这段代码在今天没有 SDK 的构建里也能编过**，SDK 装好后一行不用改。

### 2.2 装配（`EnsureOn` / `EnsureInstalled` / `KindOf`）

装配分两步，因为 FishNet 的时序不允许一步：

1. **装组件**（物体**激活前**）——`NetworkManager` 的 `TransportManager` 这时还不存在，
   所以只能往 GameObject 上加组件。
2. **钉住**（物体**激活后**）——显式写 `TransportManager.Transport`。

第 2 步是这一轮最要紧的一处。FishNet 自己是这么兜的：

```csharp
private void TryAddDefaultTransport()
{
    if (Transport == null && !gameObject.TryGetComponent(out Transport))
        Transport = gameObject.AddComponent<FishNet.Transporting.Tugboat.Tugboat>();
}
```

`TryGetComponent` 抓的是"场景里**任意一个**"。只有一个传输层时这完全没问题——
M56 之前的代码就是靠它兜住的，而它一直"能用"，所以没人发现少了什么。
一旦局域网兜底（Tugboat）和公网主路（EOS）同时装着，真正跑的是哪一个就取决于**组件顺序**。

> **拆成两个文件**：说明书在 `Net/NetTransport.cs`（不碰任何 FishNet 类型），
> 装配动作在 `Net/NetTransportSetup.cs`（整个文件包在 `FISHNET_ENABLED` 里）。
> 一开始是写在一个文件里用条件编译分段的，静态审计第 5 项直接报错：一个类型一半在
> `#if` 里一半在外，"没装插件时它到底存不存在"就没有确定答案。
> 拆开之后这个问题一眼可见。**改代码，不给检查加豁免**。

### 2.3 目标与端口（`ApplyTarget` / `ApplyPort`）

`ApplyTarget` 按种类分派，类型不符就**拒绝**，绝不退化成"反正都是 Transport，一律 `SetClientAddress`"。
`ApplyPort` 只在 `UsesPort` 为真时才动实例，否则如实返回 false 且**不碰它**。

### 2.4 顺手修掉一处真实的错位：公布 7777，实际绑 7770

查这件事的时候发现：`NetBootstrap.unity` 里 Tugboat 序列化的是 `_port: 7770`，
而 `NetProtocol.DefaultPort = 7777` 被写进局域网发现应答、发给了队友。

眼下没出事，纯粹因为**客户端也从来不设端口**，两边一起用 7770，
于是那个公布出去的 7777 从来没被真的用到过。一旦两台机器的资产端口不同，
就会变成"码对、房在、怎么都连不上"。

现在端口只认 `NetProtocol.DefaultPort` 这一个源，在 `ConfigureTransport` 里统一设一次。

### 2.5 界面上多一句话

大厅在传输层不能跨公网时显示：

> 当前传输层只在同一局域网内可用；跨网络组队需要平台中继（EOS / Steam）

不写这句话，玩家会理所当然地以为"把码发给外地的朋友就能连"，
然后把连不上归咎于游戏——而这一层确实还做不到，只是没人说过。

## 3. 判据

`transport_probe.py` 拉起无头进程，三场景 26 条：

| 场景 | 条数 | 管什么 |
|---|---|---|
| `SPEC` | 10 | 能力位、类型名、以及"EOS 不走 SetClientAddress"这条事实 |
| `INSTALL` | 9 | 装上了没有、认的是不是它、重复装会不会变两个、缺 SDK 时会不会撒谎、提示两面 |
| `TARGET` | 7 | 目标设得上读得回、不符时设不上且不留副作用、端口不适用时不许改 |

其中三条是本轮的重点：

- **装配后管理器认它** —— 只有这一条能抓住"只 `AddComponent`、从不给 `Transport` 赋值"。
  而那正是 M56 之前的代码。
- **失败时不许回落** —— 缺 SDK 时必须失败，失败后不许偷偷塞一个 Tugboat。
- **EOS 不走客户端地址** —— 把 §1 那段源码里的事实钉成判据，
  防止将来有人把这张表"简化"回一句 `SetClientAddress`。

## 4. 区分力注入（九组）

`m56_inject.py`：每组只改一处 → 重建 → 跑对应场景 → 比对"实际变红"与事先写死的"期望变红"。

| 组 | 改了什么 | 期望变红 |
|---|---|---|
| Q1 | 不改（对照） | 无 |
| Q2 | 只装组件、不赋 `Transport`（= M56 之前的代码） | 装配后管理器认它 / 装配类型正确 / 反查得到 Tugboat |
| Q3 | 缺 SDK 时悄悄回落成 Tugboat | 缺 SDK 时装配失败 / 失败时不许回落 |
| Q4 | 把 EOS 说成不能跨公网 | EOS 能跨公网 |
| Q5 | 把 EOS 改成走 `SetClientAddress` | EOS 不走客户端地址 |
| Q6 | 设目标一律走 `SetClientAddress` | 种类不符时设不上 / 设不上时不留副作用 |
| Q7 | 端口不检查种类，一律设上去 | 端口不适用时不改 |
| Q8 | 反查一律说成 Tugboat | 空管理器反查未知 |
| Q9 | "仅限局域网"提示恒显示 | 能跨公网时提示为空 |

Q2 与 Q3 是一对：Q2 是"装上了但没生效"，Q3 是"没装上却假装装上了"。
把这两个方向都堵住，"装配成功"这个词才算有内容。

Q4 与 Q9 也是一对：能力位说反了，界面上那句话就跟着说反。
两条判据分别盯住"表里那一格"和"屏幕上那句话"。

### 4.1 这一轮抓出的三处问题

**① 注入脚本自己读错了输出**（第一轮跑完，四组对不上）

`JUDGE_RE` 用 `\S+` 取判据名，而这次有几条判据名带空格（`反查得到 Tugboat`、`EOS 能跨公网`）。
正则先在空格处把名字截断，再撞不上「期望」，**整行匹配失败**——判据红了，脚本却看不见。

直接跑产物看原始输出，三条红得清清楚楚：

```
[SMOKE] 装配后管理器认它              否
[SMOKE] 装配类型正确                否
[SMOKE] 反查得到 Tugboat          否
[SMOKE] 断点通过                  6/9
```

改成 `(?P<key>.+?)` 之后九组一次全对。`m55_inject.py` 里是同一个写法，
虽然它那轮没出事（`IP 直通仍可用` 那条从没红过），也一并改了。

教训很直白：**判据红了却读不到，等于没红**。所以这一轮之后，`Line()` 末尾固定补两个空格——
分隔符不能再依赖 `PadRight(20)` 是否恰好够长。

**② 静态审计：条件编译不一致**

`NetTransport` 一开始是"一个文件、一半在 `#if FISHNET_ENABLED` 里"。
审计报了这个——不是误报：一个类型一半受保护，读者无法判断没装插件时它存不存在。
拆成两个文件。

**③ 静态审计：枚举分支缺失**

`Describe` 的 `switch` 覆盖了三种已知种类，靠 `switch` 之后的 `return null` 兜底。
审计要求"要么 case 全，要么有 default"。补了 `case TransportKind.Unknown: default:`——
而且这本来就是更准确的写法：未知种类不是"走到函数末尾了"，而是"明确地不认识"。

**④ 注入跑完没重建产物，回归拿注入版当了基线**

注入脚本每组跑完会还原**源码**，但 `Build/Smoke` 里留着的是最后一组的**产物**。
紧接着跑回归探针，`transport_probe` 在 INSTALL 场景红了「能跨公网时提示为空」——
而那一格恰好就是最后一组（Q9「提示恒显示」）改的地方。

现象极具误导性：SPEC 与 TARGET 全绿、INSTALL 只红一条，看起来像产品代码的边界条件没处理好。
实际只是产物没重建。**这类"上次的东西没收拾干净"和真缺陷在现象上没有区别。**

修法不是"下次记得"：`m56_inject.py` 现在在 `finally` 里还原源码之后**自动重建一次产物**。
把"记性"变成"流程"。

## 5. 接外网还需要什么（这一步我做不了主）

### 路线 B1：Epic Online Services（免费，推荐）

**你需要提供的五项**（在 <https://dev.epicgames.com/portal> 注册开发者账号后，
Products → 你的产品 → Product Settings / Client 里）：

| # | 名称 | 在哪里 |
|---|---|---|
| 1 | Product ID | Product Settings → General |
| 2 | Sandbox ID | Product Settings → Sandboxes |
| 3 | Deployment ID | Sandbox → Deployments |
| 4 | Client ID | Product Settings → Clients |
| 5 | Client Secret | 同上（只显示一次，拿到就存好） |

**拿到之后我要做的步骤**（都在工程内，不需要你再动手）：

1. 装 PlayEveryWare 的 EOS 插件——**必须 `git clone`，不要下 zip**，
   官方说明里明确写了 zip 会让附带的 DLL 出错；
2. 装 FishyEOS（UPM git URL：`https://github.com/ETdoFresh/FishyEOS.git?path=/FishNet/Plugins/FishyEOS`）；
3. 把上面五项填进 `Assets/StreamingAssets/EOS/EpicOnlineServicesConfig.json`；
4. **改 `NetTransport.Default` 为 `TransportKind.Eos`**——只有这一行；
5. 接 EOS Connect 登录：`NeedsLogin` 那一格现在还是"位置留好了但没接"。
   玩家进大厅前要先拿到 ProductUserId，这是 Tugboat 完全没有的一步；
6. 建房时把 EOS 房间标识写进组队码对应的记录，加入时用 `RemoteProductUserId` 指定目标；
   局域网发现作为同一网段内的快速路径保留。

第 4 步之后，`EnsureInstalled` / `ApplyTarget` / `ApplyPort` **一行都不用改**——
这是 M56 的全部目的。

### 路线 B2：Steamworks

需要 Steamworks 合作方资格（每个应用 100 美元）+ App ID。
技术上是 FishySteamworks，路子与 EOS 一样，表里那一行已经占好位置（`TargetMember` 故意留空，不猜）。

### 两条都不想走的话

Tugboat + 端口转发也能让熟人连上，代价是房主要自己会配路由器，
而且 CGNAT（国内家宽基本没有公网 IPv4）下直接无解。这条路现在也还通着——
大厅的"加入目标"里可以直接填 IP。

## 6. 成本（回到最初那个问题）

带宽粗估：每客户端 0.5–1.5 Mbps。4 人房走自建中继 ≈ 2.7 GB/小时；
10 个房 × 8 小时/天 ≈ 6.5 TB/月，远超轻量应用服务器的月流量包。

EOS / Steam 的中继走平台自己的基础设施，这部分钱不用你出——这就是选"借平台中继"的原因。

## 7. 回归

| 项 | 结果 |
|---|---|
| 三套构建（Headless / HeadlessNet / Mono） | `error CS` = 0 |
| 静态自检 30 项 | ERROR 0 / WARN 0（自检夹具全过） |
| 专项探针 | **20 个**（新增 `transport_probe` 被 `run_probes.py` 自动收编），分 4 次独立调用全过 |
| 单机长局 | 5 场景全过（GROWTH / RESTART / PANEL / EXIT / CARD） |
| 联机长局 | GROWTH 通过（2 人局掉落物有界、双端计数与房主一致、零异常） |
| 区分力注入 | 九组全部一致，红面互不相交 |

四片探针必须在干净产物上跑。第一次跑时第 4 片红了 `transport_probe`，根因是 §4.1 ④ 说的
"产物没重建"——修好脚本后重建、四片重跑，全过。

## 8. 还剩什么

- **EOS 凭据**（等你）——见 §5，凭据到位后按那六步走；
- **EOS Connect 登录流程**：`NeedsLogin` 已就位，实现要等 SDK；
- **房主码在暂停界面也显示**：目前只有大厅与游戏内 HUD 两处；
- **真实广播不进判据**：`RoomDiscovery.Query` 依赖网卡与另一台机器，无头探针测不了。
