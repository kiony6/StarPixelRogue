# M32 联机本地守卫对称性：单机永远测不出来的那半边

## 为什么

`PlayerController` 上有一批「只有本地玩家该做」的动作——往 HUD 广播事件、往全局单体写本地加成。
它们此前**从未被断言过**，原因很实际：**这类缺陷只在联机下才可能出错**。单机时 `isLocal` 恒为 true，
走哪条路都一样，于是「远程玩家串台」这种事天然测不出来。

M31 收尾时就留下这么一处，并且被明确记为遗留：

> `AddSkill` 里的 `RaiseWeaponsChanged()` 无 `isLocal` 守卫而 `LevelUpWeapon` 有
> （同一类「两条路径不一致」，单机下 `isLocal` 恒为 true 故本轮未断言）

本轮的主题就是**把"只有联机才成立的分支"拉到单机无头环境里断言**。

## 关键手法：翻转 isLocal

`isLocal` 是 `public bool isLocal = false;`——默认 false，单机由 `GameBuilder` 显式置 true。
所以探针直接把它翻成 false，就等价于"这是一个远程玩家对象"，**无需真开联机**。

这个手法本身必须由 `BASE` 场景先证明有效：翻成 false 后，已知有守卫的 `LevelUpWeapon`
必须不广播。若这条不成立，后面所有绿都是假的。

## 真实缺陷（两处，同一根因）

### ① `AddSkill` 缺 `isLocal` 守卫 —— 房主 HUD 被远程玩家带着刷新

`PlayerController` 里共 14 处 `EventBus.Raise*`，**13 处有本地守卫，只有 `AddSkill` 这一处没有**：

```csharp
// AddSkill（原）
ComboManager.Instance?.Recompute(GetTags());
CheckCombos();
EventBus.RaiseWeaponsChanged();          // ← 没有 isLocal

// 同类的 LevelUpWeapon
ComboManager.Instance?.Recompute(GetTags());
CheckCombos();
if (isLocal) EventBus.RaiseWeaponsChanged();
```

实测（远程态）：解锁广播 **1 次**、重复解锁 **1 次**，而升级广播 **0 次**——
同一件事两条路表现不一致，正好印证"同类两路径不一致"。

联机下的实际后果：房主处理客户端玩家的解锁请求时（`NetPlayer` → `AddSkill`），
会往房主自己的 HUD 发事件，房主界面被别人的武器变动刷新。

### ② `ComboManager` 是全局单体 —— 远程玩家改写本地伤害加成

比广播更严重的一处，是探针跑出来才发现的：`Recompute` 会写全局单例的
`bonusDamageMult / bonusPierce / slowOnHit`，而**4 处调用点全部无守卫**
（`ApplyCharacter` / `LevelUpWeapon` / `AddSkill` / `ApplyFromSave`）。

实测：本地玩家凑成 `fire+storm` → 伤害倍率 1.0 → **1.5**；
随后远程玩家解锁 `holy`（凑成 `holy+fire`），本地倍率被改成 **1.4**。
**房主的实际伤害由别人的武器池决定。**

修法：4 处统一把 `Recompute` + `CheckCombos` + 广播收进 `if (isLocal)` 块，
与既有守卫形状保持一致。单机行为完全不变（`isLocal` 恒 true）。

## 第一层：静态检查（第 11 项）

判据：**声明了 `bool isLocal` 的类型里，每次 `EventBus.Raise*` 都必须处在本地守卫之下**
——`if (isLocal)` 同行或块内，或方法内更早处有 `if (!isLocal) return;` 早退。
支持 `// @nolocalguard: 理由` 豁免，**空理由无效**。

范围刻意收紧：文件里没声明 `isLocal` 的不管（Net 层用的是 `IsServerInitialized` / `asServer`
另一套守卫，混在一起会互相误报）。

修复前精确命中 **1 处**：`PlayerController.cs` 第 628 行——其余 13 处有守卫的全部正确放行。
自检 8/8（累计 10+6+8+7+8 = **39/39**）。

夹具覆盖三种守卫写法（同行 / 早退 / 花括号在下一行的块式），
以及两个反向用例：无本地概念的类型不该管（`guard05`）、早退写在调用之后挡不住已发出的广播（`guard08`）。

## 第二层：运行时探针（`guard_probe.py` + `Dev/GuardProbe.cs`，`-guardProbe -guardScene`）

| 场景 | 判据 |
|---|---|
| `BASE` | isLocal 字段可读写 · 单机初始为 true · **翻转手法真的生效**（翻 false 后已守卫路径零广播） |
| `REMOTE` | 远程态下解锁 / 升级 / 重复解锁三条路都必须**零广播** |
| `LOCAL` | 本地态下三条路都必须**恰好一次**（防修过头把本地也堵死） |
| `STATE` | 远程玩家不得改写 `ComboManager` 单例的本地加成（伤害 + 穿透各一条） |
| `RESTORE` | 翻转后标志要还原 · 还原后本地广播照常 · 全程零异常 |

**判据的区分力（四组注入互不相交）**

| 注入 | REMOTE | LOCAL | STATE |
|---|---|---|---|
| R1 把 `AddSkill` 的广播移出守卫 | **红** | 绿 | 绿 |
| R2 把 `LevelUpWeapon` 的广播移出守卫 | **红** | 绿 | — |
| R3 修过头：把广播整行删掉 | 绿 | **红** | — |
| R4 只把 `Recompute` 移出守卫（广播仍守着） | 绿 | — | **红** |

两组对照各自说明一件事：

- **R3 证明 `LOCAL` 正向判据不可省**：只写"远程不许广播"的话，把广播整行删掉也能全绿
  （本地同样收不到，HUD 彻底不刷新），必须靠"本地必须恰好一次"抓住。
- **R4 证明「广播泄漏」与「全局状态污染」是两条独立判据**：两者症状不同、修法不同，
  只测其中一个会漏掉另一个。

**实测（异常均 0）**

- 修复前：REMOTE **1/3**（解锁 1、升级 0、重复解锁 1）· STATE **2/3**（1.5 → **1.4**）
- 修复后：BASE 3/3 · REMOTE 3/3（0/0/0）· LOCAL 3/3（1/1/累计 2）·
  STATE 3/3（1 → 1.5 → **仍 1.5**）· RESTORE 3/3
- 静态审计 ERROR 0 / WARN 0 / INFO 16

## 踩坑

① **`try { yield }` 编译不过（CS1626）**——C# 不允许在带 catch 的 try 块里 yield。
   但探针又必须自己吞异常（协程被打断会导致日志空白、结果假绿，M29 踩过），
   改为**手动驱动迭代器**（`MoveNext` + try/catch + 在外层 yield）。
② **C# 字符串里嵌套 ASCII 直引号**会直接破坏语法——失败文案里的「已持有→升级」
   必须写成中文书名号。
③ **批量改 4 处时锚点要带前置上下文**——两个方法都以 `}` 收尾，只匹配函数体会命中 2 处；
   其中 `ApplyFromSave` 原本 `if (isLocal)` 后面就有 `{`，替换时多留了一个花括号。
④ **批量跑 15 个探针会触发 `SAFE_DELETE_BULK_CONFIRM_REQUIRED`**（本轮累计删除达 50 的阈值），
   导致 `settings` / `cloudsave` 假红（判据其实全过，只是清理阶段被拦）。
   **单独重跑两者均 exit=0**。这是测试编排问题，不是代码回归——
   但如果不查清楚就会误判成"我的改动破坏了设置面板"。
⑤ **STATE 必须先做本地对照**：不先证明"加武器确实会改加成"，
   "远程加武器后加成没变"可能只是因为这条路径本来就不改加成（假绿）。

## 教训

① **"单机测不出来"不等于"没法测"**——把控制分支的标志位直接翻过来，
   就能在单机环境里断言联机分支；但要**先证明翻转手法本身有效**，否则全绿是假的。
② **同一语义的两条实现路径，守卫也要对称**——M31 抓的是"副作用漏了"，
   M32 抓的是"守卫漏了"，同源同形，说明这类不一致是反复出现的模式。
③ **全局单体是联机下的天然串台点**——`ComboManager` 的注释早就写了
   "全局单例状态会互相覆盖，纯函数 `Compute` 保证每个玩家按自己的武器池结算"，
   但 `Recompute` 这条兼容入口一直没跟上，直到实测把 1.5 → 1.4 摆出来才暴露。
④ **判据要成对**：只断言"不该发生"的，会被"干脆不做"的改法骗过去。

## 遗留

- 第 10 项目前只配了「重算 + 广播」一对，可扩展到其他配对动作。
- M29/M30 遗留仍在：`PoolManager.DestroyAll` 不销毁模板本身；
  `WhiteTexture`（`_whiteTex`）与共享 `_solid` 两张白纹理语义重叠。
- 本轮只覆盖了 `PlayerController`；`NetPlayer` 里 `_ctrl.AddSkill` 等调用点
  走的是同一批方法，已被本轮守卫一并覆盖，但 Net 层自身的广播用的是另一套
  `IsServerInitialized` 守卫，尚未纳入第 11 项。
