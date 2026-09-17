using System.Text.Json;
using RemoteController.Protocol;

namespace RemoteController.Protocol.Tests;

public class MessageJsonTests
{
    [Fact]
    public void HelloRoundTrips()
    {
        var hello = new Hello(100, 200, 1920, 1080, 144);
        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);

        Assert.Equal(hello, JsonSerializer.Deserialize<Hello>(json, ProtocolJson.Options));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("middle")]
    public void MouseButtonDiscriminatorRoundTrips(string button)
    {
        var message = new MouseButton(Enum.Parse<MouseButtonKind>(button, ignoreCase: true), IsDown: true);
        var json = JsonSerializer.Serialize<MouseMessage>(message, ProtocolJson.Options);

        Assert.Equal(message, JsonSerializer.Deserialize<MouseMessage>(json, ProtocolJson.Options));
    }

    [Fact]
    public void MouseMoveRoundTrips()
    {
        MouseMessage message = new MouseMove(100, 200);
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);

        Assert.Equal(message, JsonSerializer.Deserialize<MouseMessage>(json, ProtocolJson.Options));
    }
}
