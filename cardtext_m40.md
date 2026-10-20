# M40 联机推卡不许自带文案：卡片必须按接收端自己的语言现算

> 里程碑：M40 ｜ 日期：2026-10-20 ｜ 基线提交：`1a724d8`（M39）

---

## 1. 起因

M39 已经认识到一件很常见的事：**房间里的两个人语言可以不同**。
所以 M39 把**收场提示**（确认 / 拒绝 / 作废 / 超时）改成了"房主只回 `kind`，
标题由客户端用 `TitleOfKind` 按自己的语言现算"。同一次改动里漏了一半：

```csharp
// NetPlayer.OfferCards（修复前）
var titles = new string[offers.Count];
var descs  = new string[offers.Count];
for (int i = 0; i < offers.Count; i++)
{
    titles[i] = offers[i].title;   // 房主语言
    descs[i]  = offers[i].desc;    // 房主语言
}
ShowCardsTargetRpc(Owner, batchId, titles, descs, kinds);

// NetPlayer.ShowCardsTargetRpc（修复前）
offers.Add(new UpgradeOffer { title = titles[i], desc = descs[i], kind = kinds[i] });
```

**卡片本体仍然带着房主的文案。** 房主中文 + 客户端英文时，英文玩家升一级，
眼前弹出的是：

```
解锁 · 裂地波
周期性以自身为中心爆发
```

——一整块他看不懂的字。而这**不是罕见配置**：外服/双语房间里两边语言不同是常态。

更隐蔽的第二处：标题里的武器等级是**房主侧**的等级。客户端武器库有同步延迟时
（`SyncList` 还没到），房主写"强化 · 散射枪 Lv4"，客户端手上其实才 Lv2——
玩家看到的那张卡，描述的是他拿不到的东西。

### 为什么这一处特别难抓

- **同语言环境下完全不可见**：两端都中文时，卡片是中文，一切正常；
- **计数全对**：收到 3、上屏 1、入队 2、丢弃 0、收支平；
- **日志全对**：`[M37]`/`[M38]` 的对账数字一个不差；
- **面板也对**：只是文字不是玩家能读的语言。

也就是说：M37–M39 建立的所有检查都覆盖不到它——它们检查的是"卡有没有丢、
应用对不对、收场有没有声音"，没有一条检查**"这行字是谁的语言"**。

---

## 2. 修法

### 2.1 协议：只发 `kind`，一个字节文案都不过网

```csharp
private void OfferCards()
{
    var kinds = new int[offers.Count];
    for (int i = 0; i < offers.Count; i++) kinds[i] = offers[i].kind;
    ShowCardsTargetRpc(Owner, batchId, kinds);
}

[TargetRpc]
private void ShowCardsTargetRpc(NetworkConnection conn, int batchId, int[] kinds)
{
    var offers = UpgradeCatalog.OffersFromKinds(kinds, _ctrl);   // 按本机语言现算
    ...
}
```

`kind` 本来就是协议里已有的字段（M4.5 起用于本地镜像），只是此前**只是附加信息**，
文案另走两个字符串数组。现在 `kind` 成为唯一信息源。

### 2.2 `UpgradeCatalog.OffersFromKinds` / `DescOfKind`（纯逻辑）

```csharp
public static List<UpgradeOffer> OffersFromKinds(int[] kinds, PlayerController p);
public static string DescOfKind(int kind, PlayerController p);
```

`DescOfKind` 与 `TitleOfKind`、`ApplyKind` 三档完全对齐：

| kind | 含义 | 标题 | 描述 |
|---|---|---|---|
| `0..6` | 属性强化 | `upgrade.stat.N` | `upgrade.stat.desc.N`（**不一定存在**） |
| `100+id` | 武器解锁 | `upgrade.unlock` | 一句话武器定位 `weapon.desc.*` |
| `200+id` | 武器升级 | `upgrade.level`（**本机**下一级） | `NextLevelDesc(下一级)` |

`apply` 恒为 `null`：客户端不许本地执行应用（M38 的确认门在后面兜着）。

### 2.3 顺带：等级必须按**本机**现算

`DescOfKind(200+id, p)` 与 `TitleOfKind(200+id, p)` 都从 `p.Weapons` 里找那把武器，
取 `min(level + 1, MaxLevel)`。满级（Lv4）时不会冒出"Lv5"。
找不到这把武器（还没同步到）时退化为"解锁"文案，而不是编一个等级出来。

### 2.4 新增 `Localization.Has`

卡片描述对**大多数**属性强化本来就是空串（"移速 +8%"没有补充说明），
而 `T()` 找不到键会**原样返回键名**。直接 `T("upgrade.stat.desc." + kind)`
会把 `upgrade.stat.desc.2` 贴到玩家脸上——既不空也不对。

`Has(key)` 让 `DescOfKind` 能区分两件事：

- **本来就没有说明** → 返回 `""`（该空的空着）
- **漏翻了** → 英文态返回 `""` 会被判据抓到（`LOCALE` 断言 `en[0].desc != ""`）

### 2.5 让"语言不同"可以被测：`-lang` 命令行

`GameBuilder.ApplyLanguageArg()`：`-lang zh|en`，在 `SettingsManager.Ensure` **之后**调用
（Ensure 会用存档里的语言覆盖一遍），只改运行时、不写存档。
`net_smoke.py` 默认给客户端传 `-lang en`——**房主中文 / 客户端英文**从此成为默认双开配置。

### 2.6 让"眼前那几行字"可被核对

- `LevelUpManager.LastPresentedText`：`BuildPanel` 里记录**真正上屏**的标题 + 描述
  （本地 / 联机 / 队列补弹三条路都汇到这里，记一处全覆盖）；
- `SmokeTest` 输出 `界面语言` 与 `推卡文案`（客户端自述的那几行字）；
- `NetPlayer.ShowCardsTargetRpc` 打一行 `[M40] 推卡文案 batch=… lang=… text=…`。

三层证据：**生成 → 上屏 → 自述**，缺一层都可能被"函数对、面板错"蒙过去。

---

## 3. 判据

### 3.1 运行时 `card_probe.py` 新增 `LOCALE` 场景（6 条）

1. **地基**：`Has` 能区分"有这条说明"与"本来就没有"（否则描述只能二选一地错）
2. **中文态卡片完整**：四张卡标题都有字；属性 0 有描述、属性 1 描述为空、解锁卡有描述
3. **英文态零中文零键名**：标题/描述都不含汉字、**且不以 `upgrade.` 开头**（缺键会回退成键名）
4. **等级按本机现算**：手上 Lv3 → 卡片写 Lv4；无玩家版不带等级；满级不出 Lv5
5. **非法 kind 安全**：`-1` / `9999` 不抛异常、标题非空、`apply` 必须为 `null`
6. **面板真实英文**：英文态推一组卡，读**面板上真实渲染的 `Text`**，零汉字

第 6 条是关键：前五条都只证明"函数返回对了"，玩家看到的是面板。

### 3.2 联机 `net_smoke.py` 新增 M40 判据

- 客户端 `界面语言` 必须是 `en-US`（否则本组判据没有区分力——`-lang` 没传到位）
- **`推卡文案` 不得含汉字**（读的是客户端眼前真正写着的那几行字）
- 收到过推卡却没有任何上屏文案 → 红（要么没上屏，要么没留下可核对的文本）

### 3.3 静态第 18 项「联机推卡不得跨端传界面文案」

两层，任一层命中即报错：

- **(a)** RPC 方法签名里出现 `string[] <名字含 title/desc/text/label>` —— 文案上了网
- **(b)** RPC 方法体内出现 `title = 某数组[i]` —— 把跨端数组当界面文本用

只作用于含 `[ServerRpc]`/`[TargetRpc]` 的文件，且只查 **RPC 方法本身**（夹具 xt04 验证：
普通方法里传字符串数组不构成跨端串味）。`@xnettext:` 豁免且空理由无效（xt06）。

为什么必须静态兜底：这类缺陷在**同语言**环境下完全不可见，
而双开默认两端同语言——不把客户端调成英文，跑一百次也抓不到。
R1 正是在"静态红、LOCALE 绿、联机红"三者同时成立时才有说服力。

---

## 4. 顺带修掉：对账字段的采样口径不一致

M40 首轮联机的表格里出现了一组自相矛盾的数字：

```
推卡批次  4        | 推卡收到  8
房主已应用 4        | 本地已应用 4
```

看起来像"客户端收了 8 批、只落地 4 批"——**丢了 4 单**。查下来不是产品问题：
`SmokeTest` 里 `_extReceived` 等字段在**收尾瞬时**被无条件重采，而房主侧的
`_hostOffered` 只在**收尾前 1 秒**采（M37 为避开退出竞态专门改的）。
客户端在最后 1 秒又升了 4 级、收了 4 批卡，于是两侧表格拼在一起像是丢了单。

这与 M36 修过的"双端采样时刻不同"是同一类错误，只是换了个字段。
改成：`_ext*` 只在 `_endSampled` 那次采（时长短于 1 秒时才用瞬时值兜底）。
修完 7 / 7 / 7 全线自洽。

**为什么值得单独记一笔**：判据本身没错——`收到 == 上屏 + 队列残余` 仍然成立，
`本地已应用 ≤ 房主已应用` 也仍然成立；错的是**表格读法**。而报告的第一读者是人，
一张自相矛盾的表会让人去查一个不存在的缺陷。

---

## 5. 实测

**联机双开 60s（房主中文 / 客户端英文，同时刻收尾）**

| 指标 | 房主 | 客户端 |
|---|---|---|
| 界面语言 | zh-CN | **en-US** |
| 等级 / 权威等级 | 8 / 8 | 8 / 8 |
| 同步波次 | 7 | 7 |
| 同步击杀 | 141 | 140 |
| 玩家数（收尾前） | 2 | 2 |
| 推卡批次 / 上屏 / 丢弃 | 7 / - / 0 | 7 / 7 / 0 |
| 房主已应用 / 本地已应用 / 本地强化次数 | 7 | 7 / 7 |
| 收场总数 / 通知上屏 / 通知被吞 | - | 7 / 7 / 0 |
| **推卡文案** | - | `Projectile +1 — Direct / scatter weapons \| Unlock - Pulse Mine — Area denial: planted, then auto-detonates \| Area +15%` |
| 运行时异常 | 0 | 0 |

**单机冒烟 45s**：击杀 72 / 波次 5 / 等级 5 / 选卡 4 次 —— 通过。

**探针**：`card_probe` 10 场景全通过（新增 `LOCALE`）；其余 17 个探针全通过。
（批量连跑时 `settings` / `upgrade` / `visual` 三个曾因**测试台进程竞争**中断，
单独重跑全部通过——日志止于首个场景、无异常栈，属测试台启动竞态，非产品回归。）

**静态审计** ERROR 0 / WARN 0 / INFO 27；**自检 95/95**（原 87 + 第 18 项 8 夹具）。

---

## 6. 区分力（四组，失败面互不相交）

| 组 | 注入 | 变红的判据 | 仍绿的 |
|---|---|---|---|
| **R1** | 房主把 `title[]`/`desc[]` 一起发过去（修复前的原样） | 静态第 18 项**两条**（`NetPlayer` 第 394 行签名、第 403 行 `title = titles[i]`）+ 联机「英文客户端看到中文卡片」 | **LOCALE 全绿** |
| **R2** | 武器解锁卡没有描述 | **只有 LOCALE「中文态卡片完整」** | 英文态、面板、静态全绿 |
| **R3** | 武器升级卡不按本机等级算（固定 1→2） | **只有 LOCALE「等级按本机现算」** | 中文态、英文态、面板全绿 |
| **R4** | `OffersFromKinds` 给卡片带上可执行的 `apply` | **只有 LOCALE「非法kind安全」** | 其余全绿 |

R1 的联机证据（原样摘录）：

```
M40：英文客户端眼前的卡片是中文（解锁 · 裂地波 — 周期性以自身为中心爆发 |
拾取范围 +30% | 解锁 · 圣光领域 — 持续伤害身边敌人的领域）——推卡文案跟着房主语言走
```

几点说明：

- **R1 的签名最能说明问题**：LOCALE 全绿（文案生成函数没坏）、静态红（协议里带了文案）、
  联机红（玩家眼前是中文）。三条合起来才指向真正的缺陷：**坏的不是文案，是谁的语言**。
- **R2 / R3 / R4 都只红一条断点**，且互不重叠：
  卡片完整性、等级来源、`apply` 泄漏是三件不同的事，任何一条被合并进别的断言都会失去区分力。
- **R3 最容易被"只看汉字"的判据放过去**：标题是英文、描述也非空，唯独等级对不上本机。
  所以第 4 条必须断言**数字**，不能只断言"非空且无汉字"。

---

## 7. 新增 / 改动文件

**新增**：`cardtext_m40.md`、`m40_inject.py`

**改动**：
- `Combat/UpgradeCatalog.cs` —— `DescOfKind` / `OffersFromKinds`（纯逻辑，三档与 `ApplyKind` 对齐）
- `Core/Localization.cs` —— `Has(key)`（区分"本来就没有"与"漏翻"）
- `Net/NetPlayer.cs` —— `ShowCardsTargetRpc` 只收 `kind`；客户端现算；`[M40] 推卡文案` 日志
- `UI/LevelUpManager.cs` —— `LastPresentedText`（**真正上屏**的那几行字）
- `GameBuilder.cs` —— `-lang zh|en`（`ApplyLanguageArg`，Ensure 之后、只改运行时）
- `Dev/CardProbe.cs` + `card_probe.py` —— 新增 `LOCALE` 场景（10 场景）
- `Dev/SmokeTest.cs` —— 输出 `界面语言` / `推卡文案`；修对账字段采样口径（见第 4 节）
- `net_smoke.py` —— `--client-lang`（默认 en）+ M40 判据 + 交叉表 2 行
- `audit_project.py` —— 第 18 项 + 8 夹具

---

## 8. 遗留候选

- **其它跨端文本**：`NetWorldState` / `NetStatus` 里是否还有别的"房主语言"字段未逐项核对；
- **描述与等级强绑定**：`NextLevelDesc` 只按 `weaponType` 分流，同一武器 2→3 与 3→4 的描述
  可能相同（`projectile` 例外），玩家未必能看出"这一级到底加了什么"；
- **`TitleOfKind(200+id, null)` 回退成"解锁"文案**语义不准（应是"强化"泛化），
  目前只在拿不到玩家对象时触发，未被判据覆盖；
- **客户端武器库同步延迟**未单独压测：`DescOfKind` 退化为 Lv2 描述的那一刻是否会闪一下；
- 队列溢出丢弃（`ExternalDropped`）仍无玩家可见出口（M37 遗留）；
- 第 10 项静态检查仍只配一对。
