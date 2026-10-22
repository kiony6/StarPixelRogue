#if FISHNET_ENABLED
using System;
using UnityEngine;
using FishNet.Managing.Transporting;
using FishNet.Transporting;

namespace StarPixel.Net
{
    /// <summary>
    /// 传输层的装配与设参。**整个文件只在装了 Fish-Net 时编译**。
    ///
    /// <para>
    /// 与 <see cref="NetTransport"/> 的分工：那边是"说明书"（纯数据，谁都能查），
    /// 这边是"动手"（要 <c>Transport</c> / <c>TransportManager</c> 这两个类型）。
    /// 拆成两个文件而不是一个文件里套条件编译，是因为静态审计第 5 项要回答
    /// "这个类型在没装插件时存不存在"——一半在里一半在外，那个问题就没有确定答案。
    /// </para>
    /// </summary>
    public static class NetTransportSetup
    {
        /// <summary>
        /// 在宿主物体上装好传输层组件，返回装上的那个（失败返回 null，原因写进 <paramref name="reason"/>）。
        /// <para>
        /// **幂等**：已经有一个同类组件就直接返回它。重复添加会让同一个物体上出现两个 Transport，
        /// 而 <c>TryGetComponent</c> 取哪个是不确定的——那种"有时连得上有时连不上"最难查。
        /// </para>
        /// <para>
        /// **类型找不到就如实失败**，绝不回落成 Tugboat。回落会把"我以为在跑 EOS"变成
        /// "其实在跑局域网直连"，而现象只有一个：跨公网连不上。
        /// </para>
        /// <para>
        /// 用宿主物体而不是 <c>NetworkManager</c>：鱼网在物体**激活前**就要看见传输层
        /// （<c>InitializeOnce</c> 里会用），那时 <c>TransportManager</c> 还没被创建出来。
        /// </para>
        /// </summary>
        public static Transport EnsureOn(GameObject host, TransportKind kind, out string reason)
        {
            reason = "";
            if (host == null) { reason = "host is null"; return null; }

            var spec = NetTransport.Describe(kind);
            if (spec == null) { reason = "unknown transport kind: " + (int)kind; return null; }

            var type = FindType(spec.TypeName);
            if (type == null) { reason = "type not found: " + spec.TypeName + " (SDK not installed)"; return null; }

            var existing = host.GetComponent(type) as Transport;
            if (existing != null) return existing;

            var added = host.AddComponent(type) as Transport;
            if (added == null) { reason = "not a Transport component: " + spec.TypeName; return null; }
            return added;
        }

        /// <summary>
        /// 装好传输层，并**显式**指定 <c>TransportManager</c> 用这一个。
        /// <para>
        /// 显式赋值这一步不能省：FishNet 的 <c>TryAddDefaultTransport</c> 在 <c>Transport</c> 为空时
        /// 用 <c>TryGetComponent</c> 抓"场景里任意一个"。少了这一步，场景里一旦有两个传输层，
        /// 真正跑的是哪一个取决于组件顺序，而不是取决于 <see cref="NetTransport.Default"/>。
        /// </para>
        /// </summary>
        public static bool EnsureInstalled(TransportManager tm, TransportKind kind, out string reason)
        {
            reason = "";
            if (tm == null) { reason = "TransportManager is null"; return false; }

            var t = EnsureOn(tm.gameObject, kind, out reason);
            if (t == null) return false;          // 失败就失败，不许在这里偷偷换成别的

            tm.Transport = t;
            return true;
        }

        /// <summary>
        /// 告诉传输层"连谁"。返回是否真的设上了。
        /// <para>
        /// Tugboat 走 <c>SetClientAddress</c>；平台中继走它组件自己的成员。
        /// 平台 SDK 不在本构建里时**如实返回 false**——
        /// 绝不能退回 <c>SetClientAddress</c> 装作设上了：FishyEOS 会把它扔掉，
        /// 于是表现为"点了加入、没有报错、永远连不上"。
        /// </para>
        /// </summary>
        public static bool ApplyTarget(Transport t, TransportKind kind, string target, out string reason)
        {
            reason = "";
            if (t == null) { reason = "transport is null"; return false; }

            var spec = NetTransport.Describe(kind);
            if (spec == null) { reason = "unknown transport kind: " + (int)kind; return false; }

            if (spec.UsesClientAddress)
            {
                // 类型对不上就没资格往这个实例上设地址（探针会故意喂这种组合）
                if (!Matches(t, spec)) { reason = "transport type mismatch: expected " + spec.TypeName; return false; }
                t.SetClientAddress(target ?? "");
                return true;
            }

            if (string.IsNullOrEmpty(spec.TargetMember))
            {
                reason = "target member not wired for " + spec.TypeName;
                return false;
            }

            if (!Matches(t, spec)) { reason = "transport type mismatch: expected " + spec.TypeName; return false; }

            // 反射设成员：这样凭据到货、SDK 装好后**这段代码一行不用改**，
            // 也不需要在没有 SDK 的构建里引用它的类型。
            var prop = t.GetType().GetProperty(spec.TargetMember);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
            {
                prop.SetValue(t, target ?? "", null);
                return true;
            }
            var field = t.GetType().GetField(spec.TargetMember);
            if (field != null && field.FieldType == typeof(string))
            {
                field.SetValue(t, target ?? "");
                return true;
            }

            reason = "no writable string member " + spec.TargetMember + " on " + t.GetType().FullName;
            return false;
        }

        /// <summary>
        /// 设置传输层端口。返回是否真的设上了。
        /// <para>
        /// 只在 <see cref="TransportSpec.UsesPort"/> 为真时才该调它。
        /// 平台中继没有端口概念（<c>SetPort</c> 被覆写成空实现），这里如实返回 false 且**不碰实例**。
        /// </para>
        /// </summary>
        public static bool ApplyPort(Transport t, TransportKind kind, int port, out string reason)
        {
            reason = "";
            if (t == null) { reason = "transport is null"; return false; }
            if (port <= 0 || port > 65535) { reason = "port out of range: " + port; return false; }

            var spec = NetTransport.Describe(kind);
            if (spec == null) { reason = "unknown transport kind: " + (int)kind; return false; }
            if (!spec.UsesPort) { reason = "transport has no port concept: " + spec.TypeName; return false; }
            if (!Matches(t, spec)) { reason = "transport type mismatch: expected " + spec.TypeName; return false; }

            t.SetPort((ushort)port);
            return true;
        }

        /// <summary>
        /// 反查 <c>TransportManager</c> 现在真正认的是哪一种。认不出返回 <see cref="TransportKind.Unknown"/>。
        /// <para>装配"成功"与"真的生效"是两件事，这个方法让后者可以被断言。</para>
        /// </summary>
        public static TransportKind KindOf(TransportManager tm)
        {
            if (tm == null || tm.Transport == null) return TransportKind.Unknown;
            string actual = tm.Transport.GetType().FullName;
            for (int i = 1; i <= 3; i++)
            {
                var spec = NetTransport.Describe((TransportKind)i);
                if (spec != null && spec.TypeName == actual) return spec.Kind;
            }
            return TransportKind.Unknown;
        }

        private static bool Matches(Transport t, TransportSpec spec)
        {
            return t != null && t.GetType().FullName == spec.TypeName;
        }

        /// <summary>按完整名找类型。走 <c>AppDomain</c> 遍历，因为传输层可能在任何插件程序集里。</summary>
        private static Type FindType(string fullName)
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
                catch
                {
                    // 个别动态程序集在 GetType 时会抛：跳过它，别让一次探测毁掉整条装配链
                }
            }
            return null;
        }
    }
}
#endif
