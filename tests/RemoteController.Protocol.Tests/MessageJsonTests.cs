using System.Text.Json;
using RemoteController.Protocol;

namespace RemoteController.Protocol.Tests;

public class MessageJsonTests
{
    [Fact]
    public void HelloRoundTrips()
    {
        var hello = new Hello(100, 200, 1600, 2560, 144, 270);
        var json = JsonSerializer.Serialize(hello, ProtocolJsonContext.Default.Hello);

        Assert.Equal(hello, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.Hello));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("middle")]
    public void MouseButtonDiscriminatorRoundTrips(string button)
    {
        var message = new MouseButton(Enum.Parse<MouseButtonKind>(button, ignoreCase: true), IsDown: true);
        var json = JsonSerializer.Serialize(message, ProtocolJsonContext.Default.MouseMessage);

        Assert.Equal(message, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MouseMessage));
    }

    [Fact]
    public void MouseMoveRoundTrips()
    {
        MouseMessage message = new MouseMove(100, 200);
        var json = JsonSerializer.Serialize(message, ProtocolJsonContext.Default.MouseMessage);

        Assert.Equal(message, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MouseMessage));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(0.125)] // 触控板精细滚动的小数增量
    public void MouseWheelRoundTrips(double delta)
    {
        MouseMessage message = new MouseWheel(delta);
        var json = JsonSerializer.Serialize(message, ProtocolJsonContext.Default.MouseMessage);

        Assert.Equal(message, JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MouseMessage));
    }
}
