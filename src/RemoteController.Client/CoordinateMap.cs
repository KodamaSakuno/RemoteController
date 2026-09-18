using System;
using Avalonia;

namespace RemoteController.Client;

/// <summary>坐标映射纯函数：控件本地空间 → 服务器虚拟屏幕坐标，与服务端 logical→texture 公式严格互逆。</summary>
internal static class CoordinateMap
{
    /// <summary>纹理归一化坐标 → 逻辑像素（u,v ∈ [0,1]；rotation 为纹理相对逻辑桌面的旋转角）。</summary>
    public static (double X, double Y) TextureToLogical(double u, double v, int rotation, double textureWidth, double textureHeight)
    {
        var tw = u * textureWidth;
        var th = v * textureHeight;
        return rotation switch
        {
            90 => (textureHeight - 1 - th, tw),
            270 => (th, textureWidth - 1 - tw),
            180 => (textureWidth - 1 - tw, textureHeight - 1 - th),
            _ => (tw, th),
        };
    }

    /// <summary>
    /// 指针位置 → 虚拟屏幕坐标。Uniform 信箱内归一化（黑边返回 false），再经旋转逆映射并加区域原点。
    /// </summary>
    public static bool TryMapPointer(Point position, Rect bounds, Size textureSize, int rotation, int originX, int originY, out int x, out int y)
    {
        x = y = 0;
        if (textureSize is { Width: <= 0 } or { Height: <= 0 } || bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var scale = Math.Min(bounds.Width / textureSize.Width, bounds.Height / textureSize.Height);
        var renderedWidth = textureSize.Width * scale;
        var renderedHeight = textureSize.Height * scale;
        var u = (position.X - (bounds.Width - renderedWidth) / 2) / renderedWidth;
        var v = (position.Y - (bounds.Height - renderedHeight) / 2) / renderedHeight;
        if (u is < 0 or > 1 || v is < 0 or > 1)
            return false;

        var (lx, ly) = TextureToLogical(u, v, rotation, textureSize.Width, textureSize.Height);
        x = originX + (int)lx;
        y = originY + (int)ly;
        return true;
    }
}
