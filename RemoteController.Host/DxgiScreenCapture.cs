using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteController.Host;

/// <summary>
/// Captures the primary display via DXGI Desktop Duplication and encodes frames as JPEG.
/// Acquired frames arrive in the panel's native scanout orientation; when the display runs
/// in a rotated mode (e.g. a portrait panel set to landscape) they are rotated back to the
/// desktop orientation before encoding. <see cref="CaptureJpeg"/> returns null when the
/// desktop has not changed, so an idle desktop costs neither bandwidth nor encode CPU.
/// Windows 8+ only.
/// </summary>
public sealed class DxgiScreenCapture : IDisposable
{
    private const uint AcquireTimeoutMs = 100;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;
    private readonly EncoderParameters _jpegParams;

    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private Bitmap? _encodeBuffer;

    private ModeRotation _rotation;
    private int _rawWidth;
    private int _rawHeight;

    public DxgiScreenCapture(long quality = 70)
    {
        _jpegParams = new EncoderParameters(1);
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);

        (_device, _context, _output) = CreateDuplicationSource();
        Width = _output.Description.DesktopCoordinates.Right;
        Height = _output.Description.DesktopCoordinates.Bottom;
        RecreateDuplication();
    }

    /// <summary>Primary display size in physical pixels, in the desktop's orientation.</summary>
    public int Width { get; private set; }

    /// <summary>Primary display size in physical pixels, in the desktop's orientation.</summary>
    public int Height { get; private set; }

    /// <summary>
    /// Opts the process into physical-pixel reporting. Without this, Windows virtualizes
    /// DXGI output coordinates for DPI-unaware processes (e.g. reporting 1536x864 on a
    /// 1920x1080/125% display), which would corrupt the size sent in the handshake.
    /// Must be called before the first DXGI call.
    /// </summary>
    public static void EnsureDpiAwareness() => SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    /// <summary>
    /// Captures and JPEG-encodes the next desktop update,
    /// or returns null when no update arrived within the acquire timeout.
    /// </summary>
    public byte[]? CaptureJpeg()
    {
        if (_duplication is null)
            RecreateDuplication();

        var result = _duplication!.AcquireNextFrame(AcquireTimeoutMs, out _, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return null; // desktop unchanged
        if (result == Vortice.DXGI.ResultCode.AccessDenied)
        {
            // Secure desktop (UAC prompt, lock screen) cannot be captured; back off briefly.
            Thread.Sleep(200);
            return null;
        }

        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            // Display mode change or session switch: the duplication must be recreated.
            RecreateDuplication();
            return null;
        }

        result.CheckError();

        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureBuffers((int)texture.Description.Width, (int)texture.Description.Height);
            _context.CopyResource(_staging!, texture);
            return EncodeFrame();
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _staging?.Dispose();
        _encodeBuffer?.Dispose();
        _output.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context, IDXGIOutput1 Output) CreateDuplicationSource()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; factory.EnumAdapters(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                {
                    using (output)
                    {
                        // The primary display always starts at the virtual-screen origin.
                        var bounds = output.Description.DesktopCoordinates;
                        if (bounds.Left != 0 || bounds.Top != 0)
                            continue;

                        D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                DeviceCreationFlags.None,
                                new[] { FeatureLevel.Level_11_0 },
                                out ID3D11Device? device,
                                out ID3D11DeviceContext? context)
                            .CheckError();

                        return (device!, context!, output.QueryInterface<IDXGIOutput1>());
                    }
                }
            }
        }

        throw new InvalidOperationException("No DXGI output found for the primary display.");
    }

    private void RecreateDuplication()
    {
        _duplication?.Dispose();
        _duplication = _output.DuplicateOutput(_device);

        // Acquired frames are in the panel's native scanout orientation. When the display
        // runs rotated, they must be rotated by the named amount to match the desktop.
        _rotation = _duplication.Description.Rotation;
        if (_rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            Console.WriteLine($"[Host] Display rotation is {_rotation}; frames will be rotated to the desktop orientation.");
    }

    private void EnsureBuffers(int rawWidth, int rawHeight)
    {
        if (_staging is not null && _rawWidth == rawWidth && _rawHeight == rawHeight)
            return;

        _staging?.Dispose();
        _encodeBuffer?.Dispose();

        _rawWidth = rawWidth;
        _rawHeight = rawHeight;

        // The public dimensions are in the desktop's orientation, which is what the
        // handshake reports and what input coordinates are mapped against.
        var swapped = _rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
        Width = swapped ? rawHeight : rawWidth;
        Height = swapped ? rawWidth : rawHeight;

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)rawWidth,
            Height = (uint)rawHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        // The encode buffer holds the corrected (desktop-oriented) image, so its
        // dimensions stay stable across frames even when a rotation is applied.
        _encodeBuffer = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
    }

    private byte[] EncodeFrame()
    {
        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var data = _encodeBuffer!.LockBits(
                new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    if (_rotation is ModeRotation.Identity or ModeRotation.Unspecified)
                    {
                        // DXGI gives BGRA rows, which is exactly Format32bppArgb's layout.
                        var rowBytes = _rawWidth * 4;
                        var src = (byte*)map.DataPointer;
                        var dst = (byte*)data.Scan0;
                        for (var y = 0; y < _rawHeight; y++)
                            Buffer.MemoryCopy(src + y * map.RowPitch, dst + y * data.Stride, rowBytes, rowBytes);
                    }
                    else
                    {
                        // Rotated display: rotate the scanout-oriented pixels into the
                        // desktop orientation while copying.
                        RotateInto((byte*)map.DataPointer, (int)map.RowPitch, (byte*)data.Scan0, data.Stride);
                    }
                }
            }
            finally
            {
                _encodeBuffer.UnlockBits(data);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        using var ms = new MemoryStream();
        _encodeBuffer!.Save(ms, JpegCodec, _jpegParams);
        return ms.ToArray();
    }

    /// <summary>
    /// Copies the raw (scanout-oriented) frame into the corrected-dims encode buffer,
    /// rotating 32bpp pixels by the display's rotation. Pitches are in bytes and are
    /// always multiples of 4 for a 32bpp layout.
    /// </summary>
    private unsafe void RotateInto(byte* src, int srcPitch, byte* dst, int dstStride)
    {
        var s = (uint*)src;
        var d = (uint*)dst;
        var sp = srcPitch / 4;
        var dp = dstStride / 4;

        switch (_rotation)
        {
            // D(x, y) = S(y, rawHeight-1-x): the source's bottom-left corner lands top-left.
            case ModeRotation.Rotate90:
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                    d[y * dp + x] = s[(_rawHeight - 1 - x) * sp + y];
                break;
            // D(x, y) = S(rawWidth-1-y, x): the source's top-right corner lands top-left.
            case ModeRotation.Rotate270:
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                    d[y * dp + x] = s[x * sp + (_rawWidth - 1 - y)];
                break;
            // D(x, y) = S(rawWidth-1-x, rawHeight-1-y)
            case ModeRotation.Rotate180:
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                    d[y * dp + x] = s[(_rawHeight - 1 - y) * sp + (_rawWidth - 1 - x)];
                break;
        }
    }
}
