using System;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.UI
{
    /// <summary>
    /// 登录 / 注册面板（M54）：进入主菜单之前的那一道门。
    /// - 登录态：账号 + 密码 + 记住密码；注册态多一行「确认密码」；
    /// - 成功回调 <paramref name="_onSignedIn"/> 由 <see cref="AccountGate"/> 接走（显示主菜单）；
    /// - 语言切换时整棵重建（订阅 <see cref="Core.Localization.LanguageChanged"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这个面板上<b>没有</b>「跳过 / 游客试玩」：既然做了账号，战绩与排行榜都要挂在账号上，
    /// 开一个免登录口子等于让"我的名次"变成一句空话。
    /// </para>
    /// <para>
    /// 校验与存储全在 <see cref="Core.AccountStore"/>：这里只负责把框里的字交出去、
    /// 把回来的结果码翻成一句人话。文案按结果码取，不在 UI 里拼句子——
    /// 拼出来的句子没法翻译，也没法被探针逐条比对。
    /// </para>
    /// </remarks>
    public class LoginPanel : MonoBehaviour
    {
        private GameObject _root;
        private bool _registerMode;
        private Action _onSignedIn;

        private Text _titleText;
        private InputField _accountInput;
        private InputField _pwdInput;
        private InputField _confirmInput;
        private Button _submitBtn;
        private Button _switchBtn;
        private Button _rememberBtn;
        private Text _messageText;
        private bool _remember;

        /// <summary>面板根（供无头探针定位；不能用 GameObject.Find，理由同其它面板）。</summary>
        public GameObject PanelRoot => _root;

        public bool IsVisible => _root != null && _root.activeSelf;

        /// <summary>当前是注册态还是登录态（探针据此决定填几行）。</summary>
        public bool IsRegisterMode => _registerMode;

        /// <summary>记住密码当前是否勾上。</summary>
        public bool RememberValue => _remember;

        /// <summary>提示行文案（错误信息 / 自动登录提示都写在这里）。</summary>
        public string MessageText => _messageText != null ? _messageText.text : "";

        public string AccountText
        {
            get => _accountInput != null ? _accountInput.text : "";
            set { if (_accountInput != null) _accountInput.text = value ?? ""; }
        }

        public string PasswordText
        {
            get => _pwdInput != null ? _pwdInput.text : "";
            set { if (_pwdInput != null) _pwdInput.text = value ?? ""; }
        }

        public string ConfirmText
        {
            get => _confirmInput != null ? _confirmInput.text : "";
            set { if (_confirmInput != null) _confirmInput.text = value ?? ""; }
        }

        /// <summary>提交按钮上的字（探针据此确认自己点的是登录还是注册）。</summary>
        public string SubmitLabel
        {
            get
            {
                var t = _submitBtn != null ? _submitBtn.GetComponentInChildren<Text>() : null;
                return t != null ? t.text : "";
            }
        }

        void Awake()
        {
            Core.Localization.LanguageChanged += Rebuild;
        }

        void OnDestroy()
        {
            Core.Localization.LanguageChanged -= Rebuild;
        }

        private void Rebuild()
        {
            if (_root == null) return;
            bool wasVisible = _root.activeSelf;
            Build(_registerMode);
            _root.SetActive(wasVisible);
        }

        /// <summary>
        /// 显示面板。<paramref name="onSignedIn"/> 在登录成功后回调（只调一次）。
        /// 已登录时直接回调、不显示——中途回主菜单不该再被拦一次。
        /// </summary>
        public void Show(Action onSignedIn)
        {
            _onSignedIn = onSignedIn;
            var store = Core.AccountStore.Instance;
            if (store != null && store.IsLoggedIn)
            {
                onSignedIn?.Invoke();
                return;
            }

            if (_root == null) Build(_registerMode);
            _root.SetActive(true);

            // 首屏形态：本机一个账号都没有时直接给注册页——
            // 让新玩家先在登录页吃一次"账号不存在"，是把第一分钟浪费在纠错上。
            bool nobody = store == null || !store.HasAnyAccount;
            if (nobody) SetMode(true);

            FillRemembered();
            if (nobody) SetMessage(Core.Localization.T("login.first"), Core.UIFactory.Palette.TextDim);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>切到注册态 / 登录态（重建内容区，保留已填的账号与密码）。</summary>
        public void SetMode(bool register)
        {
            if (_registerMode != register || _root == null)
            {
                string acc = AccountText;
                string pwd = PasswordText;
                _registerMode = register;
                Build(register);
                AccountText = acc;
                PasswordText = pwd;
                if (_root != null) _root.SetActive(true);
            }
            RefreshModeTexts();
        }

        // =====================================================================================
        //  构建
        // =====================================================================================

        private void Build(bool register)
        {
            if (_root != null) Core.UIFactory.Destroy(_root);

            // 站在模态层：它要盖住底下的主菜单，而不是跟主菜单争同一层。
            var canvas = Core.UIFactory.CreateCanvas("LoginCanvas", null,
                Core.UIFactory.ModalSortingOrder);
            _root = canvas.gameObject;

            var backdrop = Core.UIFactory.CreateRoundPanel(_root.transform, "Panel",
                new Vector2(0, 0), new Vector2(560, 520), Core.UIFactory.Palette.Deep);

            _titleText = Core.UIFactory.CreateTitle(backdrop.transform, "Title", "",
                new Vector2(0, 208), 40, new Vector2(480, 56), Core.UIFactory.Palette.Accent);
            var sub = Core.UIFactory.CreateText(backdrop.transform, "Subtitle",
                Core.Localization.T("login.subtitle"), new Vector2(0, 168), 18);
            sub.color = Core.UIFactory.Palette.TextDim;

            // 逐行往下排：注册态比登录态多一行，靠游标而不是写死两组坐标——
            // 写死两组的后果是改一行高度时另一组忘了跟着改。
            float y = 118;

            y = Field(backdrop.transform, "Account", Core.Localization.T("login.account"),
                Core.Localization.T("login.rule_name"), y, false, out _accountInput);
            y = Field(backdrop.transform, "Password", Core.Localization.T("login.password"),
                Core.Localization.T("login.rule_pwd"), y, true, out _pwdInput);

            if (register)
            {
                // 注册态多一行「确认密码」：直接把游标交给 Field 的返回值，
                // 不要"调用一次再手动减一次高度"——那两处高度迟早会有一个忘了改。
                y = Field(backdrop.transform, "Confirm", Core.Localization.T("login.confirm"),
                    "", y, true, out _confirmInput);
            }
            else
            {
                _confirmInput = null;
            }

            // 记住密码
            var rememberLabel = Core.UIFactory.CreateText(backdrop.transform, "RememberLabel",
                Core.Localization.T("login.remember"), new Vector2(-190, y), 18,
                TextAnchor.MiddleLeft, new Vector2(220, 32));
            rememberLabel.color = Core.UIFactory.Palette.TextDim;
            _rememberBtn = Core.UIFactory.CreateToggle(backdrop.transform, "RememberToggle", _remember,
                new Vector2(170, y), new Vector2(110, 36), v => _remember = v);
            y -= 62;

            // 提交
            _submitBtn = Core.UIFactory.CreateButton(backdrop.transform, "SubmitBtn", "",
                new Vector2(0, y), new Vector2(320, 56), OnSubmit, 22,
                Core.UIFactory.Tint(Core.UIFactory.Palette.Vital, 0.42f, 0.95f),
                Core.UIFactory.Palette.Vital);
            y -= 58;

            // 去注册 / 去登录
            _switchBtn = Core.UIFactory.CreateButton(backdrop.transform, "SwitchBtn", "",
                new Vector2(0, y), new Vector2(320, 44), OnSwitch, 17,
                Core.UIFactory.Palette.Raised, Core.UIFactory.Palette.AccentAlt);
            y -= 48;

            _messageText = Core.UIFactory.CreateText(backdrop.transform, "Message", "",
                new Vector2(0, y), 17, TextAnchor.MiddleCenter, new Vector2(500, 32));
            _messageText.color = Core.UIFactory.Palette.TextDim;

            RefreshModeTexts();
        }

        /// <summary>一行「标签 + 输入框」，返回下一行的 y。</summary>
        private static float Field(Transform parent, string node, string label, string hint,
            float y, bool password, out InputField field)
        {
            var lab = Core.UIFactory.CreateText(parent, node + "Label", label,
                new Vector2(-190, y), 18, TextAnchor.MiddleLeft, new Vector2(220, 30));
            lab.color = Core.UIFactory.Palette.TextDim;

            field = Core.UIFactory.CreateInputField(parent, node + "Input", hint,
                new Vector2(30, y - 32), new Vector2(400, 44), password);

            var rule = Core.UIFactory.CreateText(parent, node + "Hint", hint,
                new Vector2(30, y - 60), 14, TextAnchor.MiddleRight, new Vector2(400, 24));
            rule.color = Core.UIFactory.Tint(Core.UIFactory.Palette.TextDim, 1f, 0.7f);

            return y - 74;
        }

        private void RefreshModeTexts()
        {
            if (_titleText != null)
                _titleText.text = _registerMode
                    ? Core.Localization.T("login.register_submit")
                    : Core.Localization.T("login.title");

            var submitLabel = _submitBtn != null ? _submitBtn.GetComponentInChildren<Text>() : null;
            if (submitLabel != null)
                submitLabel.text = _registerMode
                    ? Core.Localization.T("login.register_submit")
                    : Core.Localization.T("login.submit");

            var switchLabel = _switchBtn != null ? _switchBtn.GetComponentInChildren<Text>() : null;
            if (switchLabel != null)
                switchLabel.text = _registerMode
                    ? Core.Localization.T("login.goto_login")
                    : Core.Localization.T("login.goto_register");
        }

        /// <summary>把记住的账号密码回填进输入框（有才填，没有就留空）。</summary>
        private void FillRemembered()
        {
            var store = Core.AccountStore.Instance;
            if (store == null || !store.HasRemembered) return;
            AccountText = store.RememberedAccount;
            PasswordText = store.RememberedPassword;
            if (_remember != store.HasRemembered)
            {
                _remember = true;
                // 重建 Toggle 才能让它的标签跟着变（CreateToggle 把初值写进了文案里）
                if (_rememberBtn != null)
                {
                    var parentT = _rememberBtn.transform.parent;
                    var pos = _rememberBtn.GetComponent<RectTransform>().anchoredPosition;
                    var size = _rememberBtn.GetComponent<RectTransform>().sizeDelta;
                    Core.UIFactory.Destroy(_rememberBtn.gameObject);
                    _rememberBtn = Core.UIFactory.CreateToggle(parentT, "RememberToggle", true,
                        pos, size, v => _remember = v);
                }
            }
            SetMessage(Core.Localization.T("login.auto"), Core.UIFactory.Palette.Accent);
        }

        // =====================================================================================
        //  交互
        // =====================================================================================

        private void OnSubmit()
        {
            var store = Core.AccountStore.Instance;
            if (store == null)
            {
                SetMessage(Core.Localization.T("login.err.no_account"), Core.UIFactory.Palette.Danger);
                return;
            }

            var code = _registerMode
                ? store.Register(AccountText, PasswordText, ConfirmText)
                : store.Login(AccountText, PasswordText, _remember);

            if (code != Core.AccountStore.Code.Ok)
            {
                SetMessage(Core.Localization.T(ErrorKey(code)), Core.UIFactory.Palette.Danger);
                return;
            }

            // 注册完必须真的登录：按钮上写的是「注册并登录」，而后面每一处
            // （战绩、排行榜）都按"当前账号"取数——只建号不登录会让它们全部落空。
            if (_registerMode)
            {
                var logged = store.Login(AccountText, PasswordText, _remember);
                if (logged != Core.AccountStore.Code.Ok)
                {
                    SetMessage(Core.Localization.T(ErrorKey(logged)), Core.UIFactory.Palette.Danger);
                    return;
                }
            }

            SetMessage(Core.Localization.T(_registerMode ? "login.ok_register" : "login.ok_login"),
                Core.UIFactory.Palette.Vital);

            var cb = _onSignedIn;
            _onSignedIn = null;
            Hide();
            cb?.Invoke();
        }

        private void OnSwitch()
        {
            SetMode(!_registerMode);
            SetMessage("", Core.UIFactory.Palette.TextDim);
        }

        private static string ErrorKey(Core.AccountStore.Code code)
        {
            switch (code)
            {
                case Core.AccountStore.Code.NameEmpty: return "login.err.name_empty";
                case Core.AccountStore.Code.NameFormat: return "login.err.name_format";
                case Core.AccountStore.Code.NameTaken: return "login.err.name_taken";
                case Core.AccountStore.Code.PwdEmpty: return "login.err.pwd_empty";
                case Core.AccountStore.Code.PwdFormat: return "login.err.pwd_format";
                case Core.AccountStore.Code.PwdMismatch: return "login.err.pwd_mismatch";
                case Core.AccountStore.Code.NoSuchAccount: return "login.err.no_account";
                case Core.AccountStore.Code.WrongPassword: return "login.err.wrong_pwd";
                default: return "login.err.wrong_pwd";
            }
        }

        private void SetMessage(string text, Color color)
        {
            if (_messageText == null) return;
            _messageText.text = text ?? "";
            _messageText.color = color;
        }
    }
}
