using RemoteController.Protocol;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteController.Server;

/// <summary>DXGI Desktop Duplication 区域采集：变化驱动，阻塞至屏幕更新才返回新帧；空闲时零开销。</summary>
internal sealed unsafe class DxgiCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _staging;

    // duplication 纹理恒为横屏模式；旋转显示器（如平板竖屏）上逻辑桌面与纹理之间需要坐标变换
    private readonly ModeRotation _rotation;
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;
    private readonly int _stageWidth;
    private readonly int _stageHeight;
    private readonly int _boxLeft;
    private readonly int _boxTop;

    public CaptureRegion Region { get; }
    public int Width => Region.Width;
    public int Height => Region.Height;

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

            // DXGI_OUTDUPL_DESC 是旋转与纹理尺寸的权威来源（纹理恒为横屏模式）
            var duplicationDesc = _duplication.Description;
            _rotation = duplicationDesc.Rotation;
            _bufferWidth = (int)duplicationDesc.ModeDescription.Width;
            _bufferHeight = (int)duplicationDesc.ModeDescription.Height;

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
                    // 逻辑(x,y) → 纹理(y, bufferH-1-x)，staging 相对逻辑帧为转置
                    _boxLeft = ly;
                    _boxTop = _bufferHeight - lx - Region.Width;
                    _stageWidth = Region.Height;
                    _stageHeight = Region.Width;
                    break;
                case ModeRotation.Rotate270:
                    // 逻辑(x,y) → 纹理(bufferW-1-y, x)
                    _boxLeft = _bufferWidth - ly - Region.Height;
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

    /// <summary>等待屏幕更新并读出最新帧。返回 false 表示超时无桌面变化。</summary>
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
            var context = _device.ImmediateContext;
            var sourceBox = new Box(_boxLeft, _boxTop, 0, _boxLeft + _stageWidth, _boxTop + _stageHeight, 1);
            context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, sourceBox);

            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyRotated((byte*)mapped.DataPointer, (int)mapped.RowPitch, destination);
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

    // 逻辑帧 = staging 像素按旋转规则重排；90/270 为转置（staging 宽高互换）
    private void CopyRotated(byte* source, int sourcePitch, Span<byte> destination)
    {
        fixed (byte* dst = destination)
        {
            for (var j = 0; j < Height; j++)
            {
                var row = dst + j * Width * 4;
                for (var i = 0; i < Width; i++)
                {
                    var (sx, sy) = _rotation switch
                    {
                        ModeRotation.Rotate90 => (j, Width - 1 - i),
                        ModeRotation.Rotate270 => (Height - 1 - j, i),
                        ModeRotation.Rotate180 => (Width - 1 - i, Height - 1 - j),
                        _ => (i, j),
                    };
                    ((uint*)row)[i] = ((uint*)(source + sy * sourcePitch))[sx];
                }
            }
        }
    }

    public void Dispose()
    {
        _staging.Dispose();
        _duplication.Dispose();
        _device.Dispose();
    }
}
