namespace RemoteController.Protocol;

/// <summary>采集区域，坐标相对虚拟屏幕左上角，单位物理像素。</summary>
public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    /// <summary>解析 "x,y,w,h" 格式；宽高必须为正。</summary>
    public static bool TryParse(string value, out CaptureRegion region)
    {
        region = default!;
        var parts = value.Split(',');
        if (parts.Length != 4)
            return false;

        if (!int.TryParse(parts[0], out var x)
            || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var width)
            || !int.TryParse(parts[3], out var height))
            return false;

        if (width <= 0 || height <= 0)
            return false;

        region = new CaptureRegion(x, y, width, height);
        return true;
    }
}
