using System;
using System.Text;

namespace StarPixel.Net
{
    /// <summary>
    /// 组队码：玩家之间口头或手打传递的房间标识（形如 <c>K7M-2QX</c>）。
    /// <para>
    /// 它只解决**寻址**——把一串人类能读能说的字符映射到一个房间。
    /// 能不能真的连上是 <see cref="JoinTarget"/> 的事（局域网发现 / 平台中继），
    /// 两层分开是为了将来换传输层时这里一行都不用改。
    /// </para>
    /// </summary>
    public static class RoomCode
    {
        /// <summary>
        /// 码的字母表（31 个字符）。
        /// <para>刻意剔除了 I、O、0、1：这四个在手写体和大部分无衬线字体里互撞
        /// （O/0、I/1/l）。玩家是**照着屏幕念给队友听**的，只要有一对撞，
        /// 就会有人输错三次然后说「联机坏了」。</para>
        /// <para>
        /// 大小取 **31（质数）** 而不是凑成 32，理由见 <see cref="CheckMod"/>——
        /// 这是被探针抓出来的：32 个字符时约 6% 的码交换相邻位后校验位不变，
        /// 而"念反"恰恰是真人传码最常见的错误。
        /// </para>
        /// </summary>
        public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXY23456789";

        /// <summary>
        /// 校验位模数。必须是**质数**，且严格大于字符集的最大索引差。
        /// <para>
        /// 为什么：设第 i 位字符索引由 a 变成 b，加权和的变化是 <c>(b-a)*w[i]</c>。
        /// 模 p 为质数时，乘积为 0 当且仅当某个因子为 0——
        /// 而 b≠a、w[i]≠0，且 |b-a| &lt; p，所以它**不可能**为 0。单字符错抄一定被抓。
        /// </para>
        /// <para>
        /// 相邻位互换同理：变化是 <c>(a-b)*(w[i+1]-w[i])</c>，
        /// 只要权重互不相同、差值不为 0，同样不可能为 0。
        /// </para>
        /// <para>
        /// 若模数取合数（比如字符集 32 个时取 32），这两个论证都不成立：
        /// 相邻权重差是 2，而 2 与 32 不互质，当两个字符索引相差 16 时
        /// <c>2*16 = 32 ≡ 0</c>——约 6% 的码念反了却照样通过。
        /// 这类缺陷读代码完全看不出来，只能靠判据抓。
        /// </para>
        /// </summary>
        public const int CheckMod = 31;

        /// <summary>码长（不含分隔符）：5 位随机 + 1 位校验。</summary>
        public const int Length = 6;

        /// <summary>随机部分长度，其余 1 位是校验位。</summary>
        private const int BodyLength = Length - 1;

        /// <summary>
        /// 单一随机源，而不是每次 <c>new Random()</c>。
        /// <para>Mono 下无参构造仍可能用时间种子，连着开两房会拿到同一个序列——
        /// 那是"两个房间码一样"这种最难复现的 bug 的温床。
        /// 房间创建只在主线程发生，不做加锁。</para>
        /// </summary>
        private static readonly System.Random _rng = new System.Random(Guid.NewGuid().GetHashCode());

        /// <summary>
        /// 校验位的权重。用质数递增而不是固定权重，是为了让**相邻位互换**也能被抓出来——
        /// 固定权重下 "AB" 和 "BA" 的和相同，校验位一样，而念反是极常见的错误。
        /// </summary>
        private static readonly int[] Weights = { 1, 3, 5, 7, 11 };

        /// <summary>
        /// 误抄纠正表：把玩家按"视觉印象"输入的字符映射回字母表里的字符。
        /// <para>
        /// 准入条件只有一条：**源字符必须不在字母表里**。
        /// 字母表里没有 O，所以玩家写的 O 只能是 Q 的误认，映射是确定的；
        /// 字母表里**有** L，那么玩家写的 L 就是 L——把他改成 J 等于把合法码改坏。
        /// </para>
        /// <para>
        /// 这一条是被注入抓出来的：M55 第一版把 <c>L→J</c> 也收了进来，
        /// 结果凡是含 L 的码，玩家照着念（输入小写 l）都会被"纠正"成 J，
        /// 表现是"码明明是对的却进不去"。区分力注入的对照组直接红了四条。
        /// 而这类错误靠读代码看不出来——L 看起来就该和 I/1 归为一类。
        /// </para>
        /// </summary>
        private static readonly (char From, char To)[] Fixes =
        {
            ('I', 'J'), ('1', 'J'),        // I / 1 都不在字母表里，只能是 J 的误认
            ('O', 'Q'), ('0', 'Q'),        // O / 0 都不在字母表里，只能是 Q 的误认
        };

        /// <summary>生成一个新码。5 位随机 + 1 位校验位。</summary>
        public static string Generate()
        {
            var rng = _rng;
            var sb = new StringBuilder(BodyLength);
            for (int i = 0; i < BodyLength; i++)
                sb.Append(Alphabet[rng.Next(Alphabet.Length)]);
            string body = sb.ToString();
            return body + CheckDigit(body);
        }

        /// <summary>
        /// 规范化玩家输入：去分隔符/空格、转大写、纠正确定性误抄。
        /// <para>返回**未校验**的串——校验位是否正确由 <see cref="IsValid"/> 单独判。
        /// 分开是因为 UI 要分别提示"位数不对"和"码打错了"，合成一个 bool 说不清。</para>
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(Length);
            foreach (char c in raw)
            {
                if (c == '-' || c == ' ' || c == '\t') continue;      // 分隔符与空白
                char u = char.ToUpperInvariant(c);
                u = FixChar(u);
                if (Alphabet.IndexOf(u) >= 0) sb.Append(u);
                // 其余字符直接丢弃：粘贴时带进来的换行、全角符号都在这里被吃掉
            }
            return sb.ToString();
        }

        /// <summary>规范化后的串是否是合法码（长度对 + 校验位对）。</summary>
        public static bool IsValid(string normalized)
        {
            if (string.IsNullOrEmpty(normalized) || normalized.Length != Length) return false;
            for (int i = 0; i < Length; i++)
                if (Alphabet.IndexOf(normalized[i]) < 0) return false;
            return CheckDigit(normalized.Substring(0, BodyLength)) == normalized[BodyLength];
        }

        /// <summary>"一步到位"：能否从玩家输入解析出一个合法码（能则返回规范化结果）。</summary>
        public static bool TryParse(string raw, out string code)
        {
            code = Normalize(raw);
            return IsValid(code);
        }

        /// <summary>展示格式：<c>ABCDEF</c> → <c>ABC-DEF</c>。念起来是两段三字符，比一串好记。</summary>
        public static string Format(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != Length) return code ?? "";
            return code.Substring(0, 3) + "-" + code.Substring(3);
        }

        /// <summary>
        /// 校验位：对前 5 位做加权和取模。
        /// <para>它能挡住的是**单字符错抄和相邻位互换**——也就是真人传码时实际会犯的错。
        /// 它挡不住蓄意伪造（那不是它的职责，房间归属由服务端校验）。</para>
        /// </summary>
        private static char CheckDigit(string body)
        {
            int sum = 0;
            for (int i = 0; i < body.Length && i < Weights.Length; i++)
                sum += Alphabet.IndexOf(body[i]) * Weights[i];
            return Alphabet[sum % CheckMod];
        }

        private static char FixChar(char c)
        {
            foreach (var f in Fixes)
                if (f.From == c) return f.To;
            return c;
        }
    }
}
