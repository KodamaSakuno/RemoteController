using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteController.Protocol;
using Windows.Win32;

namespace RemoteController.Server;

internal sealed class RemoteSession(WebSocket socket, ScreenCapture capture)
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(66); // ≈15fps，平板 CPU 的安全起点

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
        var hello = new Hello(capture.Region.X, capture.Region.Y, capture.Width, capture.Height, (int)PInvoke.GetDpiForSystem());
        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task PushFramesAsync(FrameEncoder encoder)
    {
        var payloadLength = capture.Width * capture.Height * 4;
        var payload = new byte[payloadLength];
        var headerPacket = new byte[FrameHeader.Size];
        using var timer = new PeriodicTimer(FrameInterval);
        Task? pending = null;

        try
        {
            while (socket.State == WebSocketState.Open && await timer.WaitForNextTickAsync())
            {
                if (pending is { IsCompleted: false })
                    continue; // 网络积压时丢帧保实时性；也保证 encoder 缓冲不被未完成的发送读取

                var length = capture.Capture(payload);
                encoder.Encode(payload, capture.Width, capture.Height);

                new FrameHeader(capture.Width, capture.Height, encoder.Length).WriteTo(headerPacket);
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
