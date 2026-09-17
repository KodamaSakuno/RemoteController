using RemoteController.Protocol;

namespace RemoteController.Protocol.Tests;

public class CaptureRegionTests
{
    [Theory]
    [InlineData("0,0,1920,1080", 0, 0, 1920, 1080)]
    [InlineData("100,200,800,600", 100, 200, 800, 600)]
    [InlineData("-50,-60,640,480", -50, -60, 640, 480)]
    public void ParsesValidInput(string value, int x, int y, int width, int height)
    {
        Assert.True(CaptureRegion.TryParse(value, out var region));
        Assert.Equal(new CaptureRegion(x, y, width, height), region);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("a,2,3,4")]
    [InlineData("1,2,0,4")]
    [InlineData("1,2,-3,4")]
    public void RejectsInvalidInput(string value)
    {
        Assert.False(CaptureRegion.TryParse(value, out _));
    }
}
