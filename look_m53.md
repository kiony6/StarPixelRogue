# M53 · 看得见的那一层：页面美化与技能特效

起因是一句很朴素的要求：**「优化下页面，美化下技能特效，使其更加炫酷。」**

这句话有个特点——它指向的东西，到 M52 为止**一次也没有被任何判据看着**。
全部 51 个里程碑的验证都在 `-nographics` 下完成：能证明"跑通"，证明不了"看得见"。
M52 结尾自己写下的那句是"到今天为止，这个游戏一次也没有被人的眼睛看过"。
本轮就是把这一层补齐：把"炫酷"拆成能断言的量，再一遍遍证明它们真的会红。

---

## 一、做了什么

### 1. 程序化形状工厂扩容（`Core/SpriteFactory.cs`）

M53 之前，全工程只有两张程序化图：**纯白方块**和**按色量化的方块**。
所有"发光""扩散""闪光"都是拿方块缩放冒充的——放大到一半就变成一大片雾块。

新增六个一次性生成、**按 key 缓存**的形状：

| 形状 | 规格 | 用在 |
|---|---|---|
| `MakeRoundPanel` | 48×48，圆角 12，带 9 宫格 border | 所有面板 / 按钮 / 进度条的底 |
| `MakeGloss` | 8×48，竖向高光渐变 | 叠在面板顶部做出"打了一束光" |
| `MakeGlow` | 64×64，径向柔光 `pow(1-d, 2.2)` | 拖尾、光晕、枪口 |
| `MakeRing` | 64×64，空心环，峰值在 0.82 半径 | 冲击波、Boss 前摇警示圈 |
| `MakeStar` | 48×48，四芒星（两条 `exp` 光条 + 核心） | 命中点、爆炸核心 |
| `MakeSparkPixel` | 16×16，点过滤，实心 + 半透中圈 | 崩解火星、枪口焰 |

**为什么必须 `PrecacheShapes()` 预生成**（`GameBuilder.PrepareStage` 末尾）：
`gpu_probe` 的 BURST 判据是"连放 24 轮后纹理增量为 0"。
形状的脸谱是固定的（key 不随颜色 / 半径变化），因此"每个 key 只造一次"能成立；
但如果不预生成，第一次拖尾的构造会落在**基线之后**，被判成"每轮都在泄漏"——
正经的一次性构造会被自己的判据打红。放进 `PrepareStage` 建一次，它们就都落进基线了。

### 2. 调色板与 uGUI 构件升级（`Core/UIFactory.cs`）

新增 `UIFactory.Palette`（深空底 / 抬高 / 高亮 / 星能青 / 紫罗兰 / 战利金 / 危险红 / 生机绿 / 正文 / 次要 / 描边）
与 `Tint(color, bright, alpha)`（在指定色上叠亮度，不改原色值）。

- `CreateButton` 重做：圆角九宫格底 + 顶部高光层 + 左侧强调条 + 描边文字
- 新增 `CreateRoundPanel` / `CreateTitle` / `CreateDivider` / `CreateBar`（进度条带外发光层）
- `CreateSlider` 的填充条也换成圆角图——**进度类控件最显眼的一处锯齿**
- 新增 `GradientRich`（逐字 `<color=#RRGGBB>` 富文本）与 `Hex`

#### 顺手修掉一处真实缺陷：悬停反而变暗

原 `CreateButton` 的 `highlightedColor` 给的是**绝对色值** `(0.30, 0.34, 0.48)`。
uGUI 的状态色是**乘数**——它会与本就偏暗的底色相乘，结果是"鼠标放上去按钮更暗"，
往背景里缩，与"可点"的直觉正好相反。改成 `normalColor = white`、
`highlighted = (1.35, 1.38, 1.55)` 之后，悬停才是"亮起来"。

### 3. 池化特效系统（`Effects/Fx.cs` + `Effects/FxSprite.cs`）

六个发射器：`Trail` / `Flash` / `Shockwave` / `Glow` / `Burst` / `Spray`。

**为什么不继续往 `TempEffect` 里塞**：`TempEffect` 的每个实例都是 `new GameObject`
建在场景根上，而 `VisualProbe` 的 LEFTOVER 判据写死了"重开前存量必须恰好 3"。
把高频特效挂进去，那条判据就得跟着改数字——而"改数字"正是最难被复审发现的一类改动：
下一次有人再塞一个，数字又对了，缺陷却留下了。所以新特效**走对象池**
（复用既有的 `Core.PoolManager`，它的 `DestroyAll` 已被 `TeardownWorld` 调用）。

三条硬约束：
1. 活跃上限 `MaxActive = 240`（一轮 12 枚散射每 0.03s 各留一颗拖尾、满屏 60 敌人各留 2 颗死
   亡火星，峰值落在 200 上下；240 留一点余量）。到上限时回收**最早**的一颗。
2. 精灵一律取缓存图，颜色交给 `SpriteRenderer.color`——**不存在按色生成纹理**的新路。
3. 池不可用时静默跳过：宁可少放一颗，也不在联机客户端 / 拆世界过程中抛异常。

### 4. 特效接线

| 位置 | 改动 |
|---|---|
| `Projectile` / `Boomerang` | 按**时长**（0.035s / 0.045s）而非按帧留拖尾；死亡加 Flash |
| `HomingMissile` | 尾焰 `Trail`（0.03s，橙）+ `Spray` 火星（0.07s）+ 死亡 Flash + Burst |
| `Mine` | 待引爆时每 0.32s 垫一圈 `Glow`；引爆升级为两层 Shockwave + Flash + Burst(12) + Glow |
| `Enemy` | 受击节流火星（0.09s，2 颗）；死亡崩解（精英 16 颗 + Shockwave + Glow） |
| `PlayerController` | 受伤 `Spray` 5 颗 + Flash；`FireNova` 由单环升级为两层 Shockwave + Flash + Glow + 沿环外缘 10 处 Spray；每发枪口 `Spray` 2 颗 |
| `ChainBolt` | 电弧由直线改为**带抖动的折线**（最多 4 节）；命中点加 Glow + Spray |
| `TempEffect` | 警示圈改用 `MakeRing()`——此前是"一大片半透明地板"，现在是危险环 |

### 5. 页面美化

- **`UI/StarfieldUI.cs`**（新）：uGUI 背景星场。刻意**挂在现有 Canvas 下当子节点**，
  不新建 Overlay——M51 刚证明过"两块画布同层 → 谁压谁听 Unity 内部排序"能把一颗按钮变成摆设。
  用 `Time.unscaledTime` 驱动，结算时 `timeScale = 0` 不会冻住。
- **`Core/StarfieldWorld.cs`**（新）：世界内视差星空，64×48 地砖取模绕回实现无限铺陈，
  `k = 0.55~0.9` 视差。它是"玩家正在移动"的唯一参照物。
- **`UI/MainMenu.cs`**：星场 + 压暗 `Veil`、标题改渐变（青→紫）、战绩面板改圆角 +
  分隔线 + **左标签 / 右数值两栏**、按钮按语义配色（开始=绿 / 查看=青紫 / 退出=红）。
  `CurrentCharacter` 保持纯文本（BuildProbe 逐字比对），节点名一个没动。
- **`UI/LevelUpManager.cs`**：选卡由"440×80 纯色方块 ×3"改为圆角卡 + 卡片背板 +
  渐变标题 + 分隔线，且**按 `kind` 上稀有度色**（`>=200` 武器进阶=紫 / `>=100` 新武器=金 /
  其余属性强化=青），附本地化稀有度标签。
  分档条件**刻意不另写一套**——和 `UpgradeCatalog.ApplyKind` 用同一个 `kind` 判据，
  免得视觉档位与逻辑档位哪天改一处漏一处。

---

## 二、新增判据（`Dev/VisualProbe.cs` 三个场景）

| 场景 | 断言 |
|---|---|
| `FX` | 连放 24 轮纹理零增量 / 确实放出来了 / 不越上限 / 全部归还 / 收支守恒 / 拆世界清零（6 条） |
| `SHAPE` | 六个都有图 / 两次取用同一实例 / 六个互不相同 / 重复预热零增量（4 条） |
| `SKIN` | 三档 kind 各上各的色 / 三色互不相同 / 标签已本地化 / 卡片真点得到（4 条） |

**区分力设计**：
1. 「纹理增量 == 0」与「确实放出来了」必须**同时**成立——只测前者的话，
   "一颗都没生成"的实现同样拿 0 增量。
2. 「收支守恒」（取用 − 回收 == 在场）单独成条：只看"最后归零"的话，
   "登记表漏减"这种缺陷会被最后的清零掩盖掉。
3. 「卡片可点」走 `UIHitTest.Click`（真实 `GraphicRaycaster` 射线），
   不是裸调 `onClick.Invoke()`——M48 的教训：卡片上叠了高光层 / 色条之后
   还能不能点，只有走真实射线才看得出。

## 三、区分力注入（`m53_inject.py`，六组）

| 组 | 注入 | 实际失败面 |
|---|---|---|
| Q1 | 什么都不改（对照） | **全绿** |
| Q2 | 形状精灵不再缓存（退回 M30 之前） | 纹理零增量 |
| Q3 | 取消同屏上限 | 未越过上限 |
| Q4 | 回收不再计数 | 收支守恒 |
| Q5 | 三档稀有度一律同色 | 分档正确、三色互不相同 |
| Q6 | 卡片被一层全透明遮罩盖住 | 卡片可点 |

六组**失败面互不相交**：
- Q3 只红「未越过上限」（在场数照样归零、收支照样守恒）
- Q4 只红「收支守恒」（在场数照样归零、也没越上限）
- Q5 只红"画得对不对"，Q6 只红"点得到点不到"

相交就说明其中一条是在陪跑。

---

## 四、本轮暴露的既有判据问题（一处）

`gpu_probe` RESTART 的护栏「确实生成特效」写死了整串 `4/4 局（末局 6 个）`。
它数的是**全部 `SpriteRenderer`**——M53 之前这两帧里唯一会新增渲染器的就是
`DropEffects()` 那一批，宽口径碰巧也对。M53 之后对局随时在放池化粒子
（同样带 `SpriteRenderer`），末局从 9 个涨到 19 个，护栏因此**假红**：
它红的原因是"画面上多了东西"，而那正是本轮要的效果。

修法两步：
1. `GpuProbe` 收窄为 `CountEffectObjects()`——只数 `TempEffect` + `BoltSeg` / `BoltFlash`，
   即它原本声称在数的那三种来源；
2. 判据改判开头（`4/4 局`），末局个数降级为复读信息。
   那个数是"电弧拆几节"的**观感副产品**，不是护栏要看的东西；
   写死它会让每一次视觉调整都要改判据。

---

## 五、回归

- 三套构建（无头 / 无头联机 / Mono 带画面）：`error CS` = 0
- 静态自检 30 项：ERROR 0 / WARN 0 / INFO 27；自检夹具全通过
- 18 个专项探针（分 3 片串行）：全部通过
- 单机长局 / 联机长局：通过

## 六、新增与改动文件

**新增**：`Effects/Fx.cs`、`Effects/FxSprite.cs`、`UI/StarfieldUI.cs`、
`Core/StarfieldWorld.cs`、`m53_inject.py`、本报告

**改动**：`Core/SpriteFactory.cs`、`Core/UIFactory.cs`、`Core/Localization.cs`
（新增 `upgrade.rarity.*` 三键 × 中英）、`GameBuilder.cs`、
`Combat/Skill/{Projectile,Boomerang,HomingMissile,Mine,ChainBolt}.cs`、
`Effects/TempEffect.cs`、`Enemy/Enemy.cs`、`Player/PlayerController.cs`、
`UI/{MainMenu,LevelUpManager}.cs`、`Dev/VisualProbe.cs`、`Dev/GpuProbe.cs`、
`visual_probe.py`、`gpu_probe.py`
