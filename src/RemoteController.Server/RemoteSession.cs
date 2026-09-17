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

        var push = PushFramesAsync();
        var pull = DrainClientAsync();
        await Task.WhenAny(push, pull);

        // 一端结束后中止连接，让另一端循环退出
        socket.Abort();
        await Task.WhenAll(Observe(push), Observe(pull));
    }

    private async Task SendHelloAsync()
    {
        var hello = new Hello(capture.Width, capture.Height, (int)PInvoke.GetDpiForSystem());
        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task PushFramesAsync()
    {
        var payloadLength = capture.Width * capture.Height * 4;
        var payload = new byte[payloadLength];
        var packet = new byte[FrameHeader.Size + payloadLength];
        using var timer = new PeriodicTimer(FrameInterval);
        Task? pending = null;

        try
        {
            while (socket.State == WebSocketState.Open && await timer.WaitForNextTickAsync())
            {
                var length = capture.Capture(payload);
                if (pending is { IsCompleted: false })
                    continue; // 网络积压时丢帧保实时性

                new FrameHeader(capture.Width, capture.Height, length).WriteTo(packet);
                payload.AsSpan(0, length).CopyTo(packet.AsSpan(FrameHeader.Size));
                pending = socket.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
        }
        finally
        {
            if (pending is not null)
                await Observe(pending);
        }
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
            // 鼠标消息在回控里程碑接入
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
