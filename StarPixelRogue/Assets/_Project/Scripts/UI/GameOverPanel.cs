using UnityEngine;

namespace StarPixel.UI
{
    /// <summary>
    /// 结算面板（M5）：一局结束时展示成绩、保存纪录，并提供 重新开始 / 返回主菜单。
    /// - 单机：暂停（timeScale=0）后展示；纪录经 <see cref="Core.SaveManager"/> 落盘；
    /// - 联机：不暂停（观战继续），仅提供「离开对局」（经 NetBackend.Stop 断线回大厅）；
    /// - 青少年模式到点的强制退出由 <see cref="Core.PlaytimeGuard"/> 直接处理，不走本面板。
    /// </summary>
    public class GameOverPanel : Core.MonoSingleton<GameOverPanel>
    {
        private GameObject _root;
        private bool _showing;

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("GameOverPanel").AddComponent<GameOverPanel>();
        }

        protected override void Awake()
        {
            base.Awake();
            Core.EventBus.OnRunEnded += ShowRun;
        }

        void OnDestroy()
        {
            Core.EventBus.OnRunEnded -= ShowRun;
            // 防御：面板被销毁时恢复时间（优先走 HitstopManager，避免与顿帧/选卡暂停的 timeScale 互相踩踏）
            // M41：兜底写死 1f 会在加速档下把基准拽回 1（同 LevelUpManager 的注释）
            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
            else Time.timeScale = Core.HitstopManager.BaseScale;
        }

        private void ShowRun(int wave, float time, int kills, int level, bool victory)
        {
            if (_showing) return;
            _showing = true;

            bool net = Net.NetSession.IsNetworked;

            // 纪录与埋点（联机同样保存本地成绩）
            Core.SaveManager.Ensure();
            var save = Core.SaveManager.Instance;
            bool newRecord = save != null && save.SubmitRun(wave, time, kills, level, Player.CharacterManager.SelectedId, victory);
            Core.Telemetry.Log(Core.Telemetry.EvRunEnd,
                "wave", wave, "time", Mathf.RoundToInt(time),
                "kills", kills, "level", level,
                "reason", victory ? "victory" : (net ? "net_death" : "death"));

            // 单机暂停世界（联机保持心跳）。改由 HitstopManager 统一掌管 timeScale
            if (!net)
            {
                if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(true);
                else Time.timeScale = 0f;
            }

            if (_root != null) Core.UIFactory.Destroy(_root);
            var canvas = Core.UIFactory.CreateCanvas("GameOverCanvas");
            _root = canvas.gameObject;

            var panel = Core.UIFactory.CreatePanel(_root.transform, "Panel", Vector2.zero,
                new Vector2(560, 420), new Color(0.08f, 0.06f, 0.10f, 0.96f));

            Core.UIFactory.CreateText(panel.transform, "Title",
                Core.Localization.T(victory ? "gameover.victory" : "gameover.title"),
                new Vector2(0, 160), 40);

            if (newRecord)
                Core.UIFactory.CreateText(panel.transform, "NewRecord",
                    Core.Localization.T("gameover.new_record"), new Vector2(0, 112), 22);

            Core.UIFactory.CreateText(panel.transform, "Stats",
                Core.Localization.T("gameover.stats", wave, level, kills,
                    Core.SaveManager.FormatTime(time)),
                new Vector2(0, 60), 20);

            if (!net)
            {
                Core.UIFactory.CreateButton(panel.transform, "RestartBtn",
                    Core.Localization.T("gameover.restart"), new Vector2(-130, -50), new Vector2(220, 56),
                    OnRestart, 20, new Color(0.20f, 0.45f, 0.28f, 1f));
                Core.UIFactory.CreateButton(panel.transform, "MenuBtn",
                    Core.Localization.T("gameover.menu"), new Vector2(130, -50), new Vector2(220, 56),
                    OnMainMenu);
            }
            else
            {
                Core.UIFactory.CreateButton(panel.transform, "LeaveBtn",
                    Core.Localization.T("gameover.leave"), new Vector2(0, -50), new Vector2(260, 56),
                    OnLeaveMatch);
            }
        }

        /// <summary>
        /// 收起面板并解冻世界。M46 起由 <c>GameBuilder.TeardownWorld</c> 也调用一次
        /// （见那里注释：结算面板不属于被拆的世界，只靠调用方自觉收是不够的）。
        /// 幂等：面板已收起时再调一次只是重复一次 SetPaused(false)。
        /// </summary>
        public void CloseAndResume()
        {
            // 恢复时间缩放（优先走 HitstopManager，与 GameOver 冻结/顿帧保持唯一写者一致）
            // M41：兜底写死 1f 会在加速档下把基准拽回 1（同 LevelUpManager 的注释）
            if (Core.HitstopManager.Instance != null) Core.HitstopManager.Instance.SetPaused(false);
            else Time.timeScale = Core.HitstopManager.BaseScale;
            _showing = false;
            if (_root != null) { Core.UIFactory.Destroy(_root); _root = null; }
        }

        /// <summary>
        /// M19：外部（青少年模式用时耗尽的开局拦截）调用——收起结算面板。
        /// 与 <see cref="CloseAndResume"/> 的区别：不假设当前处于暂停态，
        /// 因此不碰 timeScale（拦截发生在建世界之前，世界此时尚未被冻结）。
        /// </summary>
        public void Hide()
        {
            _showing = false;
            if (_root != null) { Core.UIFactory.Destroy(_root); _root = null; }
        }

        private void OnRestart()
        {
            Core.Telemetry.Log("run_restart");
            CloseAndResume();
            GameBuilder.TeardownWorld();
            GameBuilder.Build();
        }

        private void OnMainMenu()
        {
            CloseAndResume();
            GameBuilder.TeardownWorld();
            MainMenu.Ensure();
            MainMenu.Instance.Show();
        }

        private void OnLeaveMatch()
        {
            CloseAndResume();
            var backend = Net.NetBackend.Instance;
            if (backend != null) backend.Stop();       // 触发断线回调 → 重建联机大厅
        }
    }
}
