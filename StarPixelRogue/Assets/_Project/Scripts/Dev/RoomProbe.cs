using System;
using System.Collections;
using System.Collections.Generic;
using StarPixel.Core;
using StarPixel.Net;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M55「组队码」无头实测。
    ///
    /// 为什么要测：
    ///   组队码是玩家之间**口头传递**的一串字符，它的失败模式不是崩溃，是"连不上"。
    ///   而"连不上"在一个还没跑通公网的联机功能里，几乎无法与"网络本来就不通"区分——
    ///   玩家会归咎于联机，实际上是码被打错了一个字符。所以这一层的正确性
    ///   必须在**无头环境里被完整断言**，不能留到"等实机联调再说"。
    ///
    /// 三个场景（分开是为了让注入的失败面互不相交）：
    ///   CODE —— 码本身：生成、规范化、校验位能挡住真人会犯的错（错抄、念反、位数不对）。
    ///   WIRE —— 发现报文：编解码往返，以及**拒绝**坏报文（错前缀、字段数不对、非法地址）。
    ///   ROOM —— 目录与解析：命中/未命中/过期清理/本机不过期/建房发码/关房清码。
    ///
    /// 判据的区分力（本探针最要紧的地方）：
    /// 1) 校验位的能力要**分开断言**：能挡住"错一个字符"和"相邻位念反"是两件事——
    ///    固定权重的校验和对后者无能为力（AB 与 BA 的和相同）。只测前者的话，
    ///    把权重改成常数，判据照样全绿，而真人传码时念反恰恰是最常见的错误。
    /// 2) 「能解析出正确结果」必须配「能拒绝坏输入」的对偶判据：
    ///    只测前者的话，一个对所有输入都返回"成功"的解析器也能绿。
    /// 3) WIRE 里专门测"主机名带分隔符不会把报文切歪"——
    ///    主机名是唯一用户可控却参与报文结构的字段，单发测试永远碰不到。
    /// 4) 过期清理要**同时**断言"远端被清"和"本机不被清"：
    ///    只测前者的话，一个把本机也一起清掉的 Prune 会让房主自己从目录里消失，
    ///    而这条判据看不出来。
    /// </summary>
    public class RoomProbe : MonoBehaviour
    {
        private string _scene = "CODE";
        private int _checked;
        private int _ok;
        private readonly List<string> _bad = new List<string>();

        // —— CODE ——
        private bool _genOk;            // 批量生成的码全部合法
        private bool _lenOk;            // 长度恒为 6
        private bool _fmtOk;            // 展示格式 XXX-XXX
        private bool _lowerOk;          // 小写能还原
        private bool _sepOk;            // 分隔符/空格能还原
        private bool _fixOk;            // 易混字符误抄能纠正
        private bool _lKept;            // 字母表内的合法字符（L）不会被"纠正"掉
        private bool _typoBlocked;      // 单字符错抄被校验位挡下
        private bool _swapBlocked;      // 相邻位念反被校验位挡下
        private bool _badLenBlocked;    // 位数不对被挡
        private string _sample = "";

        // —— WIRE ——
        private bool _queryRoundTrip;
        private bool _replyRoundTrip;
        private bool _badMagicBlocked;
        private bool _badFieldsBlocked;
        private bool _badIpBlocked;
        private bool _badPortBlocked;
        private bool _pipeNameOk;       // 主机名里的分隔符不会把报文切歪

        // —— ROOM ——
        private bool _dirHit;
        private bool _dirMiss;
        private bool _remotePruned;
        private bool _localKept;
        private bool _resolveHit;
        private bool _resolveMiss;
        private bool _resolveBad;
        private bool _directIpOk;
        private bool _hostIssued;
        private bool _unhostCleared;

        public static void Start(string scene)
        {
            var go = new GameObject("RoomProbe");
            go.SetActive(false);
            var p = go.AddComponent<RoomProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Awake()
        {
            Localization.SetLanguage(Localization.LangZh, silent: true);
            StartCoroutine(Run());
        }

        /// <summary>
        /// 空一帧再跑：静态 <c>Start(string)</c> 里 AddComponent 时才刚赋值 <c>_scene</c>，
        /// 立刻执行虽然能跑，但万一将来要读其他 Manager 的实例就会踩到"还没 Ensure"。
        /// </summary>
        private IEnumerator Run()
        {
            yield return null;
            RoomDirectory.Reset();
            switch (_scene)
            {
                case "WIRE": Wire(); break;
                case "ROOM": Rooms(); break;
                default: Code(); break;
            }
            Report();
        }

        // ============================ CODE ============================

        private void Code()
        {
            // 1) 批量生成：每一个都必须通过自己的校验位
            const int N = 2000;
            bool genAllValid = true, lenAll = true, fmtAll = true;
            for (int i = 0; i < N; i++)
            {
                var c = RoomCode.Generate();
                if (!RoomCode.IsValid(c)) genAllValid = false;
                if (c.Length != RoomCode.Length) lenAll = false;
                var f = RoomCode.Format(c);
                if (f.Length != 7 || f[3] != '-') fmtAll = false;
            }
            Check(genAllValid, "生成的码全部合法");
            Check(lenAll, "长度恒为 6");
            Check(fmtAll, "展示格式 XXX-XXX");
            _genOk = genAllValid; _lenOk = lenAll; _fmtOk = fmtAll;

            var code = RoomCode.Generate();
            _sample = RoomCode.Format(code);

            // 2) 玩家不会照着大小写和分隔符抄：小写、空格、连字符都要能还原
            Check(RoomCode.IsValid(RoomCode.Normalize(code.ToLowerInvariant())), "小写可还原");
            _lowerOk = RoomCode.IsValid(RoomCode.Normalize(code.ToLowerInvariant()));
            var messy = " " + code.Substring(0, 2) + " - " + code.Substring(2, 2) + "  " + code.Substring(4) + " ";
            Check(RoomCode.IsValid(RoomCode.Normalize(messy)), "分隔符与空格可还原");
            _sepOk = RoomCode.IsValid(RoomCode.Normalize(messy));

            // 3) 易混字符：把码里的 Q 写成 O/0、J 写成 I/1 都该被纠正回来
            //    只挑**确实存在**于码里的那几个去试，否则测的是空气。
            //    注意 L 不在误抄列表里——它是字母表里的合法字符，
            //    玩家看到的 L 就是 L，纠正它等于把合法码改坏（见下面那条判据）。
            bool fixAll = true, fixTried = false;
            var confusable = new[] { ('Q', new[] { 'O', '0' }), ('J', new[] { 'I', '1' }) };
            foreach (var pair in confusable)
            {
                int at = code.IndexOf(pair.Item1);
                if (at < 0) continue;
                foreach (var wrong in pair.Item2)
                {
                    var broken = code.Remove(at, 1).Insert(at, wrong.ToString());
                    fixTried = true;
                    if (RoomCode.Normalize(broken) != code) fixAll = false;
                }
            }
            // 一个都没试到说明这个码恰好不含易混字符：换个必然含有的再试一次，
            // 否则这条判据会随随机码**有时根本没在测**
            if (!fixTried)
            {
                var probe = RoomCode.Normalize("ABCDE" + RoomCode.Alphabet[0]);
                // 构造：拿一个合法码，把首位替换成 Q 的误抄形式再验一次
                var c2 = "Q" + code.Substring(1);
                var normalizedC2 = RoomCode.Normalize(c2);
                fixTried = true;
                if (RoomCode.Normalize(normalizedC2.Replace('Q', 'O')) != normalizedC2) fixAll = false;
            }
            Check(fixTried && fixAll, "易混字符误抄可纠正");
            _fixOk = fixTried && fixAll;

            // 3b) 反向那条：**合法字符不许被纠正**。
            //     L 在字母表里，玩家照屏幕念 L 输入的就是 L。
            //     把它收进误抄纠正表（看起来理所当然：L 和 I/1 长得像）会毁掉所有含 L 的码，
            //     而读代码完全看不出来——所以这里专门立一条判据盯着。
            string codeWithL = "";
            for (int i = 0; i < 500 && codeWithL.Length == 0; i++)
            {
                var c = RoomCode.Generate();
                if (c.IndexOf('L') >= 0) codeWithL = c;
            }
            bool lKept = codeWithL.Length > 0
                         && RoomCode.Normalize(codeWithL) == codeWithL
                         && RoomCode.Normalize(codeWithL.ToLowerInvariant()) == codeWithL;
            Check(lKept, "含L的码不被改");
            _lKept = lKept;

            // 4) 单字符错抄：换掉一个字符（换成字母表里**另一个**字符）
            bool typoAllBlocked = true;
            var alphabet = RoomCode.Alphabet;
            for (int at = 0; at < RoomCode.Length - 1; at++)      // 不动校验位
            {
                int orig = alphabet.IndexOf(code[at]);
                int other = (orig + 7) % alphabet.Length;          // 固定偏移，保证不同
                if (other == orig) continue;
                var broken = code.Remove(at, 1).Insert(at, alphabet[other].ToString());
                if (RoomCode.IsValid(broken)) typoAllBlocked = false;
            }
            Check(typoAllBlocked, "单字符错抄被挡");
            _typoBlocked = typoAllBlocked;

            // 5) 相邻位念反：交换 body 内相邻两位，**校验位保持原样**
            //    （真人是照着念错的顺序把六个字符都念完的，不会顺手改校验位）
            bool swapAllBlocked = true;
            int swapTried = 0;
            for (int at = 0; at < RoomCode.Length - 2; at++)
            {
                if (code[at] == code[at + 1]) continue;            // 两位相同，交换等于没换
                var swapped = code.Remove(at, 2).Insert(at, code[at + 1].ToString() + code[at]);
                swapTried++;
                if (RoomCode.IsValid(swapped)) swapAllBlocked = false;
            }
            // 一个都没试到 = 这个码全是重复字符（极罕见），换一个再试
            if (swapTried == 0)
            {
                for (int i = 0; i < 64 && swapTried == 0; i++)
                {
                    var c3 = RoomCode.Generate();
                    for (int at = 0; at < RoomCode.Length - 2; at++)
                    {
                        if (c3[at] == c3[at + 1]) continue;
                        var s3 = c3.Remove(at, 2).Insert(at, c3[at + 1].ToString() + c3[at]);
                        swapTried++;
                        if (RoomCode.IsValid(s3)) swapAllBlocked = false;
                        break;
                    }
                }
            }
            Check(swapTried > 0 && swapAllBlocked, "相邻位念反被挡");
            _swapBlocked = swapTried > 0 && swapAllBlocked;

            // 6) 位数不对
            bool badLenBlocked = !RoomCode.IsValid(code.Substring(0, 5))
                                 && !RoomCode.IsValid(code + "X")
                                 && !RoomCode.IsValid("");
            Check(badLenBlocked, "位数不对被挡");
            _badLenBlocked = badLenBlocked;
        }

        // ============================ WIRE ============================

        private void Wire()
        {
            bool qOk = true, rOk = true;
            for (int i = 0; i < 200; i++)
            {
                var c = RoomCode.Generate();
                string got;
                if (!RoomDiscovery.TryParseQuery(RoomDiscovery.BuildQuery(c), out got) || got != c) qOk = false;

                string ip = "192.168.1." + (i % 254 + 1);
                int port = 7777 + (i % 10);
                var msg = RoomDiscovery.BuildReply(c, ip, port, "Host" + i, 2);
                RoomReply rr;
                if (!RoomDiscovery.TryParseReply(msg, out rr)) { rOk = false; continue; }
                if (rr.RoomKey != c || rr.Address != ip || rr.Port != port || rr.HostName != "Host" + i) rOk = false;
            }
            Check(qOk, "询问报文往返");
            Check(rOk, "应答报文往返");
            _queryRoundTrip = qOk; _replyRoundTrip = rOk;

            var code = RoomCode.Generate();

            // 坏报文必须被拒绝，而不是解出个错的来
            string dummy;
            RoomReply dummyReply;
            bool badMagic = !RoomDiscovery.TryParseQuery("SPD0|Q|" + code, out dummy)
                            && !RoomDiscovery.TryParseReply("XXXX|A|" + code + "|1.2.3.4|7777|h|1", out dummyReply);
            Check(badMagic, "错误前缀被挡");
            _badMagicBlocked = badMagic;

            bool badFields = !RoomDiscovery.TryParseQuery("SPD1|Q", out dummy)
                             && !RoomDiscovery.TryParseQuery("SPD1|Q|" + code + "|extra", out dummy)
                             && !RoomDiscovery.TryParseReply("SPD1|A|" + code + "|1.2.3.4|7777|h", out dummyReply);
            Check(badFields, "字段数不对被挡");
            _badFieldsBlocked = badFields;

            bool badIp = !RoomDiscovery.TryParseReply(
                RoomDiscovery.Magic + "|A|" + code + "|999.999.999.999|7777|h|1", out dummyReply);
            Check(badIp, "非法地址被挡");
            _badIpBlocked = badIp;

            bool badPort = !RoomDiscovery.TryParseReply(
                RoomDiscovery.Magic + "|A|" + code + "|1.2.3.4|0|h|1", out dummyReply)
                && !RoomDiscovery.TryParseReply(
                RoomDiscovery.Magic + "|A|" + code + "|1.2.3.4|99999|h|1", out dummyReply);
            Check(badPort, "非法端口被挡");
            _badPortBlocked = badPort;

            // 主机名是唯一"用户可控又参与报文结构"的字段：带分隔符必须被中和掉
            var withPipe = RoomDiscovery.BuildReply(code, "10.0.0.7", 7777, "a|b|c|d", 3);
            RoomReply pr;
            bool pipeOk = RoomDiscovery.TryParseReply(withPipe, out pr) && pr.RoomKey == code && pr.Port == 7777;
            Check(pipeOk, "主机名不切歪报文");
            _pipeNameOk = pipeOk;
        }

        // ============================ ROOM ============================

        private void Rooms()
        {
            RoomDirectory.Reset();

            var code = RoomCode.Generate();
            RoomDirectory.Put(code, "192.168.1.50", 7777, "Alice", 2, false);

            bool hit = RoomDirectory.Find(code) != null;
            Check(hit, "目录命中");
            _dirHit = hit;

            bool miss = RoomDirectory.Find(RoomCode.Generate()) == null;
            Check(miss, "目录未命中");
            _dirMiss = miss;

            // 过期：远端房间超时必须被清掉，否则玩家会看到一个永远连不上的幽灵房间
            var stale = RoomCode.Generate();
            RoomDirectory.Put(stale, "192.168.1.51", 7777, "Bob", 1, false);
            RoomDirectory.Find(stale).LastSeen = RoomDirectory.Now - 999f;
            int pruned = RoomDirectory.Prune();
            bool remotePruned = pruned >= 1 && RoomDirectory.Find(stale) == null;
            Check(remotePruned, "过期房间被清");
            _remotePruned = remotePruned;

            // 对偶：本机那条**不能**被清——它自己知道自己还活着
            var mine = RoomCode.Generate();
            RoomDirectory.Put(mine, "127.0.0.1", 7777, "Me", 1, true);
            RoomDirectory.Find(mine).LastSeen = RoomDirectory.Now - 999f;
            RoomDirectory.Prune();
            bool localKept = RoomDirectory.Find(mine) != null;
            Check(localKept, "本机房间不过期");
            _localKept = localKept;

            // 解析：命中
            var rHit = JoinTarget.ResolveCached(RoomCode.Format(code).ToLowerInvariant());
            bool resolveHit = rHit.Ok && rHit.Address == "192.168.1.50";
            Check(resolveHit, "解析命中");
            _resolveHit = resolveHit;

            // 解析：目录里没有 → 必须报"没这个房间"，而不是"码错了"
            var rMiss = JoinTarget.ResolveCached(RoomCode.Generate());
            bool resolveMiss = !rMiss.Ok && rMiss.ReasonKey == "net.err_no_room";
            Check(resolveMiss, "解析未命中");
            _resolveMiss = resolveMiss;

            // 解析：码本身不合法 → 必须报"码错了"，两种失败不能混为一谈
            var rBad = JoinTarget.ResolveCached("ZZZ-ZZZ");
            bool resolveBad = !rBad.Ok && rBad.ReasonKey == "net.err_bad_code";
            Check(resolveBad, "解析非法码");
            _resolveBad = resolveBad;

            // 直接填 IP 的老路径必须还在
            string ip; int port;
            bool directIp = JoinTarget.TryParseDirectAddress("10.0.0.9:8888", out ip, out port)
                            && ip == "10.0.0.9" && port == 8888
                            && JoinTarget.TryParseDirectAddress("127.0.0.1", out ip, out port)
                            && port == NetProtocol.DefaultPort
                            && !JoinTarget.TryParseDirectAddress(RoomCode.Generate(), out ip, out port);
            Check(directIp, "IP 直通仍可用");
            _directIpOk = directIp;

            // 建房：必须真的发出一个合法码，并登记为本机
            RoomDirectory.Reset();
            var issued = JoinTarget.Host("ProbeHost", NetProtocol.DefaultPort);
            var issuedEntry = RoomDirectory.Find(issued);
            bool hostIssued = !string.IsNullOrEmpty(issued) && RoomCode.IsValid(issued)
                              && RoomDirectory.LocalCode == issued
                              && issuedEntry != null && issuedEntry.IsLocal;
            Check(hostIssued, "建房发出组队码");
            _hostIssued = hostIssued;
            // 四个子条件分开复读：失败时能直接看出是"没发出码"还是"发了但没登记"，
            // 合成一个 bool 只能知道"建房那条挂了"。
            Line("建房-码非空", Yn(!string.IsNullOrEmpty(issued)));
            Line("建房-码合法", Yn(!string.IsNullOrEmpty(issued) && RoomCode.IsValid(issued)));
            Line("建房-LocalCode一致", Yn(RoomDirectory.LocalCode == issued));
            Line("建房-登记为本机", Yn(issuedEntry != null && issuedEntry.IsLocal));
            Line("建房-返回码", issued ?? "<null>");

            // 关房：码必须清干净，且重复关房不能出事
            JoinTarget.Unhost();
            bool unhostCleared = string.IsNullOrEmpty(RoomDirectory.LocalCode)
                                 && RoomDirectory.Find(issued) == null;
            JoinTarget.Unhost();                       // 幂等：再关一次也不该抛
            Check(unhostCleared, "关房清掉组队码");
            _unhostCleared = unhostCleared;

            RoomDirectory.Reset();
            RoomDiscovery.Reset();
        }

        // ============================ 公共 ============================

        private void Check(bool ok, string what)
        {
            _checked++;
            if (ok) _ok++;
            else _bad.Add(what);
        }

        private void Report()
        {
            bool pass = _ok == _checked && _checked > 0;
            if (_scene == "CODE")
            {
                Line("生成的码全部合法", Yn(_genOk));
                Line("长度恒为 6", Yn(_lenOk));
                Line("展示格式正确", Yn(_fmtOk));
                Line("小写可还原", Yn(_lowerOk));
                Line("分隔符可还原", Yn(_sepOk));
                Line("误抄可纠正", Yn(_fixOk));
                Line("含L的码不被改", Yn(_lKept));
                Line("单字符错抄被挡", Yn(_typoBlocked));
                Line("相邻位念反被挡", Yn(_swapBlocked));
                Line("位数不对被挡", Yn(_badLenBlocked));
                Line("样例码", _sample);
            }
            else if (_scene == "WIRE")
            {
                Line("询问报文往返", Yn(_queryRoundTrip));
                Line("应答报文往返", Yn(_replyRoundTrip));
                Line("错误前缀被挡", Yn(_badMagicBlocked));
                Line("字段数不对被挡", Yn(_badFieldsBlocked));
                Line("非法地址被挡", Yn(_badIpBlocked));
                Line("非法端口被挡", Yn(_badPortBlocked));
                Line("主机名不切歪", Yn(_pipeNameOk));
            }
            else
            {
                Line("目录命中", Yn(_dirHit));
                Line("目录未命中", Yn(_dirMiss));
                Line("过期房间被清", Yn(_remotePruned));
                Line("本机房间不过期", Yn(_localKept));
                Line("解析命中", Yn(_resolveHit));
                Line("解析未命中", Yn(_resolveMiss));
                Line("解析非法码", Yn(_resolveBad));
                Line("IP 直通仍可用", Yn(_directIpOk));
                Line("建房发出组队码", Yn(_hostIssued));
                Line("关房清掉组队码", Yn(_unhostCleared));
            }
            Line("失败明细", _bad.Count == 0 ? "<无>" : string.Join(" | ", _bad.ToArray()));
            Line("断点通过", _ok + "/" + _checked);
            Debug.Log(pass ? "[SMOKE] 房间码场景通过 " + _scene : "[SMOKE] 房间码场景失败 " + _scene);

            // 必须自己退出：本机监听会占住发现端口 47777，进程不就退的话
            // 下一个场景（或下一次跑）抢不到端口，表现为**偶发**的建房失败——
            // 那种"有时红有时绿"最难查，而根因只是上一个进程没走。
            Application.Quit(pass ? 0 : 1);
        }

        private static void Line(string key, string value)
        {
            Debug.Log("[SMOKE] " + key.PadRight(20) + value);
        }

        private static string Yn(bool b) => b ? "是" : "否";
    }
}
