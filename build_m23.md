# M23 选角 → 开局 → 成长 整条链路的无头实测报告

> 对应提交：`M23 选角/成长链路无头实测：修掉换角色时数值叠乘`
> 脚本：`build_probe.py`　探针：`Assets/_Project/Scripts/Dev/BuildProbe.cs`　入口：`-buildProbe -buildScene SELECT|APPLY|PROGRESSION`

## 为什么要做这一轮

到 M22 为止，被实测过的链路是：**启动 → 主菜单 → 设置**，以及**战斗中**的若干单点行为。
但中间这一段——**玩家点了「角色」、选了某个守卫、然后真的开一局**——从来没有被任何探针驱动过。

这段链路有个特点：**它跨进程、跨场景、跨存档**。

| 环节 | 在哪发生 | 谁是见证者 |
| --- | --- | --- |
| 点「选择」 | 角色面板的按钮回调 | 同一个进程 |
| 落盘 | `CharacterManager.SelectedId` 的 setter → `save.Save()` | 磁盘 |
| 读取并生效 | **下一次** `GameBuilder.Build()` | 新进程 |
| 成长 | 局内 `UpgradeCatalog` → `ComboManager` → `Skill` | 同一进程 |

任何一环断了，现象都一样：**游戏照常运行，只是你选的角色没生效**。不会崩、不会报错、日志干净。
所以这一轮的核心方法是：**把「内存里变了」「面板上看见了」「磁盘上有了」「新进程里生效了」拆成四个独立断言**，
并且用 Python 驱动在**进程外**读写存档，不信任被测进程的自述。

## 三个场景与判据

### SELECT —— 一次点击要让四处同时跟上

流程：点主菜单「角色」→ 断言初始 `SelectedId == 0`（默认角色，尚未选择）→ 点 `Card1/SelectBtn`。

然后**同时**断言四件事，任何一件不成立都算失败：

1. **内存**：`CharacterManager.SelectedId == 1`；
2. **卡片标记**：卡片名以 `◉` 开头，且按钮文字变成 `char.selected`（"已选择"）；
3. **主菜单回显**：主菜单上的「当前角色」文本跟着变；
4. **磁盘**：把 `save.json` 用 `JsonUtility.FromJson<SaveManager.Record>` 读回来，`lastCharacterId == 1`。

另外两条容易被忽略的：

- **选角不得算作"玩过一局"**：断言 `characterRuns[1]` 在选角前后**不变**。
  一个偷懒的实现很容易在 `Save()` 里顺手把所有计数刷一遍，导致"只看了一眼角色列表就算玩过"。
- **关闭按钮真的关得掉**：点 `CloseBtn` 后面板不可见。

第四项（磁盘）的**最终判据在进程外**：`build_probe.py` 自己 `json.loads` 那个文件，不看探针的自述。

### APPLY —— 新进程读到角色，7 个乘区必须逐个对上

这个场景是整轮的重点，也是最容易做出"假通过"的地方。

前置：脚本**不清空** `save.json`（依赖 SELECT 刚写下的那份），探针先 `ReadSelectedFromDisk()` 跨进程读回 id。
然后：

```csharp
GameBuilder.Build();          // 真实开局
em.FreezeWaves = true;        // 冻住刷怪，否则 12 只怪会在断言前涌上来
```

逐项对比 7 个乘区与 `CharacterDef` 的期望值：

| 乘区 | 期望（相对基准） |
| --- | --- |
| `maxHp` | `110f * hpMult` |
| `moveSpeed` | `5.6f * speedMult` |
| `attackDamageMult` | `1f * damageMult` |
| `cooldownMult` | `1f * cooldownMult` |
| `pickupRadius` | `1.6f * pickupMult` |
| `areaMult` | `1f * areaMult` |
| `xpGainMult` | `1f * xpMult` |

外加三条：

- **起始武器被替换**：`Weapons.Count == 1 && Weapons[0].id == 4`（守卫自带 4 号武器）；
- **重复应用幂等**：再 `ApplyCharacter()` 一次，数值不得再变；
- **换角色不叠乘**：从守卫切到游侠，再切回守卫，最终值必须**等于**首次守卫的值，而不是它的 1.35 倍。

### PROGRESSION —— 10 把武器、4 条组合技、7 项属性强化、等级封顶

对 `UpgradeCatalog.AllWeaponIds` 里的每一把武器：

```csharp
清空武器 → AddSkill(id) → 在指定偏移处放一个 50 万血的目标 → 等 3.4s → 读 hp0 - dmg.currentHp
```

**目标位置必须按武器类型分别给**，这是本轮踩得最深的坑（见下文）：

```csharp
private static Vector2 TargetOffsetFor(int weaponId) {
    switch (weaponId) {
        case 5: return new Vector2(2.0f, 0f);    // 雷环刃：环绕半径 2.0
        case 10: return new Vector2(0.5f, 0f);   // 脉冲地雷：需踩进 1.6 触发半径
        default: return new Vector2(3.0f, 0f);
    }
}
```

然后是 4 条组合技（各自断言 `keys.Count == 1 && keys[0] == expected`，即"只触发应触发的那一条"）、
7 项属性强化（伤害/攻速/范围/拾取/经验/生命/移速各自读回生效）、以及等级封顶与 **满级后不再提供已满选项**。

## 本轮修掉的缺陷① `ApplyCharacter()` 在当前值上连乘，换角色会叠乘（主）

旧实现（`PlayerController.ApplyCharacter`）：

```csharp
stats.maxHp            *= def.hpMult;
stats.moveSpeed        *= def.speedMult;
stats.attackDamageMult *= def.damageMult;
stats.cooldownMult     *= def.cooldownMult;
stats.pickupRadius     *= def.pickupMult;
stats.areaMult         *= def.areaMult;
stats.xpGainMult       *= def.xpMult;
```

每一次调用都是"在**当前**数值上再乘一遍"。只要 `ApplyCharacter` 被调用两次，
守卫那份 `hpMult = 1.35` 就会变成 `1.35 × 1.35 = 1.8225`。

而"被调用两次"在真实操作里是很普通的路径：

- 开局应用一次（从存档读到的角色）；
- 局内换人/重生/重开局再应用一次。

结果就是**生命上限凭空多出 35%**，而且这个错误**不会报任何东西**——
玩家只会觉得"这角色好像有点肉"，而平衡数值表被彻底污染。

更糟的是它是**单向累积**的：切到的角色越多，数值越离谱，且不可逆。

修：首次应用时把基准值克隆一份，之后**永远从基准值重算**，而不是在当前值上再乘：

```csharp
private PlayerStats _characterBaseStats;
private bool _hasCharacterBase;
...
if (!_hasCharacterBase) { _characterBaseStats = stats.Clone(); _hasCharacterBase = true; }
stats.maxHp = _characterBaseStats.maxHp * def.hpMult;   // 从基准值重算
// …其余 6 项同理
```

配套在 `PlayerStats` 上加了 `public PlayerStats Clone() => (PlayerStats)MemberwiseClone();`。

> 这里刻意没有把基准值做成"每次 Build 重新取默认值"，而是**首次快照**：
> 因为 `stats` 可能已经被局内升级改过（M23 后面那 7 项属性强化正是干这个的），
> 每次重取默认值会把玩家的强化清零。**基准 = 进入第一局时的状态**，这是唯一不会丢东西的语义。

## 本轮修掉的缺陷② `net_smoke.py` 默认指向单机产物，联机冒烟一直在测错二进制（工具链）

这是我在这轮回归时真正卡住的地方，值得单独记一笔。

`net_smoke.py` 里：

```python
DEFAULT_EXE = os.path.join(ROOT, "StarPixelRogue", "Build", "Smoke", "StarPixelRogue.exe")
```

而联机版构建产物在 `Build/SmokeNet/`。于是"联机双开冒烟"实际启动的是**两个单机客户端**，
产物输出长这样：

```
网络玩家数        0                0
同步波次         -1               -1
同步击杀         -1               -1
运行时异常：房主 0 条 / 客户端 0 条
结论：未通过
  - 房主全程最多只看到 0 名玩家（期望 ≥2，说明客户端未成功加入）
  - 客户端同步击杀为 -1——SyncVar 未把击杀同步过来（联机同步链路可疑）
```

**两端都正常启动、正常跑满 60 秒、零异常**，只是所有网络指标都是"空的"。
这份报告读起来像"联机同步坏了"，实际上**联机版本压根没被运行过**。
排查时我一度去翻 `HeadlessNetBuild` 的方法名（确实也踩了一次：正确名是 `HeadlessNetBuild`，
而 `HeadlessBuildNet` 会 `executeMethod could not be found` 并 exit 1），
但这只是第一层；真正让结果归零的是**产物路径**。

修两处：

1. `DEFAULT_EXE` 改为**优先选 `SmokeNet`**，不存在才回退单机；
2. 显式传单机产物时**直接拒绝运行并返回退出码 2**，而不是给出一个高度误导的"同步失败"。

```python
if os.path.normcase(os.path.abspath(os.path.dirname(exe))) == \
        os.path.normcase(os.path.abspath(os.path.dirname(SOLO_EXE))):
    print("[错误] 这是单机产物（Build/Smoke），不含联机装配，跑联机冒烟只会得到假故障。")
    print("       联机产物在 %s。" % rel(NET_EXE))
    return 2
```

修后用正确的联机产物重跑，60 秒双开**通过**：玩家 1/2、峰值 2/2、波次 7/6、击杀 140/104、双端遇到 Boss、异常 0。

> **这个缺陷比缺陷①更值得记住**：缺陷①只是让数值错了，而这个缺陷会让**验证本身失真**。
> 一个"永远报失败"的探针，和一个"永远报成功"的探针，危害是同一级别的——
> 两者都会让人得出错误结论，只是方向不同。

## 探针侧的三个坑（不是产品缺陷，但会让结果假绿/假红）

### 坑① `GetInstanceID()` 在 `SetActive(true)` 之后不保证立刻有值

第一版 `SpawnTarget` 是这么写的：

```csharp
var before = new HashSet<int>(em.ActiveEnemies.Select(e => e.GetInstanceID()));
em.SpawnAdd(pos, EnemyKind.Tank, 500000f, 0f);
var target = em.ActiveEnemies.First(e => !before.Contains(e.GetInstanceID()));
```

新对象的 `GetInstanceID()` 在这条路径上返回 **0**，而 `before` 里可能恰好没有 0 这个值，
于是"新增的那个"被匹配成了场上**已存在的某个敌人**。表现是：雷环刃死活打不出伤害，
但临时加的 `[Diag6]` 日志显示 `hp0=500000 hpNow=500000 dealt=0 targetActive=True activeList=6`——
目标活着、列表里也确实有 6 个对象，只是**我打的那个对象根本不是我以为的那个**。

修：用**列表长度差**来定位新元素，不碰 instance id：

```csharp
var list = em.ActiveEnemies;
int n0 = list.Count;
em.SpawnAdd(pos, EnemyKind.Tank, 500000f, 0f);
if (list.Count <= n0) return null;
var target = list[list.Count - 1];
```

### 坑② 每把武器测完必须把目标清掉

不清的话，下一把武器搜寻"最近目标"时会命中上一轮遗留的残血对象，
于是"某个武器打 0 伤害"的原因其实在上一把武器身上。修法：

```csharp
target.gameObject.SetActive(false);
em.RemoveEnemy(target);
yield return null;
yield return null;
```

第二个 `yield return null` 是必须的——`RemoveEnemy` 只从列表摘掉，销毁要等帧末。

### 坑③ 每场景前清空存档，但 APPLY 例外

驱动脚本原本在**每个**场景前都 `clear_artifacts(data_dir)`，把 SELECT 刚写下的 `save.json` 删了，
于是 APPLY 的跨进程读回拿到 `-1`，报告 `0/0 乘区匹配`。修：

```python
# APPLY 要读 SELECT 写下的存档，绝不能在这里清掉
if scene != "APPLY":
    clear_artifacts(data_dir)
```

同时 `--only APPLY` 时自动补跑 SELECT，否则单跑 APPLY 必然失败。

## 判据的区分力（本轮实测验证了两次）

区分力 = "把修复回退掉，判据必须精确变红，且**只有它**变红"。

### 验证① 回退 `ApplyCharacter` 的基准值重算

把 `stats.maxHp = _characterBaseStats.maxHp * def.hpMult;` 改回 `stats.maxHp *= def.hpMult;`：

| 判据 | 回退前 | 回退后 |
| --- | --- | --- |
| 乘区匹配 | 7/7 | 7/7（首次应用本来就对） |
| 起始武器已替换 | 是 | 是 |
| 重复应用幂等 | 是 | **否** |
| 换角色不叠乘 | 是 | **否** |
| 换角色差异 | 无 | **换角色后生命上限 实际 148.5 期望 110** |

`148.5 = 110 × 1.35`，正好是"守卫叠了一次"的结果——**判据自己把病因指出来了**。
其余判据全部保持绿色，说明它测的是这一处，不是别处。

### 验证② 去掉 `CharacterManager.SelectedId` setter 里的 `save.Save()`

| 判据 | 回退前 | 回退后 |
| --- | --- | --- |
| 选中态四处一致 | `id=1 卡片=是 菜单=是 落盘=是` | `id=1 卡片=是 菜单=是 落盘=**否**` |
| 进程外落盘（Python 判） | 1 | **None** |
| APPLY 全组 | 通过 | **通过 → 失败**（跨进程读回 -1） |

这里有两层区分力：**探针内部**把"落盘=否"单独拆出来报，**进程外**的 Python 又独立判了一次。
两点同时变红，说明"落盘"这个判据不是探针自己说了算的。

## 实测结果（三个场景，异常均 0）

| 场景 | 关键输出 |
| --- | --- |
| SELECT | 面板已建出 是 / 已点击选择 是 / 选中态四处一致 `id=1 卡片=是 菜单=是 落盘=是` / 关闭按钮有效 是 |
| APPLY | 乘区匹配 **7/7** / 乘区差异 **无** / 起始武器已替换 是（id=4）/ 重复应用幂等 是 / 换角色不叠乘 是 / 换角色差异 无 |
| PROGRESSION | 武器开火覆盖 **10/10** / 打出伤害 **10/10** / 组合技达标 **4/4** / 属性强化达标 **7/7** / 等级封顶 是 / 满级不再供选 是 |

10 把武器各自打出的伤害（50 万血目标，3.4 秒）：
`1=144 2=198 3=52 4=32 5=132 6 通过 7=100 8=20 9=60 10=90`
（数值只用来证明"确实打到了"，不用于平衡判定——`balance_sim.py` 负责那件事。）

## 一个观察，不算缺陷

组合技同时满足多条时，实测到 `火+风暴+圣光 伤害倍率=1.4`。

`ComboManager` 的实现是**后写覆盖**，不是累加（加法的话应是 1.9）。
这是可观察的产品事实，不是 bug——但"三条组合技同时满足，收益不如两条"这个语义，
要么是有意设计（防止叠满），要么是没想清楚。本轮只把它记进报告，**不改行为**：
没有设计意图依据的情况下改平衡，比留下一个已知语义更糟。

## 回归（异常均 0）

| 项 | 结果 |
| --- | --- |
| 单机冒烟 45s | 通过 |
| 合规门 6 场景 | 通过（同意前请求 0、同意后拉取 1、L10N 裸键 无、中文残留 无） |
| 防沉迷 MINOR + ADULT | 通过 |
| 持久化 SAVE + TELEMETRY | 通过 |
| 设置 M22 六场景 | 通过 |
| 云存档 5 场景 + UI | 通过（A~E 零请求/静默降级、双语状态正确，异常 0） |
| **联机双开 60s** | **通过（玩家 1/2、峰值 2/2、波次 7/6、击杀 140/104、双端 Boss 是）** |
| 静态审计 | ERROR 0 / WARN 0 / INFO 8 |

> 联机这一项本轮**先失败后通过**，中间修的是工具链（见缺陷②）。这一项的"通过"是靠
> 换到正确产物拿到的，不是靠改判据放水。

## 教训沉淀

1. **"生效了"和"存下来了"和"重启后还在"是三件事。**
   这一轮把它们拆成三个独立断言，缺陷①才被逼出来——它让"首次应用"完全正确，
   只在"第二次应用"才错。合成一个布尔的话，只会得到"角色系统不行"，然后去翻最不相关的代码。

2. **连乘是最隐蔽的一类数值缺陷。**
   它每次都"看起来对"（每一次乘都是对的），只在第二次暴露，而且**方向单一、不可逆**。
   凡是 `x *= def.someMult` 形态的代码，都该问一句：这段会被调用两次吗？

3. **探针的失败必须能被信任，探针的通过也必须能被信任。**
   缺陷②让一个探针**永远报失败**。它和"永远报成功"是同一级别的危害：
   都会把人引向错误的结论。所以探针自身也需要判据——这里用的是"误用产物就拒绝跑，退出码 2"。

4. **定位新对象不要用 `GetInstanceID()` 做前后差集。**
   `SetActive(true)` 之后它不保证立刻有值（返回 0）。用列表长度差，或者用显式返回的引用。

5. **测试几何要按被测对象的形状来给。**
   雷环刃打的是"半径 2.0 的环"，地雷要"踩进 1.6 才炸"。
   把一个目标统一放在 3.0 米外，会让三把武器**看起来全坏了**，而产品代码一行没问题。
   **报 0 伤害时，先怀疑探针的站位。**

6. **别把没被证明的事说成被证明了。**
   组合技覆盖这件事我只观察到"1.4 = 后写覆盖"，没找到设计文档说它该是 1.4 还是 1.9，
   所以只记录、不改动。同理，缺陷②我没有把它写成"产品同步缺陷"——它是工具链缺陷。
