namespace RemoteController.Server;

/// <summary>协议坐标（虚拟屏幕空间，0 起）→ MOUSEEVENTF_VIRTUALDESK 归一化值的纯函数。</summary>
internal static class VirtualCoordinates
{
    /// <summary>0 起虚拟坐标归一化到 0..65535，铺满整个虚拟屏幕。</summary>
    public static (int X, int Y) Normalize(int x, int y, int virtualWidth, int virtualHeight) =>
        (x * 65535 / (virtualWidth - 1), y * 65535 / (virtualHeight - 1));
}
