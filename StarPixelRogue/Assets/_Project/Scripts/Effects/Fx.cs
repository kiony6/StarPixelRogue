using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Effects
{
    /// <summary>
    /// 显现特效系统（M53）：拖尾 / 火星 / 光晕 / 冲击环 / 星芒。
    ///
    /// 为什么单开一套而不是继续往 <see cref="TempEffect"/> 里塞：
    /// <see cref="TempEffect"/> 的每个实例都是 <c>new GameObject</c> 建在场景根上的，
    /// 一轮 Nova 就要新建若干 —— 它的实例数直接上了 <see cref="Dev.VisualProbe"/> 的
    /// LEFTOVER 判据（重开前存量必须恰好 3）。把高频特效挂进去，那条判据就得跟着改数字，
    /// 而"改数字"正是最难被复审发现的一类改动：下一次有人往里面再塞一个，
    /// 数字又对了，缺陷却留下了。所以新特效走**对象池**，体格与既有的弹丸/敌人一致。
    ///
    /// 池的来源有意复用 <see cref="Core.PoolManager"/>：它的 <c>DestroyAll</c> 已被
    /// <c>TeardownWorld</c> 调用，克隆体不会漏到下一局（M24 修的就是这件事）。
    /// 模板本身由 <c>GameBuilder</c> 创建并 Track，归属方式与其余模板完全相同。
    ///
    /// 三条硬约束：
    ///   1. 活跃数有上限（<see cref="MaxActive"/>）——弹幕玩法的特效是可以每帧几十颗的，
    ///      没有上限的话"打得越爽越卡"，而卡是敌人统治不到的一种失败；
    ///   2. 精灵一律取 <see cref="Core.SpriteFactory"/> 的缓存图（ white + alpha ），
    ///      颜色交给 <c>SpriteRenderer.color</c>，因此**不存在按色生成纹理**的新路；
    ///   3. 池不可用时静默跳过：宁可少放一颗特效，也不在联机客户端 / 拆世界过程中抛异常。
    /// </summary>
    public static class Fx
    {
        /// <summary>
        /// 同屏粒子上限。
        ///
        /// 取 240 而不是"看着给"：一轮 12 枚散射每 0.03s 各留一颗拖尾、
        /// 满屏约 60 个敌人各留 2 颗死亡火星，峰值落在 200 上下；240 留一点余量，
        /// 同时把最坏情况下每帧要更新的对象数钉在几百这一档。
        /// 到上限时回收**最早**的那一颗：玩家眼里最不心疼的，永远是刚要从视线淡出的那颗。
        /// </summary>
        public const int MaxActive = 240;

        private static GameObject _template;

        /// <summary>当前使用的特效模板实例（由 GameBuilder 创建并注入）。</summary>
        public static GameObject Template { get { return _template; } }

        /// <summary>
        /// 注入模板。与 <see cref="Core.PoolManager"/> 配套：它按模板的 instanceID 分池。
        /// </summary>
        public static void SetTemplate(GameObject template) { _template = template; }

        private static readonly List<FxSprite> _active = new List<FxSprite>();

        /// <summary>此刻在场的粒子数。探针用它断言"会涨也会落"——只看最后归零，等于没看前半段。</summary>
        public static int ActiveCount { get { return _active.Count; } }

        /// <summary>累计取用次数（探针用它证明"确实放过"，免得"残留为 0"只是因为压根没生成）。</summary>
        public static int SpawnedCount { get; private set; }

        /// <summary>累计回收次数。</summary>
        public static int RecycledCount { get; private set; }

        /// <summary>因池不可用（未建世界 / 已拆世界）而没放成的次数。不计错误——见类注释第 3 条。</summary>
        public static int SkippedCount { get; private set; }

        /// <summary>
        /// 拆世界时同步清账。
        ///
        /// 粒子本体由 <see cref="Core.PoolManager.DestroyAll"/> 收掉，这里要清的是
        /// **静态登记表本身**：里面的引用会一直指向已经销毁的对象而被继续遍历，
        /// 于是下一局的 <see cref="ActiveCount"/> 一开始就带着上一局的残账起步，
        /// 上限判定形同虚设（M28 的存活登记表是同一个教训）。
        /// </summary>
        public static void Reset()
        {
            _active.Clear();
            _template = null;
        }

        /// <summary>模板或池缺位时一律不放特效——宁可少放一颗，也不在没有世界的地方新建对象。</summary>
        private static bool Available
        {
            get { return _template != null && Core.PoolManager.Instance != null; }
        }

        /// <summary>登记一颗刚出场的粒子，并在超限时挤掉最早的那颗。</summary>
        private static void Register(FxSprite p)
        {
            if (p == null) return;
            SpawnedCount++;
            if (_active.Count >= MaxActive)
            {
                var oldest = _active[0];
                _active.RemoveAt(0);
                if (oldest != null) oldest.ReturnNow();
            }
            _active.Add(p);
        }

        internal static void Unregister(FxSprite p)
        {
            if (p == null) return;
            _active.Remove(p);
            RecycledCount++;
        }

        /// <summary>取一颗粒子实例；池不可用时返回 null。</summary>
        private static FxSprite Take()
        {
            if (!Available) { SkippedCount++; return null; }
            var go = Core.PoolManager.Instance.Get(_template);
            var p = go != null ? go.GetComponent<FxSprite>() : null;
            if (p == null) { SkippedCount++; return null; }
            return p;
        }

        // —— 发光体 ——

        /// <summary>
        /// 拖尾：留在身后慢慢淡出的一块余晖。运动/方向感全靠它——没有拖尾的弹丸
        /// 在高速下就是一颗会瞬移的方块，玩家读不出它往哪儿去。
        /// </summary>
        public static void Trail(Vector2 pos, Color color, float size, float life, int order = 4)
        {
            var p = Take();
            if (p == null) return;
            p.Play(pos, Vector2.zero,
                size, size * 0.35f,
                new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a)),
                new Color(color.r, color.g, color.b, 0f),
                life, 0f, 0f, Core.SpriteFactory.MakeGlow(), order);
            Register(p);
        }

        /// <summary>
        /// 星芒闪光：爆炸核心 / 命中点那一瞬。
        /// 它替代的是"把方块放大再淡出"那种老做法——后者看着像屏幕脏了，前者像出了光。
        /// </summary>
        public static void Flash(Vector2 pos, Color color, float size, float life, int order = 5)
        {
            var p = Take();
            if (p == null) return;
            p.Play(pos, Vector2.zero,
                size, size * 0.2f,
                new Color(color.r, color.g, color.b, 1f),
                new Color(color.r, color.g, color.b, 0f),
                life, 0f, 0f, Core.SpriteFactory.MakeStar(), order);
            Register(p);
        }

        /// <summary>
        /// 冲击环：空心环，从内向外拉开。以前用实心方块缩放冒充扩散，
        /// 扩到一半就变成一大片雾块；换成空心之后才读得出"一圈能量推了出去"。
        /// </summary>
        public static void Shockwave(Vector2 pos, float fromR, float toR, Color color, float life, int order = 4)
        {
            var p = Take();
            if (p == null) return;
            p.Play(pos, Vector2.zero,
                fromR * 2f, toR * 2f,
                new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a)),
                new Color(color.r, color.g, color.b, 0f),
                life, 0f, 0f, Core.SpriteFactory.MakeRing(), order);
            Register(p);
        }

        /// <summary>
        /// 柔光晕：垫在各种光源底下的那一团。
        /// </summary>
        public static void Glow(Vector2 pos, Color color, float size, float life, int order = 3)
        {
            var p = Take();
            if (p == null) return;
            p.Play(pos, Vector2.zero,
                size, size * 0.6f,
                new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a)),
                new Color(color.r, color.g, color.b, 0f),
                life, 0f, 0f, Core.SpriteFactory.MakeGlow(), order);
            Register(p);
        }

        /// <summary>
        /// 全向爆散：一圈火星向外飞。
        /// </summary>
        public static void Burst(Vector2 pos, Color color, int count, float speed, float size, float life,
            int order = 4)
        {
            if (count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                var p = Take();
                if (p == null) return;
                // 均匀分布 + 半格抖动：纯粹均匀会让火星排成一个规矩的圆，看着像生成的
                float ang = (i / (float)count) * Mathf.PI * 2f + Random.Range(-0.12f, 0.12f);
                float sp = speed * Random.Range(0.65f, 1.15f);
                p.Play(pos, new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * sp,
                    size, size * 0.25f,
                    new Color(color.r, color.g, color.b, 1f),
                    new Color(color.r, color.g, color.b, 0f),
                    life * Random.Range(0.75f, 1.25f),
                    0f, 2.2f, Core.SpriteFactory.MakeSparkPixel(), order);
                Register(p);
            }
        }

        /// <summary>
        /// 定向喷射：命中反馈 / 枪口火焰。dir 不必归一化，速度按参数给定值走。
        /// </summary>
        public static void Spray(Vector2 pos, Vector2 dir, Color color, int count, float speed, float size,
            float life, float spreadRad, int order = 4)
        {
            if (count <= 0) return;
            Vector2 base_ = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
            float baseAng = Mathf.Atan2(base_.y, base_.x);
            for (int i = 0; i < count; i++)
            {
                var p = Take();
                if (p == null) return;
                float ang = baseAng + Random.Range(-spreadRad, spreadRad);
                float sp = speed * Random.Range(0.6f, 1.2f);
                p.Play(pos, new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * sp,
                    size, size * 0.3f,
                    new Color(color.r, color.g, color.b, 1f),
                    new Color(color.r, color.g, color.b, 0f),
                    life * Random.Range(0.7f, 1.3f),
                    0f, 3.0f, Core.SpriteFactory.MakeSparkPixel(), order);
                Register(p);
            }
        }
    }
}
