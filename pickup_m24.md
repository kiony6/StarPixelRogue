# M24 掉落 / 磁吸 / 结算 与重开局清理 — 无头实测报告

> 对应提交：`M24 掉落/重开局链路无头实测：修掉重开一局后拿不到新玩家、上一局实体整批残留`
> 脚本：`pickup_probe.py`　探针：`Assets/_Project/Scripts/Dev/PickupProbe.cs`
> 入口：`-pickupProbe -pickupScene MAGNET|CREDIT|LEFTOVER|RESTART|REPEAT`

## 为什么要做这一轮

到 M23 为止，被实测过的是「启动 → 主菜单 → 设置」「选角 → 开局」「升级 → 组合技」。
**掉落闭环**和**重开局**这两条路从来没有被真正驱动过：

| 从未验证的事 | 坏了会怎样 |
| --- | --- |
| 磁吸半径是否真的决定能否拾取 | 全局吸附也能正常升级，玩家看不出来 |
| `AddXp` 到底乘没乘 `xpGainMult` | 字段值对着我打也给不了额外经验，界面看不出来 |
| 回血是否被上限截断 | 血量能超过上限，HUD 比例失真 |
| **重开一局拿不拿得到新玩家** | 点了「重开」游戏照样跑，但**移动/开火/HUD 全线静默空转** |
| **上一局的实体有没有清干净** | 新世界里飘着一堆"看得见却捡不起来"的宝石，和围着旧玩家坐标转的幽灵敌人 |

前三条是「悄悄不对」，后两条是「点了按钮就没了」——**后者是用户点得出来的，比前者严重**。

## 五个场景与判据

### MAGNET —— 半径之外必须吸不动，改成大半径必须被吸走

```csharp
p.stats.xpGainMult = 1f;        // 钉死倍率：本场景测的是半径，不是倍率
p.stats.pickupRadius = 1.6f;    // 默认
pm.DropGem(playerPos + (6,0), 3f);
// 1.2s 后：距离必须仍是 6.00，XpRatio 必须仍是 0
p.stats.pickupRadius = 12f;
// 之后：宝石必须回到对象池（停用），且 XpRatio 恰好 0.600（3 点 / 升级线 5）
```

**两态对比**是这一判据的关键：只测单边的话，"永远吸附"和"永远不吸附"各能骗过一半。
经验用精确的 `XpRatio`（初始 `_xpToNext = 5`）而不是"等级涨了没"——后者粗粒度，发现不了入账数量不对。

### CREDIT —— 倍率逐颗生效 / 回血精确 / 超额封顶

- `xpGainMult = 2`：投入两颗 1 点经验的宝石，`XpRatio` 必须依次走到 **0.40 / 0.80**；
- 回血：半血捡一颗 30 点，必须正好 **+30**；
- 再捡一颗 999 点，必须**恰好等于 `maxHp`**（`Mathf.Abs(capped - maxHp) < 0.001`），不得溢出。

### LEFTOVER —— 真实重开路径后，上一局实体必须清零

先在远处 `(40,40)` 造出上一局的残留（3 宝石 + 1 回血球 + 2 敌人），**先断言确实造出了 6 个**，
再走 `GameOverPanel.OnRestart` 那两行：

```csharp
GameBuilder.TeardownWorld();
GameBuilder.Build();
```

然后断言残留为 **0**，并单独报出「宝石 / 回血 / 敌人」三项构成。
先断言"造出了 6 个"很重要——少了这一步，"残留 = 0"也可能只是压根没造出来（假通过）。

### RESTART —— 重开后必须拿得到**新世界**的玩家

四条断言，缺一不可：

1. `PlayerController.Instance != null`（拿不到就说明指针悬空了）；
2. `GetInstanceID() != 上一局那一个`（必须是新世界的，不能是旧引用）；
3. `PickupManager / EnemyManager / CombatManager` 三个管理器都接上了；
4. **新世界的掉落闭环真的通**——放一颗宝石看它会不会被吸走并精确入账。

第 4 条是有意为之：只查引用存在不叫验证，**世界接上了才算**。

### REPEAT —— 连续 3 次重开，残留不得累积

逐轮打印 `放 N → 残留 M`。泄漏的表现是**单调增长**，靠一排数字一眼可见，比一个布尔值诚实得多。

## 本轮修掉的三个缺陷

### 缺陷① 重开一局后拿不到新玩家（最严重）

`PlayerController` 用的是静态字段，不是 `MonoSingleton`：

```csharp
public static PlayerController Instance;
void Awake() { if (Instance == null) Instance = this; }   // 首个创建者胜出
```

而「重开一局」的路径是：

```csharp
// UI/GameOverPanel.cs — OnRestart()
GameBuilder.TeardownWorld();
GameBuilder.Build();        // 紧挨着，中间不给任何帧
```

`Object.Destroy` 的销毁**延迟到帧末**。于是：

1. `Build()` 里 `AddComponent<PlayerController>` 同步跑 `Awake`；
2. 此刻旧玩家还活着，`Instance == null` 为假 → **新玩家选择不注册**；
3. 帧末旧玩家销毁，`Instance` 变成悬空引用；
4. 重开后整局处于 `PlayerController.Instance == null` 的状态。

后果是**点了「重开一局」整个玩法层静默空转**：不崩、不报错、日志干净，只是玩家动不了、不开火、HUD 不刷新。

> 注意：`PlayerController` 里**本来就有** `OnDestroy(){ if (Instance == this) Instance = null; }`（M4.5 加的），
> 但它救不了这个场景——旧 `OnDestroy` 跑在**新 `Awake` 之后**，顺序反了。

修：让 `PlayerController` 实现 `Core.ISingletonInstance`，走 `TeardownWorld` 里已有的清理通道：

```csharp
public class PlayerController : MonoBehaviour, Core.ISingletonInstance
{
    /// TeardownWorld 的 Destroy 延迟到帧末，而 OnRestart 是两行紧挨着——
    /// 必须在新世界建立**之前**就把引用摘掉，否则新玩家会输给"还活着的"旧玩家。
    public void ClearInstance() { if (Instance == this) Instance = null; }
}
```

刻意**不改** `Awake` 的"首个创建者胜出"语义：联机下房主玩家先于远程玩家创建，
改成后来者胜出会让 `Instance` 指向某个远程玩家的控制器。

### 缺陷② 上一局的实体整批残留到新世界

`GameBuilder.TeardownWorld()` 只销毁 `Track` 进去的管理器。而宝石 / 回血球 / 敌人
全是 `PoolManager.Get(模板)` 克隆出来的实例，**挂在场景根节点，既不在 `_created` 里、
也不是任何管理器的子物体**：

```csharp
GameObject go = queue.Count > 0 ? queue.Dequeue() : Object.Instantiate(template);
```

于是这些克隆体一个都清不掉：

- 宝石留在原地，但新 `PickupManager` 的账本（`_gems`）是空的 → **看得见却捡不起来**；
- 敌人手里还攥着上一局的 `_playerTransform`（已被换掉）→ 围着旧坐标转，**打不到也清不掉**。

而 **`PickupManager.ResetAll()` 和 `EnemyManager.ResetAll()` 全仓库没有任何调用点**——
清理方法写好了，就是没接上。

修三处：

```csharp
// Core/PoolManager.cs ——新增：把本池创建过的每一个实例都记下来
private readonly List<GameObject> _owned = new List<GameObject>();
GameObject go = queue.Count > 0 ? queue.Dequeue() : Track(Object.Instantiate(template));
public void DestroyAll() { foreach (var go in _owned) if (go != null) Object.Destroy(go); _owned.Clear(); _pools.Clear(); }

// Pickups/PickupManager.cs / Enemy/EnemyManager.cs ——ResetAll 从"只停用"改成"真的销毁"
foreach (var g in _gems) if (g != null) Object.Destroy(g.gameObject);   // 旧：SetActive(false)，会变成永不回收的孤儿

// GameBuilder.TeardownWorld() ——接上（顺序不能反：先清实体，再销毁管理器自身）
Core.PoolManager.Instance?.DestroyAll();    // 已经归还到池里的那批
PickupManager.Instance?.ResetAll();         // 还在场上的掉落物
EnemyManager.Instance?.ResetAll();          // 还在场上的敌人
```

`ResetAll` 与 `DestroyAll` **缺一不可**：前者管"还活着、在账本上"的，后者管"已经停用、在队列里"的。

### 缺陷③ `net_smoke.py` 默认指向单机产物（M23 尾已修）

联机冒烟此前一直在跑 `Build/Smoke` 里的单机二进制：双端正常启动、跑满 60s、**异常 0**，
只是网络玩家数恒为 0、同步波次恒为 -1。看起来像同步链路坏了，实际联机版压根没被运行过。
已改为优先选 `SmokeNet`，且显式传单机产物时拒绝运行（退出码 2）。

## 判据的区分力（本轮做了两次，两组互相独立）

区分力 = 把修复回退掉，判据必须精确变红，且**只有它**变红。

### 验证① 回退 `ClearInstance`（改成"清完立刻写回"，等于没清）

| 场景 | 回退前 | 回退后 |
| --- | --- | --- |
| MAGNET | 通过 | **通过** |
| CREDIT | 通过 | **通过** |
| LEFTOVER | 通过 | **通过** |
| **RESTART** | 4/4 | **1/3**：拿到玩家=否 / 是新世界玩家=否 / 掉落闭环=否 |
| REPEAT | 通过 | **通过** |

### 验证② 回退 `TeardownWorld` 里的三行实体清理

| 场景 | 回退前 | 回退后 |
| --- | --- | --- |
| MAGNET | 通过 | **通过** |
| CREDIT | 通过 | **通过** |
| **LEFTOVER** | 残留 0 | **残留 6**（宝石3 回血1 敌人2） |
| RESTART | 通过 | **通过** |
| **REPEAT** | 3/3 | **0/3**：第1轮 放4→残留4 / 第2轮 放4→残留4 / 第3轮 放4→残留4 |

两组变红项**互不相交**，说明判据各自指向自己的那处修复，不是撞在一起的大杂烩。

## 实测结果（五个场景，异常均 0）

| 场景 | 关键输出 |
| --- | --- |
| MAGNET | 半径外不动 是（外 6.00 → 6.00）/ 半径内回收并入账 是（经验比 **0.600**）/ 断点 2/2 |
| CREDIT | 入账 **4/4**；经验 **0.40 / 0.80**（倍率 2 逐颗生效）；回血 **110.0/110.0**（精确 +30、超额封顶） |
| LEFTOVER | 重开前存量 **6** / 重开后残留 **0** / 残留构成 宝石0 回血0 敌人0 |
| RESTART | 重开后拿到玩家 是 / 是新世界玩家 是 / 新世界掉落闭环 是 / **4/4** |
| REPEAT | **3/3**；逐轮 第1轮 放4→残留0、第2轮 放4→残留0、第3轮 放4→残留0 |

## 探针侧踩的四个坑（都不是产品缺陷，但会让结果假红）

1. **`Object.Destroy` 是延迟到帧末的**，`TeardownWorld()` 之后立刻数对象，
   数到的还是"已标记待销毁"的那一批——会把正常清理报成残留。
   `REPEAT` 第一版就栽在这：三轮全是"放4→残留4"，而 `LEFTOVER`（中间有 yield）却正常。
   **数之前必须让帧走完。**
2. **计数必须绑定那颗具体的宝石**，不能判"场上还有没有宝石"。
   `MAGNET` 第一版就是这么写的，结果被建筑期掉落的杂散宝石骗了过去。
   改成 `DropGem` 后取回引用、等它**变停用**（= 归还对象池）。
3. **期望值要钉死前置条件**。默认角色是游侠，自带 `xpMult = 1.15`，
   3 点宝石实际到手 **3.45** → `XpRatio = 0.69` 而不是 0.6。
   那是**产品正确**，不是缺陷——但我第一版把它当成 bug 查了一轮。
   现在 `MAGNET` / `RESTART` 都显式先把 `xpGainMult` 设为 1；倍率本身交给 `CREDIT` 单测。
4. **SMOKE 的键名必须与驱动脚本逐字一致**。探针改了「半径内吸走并入账」→「半径内回收并入账」
   而驱动没跟着改，结果那一栏显示 `?`（缺值）并被判失败。这类"看起来像功能坏了"的红最浪费时间。

## 回归（异常均 0）

| 项 | 结果 |
| --- | --- |
| **M24 五场景** | **通过**（见上表） |
| 单机冒烟 45s | 通过（击杀 70 / 波次 4 / 等级 5 / 选卡 4 次 / 通过） |
| M23 选角/成长 3 场景 | 通过（乘区 7/7、开火 10/10、组合技 4/4、属性 7/7） |
| 合规门 6 场景 | 通过（同意前请求 0、同意后补拉取、L10N 无裸键无中文残留） |
| 防沉迷 MINOR + ADULT | 通过（跨天解禁、二次开局拦截、健康检查不冻结计时） |
| 持久化 SAVE + TELEMETRY | 通过（往返一致、JSONL 逐行合法、失败批次回队列重试） |
| 设置 M22 六场景 | 通过（点击即生效且落盘、11 字段往返、坏文件归档） |
| 云存档 5 场景 + UI | 通过（A~D 零请求、E 不可达静默降级、双语状态正确） |
| **联机双开 60s** | **通过（玩家 1/2、峰值 2/2、波次 7/6、击杀 141/105、双端 Boss 是）** |
| 静态审计 | ERROR 0 / WARN 0 / INFO 8 |

## 教训沉淀

1. **"连续两行语句"不等于"连续两个时刻"**。
   `TeardownWorld(); Build();` 读起来是先拆后建，但 `Destroy` 延迟到帧末意味着
   **在同一帧里旧对象仍然是活的**。任何在这类边界上做单例注册 / 引用托管的代码，
   都要显式抉择"旧的何时失效"，而不是指望 `OnDestroy` 的时序刚好对。
2. **写好的清理方法，没接上就等于没有**。
   `PickupManager.ResetAll` / `EnemyManager.ResetAll` 静静躺了若干个里程碑，
   全仓库零 caller。**"存在"和"被调用"之间的差距，是不会有任何报错的。**
   这次是 grep 出来的，不是测出来的——下一轮应该让探针直接盯着这类"孤儿方法"。
3. **对象池的所有者是灰色的**。
   池返回的实例既不属于 `GameBuilder` 的 Track 列表、也不是任何管理器的子物体，
   于是"销毁池"根本销毁不了它们。凡是 `Instantiate` 出来的东西，都要有人明确负责回收，
   否则你会发现"明明销毁了管理器，东西却还在场上"。
4. **判据跨域不准会白查一轮**。
   MAGNET 想测半径，却把角色经验倍率的变化算了进来——期望值会随角色漂移。
   一个场景只测一件事，其余变量先钉死；混在一起时，"产品正确"会被判成"缺陷"。
5. **测试的名字要能对得上**。
   SMOKE 键名改了一处忘同步驱动，输出 `?` 并被判失败——这种红和产品无关，
   但排查成本和一个真缺陷一样高。
