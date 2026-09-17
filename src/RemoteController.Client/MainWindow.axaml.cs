using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteController.Protocol;
using ProtocolMouseButton = RemoteController.Protocol.MouseButton;

namespace RemoteController.Client;

public partial class MainWindow : Window
{
    private ClientWebSocket? _socket;
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _prevBitmap; // 渲染管线可能仍持有上一帧，延迟一帧再释放
    private byte[]? _framePayload;        // 根住位图包装的原生指针背后的数组
    private Size _serverSize;
    private int _frameCount;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            HostBox.Text = args[1];

        FrameImage.PointerMoved += OnPointerMoved;
        FrameImage.PointerPressed += OnPointerButton;
        FrameImage.PointerReleased += OnPointerButton;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (TryMapToServer(e, out var x, out var y))
            Send(new MouseMove(x, y));
    }

    private void OnPointerButton(object? sender, PointerEventArgs e)
    {
        var updateKind = e.GetCurrentPoint(FrameImage).Properties.PointerUpdateKind;
        var kind = updateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButtonKind.Left,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButtonKind.Right,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButtonKind.Middle,
            _ => (MouseButtonKind?)null,
        };
        if (kind is null || !TryMapToServer(e, out _, out _))
            return;

        var isDown = updateKind.ToString().EndsWith("Pressed", StringComparison.Ordinal);
        if (isDown)
            e.Pointer.Capture(FrameImage); // 拖拽期间持续收到移动事件，即使指针移出图像
        else
            e.Pointer.Capture(null);

        Send(new ProtocolMouseButton(kind.Value, isDown));
    }

    private bool TryMapToServer(PointerEventArgs e, out int x, out int y)
    {
        x = y = 0;
        if (_serverSize is { Width: <= 0 } or { Height: <= 0 })
            return false;

        var position = e.GetPosition(FrameImage);
        var bounds = FrameImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        // Fill 拉伸下图像与控制区重合，直接按宽高比线性映射回服务器物理像素
        x = (int)(position.X / bounds.Width * _serverSize.Width);
        y = (int)(position.Y / bounds.Height * _serverSize.Height);
        return true;
    }

    private void Send(MouseMessage message)
    {
        if (_socket?.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.Serialize<MouseMessage>(message, ProtocolJson.Options);
        _ = _socket.SendAsync(
            Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
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

    private unsafe void HandleFrame(FrameHeader header, byte[] payload)
    {
        _frameCount++;
        StatusText.Text = $"帧 #{_frameCount}";

        // Avalonia 12 的 WriteableBitmap 原地写入不会使已上传纹理失效，
        // 每帧新建位图才能保证刷新；每帧 16MB 的分配开销随 M2 帧编码一并消除
        fixed (byte* ptr = payload)
        {
            _prevBitmap?.Dispose();
            _prevBitmap = _bitmap;
            _bitmap = new WriteableBitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque,
                (IntPtr)ptr,
                new PixelSize(header.Width, header.Height),
                new Vector(96, 96),
                header.Width * 4);
            FrameImage.Source = _bitmap;
        }

        // 位图仅持有原始指针，必须额外根住负载数组防止 GC 回收
        _framePayload = payload;
    }
}
