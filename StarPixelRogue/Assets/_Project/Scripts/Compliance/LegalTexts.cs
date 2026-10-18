using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Compliance
{
    /// <summary>
    /// 法律文本加载器（M5）：游戏内合规门/设置面板展示的协议文本。
    /// 唯一数据源为 Resources/Legal/*.txt（纯文本，随包体发布，无需联网）。
    /// 修改文本后请同步递增 <see cref="Core.VersionInfo.ComplianceVersion"/>，
    /// 否则已同意旧版的用户不会重新看到更新后的文本。
    /// </summary>
    public static class LegalTexts
    {
        public const string TermsKey = "terms";
        public const string PrivacyKey = "privacy";
        public const string ChildrenKey = "children";

        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        /// <summary>按键取文本（terms/privacy/children）；缺失返回占位说明。</summary>
        public static string Get(string key)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            string text = null;
            var asset = Resources.Load<TextAsset>("Legal/" + key);
            if (asset != null && !string.IsNullOrEmpty(asset.text)) text = asset.text;
            if (string.IsNullOrEmpty(text)) text = "（法律文本缺失：" + key + "。请检查 Assets/_Project/Resources/Legal/ 目录）";
            Cache[key] = text;
            return text;
        }

        /// <summary>全部文档按键（设置面板法律区按钮用）。</summary>
        public static IEnumerable<string> AllKeys()
        {
            yield return TermsKey;
            yield return PrivacyKey;
            yield return ChildrenKey;
        }
    }
}
