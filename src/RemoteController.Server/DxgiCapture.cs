using RemoteController.Protocol;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteController.Server;

/// <summary>
/// DXGI Desktop Duplication 区域采集：变化驱动，阻塞至屏幕更新才返回新帧；空闲时零开销。
/// 发出的帧为纹理方向（面板原生），旋转矫正由客户端呈现时完成。
/// </summary>
internal sealed unsafe class DxgiCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _staging;

    // duplication 纹理为面板原生方向，逻辑桌面经 Rotation 旋至于上；
    // 区域（逻辑坐标）到纹理源矩形的换算在此完成，客户端不再关心
    private readonly ModeRotation _rotation;
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;
    private readonly int _textureWidth;
    private readonly int _textureHeight;
    private readonly int _stageWidth;
    private readonly int _stageHeight;
    private readonly int _boxLeft;
    private readonly int _boxTop;

    /// <summary>采集区域（逻辑坐标系），Hello 原点的来源。</summary>
    public CaptureRegion Region { get; }

    /// <summary>帧的纹理尺寸（= staging 尺寸，90/270 时相对逻辑区域转置）。</summary>
    public int Width => _stageWidth;
    public int Height => _stageHeight;

    /// <summary>纹理相对逻辑桌面的旋转角（度），Hello 告知客户端用于呈现。</summary>
    internal int RotationDegrees => _rotation switch
    {
        ModeRotation.Rotate90 => 90,
        ModeRotation.Rotate180 => 180,
        ModeRotation.Rotate270 => 270,
        _ => 0,
    };

    // 仅供 --dump-frame 诊断输出，稳定后可随诊断路径一并移除
    internal ModeRotation Rotation => _rotation;
    internal int BufferWidth => _bufferWidth;
    internal int BufferHeight => _bufferHeight;
    internal int StageWidth => _stageWidth;
    internal int StageHeight => _stageHeight;
    internal int BoxLeft => _boxLeft;
    internal int BoxTop => _boxTop;
    internal int TextureWidth => _textureWidth;
    internal int TextureHeight => _textureHeight;
    internal int LastTextureWidth { get; private set; }
    internal int LastTextureHeight { get; private set; }

    /// <param name="region">采集区域（虚拟屏幕逻辑坐标系）；null 表示主输出整屏。须落在主输出逻辑边界内。</param>
    public DxgiCapture(CaptureRegion? region)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters(0, out var adapter).CheckError();
        ArgumentNullException.ThrowIfNull(adapter);

        // 指定 adapter 时 DriverType 必须为 Unknown（D3D11 规范），Hardware 仅限无 adapter 的重载
        D3D11.D3D11CreateDevice(
            adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out var device).CheckError();
        _device = device ?? throw new InvalidOperationException("D3D11CreateDevice 未返回设备");

        adapter.EnumOutputs(0, out var output).CheckError();
        using (output)
        {
            var outputBounds = output.Description.DesktopCoordinates;
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);

            // OUTDUPL_DESC.ModeDescription 描述逻辑桌面模式（未经旋转），纹理尺寸须按 Rotation 换算
            var duplicationDesc = _duplication.Description;
            _rotation = duplicationDesc.Rotation;
            _bufferWidth = (int)duplicationDesc.ModeDescription.Width;
            _bufferHeight = (int)duplicationDesc.ModeDescription.Height;
            (_textureWidth, _textureHeight) = _rotation switch
            {
                ModeRotation.Rotate90 or ModeRotation.Rotate270 => (_bufferHeight, _bufferWidth),
                _ => (_bufferWidth, _bufferHeight),
            };

            var virtualX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
            var virtualY = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
            var logicalWidth = outputBounds.Right - outputBounds.Left;
            var logicalHeight = outputBounds.Bottom - outputBounds.Top;
            Region = region ?? new CaptureRegion(
                outputBounds.Left - virtualX,
                outputBounds.Top - virtualY,
                logicalWidth,
                logicalHeight);

            // 逻辑坐标（已含旋转）换算到纹理坐标；不同旋转下源矩形位置与 staging 尺寸不同
            var lx = virtualX + Region.X - outputBounds.Left;
            var ly = virtualY + Region.Y - outputBounds.Top;
            if (lx < 0 || ly < 0
                || lx + Region.Width > logicalWidth
                || ly + Region.Height > logicalHeight)
                throw new ArgumentOutOfRangeException(nameof(region), "采集区域超出主输出范围");

            switch (_rotation)
            {
                case ModeRotation.Identity:
                case ModeRotation.Unspecified:
                    _boxLeft = lx;
                    _boxTop = ly;
                    _stageWidth = Region.Width;
                    _stageHeight = Region.Height;
                    break;
                case ModeRotation.Rotate90:
                    // 逻辑(x,y) → 纹理(y, textureH-1-x)，staging 相对逻辑区域为转置
                    _boxLeft = ly;
                    _boxTop = _textureHeight - lx - Region.Width;
                    _stageWidth = Region.Height;
                    _stageHeight = Region.Width;
                    break;
                case ModeRotation.Rotate270:
                    // 逻辑(x,y) → 纹理(textureW-1-y, x)
                    _boxLeft = _textureWidth - ly - Region.Height;
                    _boxTop = lx;
                    _stageWidth = Region.Height;
                    _stageHeight = Region.Width;
                    break;
                case ModeRotation.Rotate180:
                    _boxLeft = _bufferWidth - lx - Region.Width;
                    _boxTop = _bufferHeight - ly - Region.Height;
                    _stageWidth = Region.Width;
                    _stageHeight = Region.Height;
                    break;
                default:
                    throw new InvalidOperationException($"不支持的屏幕旋转: {_rotation}");
            }
        }

        // staging 仅作 CPU 回读中转，创建一次复用
        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)_stageWidth,
            Height = (uint)_stageHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _staging = _device.CreateTexture2D(stagingDesc);
    }

    /// <summary>等待屏幕更新并读出最新帧（纹理方向）。返回 false 表示超时无桌面变化。</summary>
    public bool TryAcquireFrame(Span<byte> destination, int timeoutMs)
    {
        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (result.Failure)
        {
            // 超时不算错误：屏幕无更新
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                return false;
            result.CheckError();
        }

        try
        {
            // AccumulatedFrames 为 0 表示仅指针移动无桌面变化；桌面帧仍须 ReleaseFrame
            if (info.AccumulatedFrames == 0)
                return false;
            ArgumentNullException.ThrowIfNull(resource);

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            // 诊断：duplication 纹理真实尺寸（OUTDUPL_DESC.ModeDescription 未经旋转，不可作坐标依据）
            LastTextureWidth = (int)texture.Description.Width;
            LastTextureHeight = (int)texture.Description.Height;
            var context = _device.ImmediateContext;
            var sourceBox = new Box(_boxLeft, _boxTop, 0, _boxLeft + _stageWidth, _boxTop + _stageHeight, 1);
            context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, sourceBox);

            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = _stageWidth * 4;
                var source = (byte*)mapped.DataPointer;
                for (var row = 0; row < _stageHeight; row++)
                {
                    new ReadOnlySpan<byte>(source + row * (int)mapped.RowPitch, rowBytes)
                        .CopyTo(destination.Slice(row * rowBytes, rowBytes));
                }
            }
            finally
            {
                context.Unmap(_staging, 0);
            }

            return true;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _staging.Dispose();
        _duplication.Dispose();
        _device.Dispose();
    }
}
