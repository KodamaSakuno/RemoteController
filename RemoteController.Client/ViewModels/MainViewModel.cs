using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private H264Decoder? _decoder;
    private int _uiFramePending;

    public MainViewModel()
    {
        _connection.VideoFrameReceived += OnVideoFrameReceived;
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

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ConnectCommand { get; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DisconnectCommand { get; }

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
            _decoder?.Dispose();
            _decoder = new H264Decoder(width, height);
            DisposeFrameBuffers();
            IsConnected = true;
            Status = $"已连接 {Address.Trim()}:{port}（远端 {width}×{height}）";
        }
        catch (Exception ex)
        {
            Status = $"连接失败：{ex.Message}";
        }
    }

    private readonly WriteableBitmap?[] _frameBuffers = new WriteableBitmap?[2];
    private int _frameBufferIndex = 1; // the first NextFrameBuffer() call flips this to buffer 0

    private void OnVideoFrameReceived(VideoFrameMessage frame)
    {
        if (_decoder is null)
            return;
        _decoder.Decode(frame.Data, frame.Timestamp, OnDecodedNv12);
    }

    /// <summary>Runs on the connection's receive thread; converts straight out of the decoder's buffer.</summary>
    private unsafe void OnDecodedNv12(IntPtr data, int stride, int width, int height)
    {
        var bitmap = NextFrameBuffer();
        using (var target = bitmap.Lock())
        {
            if (_decoder!.OutputIsBgra)
            {
                Nv12Converter.CopyBgra(
                    (byte*)data, stride,
                    (byte*)target.Address, target.RowBytes, RemoteWidth, RemoteHeight);
            }
            else
            {
                Nv12Converter.ToBgra(
                    (byte*)data, width, height,
                    (byte*)target.Address, target.RowBytes, RemoteWidth, RemoteHeight);
            }
        }

        // Two reusable buffers alternate, so the Source reference always changes and the
        // binding refreshes — but if the UI has not rendered the previous frame yet, skip
        // presenting this one instead of queueing up work.
        if (Interlocked.Exchange(ref _uiFramePending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _uiFramePending = 0;
            CurrentFrame = bitmap;
        });
    }

    private WriteableBitmap NextFrameBuffer()
    {
        _frameBufferIndex ^= 1;
        return _frameBuffers[_frameBufferIndex] ??= new WriteableBitmap(
            new PixelSize(RemoteWidth, RemoteHeight), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    private void OnDisconnected(string reason)
    {
        // Invoked on the connection's receive thread, which is also the only thread
        // the decoder is used on — safe to dispose here.
        _decoder?.Dispose();
        _decoder = null;

        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            Status = $"已断开：{reason}";
            CurrentFrame = null;
            DisposeFrameBuffers();
        });
    }

    private void DisposeFrameBuffers()
    {
        _frameBuffers[0]?.Dispose();
        _frameBuffers[1]?.Dispose();
        _frameBuffers[0] = _frameBuffers[1] = null;
        _frameBufferIndex = 1; // see the field comment
    }
}
