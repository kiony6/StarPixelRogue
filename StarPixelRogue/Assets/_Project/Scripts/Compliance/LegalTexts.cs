using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Compliance
{
    /// <summary>
    /// 法律文本加载器（M5）：游戏内合规门/设置面板展示的协议文本。
    /// 唯一数据源为 Resources/Legal/*.txt（纯文本，随包体发布，无需联网）。
    /// 修改文本后请同步递增 <see cref="Core.VersionInfo.ComplianceVersion"/>，
    /// 否则已同意旧版的用户不会重新看到更新后的文本。
    ///
    /// M35：原文只认 <c>Resources/Legal/&lt;key&gt;.txt</c> 一条路径，而那三份是纯中文的，
    /// 于是英文界面下用户协议/隐私政策/儿童规则**整篇仍是中文**——这不是体验瑕疵而是
    /// 合规风险：不同语言的用户拿到的法务文本不对等。现在按语言取目录：
    /// 中文 <c>Legal/&lt;key&gt;</c>、英文 <c>Legal/en/&lt;key&gt;</c>。
    ///
    /// 英文缺失时**回退中文**（给玩家看占位串更糟），但必须留下痕迹：
    /// 记 <see cref="FallbackCount"/> 并告警，由实测探针断言其为 0；
    /// 构建期则由静态检查第 14 项（成对存在）直接拦下。两层都不静默。
    /// </summary>
    public static class LegalTexts
    {
        public const string TermsKey = "terms";
        public const string PrivacyKey = "privacy";
        public const string ChildrenKey = "children";

        /// <summary>缓存键为「语言/文档键」——按语言分区，切语言后自动取另一份，无需清缓存。</summary>
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        /// <summary>
        /// 英文版缺失、被迫回退中文的次数。正常构建必须为 0；
        /// 非 0 说明 Resources/Legal/en/ 漏了文件（静态检查第 14 项会先红）。
        /// </summary>
        public static int FallbackCount { get; private set; }

        /// <summary>M35 实测专用：每个场景开跑前清零，避免跨场景串味。</summary>
        public static void ResetFallbackCountForTest()
        {
            FallbackCount = 0;
        }

        /// <summary>按键取文本（terms/privacy/children），跟随当前语言；缺失返回占位说明。</summary>
        public static string Get(string key)
        {
            string lang = Core.Localization.Language;
            string cacheKey = lang + "/" + key;
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

            string text = null;
            if (lang == Core.Localization.LangEn)
            {
                var en = Resources.Load<TextAsset>("Legal/en/" + key);
                if (en != null && !string.IsNullOrEmpty(en.text))
                {
                    text = en.text;
                }
                else
                {
                    // 不静默：回退保证玩家看得到内容，但计数与告警让实测抓得住。
                    FallbackCount++;
                    Debug.LogWarning("[LegalTexts] 英文法务文本缺失，本次回退中文：" + key);
                }
            }

            if (string.IsNullOrEmpty(text))
            {
                var asset = Resources.Load<TextAsset>("Legal/" + key);
                if (asset != null && !string.IsNullOrEmpty(asset.text)) text = asset.text;
            }

            if (string.IsNullOrEmpty(text)) text = "（法律文本缺失：" + key + "。请检查 Assets/_Project/Resources/Legal/ 目录）";
            Cache[cacheKey] = text;
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
