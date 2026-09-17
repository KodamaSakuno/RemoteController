using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteController.Protocol;

namespace RemoteController.Client;

public partial class MainWindow : Window
{
    private ClientWebSocket? _socket;
    private WriteableBitmap? _bitmap;
    private Size _serverSize;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            HostBox.Text = args[1];
    }

    private async void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_socket is not null)
            return;

        _socket = new ClientWebSocket();
        try
        {
            var host = HostBox.Text?.Trim() ?? string.Empty;
            await _socket.ConnectAsync(new Uri($"ws://{host}:5080/ws"), CancellationToken.None);
            StatusText.Text = "已连接";
            _ = ReceiveLoopAsync(_socket);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"连接失败: {ex.Message}";
            _socket.Dispose();
            _socket = null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket)
    {
        try
        {
            // Server 保证先发送 Hello 再开始推帧
            var hello = JsonSerializer.Deserialize<Hello>(
                await ReceiveTextAsync(socket), ProtocolJson.Options)
                ?? throw new InvalidDataException("Hello 解析失败");
            _serverSize = new Size(hello.Width, hello.Height);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 窗口按帧尺寸调整，显示用 Fill 拉伸避免信箱黑边影响坐标映射
                Width = hello.Width;
                Height = hello.Height + 60;
                FrameImage.Stretch = Avalonia.Media.Stretch.Fill;
                StatusText.Text = $"已连接 {hello.Width}x{hello.Height} @{hello.Dpi}dpi";
            });

            var headerBuffer = new byte[FrameHeader.Size];
            while (socket.State == WebSocketState.Open)
            {
                // WS 不保证一次 Receive 对应一个完整消息，头部与负载分别读满
                await ReadExactAsync(socket, headerBuffer);
                if (!FrameHeader.TryParse(headerBuffer, out var header))
                    throw new InvalidDataException("帧头解析失败");

                var payload = new byte[header.PayloadLength];
                await ReadExactAsync(socket, payload);

                await Dispatcher.UIThread.InvokeAsync(() => HandleFrame(header, payload));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"连接中断: {ex.Message}");
        }
        finally
        {
            socket.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
            });
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.InvalidMessageType, "连接已关闭");
            buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task ReadExactAsync(WebSocket socket, Memory<byte> buffer)
    {
        var remaining = buffer;
        while (remaining.Length > 0)
        {
            var result = await socket.ReceiveAsync(remaining, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.InvalidMessageType, "连接已关闭");
            remaining = remaining[result.Count..];
        }
    }

    private void HandleFrame(FrameHeader header, byte[] payload)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != header.Width || _bitmap.PixelSize.Height != header.Height)
        {
            _bitmap = new WriteableBitmap(
                new PixelSize(header.Width, header.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            FrameImage.Source = _bitmap;
        }

        // DIB 段无行对齐填充（宽度*4 恒为 4 的倍数），但位图行距仍按实际 RowBytes 逐行拷贝
        unsafe
        {
            using var framebuffer = _bitmap.Lock();
            var rowBytes = header.Width * 4;
            fixed (byte* src = payload)
            {
                for (var y = 0; y < header.Height; y++)
                {
                    var dst = (byte*)framebuffer.Address.ToPointer() + y * framebuffer.RowBytes;
                    Buffer.MemoryCopy(src + y * rowBytes, dst, framebuffer.RowBytes, rowBytes);
                }
            }
        }
    }
}
