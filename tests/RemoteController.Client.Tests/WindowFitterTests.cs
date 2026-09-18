using Avalonia;
using RemoteController.Client;

namespace RemoteController.Client.Tests;

public class WindowFitterTests
{
    [Theory]
    [InlineData(0, 0.6)]                 // 936/1560
    [InlineData(270, 1.6666666666666667)] // 1560/936
    public void LogicalAspectSwapsOnQuarterTurns(int rotation, double expected)
    {
        Assert.Equal(expected, WindowFitter.LogicalAspect(new Size(936, 1560), rotation), precision: 10);
    }

    [Fact]
    public void TargetHeightMatchesVerifiedCase()
    {
        // 真机验证值：窗口宽 1560、竖纹理 936x1560、旋转 270、地址栏隐藏 → 高 ≈ 958
        var height = WindowFitter.TargetHeight(1560, new Size(936, 1560), 270, chrome: 32);

        Assert.Equal(958.4, height, precision: 1);
    }

    [Fact]
    public void ImageScaleFillsCellAfterRotation()
    {
        var texture = new Size(936, 1560);
        var scale = WindowFitter.ImageScale(1544, 926, texture);

        // 不变量：旋转后视觉尺寸（纹理高×scale × 纹理宽×scale）恰好填满单元格
        Assert.True(scale * texture.Height <= 1544 + 1e-6);
        Assert.True(scale * texture.Width <= 926 + 1e-6);
        Assert.Equal(926, scale * texture.Width, precision: 6); // 高度方向精确贴合
    }
}
