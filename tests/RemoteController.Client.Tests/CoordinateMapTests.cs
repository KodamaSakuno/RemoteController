using Avalonia;
using RemoteController.Client;

namespace RemoteController.Client.Tests;

public class CoordinateMapTests
{
    public static readonly TheoryData<int, double, double, double, double> TextureToLogicalCases = new()
    {
        // rotation, u, v, expected logical x, expected logical y（纹理 936x1560）
        { 0,   0.5, 0.5, 468, 780 },
        { 90,  0.5, 0.5, 779, 468 },
        { 180, 0.5, 0.5, 467, 779 },
        { 270, 0.5, 0.5, 780, 467 },
    };

    [Theory]
    [MemberData(nameof(TextureToLogicalCases))]
    public void TextureToLogicalMapsByRotation(int rotation, double u, double v, double expectedX, double expectedY)
    {
        var (x, y) = CoordinateMap.TextureToLogical(u, v, rotation, 936, 1560);

        Assert.Equal(expectedX, x, precision: 6);
        Assert.Equal(expectedY, y, precision: 6);
    }

    [Fact]
    public void MapPointerIdentityFillsBoundsExactly()
    {
        var texture = new Size(200, 100);
        var bounds = new Rect(0, 0, 400, 200); // 恰为纹理 2 倍，无黑边

        Assert.True(CoordinateMap.TryMapPointer(new Point(0, 0), bounds, texture, 0, 10, 20, out var x, out var y));
        Assert.Equal(10, x);
        Assert.Equal(20, y);

        Assert.True(CoordinateMap.TryMapPointer(new Point(200, 100), bounds, texture, 0, 10, 20, out x, out y));
        Assert.Equal(110, x);
        Assert.Equal(70, y);
    }

    [Fact]
    public void MapPointerRejectsLetterbox()
    {
        var texture = new Size(200, 100);
        var bounds = new Rect(0, 0, 400, 300); // 高度富余，上下各 50 黑边
        var inside = new Point(200, 150);

        Assert.True(CoordinateMap.TryMapPointer(inside, bounds, texture, 0, 0, 0, out _, out _));
        Assert.False(CoordinateMap.TryMapPointer(new Point(200, 20), bounds, texture, 0, 0, 0, out _, out _));
        Assert.False(CoordinateMap.TryMapPointer(new Point(200, 280), bounds, texture, 0, 0, 0, out _, out _));
    }

    [Fact]
    public void MapPointerHandlesRotatedTexture()
    {
        // 竖纹理 936x1560 旋转 270° 后逻辑尺寸 1560x936；控件 = 旋转后视觉尺寸
        var texture = new Size(936, 1560);
        var bounds = new Rect(0, 0, 1560, 936);

        Assert.True(CoordinateMap.TryMapPointer(new Point(780, 468), bounds, texture, 270, 100, 200, out var x, out var y));
        Assert.Equal(100 + 780, x);
        Assert.Equal(200 + 467, y);
    }

    [Fact]
    public void MapPointerRejectsDegenerateInput()
    {
        Assert.False(CoordinateMap.TryMapPointer(new Point(1, 1), new Rect(0, 0, 0, 0), new Size(100, 100), 0, 0, 0, out _, out _));
        Assert.False(CoordinateMap.TryMapPointer(new Point(1, 1), new Rect(0, 0, 100, 100), new Size(0, 0), 0, 0, 0, out _, out _));
    }
}
