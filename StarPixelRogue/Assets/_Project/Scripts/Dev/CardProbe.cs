using System;
using System.Collections;
using System.Collections.Generic;
using StarPixel.Combat;
using StarPixel.Core;
using StarPixel.UI;
using UnityEngine;
using UnityEngine.UI;

namespace StarPixel.Dev
{
    /// <summary>
    /// M37「联机升级选卡不丢单」无头实测。
    ///
    /// 为什么要测：
    ///   房主推卡是**多次**的（每升一级推一组），而被推的两处状态都只维护**一份**：
    ///     ① 客户端 <c>LevelUpManager.PresentExternal</c> 开头就是 `if (_active) return;`——
    ///        面板正开着时推来的卡直接扔掉。联机客户端连升两级是常态，第二张及以后
    ///        永久消失，房主也永远收不到选择回传。
    ///     ② 房主 <c>NetPlayer._cachedOffers</c> 每次推卡 <c>Clear()</c> 再填新一组，
    ///        客户端回传的却只有一个 index。还没选完就又升一级时，玩家点的是旧组第 1 张、
    ///        房主应用的是新组第 1 张——选的和得的不是同一张，且不报错、不崩。
    ///   两处都不是"联机专属 bug"那么简单：本地路径早就有排队（_pending + TryPresent），
    ///   外部路径没有——**同一件事两条路不一致**，和 M32 的 AddSkill 缺守卫是同一类。
    ///
    /// 为什么单机无头就能测：
    ///   两处成因都是纯逻辑，不是网络时序。抽成 <c>UpgradeOfferBatches</c>（不依赖 FishNet 的
    ///   批次表）+ 面板排队后，整条链路在单机里可断言，不必真开双开。
    ///
    /// 六个场景：
    ///   BASE    地基自检：外部模式真的生效（本地升级不弹卡）/ 单组能上屏 / 文本真是那一组 / 空候选不计入
    ///   QUEUE   面板占用时连推 3 组：收到 3、上屏 1、入队 2、丢弃 0，且上屏的必须是第一组
    ///   DRAIN   依次选掉：第 2、3 组按顺序自动补上，全选完面板关闭、队列清零
    ///   ORDER   推 4 组再全部选掉：上屏顺序与回调顺序都必须等于推送顺序（防错配）
    ///   BATCH   批次表纯逻辑：各批取回不串 / 一批只能取一次 / 非法 id 与越界都拒绝 / 上限淘汰
    ///   RESTORE 收尾与反向：队列清零、切回本地后本地升级仍能弹卡（防修过头把本地堵死）、全程零异常
    ///
    /// 判据的区分力：
    ///   1) QUEUE 里"上屏的必须是第一组"是关键：若改回覆盖式实现，上屏的会变成最后一组，
    ///      但"收到 3 / 上屏 1"照样成立——只数不看的判据抓不到错配。
    ///   2) ORDER 同时断言上屏顺序与回调顺序：只排队不批次化的话，上屏顺序对、回调错配。
    ///   3) RESTORE 必须反向断言本地路径：把 PresentExternal 改成永不弹卡也能让 QUEUE/DRAIN 全绿。
    ///   4) BASE 第 1 条是手法自检：若 SetExternalCards 没生效，QUEUE 会一片假绿。
    /// </summary>
    public class CardProbe : MonoBehaviour
    {
        private string _scene = "BASE";
        private int _errors;
        private string _firstError = "";
        private readonly List<string> _fail = new List<string>();

        // —— 通用计数 ——
        private int _checked;
        private int _ok;

        // —— BASE ——
        private bool _baExternalBlocks;
        private bool _baShown;
        private bool _baTextOk;
        private bool _baEmptyIgnored;
        private int _baShownCount;
        private int _baRecvAfterEmpty;

        // —— QUEUE ——
        private int _quReceived, _quPresented, _quQueued, _quDropped, _quPending;
        private bool _quCountsOk, _quFirstGroupOk, _quRecvOk, _quDropOk;
        private string _quTitle = "?";

        // —— DRAIN ——
        private bool _drSecondOk, _drThirdOk, _drClosedOk, _drPresentedOk, _drDropOk;
        private string _drSecond = "?", _drThird = "?";
        private int _drPresented, _drDropped, _drPending;

        // —— ORDER ——
        private string _orShownSeq = "?";
        private string _orChosenSeq = "?";
        private bool _orShownOk, _orChosenOk, _orDropOk;
        private int _orDropped;

        // —— BATCH ——
        private bool _btTakeOk, _btTwiceOk, _btBadIdOk, _btRangeOk, _btEvictOk, _btApplyOk;
        private int _btEvicted, _btPending;
        private string _btTook = "?";
        private bool _btEvictEventOk;
        private string _btEvictedIds = "?";

        // —— RESTORE ——
        private bool _rsQueueOk, _rsPanelClosedOk, _rsLocalOk, _rsNoErrOk;
        private int _rsPending;

        // —— GATE（M38）：待确认表纯逻辑 ——
        private bool _gtConfirmOk, _gtTwiceOk, _gtUnknownOk, _gtMismatchOk, _gtRejectOk,
                     _gtTimeoutOk, _gtInvalidateOk;
        private int _gtConfirmed, _gtRejected, _gtTimedOut, _gtPending;

        // —— INVAL（M38）：批次作废必须能被撤销 ——
        private bool _ivShownOk, _ivClosedOk, _ivQueueOk, _ivKeptOk, _ivOtherOk;
        private int _ivInvalidated, _ivPending;
        private string _ivAfter = "?";

        // 制造可辨识的候选组（真实 BuildOffers 的标题是本地化串，分不清是哪一组）
        private static List<UpgradeOffer> Group(string tag, List<string> applied)
        {
            var list = new List<UpgradeOffer>();
            for (int i = 0; i < 3; i++)
            {
                var name = tag + "#" + i;
                list.Add(new UpgradeOffer
                {
                    title = "CARD_" + name,
                    desc = "desc_" + name,
                    kind = 0,
                    apply = () => { if (applied != null) applied.Add(name); }
                });
            }
            return list;
        }

        /// <summary>读面板上**真实渲染**的文本（不读内部列表——M35 的教训）。</summary>
        private static string PanelText(LevelUpManager lum)
        {
            var t = lum != null ? lum.CurrentPanel : null;
            if (t == null) return "";
            var texts = t.GetComponentsInChildren<Text>(true);
            var sb = new System.Text.StringBuilder();
            foreach (var x in texts)
            {
                if (x == null) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(x.text);
            }
            return sb.ToString();
        }

        /// <summary>面板上是否真的写着这一组（按组标签判断，不看 index）。</summary>
        private static bool ShowsGroup(LevelUpManager lum, string tag)
        {
            return PanelText(lum).IndexOf("CARD_" + tag, StringComparison.Ordinal) >= 0;
        }

        public static void Start(string scene)
        {
            var go = new GameObject("CardProbe");
            go.SetActive(false);
            var p = go.AddComponent<CardProbe>();
            p._scene = scene;
            go.SetActive(true);
        }

        private void Start()
        {
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                {
                    _errors++;
                    if (_firstError == "") _firstError = msg.Split('\n')[0];
                }
            };
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            SettingsManager.Ensure();
            SaveManager.Ensure();
            Application.targetFrameRate = 60;
            GameBuilder.PrepareStage();

            // BATCH 是纯逻辑，不需要搭世界（搭了反而多一堆无关对象）
            IEnumerator body;
            switch (_scene)
            {
                case "QUEUE":   body = Queue();   break;
                case "DRAIN":   body = Drain();   break;
                case "ORDER":   body = Order();   break;
                case "BATCH":   body = Batch();   break;
                case "GATE":    body = Gate();    break;   // M38：纯逻辑，不搭世界
                case "INVAL":   body = Inval();   break;   // M38：批次作废撤销
                case "RESTORE": body = Restore(); break;
                default:        body = Base();    break;
            }

            // 手动驱动迭代器：C# 不允许在带 catch 的 try 里 yield（CS1626），
            // 但探针必须自己吞掉异常——协程被打断后断言全不执行、日志一片空白，
            // 表现出来是"悄悄挂死"而不是"判据变红"（M29 踩过）。
            bool more = true;
            while (more)
            {
                object cur = null;
                try
                {
                    more = body.MoveNext();
                    if (more) cur = body.Current;
                }
                catch (Exception e)
                {
                    Fail("探针自身抛异常——协程被打断会导致日志空白、结果假绿: " + e.Message);
                    more = false;
                }
                if (more) yield return cur;
            }

            Report();
            Application.Quit(0);
        }

        /// <summary>timeScale 可能被暂停改掉，WaitForSeconds 会永远不返回——自己累加真实时间。</summary>
        private static IEnumerator WaitReal(float sec)
        {
            float acc = 0f;
            while (acc < sec)
            {
                acc += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private static IEnumerator Quiet(int frames = 3)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        /// <summary>
        /// 等条件成立（最多 timeout 秒）。
        /// 为什么不用固定睡 0.25s：无头 batchmode 帧率不稳（可能只有十几帧每秒），
        /// 而 Destroy(panel) 与延后两帧的补弹都按**帧**走——固定时长在低帧率下会刚好差一帧，
        /// 表现出来是"偶发判据变红"，最难查的那种。等条件才是稳的。
        /// </summary>
        private static IEnumerator Until(Func<bool> cond, float timeout = 4f)
        {
            float acc = 0f;
            while (acc < timeout)
            {
                if (cond != null && cond()) break;
                yield return null;
                acc += Time.unscaledDeltaTime;
            }
            yield return null;
        }

        private void Judge(bool ok, string why)
        {
            _checked++;
            if (ok) _ok++;
            else Fail(why);
        }

        // =====================================================================================
        // BASE：地基自检——外部模式必须真的生效，否则 QUEUE/DRAIN 的绿全是假的
        // =====================================================================================
        private IEnumerator Base()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("读不到 LevelUpManager——BASE 判据全部失灵"); yield break; }

            int recv0 = lum.ExternalReceived;

            // 1) 外部模式下，本地升级事件**不许**自己弹卡（证明 SetExternalCards 真的生效）
            lum.SetExternalCards(true);
            EventBus.RaiseLevelUp(2);
            yield return Quiet();
            _baExternalBlocks = !lum.IsShowing;
            Judge(_baExternalBlocks,
                  "把 SetExternalCards(true) 之后，本地 OnLevelUp 仍然弹出了面板——"
                  + "说明外部模式压根没生效，QUEUE/DRAIN 场景测的就不是外部供选路径，绿是假的");

            // 2) 单组外部推卡必须能上屏
            lum.PresentExternal(Group("B", null), _ => { });
            yield return Quiet();
            _baShown = lum.IsShowing && lum.CurrentPanel != null;
            _baShownCount = lum.ExternalPresented;
            Judge(_baShown && _baShownCount == 1,
                  "单组外部推卡没上屏（IsShowing=" + lum.IsShowing + "、面板="
                  + (lum.CurrentPanel != null ? "有" : "无") + "、上屏计数=" + _baShownCount + "）——"
                  + "外部供选入口本身不通，后面的排队判据无从谈起");

            // 3) 上屏文本必须真是这一组（读真实 Text，不是内部列表）
            _baTextOk = ShowsGroup(lum, "B");
            Judge(_baTextOk,
                  "面板上的文本不是刚推的那一组（实际：" + PanelText(lum) + "）——"
                  + "读真实渲染文本才能证明玩家看到的是对的，只数上屏次数会被错配蒙过去");

            // 4) 空候选不许计入收到（调用方问题，不该污染收支）
            lum.PresentExternal(null, _ => { });
            lum.PresentExternal(new List<UpgradeOffer>(), _ => { });
            _baRecvAfterEmpty = lum.ExternalReceived - recv0;
            _baEmptyIgnored = _baRecvAfterEmpty == 1;
            Judge(_baEmptyIgnored,
                  "空候选也被计入收到（收到增量=" + _baRecvAfterEmpty + "，应为 1）——"
                  + "空候选不该发面板也不该计数，混进来会让「收到 vs 上屏」的对账失去意义");

            // 收尾：把面板选掉，别留下暂停态
            lum.AutoChooseFirst();
            yield return Quiet();
        }

        // =====================================================================================
        // QUEUE：面板占用时连推 3 组——必须排队，且上屏的仍是第一组
        // =====================================================================================
        private IEnumerator Queue()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("读不到 LevelUpManager——QUEUE 判据全部失灵"); yield break; }

            lum.SetExternalCards(true);

            // 面板先开着（第一组直接上屏），再连推两组——模拟"升级比玩家点卡快"
            lum.PresentExternal(Group("G1", null), _ => { });
            yield return Quiet();
            lum.PresentExternal(Group("G2", null), _ => { });
            lum.PresentExternal(Group("G3", null), _ => { });
            yield return Quiet();

            _quReceived = lum.ExternalReceived;
            _quPresented = lum.ExternalPresented;
            _quQueued = lum.ExternalQueued;
            _quDropped = lum.ExternalDropped;
            _quPending = lum.ExternalPendingCount;
            _quTitle = PanelText(lum);

            Judge(_quReceived == 3,
                  "连推 3 组只收到 " + _quReceived + " 组——推卡在中途就丢了，后面无从谈起");
            _quRecvOk = _quReceived == 3;

            Judge(_quPresented == 1 && _quQueued == 2 && _quPending == 2,
                  "收支不对：上屏=" + _quPresented + "（应 1）、入队=" + _quQueued + "（应 2）、残余="
                  + _quPending + "（应 2）——外部供选没有把占用期间推来的卡排进队列");
            _quCountsOk = _quPresented == 1 && _quQueued == 2 && _quPending == 2;

            Judge(_quDropped == 0,
                  "丢弃了 " + _quDropped + " 组推卡——面板占用时推来的卡被直接扔掉，"
                  + "玩家这次升级永久拿不到强化，房主也永远收不到选择回传");
            _quDropOk = _quDropped == 0;

            // 最关键的一条：上屏的必须是**第一组**。
            // 若实现是"后来的覆盖前面的"，上屏会变成 G3，但上面三条计数照样全对——
            // 只数不看顺序的判据抓不到这种错配。
            _quFirstGroupOk = ShowsGroup(lum, "G1") && !ShowsGroup(lum, "G3");
            Judge(_quFirstGroupOk,
                  "面板上不是先推的那一组（实际：" + _quTitle + "）——后来的推卡盖掉了前面的，"
                  + "玩家看到的是最后一组、点的也是最后一组，前面几次升级的选择全被顶掉");
        }

        // =====================================================================================
        // DRAIN：依次选掉——队列必须按顺序自动补上，全选完面板关闭
        // =====================================================================================
        private IEnumerator Drain()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("读不到 LevelUpManager——DRAIN 判据全部失灵"); yield break; }

            lum.SetExternalCards(true);
            lum.PresentExternal(Group("D1", null), _ => { });
            yield return Quiet();
            lum.PresentExternal(Group("D2", null), _ => { });
            lum.PresentExternal(Group("D3", null), _ => { });
            yield return Quiet();

            // 选掉第一组 → 第二组必须自动补上。
            // 注意要等**新**面板：Destroy(panel) 到帧末才生效，刚选完时 CurrentPanel 拿到的
            // 还是被标记销毁的那一块，直接读文本会读到上一组，判据就地假红。
            var d1 = lum.CurrentPanel;
            lum.AutoChooseFirst();
            yield return Until(() => lum.CurrentPanel != null && lum.CurrentPanel != d1);
            _drSecond = PanelText(lum);
            _drSecondOk = ShowsGroup(lum, "D2") && !ShowsGroup(lum, "D1");
            Judge(_drSecondOk,
                  "选掉第一组后，补上来的不是第二组（实际：" + _drSecond + "）——"
                  + "队列没按顺序出，或者压根没自动弹下一张");

            // 选掉第二组 → 第三组
            var d2 = lum.CurrentPanel;
            lum.AutoChooseFirst();
            yield return Until(() => lum.CurrentPanel != null && lum.CurrentPanel != d2);
            _drThird = PanelText(lum);
            _drThirdOk = ShowsGroup(lum, "D3");
            Judge(_drThirdOk,
                  "选掉第二组后，补上来的不是第三组（实际：" + _drThird + "）——队列顺序错乱");

            // 选掉第三组 → 面板必须关闭（否则玩家卡在永远关不掉的选卡界面里）
            lum.AutoChooseFirst();
            yield return Until(() => lum.CurrentPanel == null);
            _drPending = lum.ExternalPendingCount;
            _drPresented = lum.ExternalPresented;
            _drDropped = lum.ExternalDropped;
            bool closed = !lum.IsShowing;
            _drClosedOk = closed && _drPending == 0;
            Judge(_drClosedOk,
                  "三组都选完后面板仍开着（IsShowing=" + lum.IsShowing + "、队列残余="
                  + _drPending + "）——队列没排空，玩家会卡在选卡界面里出不来");

            Judge(_drPresented == 3,
                  "上屏总数 " + _drPresented + "（应 3）——有一组始终没被弹出来，等于少一次强化");
            _drPresentedOk = _drPresented == 3;

            Judge(_drDropped == 0, "全程丢弃 " + _drDropped + " 组（应 0）");
            _drDropOk = _drDropped == 0;
        }

        // =====================================================================================
        // ORDER：上屏顺序与回调顺序都必须等于推送顺序
        // =====================================================================================
        private IEnumerator Order()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("读不到 LevelUpManager——ORDER 判据全部失灵"); yield break; }

            lum.SetExternalCards(true);

            var shownSeq = new List<string>();
            var chosenSeq = new List<string>();

            // 第一组直接上屏，其余三组在面板占用期间推入队列
            for (int i = 1; i <= 4; i++)
            {
                string tag = "O" + i;
                var applied = new List<string>();
                lum.PresentExternal(Group(tag, applied), idx => { chosenSeq.Add(tag); });
                if (i == 1) yield return Quiet();
            }
            yield return Quiet();
            shownSeq.Add(ShownTag(lum));          // 第一组：直接上屏

            // 依次选掉四组：每次选完记下补上来的那一组（最后一次应彻底关闭）
            for (int i = 0; i < 4; i++)
            {
                var prev = lum.CurrentPanel;
                lum.AutoChooseFirst();
                if (i < 3)
                    yield return Until(() => lum.CurrentPanel != null && lum.CurrentPanel != prev);
                else
                    yield return Until(() => lum.CurrentPanel == null);
                if (i < 3) shownSeq.Add(ShownTag(lum));
            }

            _orShownSeq = string.Join(",", shownSeq.ToArray());
            _orChosenSeq = string.Join(",", chosenSeq.ToArray());
            _orDropped = lum.ExternalDropped;

            // 上屏顺序：只排队不批次化时，这一条照样全对——错配要靠下一条抓
            _orShownOk = _orShownSeq == "O1,O2,O3,O4";
            Judge(_orShownOk,
                  "上屏顺序是 " + _orShownSeq + "（应 O1,O2,O3,O4）——队列取出的顺序与推送顺序不一致");

            // 回调顺序：每一组的选择回调必须回到它自己那一组上。
            // 房主侧若用「一份被覆盖的候选表 + 裸 index」，玩家点 O1 的第 1 张、
            // 房主应用的是 O4 的第 1 张——回调顺序看起来还是 O1..O4，
            // 所以这一条必须和批次表（BATCH 场景）配合才成立，单看它会漏。
            _orChosenOk = _orChosenSeq == "O1,O2,O3,O4";
            Judge(_orChosenOk,
                  "选择回调顺序是 " + _orChosenSeq + "（应 O1,O2,O3,O4）——"
                  + "面板与回调没按同一批次绑定，玩家看到的卡和实际生效的卡会不是同一张");

            Judge(_orDropped == 0, "全程丢弃 " + _orDropped + " 组（应 0）");
            _orDropOk = _orDropped == 0;
        }

        // =====================================================================================
        // GATE：待确认表纯逻辑（M38，不依赖 FishNet，单机直接测）
        //
        // 这里钉的是「本地镜像不许先斩后奏」：一次选择必须先记账、等房主回执、
        // 回执对得上才许生效，且一笔只许结一次。
        // =====================================================================================
        private IEnumerator Gate()
        {
            var g = new UpgradeApplyGate();

            // 1) 正常路径：提交 → 确认一次成功
            int a1 = g.Submit(7, 1);
            _gtConfirmOk = a1 > 0 && g.Confirm(a1, 7, 1) && g.ConfirmedCount == 1;
            Judge(_gtConfirmOk,
                  "正常确认没生效（ackId=" + a1 + "、确认数=" + g.ConfirmedCount + "）——"
                  + "房主点头之后本地镜像反而没应用，玩家选了卡却什么都没得到");

            // 2) 同一笔确认两次：第二次必须无效（防重放 / 防重复发放强化）
            _gtTwiceOk = !g.Confirm(a1, 7, 1) && g.ConfirmedCount == 1;
            Judge(_gtTwiceOk,
                  "同一笔选择被确认了两次（确认数=" + g.ConfirmedCount + "）——"
                  + "一条回执能刷两次强化，玩家点一次卡拿到两份");

            // 3) 未知 ackId（伪造回执 / 不是自己那笔）
            _gtUnknownOk = !g.Confirm(9999, 7, 1) && !g.Confirm(0, 0, 0);
            Judge(_gtUnknownOk, "伪造/未知的回执竟然被接受了——回执没有归属校验，可被重放");

            // 4) 回执与请求对不上（批次或 index 不符）：必须拒绝
            //    这正是「客户端按自己记得的那组去应用」的入口，对不上就不能算数
            int a2 = g.Submit(8, 2);
            _gtMismatchOk = !g.Confirm(a2, 8, 0) && !g.Confirm(a2, 9, 2) && g.ConfirmedCount == 1;
            Judge(_gtMismatchOk,
                  "回执与请求对不上也被接受了（batch/index 不一致仍生效）——"
                  + "客户端会拿着自己记的那一组去应用，与房主认定的不是同一张");

            // 5) 被拒：一笔只算一次，且确认不许再生效
            int a3 = g.Submit(10, 0);
            _gtRejectOk = g.Reject(a3) && !g.Reject(a3) && !g.Confirm(a3, 10, 0)
                          && g.RejectedCount == 1;
            Judge(_gtRejectOk,
                  "拒绝路径不对（拒收数=" + g.RejectedCount + "、之后还能被确认）——"
                  + "房主拒绝了，客户端却仍有办法把它算成生效");

            // 6) 房主不回：超时按被拒处理，不许永远悬着
            var g2 = new UpgradeApplyGate();
            int b1 = g2.Submit(11, 0, 100f);
            int b2 = g2.Submit(12, 1, 100f);
            int b3 = g2.Submit(13, 2, 200f);          // 这一笔还没到超时
            int purged = g2.PurgeExpired(100f + UpgradeApplyGate.DefaultTimeout + 1f);
            _gtTimedOut = g2.TimedOutCount;
            _gtPending = g2.PendingCount;
            _gtTimeoutOk = purged == 2 && b3 > 0 && g2.PendingCount == 1 && !g2.Confirm(b1, 11, 0);
            Judge(_gtTimeoutOk,
                  "超时清理不对（清理=" + purged + "、残余=" + g2.PendingCount + "）——"
                  + "房主不回时选择会永远悬着，客户端既不敢应用也不敢丢弃");

            _gtConfirmed = g.ConfirmedCount;
            _gtRejected = g.RejectedCount + g2.TimedOutCount;

            // 7) 批次作废：该批下面悬着的全部作废（房主淘汰了这一批）
            var g3 = new UpgradeApplyGate();
            g3.Submit(20, 0, 0f);
            g3.Submit(20, 2, 0f);
            g3.Submit(21, 1, 0f);
            int n = g3.Invalidate(20);
            _gtInvalidateOk = n == 2 && g3.InvalidatedCount == 2 && g3.PendingCount == 1
                              && !g3.Confirm(1, 20, 0);
            Judge(_gtInvalidateOk,
                  "批次作废不对（作废=" + n + "、残余=" + g3.PendingCount + "）——"
                  + "房主淘汰了这一批，客户端还留着它的选择等一个永远不会来的回执");

            yield break;
        }

        // =====================================================================================
        // INVAL：房主淘汰某批 → 客户端必须能把它撤掉（M38）
        //
        // 修之前：淘汰只记了个 EvictedCount，没有出口。玩家眼前摆着一张"点了也没用"的卡。
        // =====================================================================================
        private IEnumerator Inval()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("读不到 LevelUpManager——INVAL 判据全部失灵"); yield break; }

            lum.SetExternalCards(true);

            // 第一组上屏（批次 101），后两组入队（批次 102、103）
            lum.PresentExternal(101, Group("V1", null), _ => { });
            yield return Quiet();
            lum.PresentExternal(102, Group("V2", null), _ => { });
            lum.PresentExternal(103, Group("V3", null), _ => { });
            yield return Quiet();

            _ivShownOk = lum.CurrentBatchId == 101 && ShowsGroup(lum, "V1");
            Judge(_ivShownOk,
                  "上屏的批次 id 不对（CurrentBatchId=" + lum.CurrentBatchId + "、应 101）——"
                  + "没有批次游标，房主就没法告诉客户端该撤哪一张");

            // 1) 作废**正在展示**的那批 → 面板必须关掉，并自动补弹队列里的下一批
            bool hit = lum.InvalidateExternal(101);
            yield return Until(() => lum.CurrentPanel != null && lum.ExternalInvalidated > 0);
            yield return Quiet();
            _ivClosedOk = hit && lum.ExternalInvalidated == 1 && lum.CurrentBatchId == 102
                          && ShowsGroup(lum, "V2");
            _ivAfter = PanelText(lum);
            Judge(_ivClosedOk,
                  "作废正在展示的那批之后面板没被正确替换（作废计数=" + lum.ExternalInvalidated
                  + "、当前批次=" + lum.CurrentBatchId + "、面板=" + _ivAfter + "）——"
                  + "玩家眼前会留着一张点了也没用的卡");

            // 2) 作废**队列里**的那批（103）→ 只剩空队列
            bool hit2 = lum.InvalidateExternal(103);
            _ivPending = lum.ExternalPendingCount;
            _ivQueueOk = hit2 && _ivPending == 0 && lum.ExternalInvalidated == 2;
            Judge(_ivQueueOk,
                  "作废队列里的批次没生效（残余=" + _ivPending + "、作废计数="
                  + lum.ExternalInvalidated + "）——队列里会留下一张永远不会被选的废卡");

            // 3) 作废一个不存在的批次：不许误伤、不许计数
            _ivOtherOk = !lum.InvalidateExternal(9999) && lum.ExternalInvalidated == 2
                         && lum.CurrentBatchId == 102;
            Judge(_ivOtherOk,
                  "作废不存在的批次误伤了当前面板（当前批次=" + lum.CurrentBatchId + "）——"
                  + "撤销必须精确命中，不能把玩家正在选的卡一并干掉");

            // 4) 剩下的那一张还能正常选掉（撤销没把本地路径堵死）
            _ivKeptOk = lum.IsShowing;
            Judge(_ivKeptOk, "撤销之后没有任何面板在展示了——撤销做过头了，把正常的卡也收走了");

            lum.AutoChooseFirst();
            yield return Until(() => lum.CurrentPanel == null);
            _ivInvalidated = lum.ExternalInvalidated;
            bool noDrop = lum.ExternalDropped == 0;
            Line("零丢弃", Yn(noDrop));
            Judge(noDrop, "全程丢弃 " + lum.ExternalDropped + " 组（应 0）");
        }

        /// <summary>当前面板上写着哪一组（按 CARD_Ox# 前缀判断）。</summary>
        private static string ShownTag(LevelUpManager lum)
        {
            for (int i = 1; i <= 9; i++)
            {
                string tag = "O" + i;
                if (ShowsGroup(lum, tag)) return tag;
            }
            return lum != null && lum.IsShowing ? "?" : "-";
        }

        // =====================================================================================
        // BATCH：批次表纯逻辑（不依赖 FishNet，单机直接测）
        // =====================================================================================
        private IEnumerator Batch()
        {
            var applied = new List<string>();
            var b = new UpgradeOfferBatches();

            // 连开三批，乱序取回：每一批取回的必须是它自己那一组
            int id1 = b.Add(Group("P1", applied));
            int id2 = b.Add(Group("P2", applied));
            int id3 = b.Add(Group("P3", applied));

            UpgradeOffer o1, o2, o3;
            bool t1 = b.TryTake(id2, 0, out o2) && o2.title == "CARD_P2#0";
            bool t2 = b.TryTake(id3, 1, out o3) && o3.title == "CARD_P3#1";
            bool t3 = b.TryTake(id1, 2, out o1) && o1.title == "CARD_P1#2";
            _btTakeOk = t1 && t2 && t3;
            _btTook = (o2.title + " / " + o3.title + " / " + o1.title);
            Judge(_btTakeOk,
                  "乱序取回拿到了错的卡（实际 " + _btTook + "）——候选表没有按批次隔离，"
                  + "第 2 批推来时把第 1 批盖掉了，玩家点的是旧一组、生效的是新一组");

            // 同一批取两次：第二次必须失败（一批只允许选一次，防止重复发放强化）
            _btTwiceOk = !b.TryTake(id1, 0, out o1);
            Judge(_btTwiceOk,
                  "同一批次被取走了两次——玩家能把一次升级刷成两次强化");

            // 非法 id / 已被淘汰的批次：必须拒绝，且绝不能拿别的批次来凑
            _btBadIdOk = !b.Contains(9999) && !b.TryTake(9999, 0, out o1) && !b.TryTake(0, 0, out o1);
            Judge(_btBadIdOk,
                  "非法批次 id 竟然取到了卡——回传错配时必须拒绝，宁可少发一次强化也不能发错一张");

            // 越界 index
            var b2 = new UpgradeOfferBatches();
            int bid = b2.Add(Group("R", null));
            _btRangeOk = !b2.TryTake(bid, 3, out o1) && !b2.TryTake(bid, -1, out o1);
            Judge(_btRangeOk, "越界 index 没有拒绝——会拿到别的张，或者越界崩溃");

            // 上限淘汰：容量 3，连开 4 批 → 最旧那批被淘汰，其余仍在
            var b3 = new UpgradeOfferBatches(3);
            // M38：淘汰**必须有出口**——只记 EvictedCount 是不够的，房主得能立刻
            // 通知对端把那张卡撤掉，否则玩家眼前摆着一张点了也没用的卡（详见 INVAL）。
            var evictedIds = new List<int>();
            b3.OnEvicted += id => evictedIds.Add(id);
            int e1 = b3.Add(Group("E1", null));
            b3.Add(Group("E2", null));
            b3.Add(Group("E3", null));
            int e4 = b3.Add(Group("E4", null));
            _btEvicted = b3.EvictedCount;
            _btPending = b3.PendingCount;
            _btEvictOk = _btEvicted == 1 && _btPending == 3 && !b3.Contains(e1) && b3.Contains(e4);
            Judge(_btEvictOk,
                  "上限淘汰不对：淘汰=" + _btEvicted + "（应 1）、残余=" + _btPending + "（应 3）——"
                  + "玩家长时间不选卡时未回应的批次会无限堆积，必须淘汰最旧的并计数报警");

            _btEvictedIds = evictedIds.Count > 0
                ? string.Join(",", evictedIds.ConvertAll(x => x.ToString()).ToArray()) : "-";
            _btEvictEventOk = evictedIds.Count == 1 && evictedIds[0] == e1;
            Judge(_btEvictEventOk,
                  "淘汰没有出口：OnEvicted 事件没触发或带错了批次 id（实际=" + _btEvictedIds
                  + "、应 " + e1 + "）——只把 EvictedCount 加了 1，房主没法通知对端撤卡，"
                  + "玩家眼前会留着一张点了也没用的卡");

            // TryApply 必须真的执行这一批自己那张卡的 apply
            var applied2 = new List<string>();
            var b4 = new UpgradeOfferBatches();
            int a1 = b4.Add(Group("A1", applied2));
            b4.Add(Group("A2", applied2));
            bool okApply = b4.TryApply(a1, 1);
            _btApplyOk = okApply && applied2.Count == 1 && applied2[0] == "A1#1";
            Judge(_btApplyOk,
                  "TryApply 执行的不是指定批次的那张卡（实际执行：" +
                  (applied2.Count > 0 ? applied2[0] : "无") + "）——房主权威应用错配，"
                  + "玩家选 A、生效 B，且不报错");

            yield return Quiet();
        }

        // =====================================================================================
        // RESTORE：收尾与反向——队列要清零，本地路径不能被修过头堵死
        // =====================================================================================
        private IEnumerator Restore()
        {
            GameBuilder.Build();
            yield return Quiet();
            yield return WaitReal(0.3f);

            var lum = LevelUpManager.Instance;
            if (lum == null) { Fail("读不到 LevelUpManager——RESTORE 判据全部失灵"); yield break; }

            // 外部模式跑一轮并全部消化
            lum.SetExternalCards(true);
            lum.PresentExternal(Group("S1", null), _ => { });
            yield return Quiet();
            lum.PresentExternal(Group("S2", null), _ => { });
            yield return Quiet();
            var s1 = lum.CurrentPanel;
            lum.AutoChooseFirst();
            yield return Until(() => lum.CurrentPanel != null && lum.CurrentPanel != s1);
            lum.AutoChooseFirst();
            yield return Until(() => lum.CurrentPanel == null);

            _rsPending = lum.ExternalPendingCount;
            _rsQueueOk = _rsPending == 0;
            Judge(_rsQueueOk,
                  "选完后队列残余 " + _rsPending + "（应 0）——队列没排空会污染下一局的推卡");

            _rsPanelClosedOk = !lum.IsShowing && lum.CurrentPanel == null;
            Judge(_rsPanelClosedOk,
                  "选完后面板仍开着（IsShowing=" + lum.IsShowing + "）——玩家会卡在选卡界面里");

            // 反向：切回本地模式后，本地升级必须还能弹卡。
            // 少了这条，"把 PresentExternal 改成永不弹卡"也能让 QUEUE/DRAIN/ORDER 全绿——
            // 那等于把联机升级整个砍掉。
            lum.SetExternalCards(false);
            EventBus.RaiseLevelUp(3);
            yield return Quiet();
            bool localShown = lum.IsShowing;
            Judge(localShown,
                  "切回本地模式后，本地升级不再弹卡——修排队时把本地路径一起堵死了，"
                  + "单机与房主自己会彻底失去升级选卡");
            _rsLocalOk = localShown;

            // 本地面板会把 timeScale 归零，探针收尾前必须选掉，别留下暂停态
            if (localShown)
            {
                lum.AutoChooseFirst();
                yield return Quiet();
            }

            _rsNoErrOk = _errors == 0;
            Judge(_rsNoErrOk, "全程出现 " + _errors + " 条异常/错误（首条：" + _firstError + "）");
        }

        // =====================================================================================
        // 工具
        // =====================================================================================
        private void Fail(string msg)
        {
            _fail.Add(msg);
            if (_firstError == "") _firstError = msg;
        }

        private static string Yn(bool b) => b ? "是" : "否";

        private void Line(string k, string v) => Debug.Log("[SMOKE] " + k + "              " + v);

        private void Report()
        {
            int expect = _scene == "BASE" ? 4
                       : _scene == "QUEUE" ? 4
                       : _scene == "DRAIN" ? 5
                       : _scene == "ORDER" ? 3
                       : _scene == "BATCH" ? 7
                       : _scene == "GATE" ? 7
                       : _scene == "INVAL" ? 6
                       : _scene == "RESTORE" ? 4 : 0;

            bool pass = _errors == 0 && _fail.Count == 0 && _checked == expect && _ok == expect;

            Debug.Log("[SMOKE] ===== 联机升级选卡不丢单实测结果 =====");
            Line("场景", _scene);
            Line("断点通过", _ok + "/" + _checked);
            switch (_scene)
            {
                case "BASE":
                    Line("外部模式挡住本地弹卡", Yn(_baExternalBlocks));
                    Line("单组上屏计数", _baShownCount.ToString());
                    Line("单组成功上屏", Yn(_baShown));
                    Line("上屏文本是这一组", Yn(_baTextOk));
                    Line("空候选未计入", Yn(_baEmptyIgnored));
                    Line("收到增量", _baRecvAfterEmpty.ToString());
                    break;
                case "QUEUE":
                    Line("收到", _quReceived.ToString());
                    Line("上屏", _quPresented.ToString());
                    Line("入队", _quQueued.ToString());
                    Line("丢弃", _quDropped.ToString());
                    Line("队列残余", _quPending.ToString());
                    Line("收支正确", Yn(_quCountsOk));
                    Line("上屏的是第一组", Yn(_quFirstGroupOk));
                    Line("零丢弃", Yn(_quDropOk));
                    break;
                case "DRAIN":
                    Line("补上第二组", Yn(_drSecondOk));
                    Line("补上第三组", Yn(_drThirdOk));
                    Line("选完面板关闭", Yn(_drClosedOk));
                    Line("上屏总数", _drPresented.ToString());
                    Line("上屏三组齐", Yn(_drPresentedOk));
                    Line("零丢弃", Yn(_drDropOk));
                    break;
                case "ORDER":
                    Line("上屏顺序", _orShownSeq);
                    Line("回调顺序", _orChosenSeq);
                    Line("上屏顺序正确", Yn(_orShownOk));
                    Line("回调顺序正确", Yn(_orChosenOk));
                    Line("丢弃", _orDropped.ToString());
                    Line("零丢弃", Yn(_orDropOk));
                    break;
                case "BATCH":
                    Line("乱序取回正确", Yn(_btTakeOk));
                    Line("取回内容", _btTook);
                    Line("一批只取一次", Yn(_btTwiceOk));
                    Line("非法id被拒", Yn(_btBadIdOk));
                    Line("越界被拒", Yn(_btRangeOk));
                    Line("上限淘汰", _btEvicted.ToString());
                    Line("淘汰后残余", _btPending.ToString());
                    Line("淘汰正确", Yn(_btEvictOk));
                    Line("淘汰有出口", Yn(_btEvictEventOk));
                    Line("淘汰事件批次", _btEvictedIds);
                    Line("应用的是本批那张", Yn(_btApplyOk));
                    break;
                case "GATE":
                    Line("确认后才生效", Yn(_gtConfirmOk));
                    Line("重复确认被拒", Yn(_gtTwiceOk));
                    Line("伪造回执被拒", Yn(_gtUnknownOk));
                    Line("回执对不上被拒", Yn(_gtMismatchOk));
                    Line("拒绝路径正确", Yn(_gtRejectOk));
                    Line("超时清理正确", Yn(_gtTimeoutOk));
                    Line("批次作废生效", Yn(_gtInvalidateOk));
                    Line("确认数", _gtConfirmed.ToString());
                    Line("拒绝数", _gtRejected.ToString());
                    Line("超时数", _gtTimedOut.ToString());
                    Line("超时后残余", _gtPending.ToString());
                    break;
                case "INVAL":
                    Line("上屏批次id正确", Yn(_ivShownOk));
                    Line("作废展示中的批次", Yn(_ivClosedOk));
                    Line("作废队列中的批次", Yn(_ivQueueOk));
                    Line("不存在的批次不误伤", Yn(_ivOtherOk));
                    Line("其余卡片仍在", Yn(_ivKeptOk));
                    Line("作废计数", _ivInvalidated.ToString());
                    Line("队列残余", _ivPending.ToString());
                    Line("替换后面板", _ivAfter);
                    break;
                case "RESTORE":
                    Line("队列残余", _rsPending.ToString());
                    Line("队列已排空", Yn(_rsQueueOk));
                    Line("面板已关闭", Yn(_rsPanelClosedOk));
                    Line("本地仍能弹卡", Yn(_rsLocalOk));
                    Line("全程零异常", Yn(_rsNoErrOk));
                    break;
            }
            Line("运行时异常", _errors.ToString());
            Line("结论", pass ? "通过" : "不通过");
            if (_firstError != "") Line("首个失败", _firstError);
            foreach (var f in _fail) Line("失败项", f);
        }
    }
}
