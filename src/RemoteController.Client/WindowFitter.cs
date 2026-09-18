using System;
using Avalonia;

namespace RemoteController.Client;

/// <summary>窗口与图像控件的尺寸计算纯函数。</summary>
internal static class WindowFitter
{
    /// <summary>旋转还原后的逻辑宽高比（宽/高）；90/270 时纹理宽高互换。</summary>
    public static double LogicalAspect(Size textureSize, int rotation) =>
        rotation is 90 or 270
            ? textureSize.Height / textureSize.Width
            : textureSize.Width / textureSize.Height;

    /// <summary>宽高比锁定的目标窗口高度；windowWidth 含边框估算，chrome 为标题栏+地址栏余量。</summary>
    public static double TargetHeight(double windowWidth, Size textureSize, int rotation, double chrome) =>
        (windowWidth - 16) / LogicalAspect(textureSize, rotation) + chrome;

    /// <summary>
    /// 图像控件缩放率：按纹理（未旋转）比例设定控件，旋转后视觉尺寸 = 纹理高×scale × 纹理宽×scale，
    /// 恰好填满单元格——禁止依赖 Uniform 把竖纹理适配进横向单元格（黑边会被 RenderTransform 一起旋转）。
    /// </summary>
    public static double ImageScale(double cellWidth, double cellHeight, Size textureSize) =>
        Math.Min(cellWidth / textureSize.Height, cellHeight / textureSize.Width);
}
