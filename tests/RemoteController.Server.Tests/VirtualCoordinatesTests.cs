using RemoteController.Server;

namespace RemoteController.Server.Tests;

public class VirtualCoordinatesTests
{
    [Fact]
    public void OriginMapsToZero()
    {
        // 契约：协议坐标是 0 起的虚拟屏幕空间，原点归一化为 0（不得减虚拟屏原点）
        var (x, y) = VirtualCoordinates.Normalize(0, 0, 3840, 3240);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void FarCornerMapsToMaxValue()
    {
        var (x, y) = VirtualCoordinates.Normalize(3839, 3239, 3840, 3240);

        Assert.Equal(65535, x);
        Assert.Equal(65535, y);
    }

    [Fact]
    public void VerifiedCaseFromPcBisect()
    {
        // 真机验证场景（PC 多屏）：虚拟坐标 (1901,2700) 应使指针落在主屏中心附近的归一化值
        var (x, y) = VirtualCoordinates.Normalize(1901, 2700, 3840, 3240);

        Assert.Equal(32451, x);
        Assert.Equal(54629, y);
    }
}
