# M8 调参方案清单（待用户选定后实施，当前未改任何代码）

依据：`balance_findings_m8.md`（480 局模拟结论）。以下改动点均已在工程中定位到具体文件/行。

---

## 方案 A：「存活 900s = 通关」

核心思路：接受「游侠 15 分钟内不死」为设计预期，把 900s 从观测窗变成胜利条件。
难度参数（EnemyManager）完全不动。

1. **`UI/HUDController.cs`**
   - `Update()` 第 107 行 `_time += Time.deltaTime;` 之后加胜利判定：
     `if (!_runEnded && _time >= 900f) OnVictory();`（常量建议提为 `private const float ClearTime = 900f;`）
   - 新增 `OnVictory()`，结构照抄现有 `OnGameOver()`（第 167-176 行）：置 `_runEnded`、
     收 Boss 条/红晕/toast，然后走结算事件——timeText 显示 "CLEAR!" 而非 "GAME OVER"。
2. **`Core/EventBus.cs` 第 33 行**：`OnRunEnded` 增加 `bool victory` 参数
   （`Action<int, float, int, int, bool>`）。全工程只有 GameOverPanel 一个订阅方，改动面可控。
   - 备选：新增独立 `OnRunVictory` 事件，不动现有签名——如果你倾向零破坏，选这个。
3. **`UI/GameOverPanel.cs` `ShowRun()` 第 34 行**：按 victory 切换标题
   （`gameover.victory` / `gameover.title`）、Telemetry 的 reason 记 "victory" / "death"；
   按钮区不变（联机仍只给「离开对局」）。
4. **`Core/Localization.cs`**：补 `gameover.victory` 中英两键（如「通关！」/"STAGE CLEAR!"）。
5. **联机决策点**：胜利判定放哪。最简方案=各客户端本地计时触发（timeText 本就是本地计时，
   与现有联机死亡同步不冲突）；若要房主权威需经 NetSession 广播，工作量大一些。建议先用本地计时。
6. **可选**：`Core/SaveManager.cs` 的 SubmitRun 增加通关计数，CodexPanel 统计页加一行「通关 N 次」。

**工作量**：约 4 个文件、每处 <10 行；**风险低**（不碰任何数值）。

---

## 方案 B：「死亡是正常结局」

核心思路：不加敌数（实验证明只拖慢推进不制造死亡），改打 XP 停滞点——
Wave 30 后难度递增从 0.10/波 提到 0.18/波，让线性难度在 build 停止成长后不久追平。

1. **`Enemy/EnemyManager.cs` 第 87 行**（唯一难度总阀）：
   ```csharp
   // 现：_difficultyScale = 1f + (_wave - 1) * 0.10f;
   _difficultyScale = _wave <= 30
       ? 1f + (_wave - 1) * 0.10f
       : 1f + 29f * 0.10f + (_wave - 30) * 0.18f;
   ```
   作用于三系敌人 HP（第 203/205/207 行 ×scale），不动速度与接触伤害。
2. **`Player/CharacterDef.cs` `CreateBulwark()` 第 54 行**：`cooldownMult = 0.92f` → `0.88f`（G2，
   模拟中唯一把守卫撞限率 82%→100% 的杠杆）。
   - 若想保留「守卫更高风险」的角色区分度，改用 G1：`speedMult 0.90→0.95`、`xpMult 1.0→1.10`，
     但模拟显示 G1 只抬高死亡分布下限（P10 波 38→41），中位不变——要有心理预期。
3. **顺手修速度阶跃**（M8 报告遗留）：第 207 行疾行怪 `speed = 3.8f + _wave * 0.08f`
   Wave 23 起超过玩家基础移速 5.6 → 封顶 `Mathf.Min(3.8f + _wave * 0.08f, 5.2f)`，
   或给玩家基础移速随波次成长。二选一，建议封顶（改动 1 行）。
4. **先模拟后上工程**：分段难度曲线是**未经模拟验证的新候选**。流程应为：
   在 `balance_sim.py` 加分段 diff 候选（如 D4）→ `balance_experiments.py` 跑 40 种子
   → 确认游侠死亡波次落进 12–18 分钟区间、守卫不出现过早秒杀 → 再改工程常量。
   （G2 已模拟过，可直接用。）

**工作量**：1 处数值 + 1 处角色乘区 + 1 处速度封顶；**风险中**（改变手感，需真机验证）。

---

## 共同注意事项

- 两个方案**不互斥**：可以 A+B 同做（900s 通关 + 30 波后加压 + 守卫 CD），
  模拟显示 B 单独上时游侠在窗内仍 0 死亡，A 正是给这种「build 碾压」局面一个体面出口。
- sim 未建模拾取半径（守卫 pickupMult 0.85）与精英波动细节，结论方向可信、数值需真机复核
  （README 的 M4.5/M5 测试清单仍适用）。
- 改动落地后建议重跑一次 `balance_experiments.py` 对齐工程常量，保持 sim=工程单一数据源。
