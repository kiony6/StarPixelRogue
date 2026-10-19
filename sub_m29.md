# M29 静态事件总线订阅对称性：漏的是引用，不是对象

## 为什么要做

前几轮一直在抓「跨局残留」，但抓的都是 **GameObject**——场景里数得出来、看得到：

| 轮次 | 漏的是什么 | 怎么发现的 |
|---|---|---|
| M24 | 池化出来的宝石/敌人挂在场景根 | 人肉 grep |
| M26 | Boss 前摇警示圈 / Nova 爆发环建在场景根 | 顺着 M24 复查 |
| M27 | `Track(组件)` 只摘组件，留下 GameObject 与子物体 | 人肉读 `TeardownWorld` |
| M28 | 以上三轮做成 `audit_project.py` 第 7 项静态检查 | — |

本轮是**同一类问题的另一种形态**：`EventBus` 是 `static` 的，订阅者却全是有生命周期的
`MonoBehaviour`。忘了退订，委托链会一直攥着已经销毁的对象——

- 场景里**什么都多不出来**，扫场景根扫不到它（M28 的 ROOT 判据完全看不见）；
- 已销毁对象上的方法大多只写写字段、**不抛异常**，日志一片干净；
- 只能靠反射读委托链才看得到。

而后果是玩家能直接撞上的：重开三局后升一级，**升级面板弹不出来，游戏卡在暂停态**。

本轮同样固化成两层防护：

1. **`audit_project.py` 第 8 项静态检查**——订阅必须退订得掉，否则 ERROR；
2. **`sub_probe.py` + `Dev/SubProbe.cs` 运行时探针**——证明"不退订时，事件真的会被重复处理"。

## 静态检查怎么判

扫描 `_Project/Scripts`（排除 `Dev/` 与 `Editor/`）里每一处 `EventBus.OnXxx += ...`，
按**事件名**配对同文件内的 `-=`：

| 订阅形态 | 判据 |
|---|---|
| 方法组 `+= Handler` | 必须有同事件的 `-= Handler`（按名精确匹配） |
| 匿名委托 `+= _ => ...` | 匿名委托没法按名匹配，退而要求该文件对**该事件**有任意 `-=` |

第二种形态的正确写法是**先存成字段再订阅**（`_h = ...; += _h;`），`OnDestroy` 里 `-= _h`——
这样它就退化成第一种、可以按名匹配了。

两个刻意的细节：

- **按事件配对，不是按文件**：`OnGameOver += OnEnd` 配 `OnLevelUp -= OnEnd` 不算配对
  （夹具 `eb05`）；
- **文件的别的事件有退订也不算**（夹具 `eb06`）：必须**该事件**自己有退订。

### 自检

```
事件总线订阅检查（第 8 项） · 6 个夹具
  [  OK] eb01_方法组漏退订                期望报错 实际报错
  [  OK] eb02_方法组对称                 期望放行 实际放行
  [  OK] eb03_lambda直接订阅            期望报错 实际报错
  [  OK] eb04_lambda存字段后退订          期望放行 实际放行
  [  OK] eb05_跨事件同名不算               期望报错 实际报错
  [  OK] eb06_别的事件有退订不算             期望报错 实际报错
```

`eb03` 就是本轮抓到的真实缺陷形态，`eb04` 是修好之后的形态。

## 扫出来的两处

```
事件总线: UI/LevelUpManager.cs 第 23 行 EventBus.OnLevelUp += 匿名委托，该文件没有任何 -=
事件总线: Core/CameraShake.cs  第 30 行 EventBus.OnShakeRequest += Shake，但同文件没有对应的 -= Shake
```

只有这两处。**其余 12 个订阅点全部对称**（HUDController 12/12、SfxManager 6/6、
MusicManager 3/3、HitstopManager 3/3、DamageNumberManager 1/1、GameOverPanel 1/1、
NetWorldState 1/1）——说明这不是"到处都漏"，而是两处具体的疏漏。

## 真实缺陷：`LevelUpManager` 每局攒一份订阅

`LevelUpManager` 由 `GameBuilder` 每局 `Track` 新建、拆世界时销毁，而订阅写的是匿名 lambda：

```csharp
Core.EventBus.OnLevelUp += _ => { _pending++; TryPresent(); };   // 改前
```

`EventBus` 是静态的，销毁不会带走委托。重开三局后链上就攒了三份，实测：

| | 重开前 | 重开三局后 |
|---|---|---|
| `OnLevelUp` 订阅数 | 3 | **6** |
| 链上已销毁对象 | 0 | **3**（全是 `LevelUpManager`） |

而真正致命的是**执行顺序**：旧实例先订阅、先执行，`TryPresent → ShowPanel → BuildPanel` 里
`canvasGO.transform.SetParent(transform, false)` 访问的是**已销毁的 transform**，
直接抛 `NullReferenceException`——**异常会中断整条委托链**，排在后面的新实例压根收不到事件。

于是玩家侧的表现是：

1. `ShowPanel` 已经先把游戏暂停了（`HitstopManager.SetPaused(true)`）；
2. 面板的 GameObject 建出来了，但 `SetParent` 没跑成，**挂在场景根上、没有任何父级**；
3. 新实例收不到事件，正常面板一个都不弹。

**升级时画面停住、面板不出来，只能杀进程。** 这条链路此前从未被断言过。

修法：委托存成字段，`OnDestroy` 里退订同一实例。

```csharp
private System.Action<int> _onLevelUp;

protected override void Awake()
{
    base.Awake();
    _onLevelUp = _ => { _pending++; TryPresent(); };
    Core.EventBus.OnLevelUp += _onLevelUp;
}

void OnDestroy()
{
    if (_onLevelUp != null) { Core.EventBus.OnLevelUp -= _onLevelUp; _onLevelUp = null; }
    // ...原有收尾
}
```

`CameraShake` 那处同样补了 `OnDestroy` 退订（顺带清 `Instance`）。
它挂在相机上、而相机是 `@owner: stage` 不随拆世界销毁，所以眼下**不会**一局攒一份——
但那是运气不是设计：一旦把相机纳入 `Track`，立刻变成同一个坑。

## 探针怎么判

三个场景，11 条断点。

### BASE — 反射自检（地基）

`EventBus` 的 `public static event Action<int> OnLevelUp;` 由编译器生成一个**同名**的私有静态
委托字段，用 `GetField(name, Static|NonPublic)` 才能拿到委托链、才能调 `GetInvocationList()`。

这条必须单独判死：反射取不到字段时 `Len()` 一律返回 0，
**「没有泄漏」和「根本没读着」长得一模一样**，后面所有判据都会假绿。

| 判据 | 实测 |
|---|---|
| 事件字段 18/18 | 18/18 |
| 建世界后订阅总数 > 0 | 25 |

### RESTART — 重开三局不许增长

| 判据 | 实测 |
|---|---|
| 订阅总数基线 > 0（前置：否则"不增长"只是两边都 0） | 25 |
| 净增长 = 0 | **25 → 25，+0** |
| `OnLevelUp` 不增长 | **3 → 3** |
| 链上已销毁对象 = 0 | **0** |

**死订阅分两种形态分别识别**，这是本轮最关键的一处：

- 方法组 `+= Handler` —— `Target` 就是订阅者本身；
- 匿名 lambda —— `Target` 是编译器生成的**闭包**，捕获的对象藏在闭包字段里。

只认前一种的检查会对 lambda 完全失明，而 `LevelUpManager` 正是 lambda。
另外判断"已销毁"必须先用 `is` 判类型、再用 Unity 重载的 `==`：
反过来写成 `var u = tgt as Object; if (u != null)` 的话，已销毁对象会被判成 `false` 直接漏过去。

### LEVELUP — 行为后果

重开三局后只升一级，判 5 条：

| 判据 | 实测 |
|---|---|
| 世界就绪（管理器与玩家都还在） | 是 |
| 正常面板 ≥ 1（证明判据本身没失灵） | 1 |
| 正常面板 ≤ 1 | 1 |
| 孤儿面板 = 0 | 0 |
| 升级事件未抛异常 | 无 |

**「正常面板」与「孤儿面板」必须分开数**——这是首轮回退实测逼出来的改动：
有缺陷时旧实例是在 `new GameObject("LevelUpCanvas")` **之后**、`SetParent` **之前**抛的异常，
于是场景根上留一个没有父级的幽灵 canvas。笼统地数同名对象的话，
「弹出 1 个」看起来完全正常，判据会**假绿**。

另外探针必须自己 `try/catch` 住 `RaiseLevelUp`：异常冒到协程上会把整个 `Run()` 打断，
`Report` 与 `Application.Quit` 全都不执行，进程挂到超时、日志一片空白——
那样"失败"和"探针坏了"没法区分（首轮 LEVELUP 就是这样，跑了 4 分钟没输出）。

## 判据的区分力

三次回退，每次只让**对应**的判据变红：

| 回退 | BASE | RESTART | LEVELUP | 静态检查 |
|---|---|---|---|---|
| R1 退回匿名 lambda（`LevelUpManager`） | 绿 2/2 | **红 1/4** | **红 2/5** | 红 |
| R2 去掉 `OnDestroy` 退订（`CameraShake`） | — | **绿 4/4** | — | **红** |
| R3 反射只读 1 个事件 | **红 1/2** | 绿 4/4 | — | — |

**R2 是本轮最值得记的一条**：`CameraShake` 的漏退订，运行时探针**完全抓不到**
（相机不销毁、`EnsureCameraShake` 有 null 守卫，压根不会重复订阅），
只有静态检查会红。两层防护不是重复建设，各自盖住对方盖不住的那一半。

**R3** 证明 BASE 的地基判据独立于其他判据：反射读数变少时只有 BASE 红，RESTART 仍然绿
（它只关心"有没有增长"，不关心"读了多少个"）。

R1 的具体失败项：

```
RESTART  链上还有 3 个已销毁对象的订阅（OnLevelUp:LevelUpManager ×3）
LEVELUP  升级事件抛出 NullReferenceException —— 委托链被中断，排在其后的新实例收不到事件
         弹出面板数 0 / 孤儿面板数 1
```

## 结果

| 场景 | 断点 | 关键实测 |
|---|---|---|
| BASE | 2/2 | 事件字段 18/18、订阅总数 25 |
| RESTART | 4/4 | 25 → 25（+0）、`OnLevelUp` 3 → 3、死订阅 0 |
| LEVELUP | 5/5 | 正常面板 1、孤儿面板 0、无异常 |

静态审计：ERROR 0 / WARN 0 / INFO 16（第 8 项自检 6/6）。

## 全量回归

| 项 | 结果 |
|---|---|
| 单机冒烟 45s | 通过（击杀 48 / 波次 3 / 等级 5 / 选卡 4 次） |
| M23 选角/成长 3 场景 | 通过 |
| M24 掉落/重开 5 场景 | 通过 |
| M25 手感层 6 场景 | 通过 |
| M26 可见层第二批 5 场景 | 通过 |
| M27 飘字对象池 4 场景 | 通过 |
| M28 新建对象归属 3 场景 | 通过 |
| 合规 / 防沉迷 / 持久化 / 设置 / 云存档 | 通过 |
| 联机双开 60s | 通过 |
| 静态审计 | ERROR 0 / WARN 0 / INFO 16 |
| 审计自检 | 归属 10/10 + 事件总线 6/6 |

异常均 0，无一项出现「不通过」。
