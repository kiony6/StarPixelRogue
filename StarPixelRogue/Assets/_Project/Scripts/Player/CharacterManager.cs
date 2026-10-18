using StarPixel.Combat;
using StarPixel.Combat.Skill;
using StarPixel.Core;
using UnityEngine;

namespace StarPixel.Player
{
    /// <summary>
    /// 角色管理（M6.2）：角色目录 + 选择持久化 + 应用到玩家。
    /// 选择态存于 <see cref="SaveManager"/>（本机存档，不含个人信息，符合隐私政策）。
    /// </summary>
    public static class CharacterManager
    {
        /// <summary>角色数量（存档统计数组长度与此保持一致）。</summary>
        public const int Count = 2;

        private static readonly CharacterDef[] Catalog =
        {
            CharacterDef.CreateRanger(),
            CharacterDef.CreateBulwark(),
        };

        public static CharacterDef ById(int id)
        {
            foreach (var c in Catalog) if (c.id == id) return c;
            return Catalog[0];
        }

        /// <summary>当前选择的角色 id（持久化）。</summary>
        public static int SelectedId
        {
            get
            {
                var save = SaveManager.Instance;
                return save != null ? Mathf.Clamp(save.Data.lastCharacterId, 0, Count - 1) : 0;
            }
            set
            {
                var save = SaveManager.Instance;
                if (save == null) return;
                save.Data.lastCharacterId = Mathf.Clamp(value, 0, Count - 1);
                save.Save();
            }
        }

        public static CharacterDef Selected => ById(SelectedId);

        /// <summary>
        /// 应用角色到玩家：属性乘区 + 替换起始武器 + 生命上限同步。
        /// 联机下房主与客户端各自对本机玩家调用（客户端为本地预测，权威数值以房主为准）。
        /// </summary>
        public static void ApplyTo(PlayerController p, CharacterDef def)
        {
            if (p == null || def == null) return;
            p.ApplyCharacter(def);
        }
    }
}
