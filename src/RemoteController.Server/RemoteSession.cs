using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteController.Protocol;
using Windows.Win32;

namespace RemoteController.Server;

internal sealed class RemoteSession(WebSocket socket, DxgiCapture capture)
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(33); // 上限 30fps，duplication 变化可能更频繁

    public async Task RunAsync()
    {
        await SendHelloAsync();

        using var encoder = new FrameEncoder();
        var push = PushFramesAsync(encoder);
        var pull = DrainClientAsync();
        await Task.WhenAny(push, pull);

        // 一端结束后中止连接，让另一端循环退出
        socket.Abort();
        await Task.WhenAll(Observe(push), Observe(pull));
    }

    private async Task SendHelloAsync()
    {
        var hello = new Hello(
            capture.Region.X, capture.Region.Y,
            capture.Width, capture.Height,
            (int)PInvoke.GetDpiForSystem(),
            capture.RotationDegrees);
        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task PushFramesAsync(FrameEncoder encoder)
    {
        var payloadLength = capture.Width * capture.Height * 4;
        var payload = new byte[payloadLength];
        var headerPacket = new byte[FrameHeader.Size];
        Task? pending = null;
        var lastSent = DateTime.MinValue;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                // 阻塞至屏幕更新或超时；空闲时零采集零编码零流量
                if (!capture.TryAcquireFrame(payload, 250))
                    continue;
                if (pending is { IsCompleted: false })
                    continue; // 网络积压时丢帧；也保证 encoder 缓冲不被未完成的发送读取
                if (DateTime.UtcNow - lastSent < MinFrameInterval)
                    continue; // 帧率上限，超出部分丢弃（duplication 会聚合后续变化）

                encoder.Encode(payload, capture.Width, capture.Height);

                new FrameHeader(capture.Width, capture.Height, encoder.Length).WriteTo(headerPacket);
                lastSent = DateTime.UtcNow;
                pending = SendFrameAsync(headerPacket, encoder.Buffer, encoder.Length);
            }
        }
        finally
        {
            if (pending is not null)
                await Observe(pending);
        }
    }

    // 头与负载分两次发送（endOfMessage 仅最后一次为 true），WS 层仍视为一条消息，省一次整帧拷贝
    private async Task SendFrameAsync(byte[] header, byte[] payload, int payloadLength)
    {
        await socket.SendAsync(header, WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None);
        await socket.SendAsync(new Memory<byte>(payload, 0, payloadLength), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    private async Task DrainClientAsync()
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
            {
                var message = JsonSerializer.Deserialize<MouseMessage>(
                    Encoding.UTF8.GetString(buffer, 0, result.Count), ProtocolJson.Options);
                if (message is not null)
                    MouseInjector.Inject(message);
            }
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }
}
