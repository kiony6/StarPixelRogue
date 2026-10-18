using System;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 轻量全局事件总线（原型用，解耦各系统；后续可替换为更严谨的事件系统）。
    /// </summary>
    public static class EventBus
    {
        public static event Action<int> OnEnemyKilled;            // 累计击杀数
        public static event Action<float> OnPlayerHealthRatio;    // 0..1 血量比例
        public static event Action<int> OnWaveChanged;            // 当前波次
        public static event Action OnGameOver;                    // 游戏结束
        public static event Action<float> OnSurvivalTime;         // 累计存活秒数
        public static event Action<float> OnXpChanged;            // 当前经验比例 0..1
        public static event Action<int> OnLevelUp;                // 升级后等级
        public static event Action<float, Vector3> OnDamaged;     // 受到伤害数值与位置（跳字用）

        // —— M3 新增：Boss / 区域 / 玩家受击 ——
        public static event Action<float, float> OnBossSpawned;   // Boss 出现（当前HP, 最大HP）
        public static event Action<float> OnBossHp;               // Boss 血量比例 0..1
        public static event Action OnBossDefeated;                // Boss 被击败
        public static event Action<int> OnZoneChanged;            // 进入新区域
        public static event Action OnPlayerHurt;                  // 玩家受伤（音效/反馈）

        // —— M3.5 新增：震屏 / 精英怪 ——
        public static event Action<float, float> OnShakeRequest;  // 请求震屏（强度, 时长）
        public static event Action<Vector3> OnEliteKilled;        // 精英怪被击杀（位置）
        public static event Action OnWeaponsChanged;              // 武器列表变化（解锁/升级）

        // —— M5 新增：一局结束（结算面板/存档/埋点用） ——
        // M8 方案 A：末位 victory 区分「死亡结束」与「存活 ClearTime 通关」
        public static event Action<int, float, int, int, bool> OnRunEnded; // 波次, 时长, 击杀, 等级, 是否通关

        // —— M7 新增：组合技激活（键如 "fire_storm"，供 HUD 提示/音效） ——
        public static event Action<string> OnComboActivated;

        public static void RaiseEnemyKilled(int total) => OnEnemyKilled?.Invoke(total);
        public static void RaisePlayerHealthRatio(float ratio) => OnPlayerHealthRatio?.Invoke(ratio);
        public static void RaiseWaveChanged(int wave) => OnWaveChanged?.Invoke(wave);
        public static void RaiseGameOver() => OnGameOver?.Invoke();
        public static void RaiseSurvivalTime(float t) => OnSurvivalTime?.Invoke(t);
        public static void RaiseXpChanged(float ratio) => OnXpChanged?.Invoke(ratio);
        public static void RaiseLevelUp(int level) => OnLevelUp?.Invoke(level);
        public static void RaiseDamaged(float amount, Vector3 pos) => OnDamaged?.Invoke(amount, pos);

        public static void RaiseBossSpawned(float hp, float max) => OnBossSpawned?.Invoke(hp, max);
        public static void RaiseBossHp(float ratio) => OnBossHp?.Invoke(ratio);
        public static void RaiseBossDefeated() => OnBossDefeated?.Invoke();
        public static void RaiseZoneChanged(int zone) => OnZoneChanged?.Invoke(zone);
        public static void RaisePlayerHurt() => OnPlayerHurt?.Invoke();
        public static void RaiseShake(float magnitude, float duration) => OnShakeRequest?.Invoke(magnitude, duration);
        public static void RaiseEliteKilled(Vector3 pos) => OnEliteKilled?.Invoke(pos);
        public static void RaiseWeaponsChanged() => OnWeaponsChanged?.Invoke();

        // M5：一局结束（HUD 汇总本局数据后触发）；M8：victory=true 表示达标通关而非死亡
        public static void RaiseRunEnded(int wave, float time, int kills, int level, bool victory) =>
            OnRunEnded?.Invoke(wave, time, kills, level, victory);

        // M7：组合技激活（仅本地玩家触发，HUD/音效消费）
        public static void RaiseComboActivated(string key) => OnComboActivated?.Invoke(key);
    }
}
