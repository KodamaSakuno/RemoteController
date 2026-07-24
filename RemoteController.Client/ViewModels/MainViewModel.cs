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

    private readonly WriteableBitmap?[] _frameBuffers = new WriteableBitmap?[3];
    private readonly object _bufferSelection = new();
    private int _presentedIndex = -1; // last buffer handed to the UI; the compositor may still sample it
    private int _previousIndex = -1;  // the one before that; may still be draining a render pass

    private void OnVideoFrameReceived(VideoFrameMessage frame)
    {
        if (_decoder is null)
            return;
        _decoder.Decode(frame.Data, frame.Timestamp, OnDecodedNv12);
    }

    /// <summary>Runs on the connection's receive thread; converts straight out of the decoder's buffer.</summary>
    private unsafe void OnDecodedNv12(IntPtr data, int stride, int width, int height)
    {
        // A present is still in flight: drop this frame (the decoder was still fed, so the
        // stream stays intact) instead of queueing work for the UI. Checked before the
        // conversion so a lagging UI does not turn into wasted CPU.
        if (_uiFramePending != 0)
            return;

        // Three buffers cycle; the UI owns the two most recently presented ones and the
        // compositor may still be sampling either, so conversion only ever targets the
        // third. Writing into a buffer the compositor reads shows as tearing/black
        // flashes — most visible exactly when the picture changes.
        int index;
        lock (_bufferSelection)
        {
            index = 0;
            for (var i = 0; i < _frameBuffers.Length; i++)
                if (i != _presentedIndex && i != _previousIndex)
                {
                    index = i;
                    break;
                }
        }

        var bitmap = _frameBuffers[index] ??= new WriteableBitmap(
            new PixelSize(RemoteWidth, RemoteHeight), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);

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

        if (Interlocked.Exchange(ref _uiFramePending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _uiFramePending = 0;
            lock (_bufferSelection)
            {
                _previousIndex = _presentedIndex;
                _presentedIndex = index;
            }

            CurrentFrame = bitmap;
        });
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
        for (var i = 0; i < _frameBuffers.Length; i++)
        {
            _frameBuffers[i]?.Dispose();
            _frameBuffers[i] = null;
        }

        _presentedIndex = _previousIndex = -1;
    }
}
