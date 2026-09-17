namespace RemoteController.Protocol.Tests;

public class FrameHeaderTests
{
    [Fact]
    public void RoundTrips()
    {
        var header = new FrameHeader(1920, 1080, 1920 * 1080 * 4);
        var buffer = new byte[FrameHeader.Size];

        header.WriteTo(buffer);

        Assert.True(FrameHeader.TryParse(buffer, out var parsed));
        Assert.Equal(header.Width, parsed.Width);
        Assert.Equal(header.Height, parsed.Height);
        Assert.Equal(header.PayloadLength, parsed.PayloadLength);
    }

    [Fact]
    public void RejectsBadMagic()
    {
        var buffer = new byte[FrameHeader.Size];
        new FrameHeader(1, 1, 4).WriteTo(buffer);
        buffer[0] ^= 0xFF;

        Assert.False(FrameHeader.TryParse(buffer, out _));
    }

    [Fact]
    public void RejectsShortBuffer()
    {
        Assert.False(FrameHeader.TryParse(new byte[FrameHeader.Size - 1], out _));
    }
}
