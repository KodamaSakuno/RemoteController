using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using RemoteController.Client.Services;
using RemoteController.Shared.Protocol;

namespace RemoteController.Client.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly RemoteConnection _connection = new();
    private string _address = "127.0.0.1";
    private string _port = "47800";
    private string _status = "未连接";
    private bool _isConnected;
    private Bitmap? _currentFrame;
    private int _remoteWidth;
    private int _remoteHeight;

    public MainViewModel()
    {
        _connection.FrameReceived += OnFrameReceived;
        _connection.Disconnected += OnDisconnected;

        ConnectCommand = ReactiveCommand.CreateFromTask(
            ConnectAsync,
            this.WhenAnyValue(x => x.IsConnected, connected => !connected));
        DisconnectCommand = ReactiveCommand.Create(
            () => _connection.Disconnect(),
            this.WhenAnyValue(x => x.IsConnected));
    }

    public string Address
    {
        get => _address;
        set => this.RaiseAndSetIfChanged(ref _address, value);
    }

    public string Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public Bitmap? CurrentFrame
    {
        get => _currentFrame;
        private set => this.RaiseAndSetIfChanged(ref _currentFrame, value);
    }

    public int RemoteWidth
    {
        get => _remoteWidth;
        private set => this.RaiseAndSetIfChanged(ref _remoteWidth, value);
    }

    public int RemoteHeight
    {
        get => _remoteHeight;
        private set => this.RaiseAndSetIfChanged(ref _remoteHeight, value);
    }

    public RemoteConnection Connection => _connection;

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }

    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    private async Task ConnectAsync()
    {
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535)
        {
            Status = "端口无效";
            return;
        }

        try
        {
            Status = "连接中…";
            var (width, height) = await _connection.ConnectAsync(Address.Trim(), port);
            RemoteWidth = width;
            RemoteHeight = height;
            IsConnected = true;
            Status = $"已连接 {Address.Trim()}:{port}（远端 {width}×{height}）";
        }
        catch (Exception ex)
        {
            Status = $"连接失败：{ex.Message}";
        }
    }

    private void OnFrameReceived(FrameMessage frame)
    {
        // Decode off the UI thread, then swap on the UI thread.
        var bitmap = new Bitmap(new MemoryStream(frame.JpegData, writable: false));
        Dispatcher.UIThread.Post(() =>
        {
            var old = CurrentFrame;
            CurrentFrame = bitmap;
            old?.Dispose();
        });
    }

    private void OnDisconnected(string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            Status = $"已断开：{reason}";
            var old = CurrentFrame;
            CurrentFrame = null;
            old?.Dispose();
        });
    }
}
