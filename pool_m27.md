# M27 伤害飘字对象池无头实测

> 目标：把**最后一个没池化的高频对象**（伤害飘字）纳入对象池，并把"改成池"这一步自带的风险钉住。
> 结果：飘字池化完成；过程中顺带挖出一个**此前 26 轮无人发现的系统性泄漏**——
> 每次重开一局都会往场景里遗留一整套空壳管理器。

---

## 1. 为什么是它

项目里敌人 / 弹丸 / 回旋镖 / 追踪弹 / 地雷 / 宝石 / 回血球**全都**走了 `PoolManager`，
唯独伤害飘字没有：`DamageNumberManager.Show()` 每次伤害都

```csharp
var go = new GameObject("Dmg");
var t  = go.AddComponent<Text>();
t.font = GetUIFont();            // 每次伤害查两次 Resources.GetBuiltinResource
```

而它恰好是**全项目每秒触发次数最多**的那个对象——每个敌人每次被打一张。
重战况下每秒可达数百次分配，与项目「对象池抗 GC」的原则直接相悖。

## 2. 真正危险的地方：改成池这一步

复用实例时**字段初始化器不会重跑**。`FloatingText._life` 的初值 `0.6f`
只在 `AddComponent` 时赋过一次；漏掉在 `Init()` 里重置的话，
第二次被复用的实例 `_life` 仍是 0，`Update()` 里立刻归还——
玩家看到的是「伤害数字只出现前几次，之后全消失」，而崩溃日志一行都没有。

这正是 M25「字段默认值不是未初始化，它会被立刻使用」的同一类陷阱。
所以本轮的判据不是"有没有池"，而是**"池化之后复用出来的还是不是一张好的飘字"**。

## 3. 两个缺陷

### 缺陷 ①：飘字未池化（性能）

修法：`DamageNumberManager.Awake()` 建一个**不活跃**的模板 `DmgTemplate`，
之后一律 `PoolManager.Get / Release` 复用：

- 字体只在建模板时查一次，且改用 `Core.UIFactory.UIFont`（自带缓存，
  顺带把"三处各自实现字体回退链"收敛掉一处）；
- `Object.Instantiate` 会把克隆体落在**场景根节点**，必须挂回画布——
  否则 uGUI 的 `Text` 根本不渲染；
- 到期由 `FloatingText.Expire()` 归还，没有池时才真销毁。

### 缺陷 ②：`Track(组件)` —— 每次重开遗留一整套空壳管理器（严重，系统性）

```csharp
Track(new GameObject("DamageNumberManager").AddComponent<DamageNumberManager>());
```

`AddComponent` 返回的是**组件**，`Track<T>(T obj)` 原样把它塞进 `_created`。
而 `TeardownWorld()` 里：

```csharp
foreach (var o in _created) { ... Object.Destroy(o); }
```

**`Object.Destroy(组件)` 只把这个组件摘掉，GameObject 本体和它的所有子物体全部留在场景里。**
于是每重开一局就遗留一整套空壳：

| 残留 | 说明 |
|---|---|
| `DamageNumberManager` + `DmgCanvas` + `DmgTemplate` | 一整块残留的 ScreenSpaceOverlay 画布 |
| `LevelUpManager` | 它的 `BuildPanel` 注释写着「M5：随管理器一起拆世界」，**从未兑现** |
| `HUD` / `PoolManager` / `CombatManager` / `ComboManager` / `SfxManager` / `HitstopManager` / `EnemyManager` / `PickupManager` | 空壳 |

实测一次重开后残留 **8 个空壳管理器 + 2 块 `DmgCanvas`**，一局一局往下堆，永不消失。

修法：`Track` 内部归一——登记的一律是 **GameObject**。

### 连带（我自己引入、被回归套件当场抓住的）

改成登记 GameObject 后，`TeardownWorld` 里的 `if (o is ISingletonInstance s) s.ClearInstance()`
就永远不成立了（GameObject 当然不是单例），于是 `EnemyManager` / `PickupManager` /
`PlayerController` 的静态 `Instance` 全都指向已销毁组件，紧随其后的 `Build()`
被「Instance != null」守卫整个拦下——**正是 M24 修过的那个失败模式**。

M24 的 `RESTART` 判据立刻报红：「重开局后拿不到玩家 / 管理器缺失 Pickup=否 Enemy=否 Combat=否」。
补 `ClearSingletons(o)`：销毁 GameObject 时把它身上的单例一并清掉。

> 这条值得单独记一笔：它不是靠读代码想明白的，是**回归套件告诉我的**。

## 4. 四个场景与判据

| 场景 | 判据 |
|---|---|
| **POOL** | 120 次高频伤害（12 轮 ×10，间隔 0.35s）：累计新建 ≤40、复用率 ≥50%、峰值并发 ≥10、结束后活跃 0 |
| **REUSE** | 同一实例**第二次**被使用：必须复用同一实例、文案必须更新为 89、0.30s 后仍在场、alpha >0.2、字体非空、最后仍归还 |
| **BURST** | 单帧 40 次伤害：峰值并发必须 =40（一张不丢）、新建 ≤48、随后全部归还 |
| **LEFTOVER** | 真实重开序列后：上一局飘字存活 0、空壳管理器 0、`DmgCanvas` 只剩 1 块、新世界模板存在 |

判据设计要点：

1. **累计新建数由逐帧采样 instanceID 去重得到**，不读被测对象的账本——
   产品代码里根本没有"新建计数"，加了就是为测试而写的假账本（M21 DECLINE 假通过同类）。
2. **「新建得少」必须配「峰值并发确实很大」**：只测前者，"压根没显示几张"也能让新建数很小。
3. **REUSE 只盯第二次使用**：单发命中完全正常，复用才暴露（M23/M26 叠乘同类）。
4. **LEFTOVER 比对对象引用而非 instanceID**：Unity 里已销毁对象 `== null`，比对引用才可靠。
5. **"空壳"定义为「GameObject 在、组件没了」**——直接对应缺陷 ② 的形态。

## 5. 区分力验证：三次，变红互不相交

| 回退 / 注入 | 变红的判据 | 仍绿 |
|---|---|---|
| `Track` 改回登记组件 | LEFTOVER：空壳管理器 **8**、飘字画布 **2**、上一局存活 **1**（`DmgTemplate`）→ 2/5 | POOL / REUSE / BURST 全绿 |
| 飘字改回每次 `new GameObject` | POOL：累计新建 **120**、复用率 **0.0%**；REUSE：复用同一实例 **否** | BURST 全绿、LEFTOVER 全绿 |
| `Init` 不重置 `_life`（改回字段初始化器） | REUSE **1/6**：复用实例号 0、文案 null、0.3s 后活跃 0、alpha −1 | POOL / BURST 全绿 |

第三行是最关键的一条：**POOL 对"池化了但状态没重置"完全测不出来**（并发数与新建数都正常），
只有 REUSE 能抓。两种失败必须分开判。

## 6. 实测（异常均 0）

| 场景 | 结果 |
|---|---|
| POOL | 120 次伤害 → 累计新建 **20**、峰值并发 **20**、复用率 **83.3%**、结束后活跃 **0** |
| REUSE | 首次 / 复用实例号同为 `-502`，文案 `37 → 89`，0.30s 后仍活跃、alpha **0.471** |
| BURST | 单帧 40 次 → 峰值 **40**、累计新建 **40**、结束后活跃 **0** |
| LEFTOVER | 重开前 6 → 存活 **0**、空壳 **0**、`DmgCanvas` **1**、新世界模板 **1** |

## 7. 回归（异常均 0）

M27 四场景 · 单机冒烟 45s（71 杀 / 波次 4 / 等级 5 / 选卡 4）· M26 五场景 · M25 六场景 ·
M24 五场景（含 RESTART）· M23 三场景 · 合规门 6 · 防沉迷 · 持久化 · 设置 · 云存档 ·
联机双开 60s（玩家 1/2、峰值 2/2、波次 6/4、击杀 95/72、房主遇 Boss）·
静态审计 ERROR 0 / WARN 0 / INFO 8。

## 8. 踩坑

1. **`Object.Destroy(组件)` 不销毁 GameObject**——本次缺陷 ② 的根因，最容易想当然的一条 Unity 语义。
2. **改共享工具函数必须同步检查它的全部使用契约**：`Track` 从"登记组件"改成"登记 GameObject"，
   连带打断了 `TeardownWorld` 的单例清理路径。
3. `PoolManager.Get` 出来的克隆体落在**场景根**（`Instantiate` 不带父级），`Text` 必须挂回画布才渲染。
4. **复用实例的状态重置必须写在 `Init()` 里**，字段初始化器只在 `AddComponent` 时跑一次。
5. **池化后"残留"的定义变了**：归还池里的对象 inactive 但仍然存在。
   M26 的 `CountFloating()` 相应改成只数 active（并按 `Template` 后缀排除模板），
   否则"打完后残留 0"会永远为假。

## 9. 教训沉淀

1. **泛型工具函数要在内部归一边界**：`Track<T>(T obj)` 什么都收，
   于是"传组件"和"传 GameObject"两种用法长期共存，语义差异被泛型擦掉了。
2. **注释不是契约，测试才是**：`LevelUpManager` 写着"随管理器一起拆世界"，
   26 轮实测都没发现管理器本体根本没被拆掉。
3. **复用 ≠ 正确**：池化的风险不在"取不到"，而在"取到的是脏的"。凡池化必配"第二次使用"判据。
4. **回归套件是发现回归的主力**：本次 `ClearInstance` 失效是被 M24 的 RESTART 判据抓住的。
5. **"没发生"与"发生了但立刻结束"是两种失败**，要分别设判据
   （REUSE 的「0.30s 后仍活跃」正是用来分开这两者的）。

## 10. 遗留候选

- 三处字体实现还剩 `LevelUpManager` 自己那份（本轮只收敛了飘字侧）。
- 飘字池没有上限，与项目其余池一致（靠 0.6s 寿命自限）；若将来加上限，BURST 判据正好守着它。
- 「`new GameObject` 却无人负责回收」已连续三轮（M24 池化实体 / M26 TempEffect / M27 空壳）
  命中缺陷，建议做成 `audit_project.py` 的常态检查项。
