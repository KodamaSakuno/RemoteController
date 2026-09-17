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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RemoteController.Protocol;
using ProtocolMouseButton = RemoteController.Protocol.MouseButton;

namespace RemoteController.Client;

public partial class MainWindow : Window
{
    private ClientWebSocket? _socket;
    private Bitmap? _bitmap;
    private Bitmap? _prevBitmap; // 渲染管线可能仍持有上一帧，延迟一帧再释放
    private Size _textureSize;   // 帧的纹理尺寸（hello.Width/Height，未旋转）
    private int _rotation;       // 纹理相对逻辑桌面的旋转角（0/90/180/270）
    private int _regionOriginX;
    private int _regionOriginY;
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
        if (_textureSize is { Width: <= 0 } or { Height: <= 0 })
            return false;

        // Bounds 是纹理方向的布局矩形；视觉经 RenderTransform 旋转，指针需反向旋回
        var bounds = FrameImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var position = e.GetPosition(FrameImage);
        var radians = -_rotation * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = position.X - bounds.Width / 2;
        var dy = position.Y - bounds.Height / 2;
        var u = (dx * cos - dy * sin) / bounds.Width + 0.5;
        var v = (dx * sin + dy * cos) / bounds.Height + 0.5;
        if (u is < 0 or > 1 || v is < 0 or > 1)
            return false; // 落在旋转后视觉的黑区，不产生注入

        // 纹理 → 逻辑（与服务端 logical→texture 公式互逆）
        var tw = (int)(u * _textureSize.Width);
        var th = (int)(v * _textureSize.Height);
        var (lx, ly) = _rotation switch
        {
            90 => (_textureSize.Height - 1 - th, tw),
            270 => (th, _textureSize.Width - 1 - tw),
            180 => (_textureSize.Width - 1 - tw, _textureSize.Height - 1 - th),
            _ => (tw, th),
        };

        // 逻辑像素 + 区域原点 = 虚拟屏幕绝对物理像素
        x = _regionOriginX + (int)lx;
        y = _regionOriginY + (int)ly;
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
            _textureSize = new Size(hello.Width, hello.Height);
            _rotation = hello.Rotation;
            _regionOriginX = hello.X;
            _regionOriginY = hello.Y;

            // 逻辑尺寸 = 纹理尺寸按旋转还原（90/270 宽高互换），窗口按逻辑宽高比适配屏幕
            var rotated = _rotation is 90 or 270;
            var logicalWidth = rotated ? hello.Height : hello.Width;
            var logicalHeight = rotated ? hello.Width : hello.Height;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 初始尺寸按逻辑宽高比适配屏幕工作区；超出屏幕会被系统钳制产生黑边
                const double chrome = 60; // 地址栏 + 标题栏余量
                var area = (Screens.ScreenFromWindow(this)?.WorkingArea).GetValueOrDefault();
                var scale = area.Width > 0
                    ? Math.Min(1.0, Math.Min(area.Width / (double)logicalWidth, area.Height / (double)(logicalHeight + chrome)))
                    : 1.0;
                Width = logicalWidth * scale;
                Height = (logicalHeight + chrome) * scale;

                // 图像控件按纹理尺寸设定（位图同向，无变形），旋转交给 RenderTransform
                FrameImage.Width = hello.Width * scale;
                FrameImage.Height = hello.Height * scale;
                FrameImage.RenderTransform = new RotateTransform(_rotation);
                FrameImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

                StatusText.Text = $"已连接 {logicalWidth}x{logicalHeight} @{hello.Dpi}dpi (旋转 {_rotation}°)";
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
        _frameCount++;
        StatusText.Text = $"帧 #{_frameCount}";

        // 每帧解码新位图：解码由 Skia 完成，替换 Source 即触发重绘
        _prevBitmap?.Dispose();
        _prevBitmap = _bitmap;
        _bitmap = new Bitmap(new MemoryStream(payload, writable: false));
        FrameImage.Source = _bitmap;
    }
}
