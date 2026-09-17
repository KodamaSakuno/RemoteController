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

    public CaptureRegion Region { get; }
    public int Width => Region.Width;
    public int Height => Region.Height;
    private readonly int _sourceLeft;
    private readonly int _sourceTop;

    /// <param name="region">采集区域（虚拟屏幕坐标系）；null 表示主输出整屏。必须与主输出相交，否则抛异常。</param>
    public DxgiCapture(CaptureRegion? region)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters(0, out var adapter).CheckError();

        D3D11.D3D11CreateDevice(
            adapter, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out _device).CheckError();

        adapter.EnumOutputs(0, out var output).CheckError();
        using (output)
        {
            var outputBounds = output.Description.DesktopCoordinates;
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);

            var virtualX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
            var virtualY = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
            Region = region ?? new CaptureRegion(
                outputBounds.Left - virtualX,
                outputBounds.Top - virtualY,
                outputBounds.Right - outputBounds.Left,
                outputBounds.Bottom - outputBounds.Top);

            _sourceLeft = virtualX + Region.X - outputBounds.Left;
            _sourceTop = virtualY + Region.Y - outputBounds.Top;
            if (_sourceLeft < 0 || _sourceTop < 0
                || _sourceLeft + Region.Width > outputBounds.Right - outputBounds.Left
                || _sourceTop + Region.Height > outputBounds.Bottom - outputBounds.Top)
                throw new ArgumentOutOfRangeException(nameof(region), "采集区域超出主输出范围");
        }

        // 暂存纹理仅作 CPU 回读，尺寸与输出模式无关，创建一次复用
        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)Region.Width,
            Height = (uint)Region.Height,
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

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var context = _device.ImmediateContext;
            var sourceBox = new Box(_sourceLeft, _sourceTop, 0, _sourceLeft + Width, _sourceTop + Height, 1);
            context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, sourceBox);

            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = Width * 4;
                var source = (byte*)mapped.DataPointer;
                for (var y = 0; y < Height; y++)
                {
                    new ReadOnlySpan<byte>(source + y * mapped.RowPitch, rowBytes)
                        .CopyTo(destination.Slice(y * rowBytes, rowBytes));
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
            resource.Dispose();
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
