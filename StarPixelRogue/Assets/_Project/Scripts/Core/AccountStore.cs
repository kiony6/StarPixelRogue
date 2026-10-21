using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 本地账号库（M54）：注册 / 登录 / 记住密码 / 自动登录。
    /// 单文件 JSON（Application.persistentDataPath/accounts.json），原子写（tmp + replace）。
    /// </summary>
    /// <remarks>
    /// <para><b>密码是怎么存的，以及这样存够不够。</b></para>
    /// <para>
    /// 校验用的凭据是 <c>SHA256(salt + ":" + 密码)</c>，salt 每账号随机。文件里因此
    /// <b>没有</b>任何能反推出原密码的东西——把 accounts.json 拷走也登不上别的系统，
    /// 因为它不是通用口令，只是这个本地库里的一串摘要。
    /// </para>
    /// <para>
    /// 「记住密码」是另一回事：它的<b>目的就是回填明文</b>，否则下次打开输入框还是空的，
    /// 勾了等于没勾。所以它只能存可还原的形式，这里用固定异或 + Base64。
    /// <b>这是混淆，不是加密</b>：它能挡住"顺手打开 json 瞄一眼"，挡不住任何真想读的人。
    /// 边界写在这里——本库只保护"校验凭据不可还原"，不保护"记住的密码不可还原"。
    /// 真要接线上账号体系，密码根本不进客户端，这条字段一并删掉。
    /// </para>
    /// <para>
    /// 另外这是<b>纯本地</b>账号：没有服务端，换台机器就没有这个号。UI 上不假装它是云账号。
    /// </para>
    /// </remarks>
    public class AccountStore : MonoSingleton<AccountStore>
    {
        // —— 规则 ——
        // 定在这里而不是散在 UI 里：校验规则一旦有两处，迟早在某一处漏掉一条。
        public const int NameMin = 3;
        public const int NameMax = 16;
        public const int PwdMin = 6;
        public const int PwdMax = 20;

        /// <summary>注册/登录的结果码（UI 据此取本地化文案，不自己拼句子）。</summary>
        public enum Code
        {
            Ok = 0,
            NameEmpty,
            NameFormat,      // 长度或字符不合规则
            NameTaken,
            PwdEmpty,
            PwdFormat,       // 长度不合规则
            PwdMismatch,     // 注册时两次输入不一致
            NoSuchAccount,
            WrongPassword,
        }

        [Serializable]
        public class AccountEntry
        {
            public string name = "";
            public string salt = "";
            public string hash = "";
            public string createdAt = "";
            public string lastLoginAt = "";
        }

        [Serializable]
        public class Data
        {
            public List<AccountEntry> accounts = new List<AccountEntry>();
            public string lastAccount = "";
            public bool remember = false;
            /// <summary>记住的密码（混淆后）。清空条件见 <see cref="SetRemember"/>。</summary>
            public string rememberedSecret = "";
        }

        public Data Current = new Data();

        private static readonly string FilePath =
            Path.Combine(Application.persistentDataPath, "accounts.json");

        // —— 登录态 ——
        private string _session = "";

        /// <summary>当前登录的账号名（未登录为空串）。</summary>
        public string Account => _session ?? "";

        public bool IsLoggedIn => !string.IsNullOrEmpty(_session);

        public int AccountCount => Current.accounts != null ? Current.accounts.Count : 0;

        /// <summary>库里是否已有账号（决定登录页首屏是「登录」还是「先注册」）。</summary>
        public bool HasAnyAccount => AccountCount > 0;

        protected override void Awake()
        {
            base.Awake();
            Load();
        }

        public static void Ensure()
        {
            if (Instance != null) return;
            new GameObject("AccountStore").AddComponent<AccountStore>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoEnsure() => Ensure();

        // =====================================================================================
        //  持久化
        // =====================================================================================

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var loaded = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    Current = loaded;
                    if (Current.accounts == null) Current.accounts = new List<AccountEntry>();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Account] 读取失败: " + e.Message);
                Current = new Data();
            }
        }

        public void Save()
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(Current, true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Account] 写入失败: " + e.Message);
            }
        }

        // =====================================================================================
        //  校验规则
        // =====================================================================================

        /// <summary>账号名是否合规：3~16 位，只允许字母、数字、下划线。</summary>
        public static Code CheckName(string name)
        {
            if (string.IsNullOrEmpty(name)) return Code.NameEmpty;
            if (name.Length < NameMin || name.Length > NameMax) return Code.NameFormat;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return Code.NameFormat;
            }
            return Code.Ok;
        }

        /// <summary>密码是否合规：6~20 位。不限制字符集——限制字符集只会逼出更弱的密码。</summary>
        public static Code CheckPassword(string pwd)
        {
            if (string.IsNullOrEmpty(pwd)) return Code.PwdEmpty;
            if (pwd.Length < PwdMin || pwd.Length > PwdMax) return Code.PwdFormat;
            return Code.Ok;
        }

        // =====================================================================================
        //  注册 / 登录 / 登出
        // =====================================================================================

        public AccountEntry Find(string name)
        {
            if (Current.accounts == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < Current.accounts.Count; i++)
            {
                var a = Current.accounts[i];
                if (a != null && string.Equals(a.name, name, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        /// <summary>注册。<paramref name="pwd2"/> 为确认密码（登录态下可传同一串）。</summary>
        public Code Register(string name, string pwd, string pwd2 = null)
        {
            var nc = CheckName(name);
            if (nc != Code.Ok) return nc;
            var pc = CheckPassword(pwd);
            if (pc != Code.Ok) return pc;
            if (pwd2 != null && !string.Equals(pwd, pwd2, StringComparison.Ordinal)) return Code.PwdMismatch;
            if (Find(name) != null) return Code.NameTaken;

            string salt = NewSalt();
            Current.accounts.Add(new AccountEntry
            {
                name = name,
                salt = salt,
                hash = Hash(salt, pwd),
                createdAt = SaveManager.UtcNowIso(),
                lastLoginAt = "",
            });
            Save();
            return Code.Ok;
        }

        /// <summary>
        /// 登录。成功时写入登录态并按 <paramref name="remember"/> 更新记住的凭据。
        /// <b>记住与否是一次登录的选择</b>：取消勾选时旧凭据必须一并清掉，
        /// 否则"我明明取消了"但下次还是自动登录。
        /// </summary>
        public Code Login(string name, string pwd, bool remember = false)
        {
            var nc = CheckName(name);
            if (nc != Code.Ok) return nc;
            var pc = CheckPassword(pwd);
            if (pc != Code.Ok) return pc;

            var acc = Find(name);
            if (acc == null) return Code.NoSuchAccount;
            if (!string.Equals(acc.hash, Hash(acc.salt, pwd), StringComparison.Ordinal)) return Code.WrongPassword;

            _session = name;
            acc.lastLoginAt = SaveManager.UtcNowIso();
            Current.lastAccount = name;
            SetRemember(remember ? name : "", remember ? pwd : "");
            Save();
            return Code.Ok;
        }

        /// <summary>
        /// 退出登录。<b>只清会话，不清记住的凭据</b>——退出是"这台机器上换个人"，
        /// 记住密码是"下次别让我再输"，两件事。清掉凭据等于让"记住密码"在退出后失效一次。
        /// </summary>
        public void Logout()
        {
            _session = "";
        }

        // =====================================================================================
        //  记住密码
        // =====================================================================================

        /// <summary>是否记住了某个账号的密码（用于首屏回填）。</summary>
        public bool HasRemembered =>
            Current != null && Current.remember
            && !string.IsNullOrEmpty(Current.rememberedSecret)
            && !string.IsNullOrEmpty(Current.lastAccount);

        /// <summary>回填用的账号名。</summary>
        public string RememberedAccount => HasRemembered ? Current.lastAccount : "";

        /// <summary>
        /// 回填用的密码（解混淆后的原文）。
        /// 返回原文是它的唯一用途；拿到它的人已经是本机用户。
        /// </summary>
        public string RememberedPassword => HasRemembered ? Unmask(Current.rememberedSecret) : "";

        /// <summary>
        /// 用记住的凭据自动登录。
        /// 走的仍然是 <see cref="Login"/> 的完整校验——不能因为"是本机记住的"就跳过比对，
        /// 否则改一次密码规则（或文件被手改过）就会出现"记住了却登不上/登上了却是错号"。
        /// </summary>
        public bool TryAutoLogin()
        {
            if (!HasRemembered) return false;
            return Login(Current.lastAccount, RememberedPassword, true) == Code.Ok;
        }

        private void SetRemember(string account, string pwd)
        {
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(pwd))
            {
                Current.remember = false;
                Current.rememberedSecret = "";
                return;
            }
            Current.remember = true;
            Current.lastAccount = account;
            Current.rememberedSecret = Mask(pwd);
        }

        // =====================================================================================
        //  摘要与混淆
        // =====================================================================================

        /// <summary>每账号随机 salt：同一个密码在两个账号里得到不同的摘要。</summary>
        private static string NewSalt()
        {
            var bytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return Hex(bytes);
        }

        private static string Hash(string salt, string pwd)
        {
            var raw = Encoding.UTF8.GetBytes(salt + ":" + pwd);
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(raw));
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // 固定 key 的异或：可逆、无密钥管理，只求"一眼看不出"。见类注释。
        private static readonly byte[] MaskKey =
            Encoding.UTF8.GetBytes("star-pixel-local-only");

        private static string Mask(string plain)
        {
            var raw = Encoding.UTF8.GetBytes(plain);
            var out_ = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++) out_[i] = (byte)(raw[i] ^ MaskKey[i % MaskKey.Length]);
            return Convert.ToBase64String(out_);
        }

        private static string Unmask(string secret)
        {
            try
            {
                var raw = Convert.FromBase64String(secret ?? "");
                var out_ = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i++) out_[i] = (byte)(raw[i] ^ MaskKey[i % MaskKey.Length]);
                return Encoding.UTF8.GetString(out_);
            }
            catch (Exception)
            {
                // 文件被手改过 / 换了混淆方案：当成"没有记住"，不要让登录页崩在这里。
                return "";
            }
        }

        // =====================================================================================
        //  探针用
        // =====================================================================================

        /// <summary>清空账号库（含登录态）。仅供无头探针在开局前造干净的初始态。</summary>
        public void ResetForProbe()
        {
            _session = "";
            Current = new Data();
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception) { }
        }
    }
}
