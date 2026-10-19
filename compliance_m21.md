# M21 合规门无头实测报告（ComplianceGate + 同意链路）

> 脚本：`compliance_probe.py` ／ 探针：`StarPixelRogue/Assets/_Project/Scripts/Dev/ComplianceProbe.cs`
> 入口：`StarPixelRogue.exe -complianceProbe -complianceScene FIRST|AGREED|BUMP|DECLINE|CONSENT_CLOUD|L10N`
> 产物：普通单机包 `StarPixelRogue/Build/Smoke/StarPixelRogue.exe`（`HeadlessBuild`）

## 1. 为什么挑这个模块

M19（防沉迷）、M20（持久化）之后，纯本机模块里只剩**合规门 `ComplianceGate` 从未被执行过一次**。
它是全部无头路径都会绕开的模块——`-smokeTest`、`-netHost` 等入口都直接调 `AgreeTerms()` 跳过它，
所以此前所有实测从未真正见过这块 UI。

合规功能的失效是**静默**的：门没弹出来，游戏一切正常，只是"用户其实从没同意过协议"。
其中**「协议版本升级后必须重新征得同意」**最关键——文本换了而用户没重新确认，
等于拿旧同意去覆盖新条款，正是监管最关心的点。

## 2. 测出的缺陷

### 缺陷①（主）：同意后当次会话云存档只推不拉，本地旧档静默覆盖云端

`CloudSaveService` 的首次拉取只在 `Start()` 里做一次：

```
Start(): if (!Enabled) { SetStatus(Off); return; }  ← 未同意时直接返回，那次拉取永久错过
         StartCoroutine(PullAndReconcile());
```

而 `StartComplianceProbe` 的顺序是 **先 `CloudSaveService.Ensure()`、后建合规门**——
首启（或协议版本升级后重新同意）时用户还没同意，`Enabled` 为 false，首次拉取被跳过；
此后 `Update()` 只管上行，于是本会话变成**只推不拉**。

受害链路：

1. 玩家已开启云存档（settings.json `cloudSave=true`），协议版本升级后重启
2. 合规门弹出 → `Start()` 已因"未同意新版"跳过拉取
3. 用户点「同意并继续」→ `Enabled` 变 true，但没人再触发拉取
4. 玩一局 → `SaveManager.Save` → `OnLocalSaved` → `SchedulePush` → **上传本地档**
5. 若云端有更新的存档（玩家在另一台设备玩过）→ **被本地旧档覆盖，进度永久丢失**

全程不报错、不崩溃，玩家只会觉得"我的纪录怎么没了"。

**修法**：`ComplianceGate.Agree()` 里调用新增的 `CloudSaveService.OnConsentGranted()`
（已启用且本会话尚未拉取过时补一次拉取，幂等）。

### 缺陷②：状态与实际可用性漂移

`Start()` 在未启用时写死 `SetStatus(StatusOff)`，此后 `Enabled` 变 true 也不刷新，
界面会显示"未启用"而服务其实已经在上传。顺带在 `OnConsentGranted()` 里对齐状态。

## 3. 为可测性新增的注入点

沿用 M19/M20 的做法——**让产品代码本身可被注入，而不是照抄一份判定逻辑的副本**：

| 位置 | 新增 | 用途 |
| --- | --- | --- |
| `VersionInfo` | `ComplianceVersion` const → 属性 + `ComplianceVersionOverrideForTest` | 构造"法律文本已升级、用户同意的是旧版"这一场景 |
| `ComplianceGate` | `Shown` / `AgreeCount` / `DeclineCount` | 「门弹了没有」与「点了没有」分开计数，否则两者无法区分 |
| `ComplianceGate` | `QuitAction`（默认即真 `Application.Quit()`） | 断言"点不同意确实走到了退出"，而不用真把进程杀了（否则后续断言打不出来） |
| `ComplianceGate` | `ClickAgreeForTest` / `ClickDeclineForTest` | 走真实按钮 `onClick`，不复制 `Agree()` 逻辑 |
| `CloudSaveService` | `PullCount` | 把"拉取"从笼统的请求数里单列出来 |
| `MainMenu` | `IsVisible` | 断言"同意前菜单隐藏、同意后才显示" |

## 4. 六个场景与判据

| 场景 | 覆盖路径 |
| --- | --- |
| FIRST | 首启弹门 → 三份法律文本齐全 → 点同意 → **同意态落盘** → 主菜单解锁；同意前菜单必须隐藏 |
| AGREED | 已同意当前版本：门不重复弹 |
| BUMP | 已同意**旧**版本 + 法律文本已升级：**门必须重新弹出**（协议更新后重新征得同意） |
| DECLINE | 点不同意 → 走到退出 + 同意态**不得**写入 + 主菜单不解锁 |
| CONSENT_CLOUD | 同意前云存档零请求；同意后必须**发起拉取**（防本地档覆盖云端） |
| L10N | 英文界面下合规门文案无裸键、无中文残留 |

判据的区分力落在三处：

1. **同意态是否落盘**，由探针调真实 `SettingsManager.Load()` 读回比对，不查内存字段——
   只查内存的话，一个根本不保存的实现也能全绿。
2. **"门弹了没有"(`Shown`) 与"点了没有"(`AgreeCount`) 分开断言**，
   才能区分「门压根没弹」和「门弹了但按钮点不动」。
3. **CONSENT_CLOUD 用拉取次数判定，而非笼统的"联网了没"**——
   只断言请求数 >0 的话，一次都不拉、直接上传覆盖云端的实现照样能过，那正是本轮要抓的缺陷。

## 5. 区分力验证（照例做了）

只把 `OnConsentGranted()` 那一行还原为修复前，重建后重跑：

```
[CONSENT_CLOUD] 同意前请求数  期望 0    实际 0
               同意后拉取次数  期望 ≥1   实际 0     ← 精确报出
               → 不通过
其余 5 个场景：全部照常通过
```

即**只有**指向该缺陷的那一条断言失败，证明它不是"怎么跑都成立"的无效断言。
还原后重建，6 个场景全绿。

## 6. 实测结果（异常均 0）

| 场景 | 结果 |
| --- | --- |
| FIRST | 门已弹出 是 / 点同意 是 / 法律文本齐全 是 / 同意态落盘 是 / 同意前菜单可见 否 / 同意后 是 |
| AGREED | 门已弹出 否 / 不重复弹门 是 / 菜单可见 是 |
| BUMP | 门已弹出 是 / 协议升级重新弹门 是 / 同意态落盘 是 / 菜单可见 是 |
| DECLINE | 点不同意 是 / 走到退出 是 / 同意态未写入 是 / 主菜单未解锁 是 |
| CONSENT_CLOUD | 同意前请求数 0 / 同意后拉取次数 1 |
| L10N | 文案条数 9 / 裸键 无 / 中文残留 无 |

## 7. 踩坑记录

### 坑①（探针自身）：`AddComponent` 会同步执行 `Awake`

探针最初写成「先 `AddComponent`、再给 `_scene`/`_lang` 赋值」，而初始状态（法律文本版本 /
已同意版本 / 云存档开关）正是在 `Awake` 里摆好的——于是 **Awake 读到的永远是默认值**，
表现为「每个场景都在按 FIRST 跑」：门永远弹、语言永远是中文。一轮跑出 4 处假失败。

修：`go.SetActive(false)` → `AddComponent` → 赋值 → `SetActive(true)`，把 Awake 推迟到激活时。
（与 M18 的"`SetSpectator` 必须在 `StartClient` 之前"同源：**Unity 的生命周期时序不是"赋值先后"能决定的**。）

### 坑②（判据污染）：场景之间共用了 `settings.json`

首轮全绿时，DECLINE 的「同意态未写入」其实是**因为错误的原因通过的**：
FIRST/BUMP 先跑过并落盘了更高的版本号，DECLINE 随后 `Load()` 读到的是前一个场景留下的值。
这是典型的**假通过**——断言恰好成立，但与被测行为无关。

修：驱动脚本在**每个场景前删除** `settings.json` / `save.json`，让每条判据都建立在已知初始态上。

### 坑③：联机回归的产物路径

`net_smoke.py` 默认 `Build/Smoke/StarPixelRogue.exe`（单机包），联机必须用
`Build/SmokeNet/StarPixelRogue.exe`（`HeadlessNetBuild`）。M19 已踩过一次，本轮又踩——
症状是两端玩家数 0、同步波次 -1，看起来像严重回归，其实只是产物选错。
已按 `net_smoke.py --exe StarPixelRogue/Build/SmokeNet/StarPixelRogue.exe` 重跑通过。

## 8. 回归（异常均 0）

| 项目 | 结果 |
| --- | --- |
| 单机冒烟 45s | 击杀 69 / 波次 4 / 等级 5 / 选卡 4 次 |
| 防沉迷 MINOR + ADULT | 全绿 |
| 云存档 5 场景 + UI 探针 | A~D 请求 0、E 请求 3、裸键无、英文界面中文残留无 |
| 持久化 SAVE + TELEMETRY | 全绿（文件 5 行 / 非法行 0 / 丢弃泄漏 0） |
| 联机双开 60s | 波次 7/6、击杀 130/101、Boss 是/是、异常 0/0 |
| 静态审计 | ERROR 0 / WARN 0 / INFO 8 |

## 9. 教训沉淀

1. **"通过"不等于"验证过"**。本轮最值钱的不是抓到缺陷①，而是**发现 DECLINE 的假通过**——
   它提醒：多场景共用一份落盘状态时，后一个场景的断言很可能建立在前一个场景的副作用上。
   每个场景必须从已知初始态开始。
2. **合规模块的失效是静默的**，所以它比玩法模块更需要实测：玩法坏了会崩、会看不出来；
   合规坏了只是"用户其实没同意过"，没人会报警。
3. **验证"某事发生了几次"先装计数器**（本轮 `Shown`/`AgreeCount`/`DeclineCount`/`PullCount`）。
   尤其是把"拉取"从"请求数"里单列——笼统的计数会让最危险的缺陷（只推不拉）顺利过关。
4. 凡 Unity 里「先创建对象、再赋值」的写法，都要问一句 **Awake 是不是已经跑过了**。
