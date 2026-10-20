using System.Collections.Generic;
using UnityEngine;

namespace StarPixel.Pickups
{
    /// <summary>
    /// M42：掉落物容量上限与"超限回收谁"的**纯逻辑**（不依赖 Fish-Net、不是 MonoBehaviour、单机可测）。
    ///
    /// 为什么要单独抽一层：单机（<see cref="PickupManager"/>）与联机（<c>NetWorld</c>）各自维护一份账本，
    /// 但"上限多少、超限回收谁"必须是**同一套判据**——否则两条路径会在玩家眼前表现得不一样
    /// （联机明明剩更多宝石却先开始消失，或反过来）。抽成纯逻辑后，单机无头探针就能把它钉死，
    /// 不需要为了测这段代码去开一次双开。
    ///
    /// 与 M41 的关系：M41 在 <see cref="PickupManager"/> 里就地写了 <c>MaxGems/TrimGems/Farthest</c>，
    /// 联机路径（<c>NetWorld.SpawnGem</c>）当时就发现是同一个缺陷、但没修也没测。
    /// 本轮把那段逻辑原样搬到此处，两侧共用。
    /// </summary>
    public static class PickupTrim
    {
        /// <summary>
        /// 场上经验宝石上限。取值与单机侧一致（M41 实测依据：波次 49 / 击杀 4302 的稳态）。
        /// </summary>
        public const int MaxGems = 240;

        /// <summary>场上回血球上限。同上。</summary>
        public const int MaxHealth = 80;

        /// <summary>是否已超出上限（需要回收）。</summary>
        public static bool Over(int count, int cap) => count > cap;

        /// <summary>
        /// 账本里"离玩家最远"的那一项下标；全为空项时返回 -1（空项由各账本的 Update 自行清理）。
        ///
        /// 为什么回收"最远"而不是"最早"：玩家够得着的要留住，够不着的那个本来也捡不到，
        /// 牺牲它对实际收益影响最小，也比"超时消失"更不伤体验。
        ///
        /// 多玩家（联机）时按**到最近玩家的距离**取最远：合作模式下共享掉落，
        /// 一个离 A 很远但离 B 很近的宝石不该被回收。
        /// </summary>
        public static int FarthestIndex<T>(List<T> list, List<Vector2> players) where T : Component
        {
            if (list == null || list.Count == 0) return -1;
            int worst = -1;
            float worstD = -1f;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                float d = NearestDistanceSqr((Vector2)c.transform.position, players);
                if (d > worstD) { worstD = d; worst = i; }
            }
            return worst;
        }

        /// <summary>到最近玩家的**平方**距离；没有玩家时退化为到原点的距离（不会命中任何真实场景，只为不抛异常）。</summary>
        private static float NearestDistanceSqr(Vector2 pos, List<Vector2> players)
        {
            if (players == null || players.Count == 0) return pos.sqrMagnitude;
            float best = float.MaxValue;
            for (int i = 0; i < players.Count; i++)
            {
                float d = (players[i] - pos).sqrMagnitude;
                if (d < best) best = d;
            }
            return best;
        }
    }
}
