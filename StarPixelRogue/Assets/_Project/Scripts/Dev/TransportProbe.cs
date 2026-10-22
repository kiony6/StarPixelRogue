#if FISHNET_ENABLED
using System;
using System.Collections;
using System.Collections.Generic;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using StarPixel.Core;
using StarPixel.Net;
using UnityEngine;

namespace StarPixel.Dev
{
    /// <summary>
    /// M56「传输层那一层」无头实测。
    ///
    /// 为什么要测：
    ///   换传输层不是"换个组件"那么简单，有三处会**静默失效**——不报错，只是连不上：
    ///   ① FishyEOS 把 <c>SetClientAddress</c> 覆写成空实现（<c>_ = address;</c>），
    ///      而项目此前正是靠它告诉传输层"连谁"；
    ///   ② FishyEOS 把 <c>SetPort</c> 同样覆写成空实现，端口概念直接消失；
    ///   ③ FishNet 的 <c>TryAddDefaultTransport</c> 在 <c>Transport</c> 为空时抓
    ///      "场景里任意一个"，抓不到就自己塞一个 Tugboat——同时装着两个传输层时，
    ///      真正跑的是哪一个取决于组件顺序。
    ///   这三种失败的共同点是**没有异常、没有日志**，只有"点了加入，没反应"。
    ///   所以必须在这一层被断言，不能留到"等 EOS 凭据到了再一起调"。
    ///
    /// 三个场景（分开是为了让注入的失败面互不相交）：
    ///   SPEC    —— 说明书本身：能力位、类型名、以及"EOS 不走 SetClientAddress"这条事实。
    ///   INSTALL —— 装配：装上了没有、认的是不是它、重复装会不会变两个、缺 SDK 时会不会撒谎。
    ///   TARGET  —— 目标与端口：设得上、读得回、种类不符时设不上且不留副作用。
    ///
    /// 判据的区分力（本探针最要紧的地方）：
    /// 1) 「装配成功」必须配「反查得到同一个」——只测前者的话，一个只 AddComponent
    ///    却从不赋 <c>TransportManager.Transport</c> 的实现也能绿，而那正是现有代码的状态。
    /// 2) 「缺 SDK 时装配失败」必须配「失败时不许回落」——只测前者的话，
    ///    一个失败后偷偷塞个 Tugboat 的实现照样绿，而现象只有一个：跨公网连不上。
    /// 3) 端口的「设得上」必须配「不适用时不改」——只测前者的话，
    ///    对所有种类无脑 SetPort 的实现看不出来。
    /// 4) 「仅限局域网」那句提示要**两面都断言**：恒显示的提示等于没提示。
    /// </summary>
    public class TransportProbe : MonoBehaviour
    {
        private string _scene = "SPEC";
        private int _checked;
        private int _ok;
        private readonly List<string> _bad = new List<string>();

        // —— SPEC ——
        private bool _unknownNoSpec;
        private bool _tugLanOnly;
        private bool _eosCross;
        private bool _tugNoLogin;
        private bool _eosNeedsLogin;
        private bool _tugUsesPort;
        private bool _eosNoPort;
        private bool _eosNotClientAddress;
        private bool _namesDistinct;
        private bool _tugTypeFound;

        // —— INSTALL ——
        private bool _installedPinned;
        private bool _installedTypeOk;
        private bool _installIdempotent;
        private bool _eosInstallFails;
        private bool _noFallback;
        private bool _kindBack;
        private bool _kindUnknown;
        private bool _scopeShown;
        private bool _scopeHidden;

        // —— TARGET ——
        private bool _targetApplied;
        private bool _targetReadBack;
        private bool _mismatchRefused;
        private bool _mismatchNoSideEffect;
        private bool _portApplied;
        private bool _portRefusedForEos;
        private bool _badPortBlocked;

        public static void Start(string scene)
        {
            var go = new GameObject("TransportProbe");
            go.SetActive(false);
            var p = go.AddComponent<TransportProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Awake()
        {
            Localization.SetLanguage(Localization.LangZh, silent: true);
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            yield return null;
            var saved = NetTransport.Default;
            try
            {
                switch (_scene)
                {
                    case "INSTALL": Install(); break;
                    case "TARGET": Target(); break;
                    default: Spec(); break;
                }
            }
            finally
            {
                // 探针会临时改 Default 来断言"提示不是恒显示的"，
                // 无论中途怎么退出都必须还原——否则下一个场景会带着错的默认值跑。
                NetTransport.Default = saved;
            }
            Report();
        }

        // ============================ SPEC ============================

        private void Spec()
        {
            Check(NetTransport.Describe(TransportKind.Unknown) == null, "未知种类无说明书");
            _unknownNoSpec = NetTransport.Describe(TransportKind.Unknown) == null;

            var tug = NetTransport.Describe(TransportKind.Tugboat);
            var eos = NetTransport.Describe(TransportKind.Eos);
            var steam = NetTransport.Describe(TransportKind.Steam);

            bool tugLanOnly = tug != null && !tug.CanCrossInternet;
            Check(tugLanOnly, "Tugboat 不能跨公网");
            _tugLanOnly = tugLanOnly;

            bool eosCross = eos != null && eos.CanCrossInternet;
            Check(eosCross, "EOS 能跨公网");
            _eosCross = eosCross;

            bool tugNoLogin = tug != null && !tug.NeedsLogin;
            Check(tugNoLogin, "Tugboat 不用登录");
            _tugNoLogin = tugNoLogin;

            bool eosLogin = eos != null && eos.NeedsLogin;
            Check(eosLogin, "EOS 要登录");
            _eosNeedsLogin = eosLogin;

            bool tugPort = tug != null && tug.UsesPort;
            Check(tugPort, "Tugboat 有端口");
            _tugUsesPort = tugPort;

            bool eosNoPort = eos != null && !eos.UsesPort;
            Check(eosNoPort, "EOS 无端口");
            _eosNoPort = eosNoPort;

            // 这条是整轮调研的核心事实：FishyEOS 的 SetClientAddress 是空实现，
            // 所以"设目标"这件事对 EOS 必须走它自己的成员，不能走客户端地址。
            // 写成判据是为了防止将来有人把这张表"简化"成一句 SetClientAddress。
            bool eosAddr = eos != null && !eos.UsesClientAddress
                           && eos.TargetMember == "RemoteProductUserId";
            Check(eosAddr, "EOS 不走客户端地址");
            _eosNotClientAddress = eosAddr;

            bool distinct = tug != null && eos != null && steam != null
                            && !string.IsNullOrEmpty(tug.TypeName)
                            && !string.IsNullOrEmpty(eos.TypeName)
                            && !string.IsNullOrEmpty(steam.TypeName)
                            && tug.TypeName != eos.TypeName
                            && eos.TypeName != steam.TypeName
                            && tug.TypeName != steam.TypeName;
            Check(distinct, "类型名互不相同");
            _namesDistinct = distinct;

            // Tugboat 随 Fish-Net 一起分发，本构建里一定找得到。
            // EOS / Steam 找不到是**正常的**（SDK 没装），所以不为它们立"能找到"的判据——
            // 那样只会逼着人把 SDK 塞进来，测的就不再是产品代码了。
            bool tugFound = FindTypeByName(tug != null ? tug.TypeName : "") != null;
            Check(tugFound, "Tugboat 类型可找到");
            _tugTypeFound = tugFound;
        }

        // ============================ INSTALL ============================

        private void Install()
        {
            // TransportManager 没有 Awake（只有 OnValidate，且仅编辑器生效），
            // 所以可以单独挂在一个空物体上——不需要一个初始化完整的 NetworkManager。
            // 这一点很重要：它让"显式写入 Transport 字段"那一行进了判据，
            // 而不用去构造一个能真正起服的 NetworkManager。
            var host = new GameObject("ProbeTransportHost");
            var tm = host.AddComponent<TransportManager>();

            string reason;
            bool ok = NetTransportSetup.EnsureInstalled(tm, TransportKind.Tugboat, out reason);
            var spec = NetTransport.Describe(TransportKind.Tugboat);

            bool pinned = ok && tm.Transport != null;
            Check(pinned, "装配后管理器认它");
            _installedPinned = pinned;

            bool typeOk = tm.Transport != null && spec != null
                          && tm.Transport.GetType().FullName == spec.TypeName;
            Check(typeOk, "装配类型正确");
            _installedTypeOk = typeOk;

            // 幂等：再装一次不许变成两个。两个同类 Transport 并存时，
            // TryGetComponent 取哪个是不确定的——"有时连得上有时连不上"的典型来源。
            NetTransportSetup.EnsureInstalled(tm, TransportKind.Tugboat, out reason);
            var all = host.GetComponents<Transport>();
            bool idem = all.Length == 1;
            Check(idem, "重复装配不产生第二个");
            _installIdempotent = idem;

            // 缺 SDK 时必须**如实失败**。本构建没有 FishyEOS，这条今天就能跑；
            // 等 SDK 装好，同一条判据会自动变成"装得上"，不需要改探针。
            var host2 = new GameObject("ProbeTransportHost2");
            var tm2 = host2.AddComponent<TransportManager>();
            bool eosOk = NetTransportSetup.EnsureInstalled(tm2, TransportKind.Eos, out reason);
            bool eosFails = !eosOk;
            Check(eosFails, "缺 SDK 时装配失败");
            _eosInstallFails = eosFails;

            // 失败的对偶：不许回落到别的传输层。回落会把"我以为在跑 EOS"
            // 悄悄变成"其实在跑局域网直连"，而现象只有一个：跨公网连不上。
            bool noFallback = tm2.Transport == null;
            Check(noFallback, "失败时不许回落");
            _noFallback = noFallback;

            bool kindBack = NetTransportSetup.KindOf(tm) == TransportKind.Tugboat;
            Check(kindBack, "反查得到 Tugboat");
            _kindBack = kindBack;

            // 用一个**从没被 EnsureInstalled 碰过**的管理器。
            // 用 tm2 也能测，但那样一来"装不上就回落成 Tugboat"这个注入会连带把这条拉红——
            // 那是连锁，不是又抓到一个独立缺陷。这条判据问的是"从来没装过时认出什么"。
            var host3 = new GameObject("ProbeTransportHost3");
            var tm3 = host3.AddComponent<TransportManager>();
            bool kindUnknown = NetTransportSetup.KindOf(tm3) == TransportKind.Unknown
                               && NetTransportSetup.KindOf(null) == TransportKind.Unknown;
            Check(kindUnknown, "空管理器反查未知");
            _kindUnknown = kindUnknown;

            // —— 能力提示上屏 ——
            // 这句话只在"不能跨公网"时成立，所以两面都要断言：恒显示的提示等于没提示。
            var lobbyGo = new GameObject("ProbeLobby");
            var lobby = lobbyGo.AddComponent<NetLobby>();
            string lanOnly = Localization.T("net.lan_only");

            NetTransport.Default = TransportKind.Tugboat;
            lobby.RefreshScope();
            bool shown = !string.IsNullOrEmpty(lanOnly) && lobby.ScopeText == lanOnly;
            Check(shown, "仅限局域网提示上屏");
            _scopeShown = shown;

            NetTransport.Default = TransportKind.Eos;
            lobby.RefreshScope();
            bool hidden = lobby.ScopeText == "";
            Check(hidden, "能跨公网时提示为空");
            _scopeHidden = hidden;
            NetTransport.Default = TransportKind.Tugboat;

            Destroy(lobbyGo);
            Destroy(host);
            Destroy(host2);
            Destroy(host3);
        }

        // ============================ TARGET ============================

        private void Target()
        {
            var go = new GameObject("ProbeTargetHost");
            string reason;
            var t = NetTransportSetup.EnsureOn(go, TransportKind.Tugboat, out reason);
            if (t == null)
            {
                Check(false, "Tugboat 目标设得上");
                Check(false, "目标能读回");
                Check(false, "种类不符时设不上");
                Check(false, "设不上时不留副作用");
                Check(false, "端口设得上");
                Check(false, "端口不适用时不改");
                Check(false, "非法端口被挡");
                Destroy(go);
                return;
            }

            bool applied = NetTransportSetup.ApplyTarget(t, TransportKind.Tugboat, "10.0.0.9", out reason);
            Check(applied, "Tugboat 目标设得上");
            _targetApplied = applied;

            bool readBack = t.GetClientAddress() == "10.0.0.9";
            Check(readBack, "目标能读回");
            _targetReadBack = readBack;

            // 种类不符：拿一个 Tugboat 实例去设 EOS 的目标。
            // 这一条盯的是那个最容易犯的错误——"反正都是 Transport，一律 SetClientAddress"。
            // FishyEOS 会把这个参数扔掉，所以一旦退化成公共路径，EOS 下就是静默失败。
            bool refused = !NetTransportSetup.ApplyTarget(t, TransportKind.Eos, "0002abcdef", out reason);
            Check(refused, "种类不符时设不上");
            _mismatchRefused = refused;

            bool noSideEffect = t.GetClientAddress() == "10.0.0.9";
            Check(noSideEffect, "设不上时不留副作用");
            _mismatchNoSideEffect = noSideEffect;

            bool portOk = NetTransportSetup.ApplyPort(t, TransportKind.Tugboat, NetProtocol.DefaultPort, out reason)
                          && t.GetPort() == (ushort)NetProtocol.DefaultPort;
            Check(portOk, "端口设得上");
            _portApplied = portOk;

            // 顺序要紧：非法端口这条必须在"不适用种类"那条**之前**跑。
            // 反过来的话，一旦某个实现让 EOS 也能改端口，这个实例的端口就已经被改掉了，
            // 后面这条"非法端口不许改变现状"会跟着红——那是连锁，不是又抓到一个独立缺陷。
            bool badPort = !NetTransportSetup.ApplyPort(t, TransportKind.Tugboat, 0, out reason)
                           && !NetTransportSetup.ApplyPort(t, TransportKind.Tugboat, 70000, out reason)
                           && t.GetPort() == (ushort)NetProtocol.DefaultPort;
            Check(badPort, "非法端口被挡");
            _badPortBlocked = badPort;

            // 对偶：不适用端口的种类，不许动这个实例（否则 Tugboat 的端口会被顺手改掉）
            bool portRefused = !NetTransportSetup.ApplyPort(t, TransportKind.Eos, 7000, out reason)
                               && t.GetPort() == (ushort)NetProtocol.DefaultPort;
            Check(portRefused, "端口不适用时不改");
            _portRefusedForEos = portRefused;

            Destroy(go);
        }

        // ============================ 公共 ============================

        private void Check(bool ok, string what)
        {
            _checked++;
            if (ok) _ok++;
            else _bad.Add(what);
        }

        /// <summary>按完整名找类型（探针自己用，与 NetTransport 内部那份相互独立）。</summary>
        private static Type FindTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            var t = Type.GetType(fullName);
            if (t != null) return t;
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    t = asms[i].GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private void Report()
        {
            bool pass = _ok == _checked && _checked > 0;
            if (_scene == "SPEC")
            {
                Line("未知种类无说明书", Yn(_unknownNoSpec));
                Line("Tugboat 不能跨公网", Yn(_tugLanOnly));
                Line("EOS 能跨公网", Yn(_eosCross));
                Line("Tugboat 不用登录", Yn(_tugNoLogin));
                Line("EOS 要登录", Yn(_eosNeedsLogin));
                Line("Tugboat 有端口", Yn(_tugUsesPort));
                Line("EOS 无端口", Yn(_eosNoPort));
                Line("EOS 不走客户端地址", Yn(_eosNotClientAddress));
                Line("类型名互不相同", Yn(_namesDistinct));
                Line("Tugboat 类型可找到", Yn(_tugTypeFound));
            }
            else if (_scene == "INSTALL")
            {
                Line("装配后管理器认它", Yn(_installedPinned));
                Line("装配类型正确", Yn(_installedTypeOk));
                Line("重复装配不产生第二个", Yn(_installIdempotent));
                Line("缺 SDK 时装配失败", Yn(_eosInstallFails));
                Line("失败时不许回落", Yn(_noFallback));
                Line("反查得到 Tugboat", Yn(_kindBack));
                Line("空管理器反查未知", Yn(_kindUnknown));
                Line("仅限局域网提示上屏", Yn(_scopeShown));
                Line("能跨公网时提示为空", Yn(_scopeHidden));
            }
            else
            {
                Line("Tugboat 目标设得上", Yn(_targetApplied));
                Line("目标能读回", Yn(_targetReadBack));
                Line("种类不符时设不上", Yn(_mismatchRefused));
                Line("设不上时不留副作用", Yn(_mismatchNoSideEffect));
                Line("端口设得上", Yn(_portApplied));
                Line("端口不适用时不改", Yn(_portRefusedForEos));
                Line("非法端口被挡", Yn(_badPortBlocked));
            }
            Line("失败明细", _bad.Count == 0 ? "<无>" : string.Join(" | ", _bad.ToArray()));
            Line("断点通过", _ok + "/" + _checked);
            Debug.Log(pass ? "[SMOKE] 传输层场景通过 " + _scene : "[SMOKE] 传输层场景失败 " + _scene);

            // 必须自己退出：进程挂着不走，下一次跑会抢不到端口/资源，
            // 表现成**偶发**的失败——那种"有时红有时绿"最难查（M55 已经踩过一次）。
            Application.Quit(pass ? 0 : 1);
        }

        private static void Line(string key, string value)
        {
            // 末尾固定补两个空格：标签一旦超过 20 个字符，PadRight 就不再生效，
            // 键和值会**粘成一段**——驱动脚本按"两个以上空白"切分，整条判据就读不到。
            // 读不到的判据等于没有判据，所以分隔符不能依赖 PadRight 是否恰好够长。
            Debug.Log("[SMOKE] " + key.PadRight(20) + "  " + value);
        }

        private static string Yn(bool b) => b ? "是" : "否";
    }
}
#endif
