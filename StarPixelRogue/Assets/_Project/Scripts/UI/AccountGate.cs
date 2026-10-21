using UnityEngine;

namespace StarPixel.UI
{
    /// <summary>
    /// 登录闸门（M54）：主菜单前面的那道检查。
    /// 启动链路变成 <b>合规门 → 登录 → 主菜单</b>，本类是这三者之间的那一段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么单独一层，而不是把判断写进 MainMenu.Show()：
    /// 「进主菜单」有四个入口（启动、一局结束返回、青少年模式强制退出、联机断线回退），
    /// 判断写在其中任何一处，另外三处都会绕过它。闸门只有一个，四个入口都得过。
    /// </para>
    /// <para>
    /// 记住密码的自动登录走的是<b>完整校验</b>（<see cref="Core.AccountStore.TryAutoLogin"/>），
    /// 不是"文件里有就当登录了"：后者会让一份被改坏的 accounts.json 直接把陌生人放进去。
    /// </para>
    /// </remarks>
    public static class AccountGate
    {
        private static LoginPanel _panel;

        public static bool IsSignedIn =>
            Core.AccountStore.Instance != null && Core.AccountStore.Instance.IsLoggedIn;

        public static string Account =>
            Core.AccountStore.Instance != null ? Core.AccountStore.Instance.Account : "";

        /// <summary>幂等建好登录面板（默认隐藏）。</summary>
        public static void Ensure()
        {
            Core.AccountStore.Ensure();
            if (_panel != null) return;
            // @owner: app —— 登录页全程常驻（登出时还要回到它），不随世界拆装
            var go = new GameObject("LoginPanel");
            Object.DontDestroyOnLoad(go);
            _panel = go.AddComponent<LoginPanel>();
        }

        public static LoginPanel Panel => _panel;

        /// <summary>
        /// 要进主菜单：已登录就直接进；记住了密码就自动登录再进；否则先弹登录页。
        /// </summary>
        public static void ShowMenu()
        {
            Ensure();
            MainMenu.Ensure();

            var store = Core.AccountStore.Instance;
            if (store != null && store.IsLoggedIn)
            {
                MainMenu.Instance.Show();
                return;
            }
            if (store != null && store.TryAutoLogin())
            {
                MainMenu.Instance.Show();
                MainMenu.Instance.ShowWithNotice(Core.Localization.T("login.auto"));
                return;
            }
            // 未登录：先把菜单收起来。登录页站在模态层上，底下的按钮本来就点不到，
            // 露半截战绩面板在那里只是噪音（也会让"未登录能不能看到菜单"说不清）。
            MainMenu.Instance.Hide();
            _panel.Show(() => MainMenu.Instance.Show());
        }

        /// <summary>退出登录并回到登录页（主菜单上的「退出登录」）。</summary>
        public static void SignOut()
        {
            Ensure();
            var store = Core.AccountStore.Instance;
            if (store != null) store.Logout();
            if (MainMenu.Instance != null) MainMenu.Instance.Hide();
            // 退出后不自动登录：刚点了"退出登录"又立刻被记住的密码塞回去，等于按钮是坏的。
            _panel.Show(() => MainMenu.Instance.Show());
        }
    }
}
