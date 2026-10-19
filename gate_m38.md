# M38 本地镜像不许先斩后奏

> 里程碑模式（M17 起）：补无头探针 → 判据须有区分力 → 修复真实缺陷 → 全量回归 → 写报告。
> 本轮主题：**M37 的安全设计被另一半的写法抵消掉了**。

---

## 1. 起因

M37 把房主侧的升级供选改成了批次表，取不到就拒绝，当时的结论是：

> 「宁可少发一次强化，也绝不能发错一张。」

这句话**只对了一半**。它描述了房主那半边，却默认了客户端那半边会乖乖等着。实际不是——客户端是**先斩后奏**的：

```csharp
// NetPlayer.ShowCardsTargetRpc 的面板回调（M38 修复前）
LevelUpManager.Instance?.PresentExternal(offers, index =>
{
    RequestChooseUpgradeServerRpc(batchId, index);           // 发出去，可能被拒
    if (index >= 0 && index < kinds.Length)
        UpgradeCatalog.ApplyKind(kinds[index], _ctrl);       // 无条件先把本地权威数据加上
});
```

`ApplyKind` 改的是**客户端的权威数据**（移速 / 磁吸范围 / 生命上限）。房主一旦拒绝
（批次被上限淘汰 / index 越界 / 重放），客户端就已经比房主多一次强化。

于是出现一个反直觉的后果：**M37 的安全网越严格，双端分叉越多**——
房主每拦下一次，就制造一次分叉。而且：

- 不报错；
- 不崩；
- 看字段也对（客户端的移速确实按公式乘了 1.08）；
- 唯一的表现是「客户端好像跑得快一点」。

---

## 2. 为什么藏得住：没有任何症状，也没有任何出口

| 环节 | 修复前 | 后果 |
|---|---|---|
| 房主拒绝 | `TryApply` 返回 false 后**默默 return** | 这件事谁都看不见 |
| 客户端应用 | 回调里无条件执行 | 房主拒了也照加 |
| 批次被淘汰 | 只 `EvictedCount++`，**没有出口** | 对端永远不知道那张卡已经作废，玩家点了没反应 |

M37 收尾时我自己把「上限 16 未压测、淘汰计数没有出口」记进了遗留候选，
但当时把它当成"待补的观测手段"，没意识到它正是这次分叉的**入口**。

---

## 3. 修法

三件事，缺一件都不干净：

### ① 本地镜像必须等确认（核心）

新增 `Combat/UpgradeApplyGate`（**不依赖网络的纯逻辑待确认表**）：

- `Submit(batchId, index)` → 记一笔账，拿到**一次性 ackId**；
- `Confirm(ackId, batchId, index)` → 只有"这笔还在、且回执与请求对得上"才算数，且**一笔只许结一次**（防重放 / 防伪造）；
- `Reject(ackId)` / `Invalidate(batchId)` / `PurgeExpired(now)` → 被拒、批次作废、等超时，四种收场**都有出口、都有计数**。

客户端只在 `ConfirmUpgradeTargetRpc` 里才 `ApplyKind`。回执携带的是**房主认定的那一张的 kind**，
不是"客户端自己记得的那组第 index 张"——后者正是先斩后奏的根源。

> 为什么又抽一个纯类和 M37 的 `UpgradeOfferBatches` 同理：`NetPlayer` 是 `NetworkBehaviour`
> 且整文件裹在 `#if FISHNET_ENABLED` 里，单机无头环境根本实例化不出来。
> 抽出来之后，重放、伪造回执、超时这些分支在 `GATE` 场景里**秒级**就能钉死；
> 不抽就得跑满 60 秒双开，而且看出来的只是"数值怪"。

### ② 拒绝必须有出口

`RequestChooseUpgradeServerRpc` 被拒时不再默默 return，而是 `RejectUpgradeTargetRpc` + 计数 + 日志。
**拒绝是协议的一部分，不是异常分支。**

### ③ 淘汰必须通知对端

`UpgradeOfferBatches` 加 `OnEvicted` 事件 → 房主在淘汰的同一时刻发 `InvalidateBatchTargetRpc`
→ `LevelUpManager.InvalidateExternal(batchId)` 把队列里压着的那批移除、正在展示的直接关面板。
玩家眼前不能摆一张点了也没用的卡。（这同时补上了 M37 遗留的"淘汰计数没有出口"。）

---

## 4. 判据

### 运行时

| 层 | 判据 |
|---|---|
| `card_probe.py` `GATE` | 确认后才生效 / 重复确认被拒 / 伪造回执被拒 / 回执对不上被拒 / 拒绝路径 / 超时清理 / 批次作废 |
| `card_probe.py` `INVAL` | 批次 id 正确 / 撤正在展示的 / 撤队列里的 / 不存在的批次不误伤 / 其余卡片仍在 |
| `card_probe.py` `BATCH` | 新增「淘汰有出口」：`OnEvicted` 必须触发且带对批次 id |
| `net_smoke.py` | **本地每一次应用都必须来自一次确认**（`本地强化次数 == 本地已应用`） |
| `net_smoke.py` | 双端对账：`本地已应用 ≤ 房主已应用 ≤ 本地已应用 + 待确认残留` |

最关键的是第一条。只比"确认数 vs 房主应用数"**抓不到先斩后奏**——
那种写法是在确认之外**又加了一次**，确认计数照样对得上。所以给 `UpgradeCatalog` 加了
`ApplyCount`（真实应用次数，含闭包路径，一笔只加一次），把它和"确认次数"分开统计，
多出来的那几次才无处可藏。

双端对账用不等式而不是判相等：两侧各自在**收尾前 1 秒**采样，允许有一个回执还在路上；
在途的那笔在客户端是"待确认残留"，所以上界把它算进去——既不漏判也不假红
（M36/M37 已经因为采样时刻栽过两次，这次不想栽第三次）。

### 静态（第 16 项）

含 `[ServerRpc]`/`[TargetRpc]` 的文件里，本地应用强化（`ApplyKind`/`ApplyStat`）**之前**，
同一方法体内必须先出现确认语义（`Confirm` / `ackId`）。按**方法**配对，可用
`// @nolocalapplyguard: 理由` 豁免（空理由无效）。

---

## 5. 实测

| 项目 | 结果 |
|---|---|
| 联机双开 60s（同时刻收尾） | 房主已应用 **7** / 本地已应用 **7** / 本地强化次数 **7** / 房主拒绝 0 / 待确认残留 0 / 批次淘汰 0 |
| 同上 | 等级 10/10 与 8/8、波次 7==7、击杀 141==141、玩家数 2==2、双端异常 0 |
| 单机冒烟 45s | 击杀 72 / 波次 4 / 等级 5 / 选卡 4 次 — 通过 |
| 无头探针 | 18 个脚本全通过（card_probe 扩到 8 场景：BASE/QUEUE/DRAIN/ORDER/BATCH/GATE/INVAL/RESTORE） |
| 静态审计 | ERROR 0 / WARN 0 / INFO 26 |
| 静态自检 | 79/79（原 71 + 第 16 项 8 个夹具） |

---

## 6. 区分力（四组，失败面互不相交）

| 组 | 注入 | 变红的判据 |
|---|---|---|
| R1 | 恢复先斩后奏（保留确认路径，额外先加一次） | **静态第 16 项** + 联机「本地强化次数 16 > 本地已应用 8」；card_probe 三层全绿 |
| R2 | 确认可重放（`Confirm` 不取走，同笔可确认多次） | **只有 GATE**「重复确认被拒 / 回执对不上被拒」红；BATCH/QUEUE/INVAL 与静态全绿 |
| R3 | 淘汰不出声（去掉 `OnEvicted`，只留 `EvictedCount`） | **只有 BATCH**「淘汰有出口」红；GATE/QUEUE/RESTORE 与静态全绿 |
| R4 | 修过头（确认之后也不应用） | 联机「房主已应用 8 > 本地已应用 0」红，**方向与 R1 相反**；静态仍绿（守卫还在） |

R1 与 R4 配对，证明这条判据分得清「多给」和「不给」；
R2 证明只比双端总数抓不到重放（总数照样对得上）；
R3 证明淘汰必须有出口——这正是 M37 留下来的那个候选。

---

## 7. 本轮被注入打脸的一次：静态判据曾经太宽

第 16 项最初把 `applygate` 和裸 `ack` 也算作"确认语义"。自检 8 个夹具**全过**，
但 R1 注入之后它**没有报红**——因为注入后的代码是：

```csharp
int ack = _applyGate.Submit(batchId, index, Time.unscaledTime);   // 只是记了一笔账
...
UpgradeCatalog.ApplyKind(kinds[index], _ctrl);                     // 然后不等回执就应用
```

`_applyGate.Submit` 里有 `applygate`、变量名里有裸 `ack`，两者都命中了"确认语义"→ 放行。

**记账不等于确认。** 收紧成只认 `Confirm` / `ackId` 之后，R1 立刻报在第 411 行。

教训：自检夹具全绿**不等于**判据有效——夹具只能证明"我构造出来的那几种写法"，
真实缺陷的写法可能刚好落在正则的宽容区里。注入验证这一步不能省。

---

## 8. 本轮改动

| 文件 | 改动 |
|---|---|
| `Combat/UpgradeApplyGate.cs` | 新增：待确认表（Submit/Confirm/Reject/Invalidate/PurgeExpired + 计数） |
| `Combat/UpgradeOfferBatches.cs` | `OnEvicted` 事件；`TryApply` 带回房主认定的那一张 |
| `Combat/UpgradeCatalog.cs` | `ApplyCount`（真实应用次数，一笔只加一次；拆出 `ApplyStatCore`） |
| `UI/LevelUpManager.cs` | 队列元素带 batchId、`CurrentBatchId`、`InvalidateExternal`、`ExternalInvalidated`、`CloseShownPanelWithoutChoosing` |
| `Net/NetPlayer.cs` | 记账+发请求（不再顺手 apply）、`ConfirmUpgradeTargetRpc`、`RejectUpgradeTargetRpc`、`InvalidateBatchTargetRpc`、超时清理、计数出口 |
| `Dev/CardProbe.cs` | 新增 `GATE` / `INVAL` 场景；`BATCH` 增「淘汰有出口」 |
| `card_probe.py` | 8 场景的判据与超时 |
| `Dev/SmokeTest.cs` | 输出 房主已应用 / 本地已应用 / 本地强化次数 / 房主已拒绝 / 本地被拒 / 待确认残留 / 回执超时 / 面板作废 |
| `net_smoke.py` | 双端对账判据 + 交叉表新增 6 行 |
| `audit_project.py` | 第 16 项 + 8 个自检夹具 |
| `m38_inject.py` | 四组区分力注入 |

---

## 9. 遗留候选

- 超时 15s 是拍的常数，未针对"房主长时间不回"压测；超时目前只记数，没有重试或降级。
- 客户端收到拒绝后只是记数 + 日志，**没有 UI 反馈**（玩家不知道自己那张卡没生效）。
- `LocalAppliedCount` 与 `UpgradeCatalog.ApplyCount` 是两套计数，尚未断言"两者统计口径完全一致"。
- 观战模式下推卡/确认链路等于空转，未单独断言。
- 批次作废只撤面板，**不回滚**任何已经发生的本地应用（正常路径下不会有，但边界未覆盖）。
- 第 10 项仍只配一对。
