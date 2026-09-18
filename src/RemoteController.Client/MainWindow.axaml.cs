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
    private bool _connecting;      // 连接循环运行中（含重连等待）
    private bool _manualDisconnect; // 用户点「断开」后置位，终止重连循环
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
        else if (ClientSettings.Load() is { LastHost: { } lastHost })
            HostBox.Text = lastHost;

        FrameImage.PointerMoved += OnPointerMoved;
        FrameImage.PointerPressed += OnPointerButton;
        FrameImage.PointerReleased += OnPointerButton;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (TryMapToServer(e, out var x, out var y))
            Send(new MouseMove(x, y));
    }

    // 连接后地址栏隐藏，双击画面唤出以操作「断开」
    private void OnFrameDoubleTapped(object? sender, TappedEventArgs e)
    {
        ControlBar.IsVisible = !ControlBar.IsVisible;
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

        var bounds = FrameImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        // GetPosition 已把指针逆变换到控件本地（纹理方向）坐标，无需再手动逆旋转
        var position = e.GetPosition(FrameImage);
        var u = position.X / bounds.Width;
        var v = position.Y / bounds.Height;
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
        if (_connecting)
        {
            // 连接中或重连等待中：视为停止请求
            _manualDisconnect = true;
            _socket?.Abort();
            return;
        }

        _manualDisconnect = false;
        var host = HostBox.Text?.Trim() ?? string.Empty;
        ConnectButton.Content = "断开";
        _connecting = true;
        try
        {
            await ConnectLoopAsync(host);
        }
        finally
        {
            _connecting = false;
            ConnectButton.Content = "连接";
        }
    }

    // 连接 → 运行 → 断开 → 退避重连，直到用户点「断开」
    private async Task ConnectLoopAsync(string host)
    {
        var attempt = 0;
        while (!_manualDisconnect)
        {
            var uri = host.Contains(':') ? $"ws://{host}/ws" : $"ws://{host}:5080/ws";
            _socket = new ClientWebSocket();
            try
            {
                await _socket.ConnectAsync(new Uri(uri), CancellationToken.None);
                attempt = 0;
                new ClientSettings(host).Save();
                await ReceiveLoopAsync(_socket); // 运行至断开，异常向上抛
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                if (!_manualDisconnect)
                    StatusText.Text = $"连接失败: {ex.Message}";
            }
            finally
            {
                _socket.Dispose();
                _socket = null;
            }

            if (_manualDisconnect)
                break;

            // 指数退避：2s 起，15s 封顶
            var delaySeconds = Math.Min(2 << attempt, 15);
            attempt++;
            StatusText.Text = $"连接中断，{delaySeconds}s 后重连…";
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        StatusText.Text = "已断开";
        ControlBar.IsVisible = true;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket)
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
            ControlBar.IsVisible = false; // 连接后隐藏地址栏，双击画面唤出
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
