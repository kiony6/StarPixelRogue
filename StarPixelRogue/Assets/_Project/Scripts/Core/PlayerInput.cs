using UnityEngine;

namespace StarPixel.Core
{
    /// <summary>
    /// 玩家输入统一入口（M16 跨平台）。
    /// 本游戏自动开火，移动是唯一需要的操作输入，因此只需抽象"移动向量"这一项：
    /// - 触屏平台：优先取虚拟摇杆 <see cref="TouchJoystick"/>（有手指操控时）；
    /// - 桌面/其他：沿用原有 Input 轴（Horizontal/Vertical），行为与 M15 及之前完全一致。
    /// 这样 PlayerController 不必关心输入来自键盘还是触屏。
    /// </summary>
    public static class PlayerInput
    {
        /// <summary>是否触屏平台（决定是否创建虚拟摇杆、是否锁帧等）。</summary>
        public static bool TouchPlatform => Application.isMobilePlatform;

        /// <summary>当前移动向量（未归一化；无输入时为 Vector2.zero）。</summary>
        public static Vector2 MoveVector()
        {
            // M17：测试注入点。仅无头冒烟实测（SmokeTest）会设置，正常游戏恒为 null，行为不变。
            if (Override.HasValue) return Override.Value;

            var joy = TouchJoystick.Instance;
            if (joy != null && joy.Active) return joy.Direction;
            return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        }

        /// <summary>M17 无头实测专用：非空时接管移动输入。正式构建中永远为 null。</summary>
        public static Vector2? Override;
    }
}
