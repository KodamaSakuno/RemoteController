using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteController.Host;

/// <summary>
/// GDI-based primary-screen capture with JPEG encoding. Windows only.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly EncoderParameters _jpegParams;
    private Bitmap? _bitmap;
    private Graphics? _graphics;

    public ScreenCapture(long quality = 70)
    {
        _jpegParams = new EncoderParameters(1);
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
    }

    public static int PrimaryScreenWidth => GetSystemMetrics(SM_CXSCREEN);

    public static int PrimaryScreenHeight => GetSystemMetrics(SM_CYSCREEN);

    /// <summary>
    /// Report physical pixels instead of DPI-scaled values so capture and
    /// coordinate mapping stay consistent on high-DPI displays.
    /// </summary>
    public static void EnsureDpiAwareness() => SetProcessDPIAware();

    public byte[] CaptureJpeg(out int width, out int height)
    {
        width = PrimaryScreenWidth;
        height = PrimaryScreenHeight;
        EnsureBuffer(width, height);

        _graphics!.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        using var ms = new MemoryStream();
        _bitmap!.Save(ms, JpegCodec, _jpegParams);
        return ms.ToArray();
    }

    private void EnsureBuffer(int width, int height)
    {
        if (_bitmap is { Width: var w, Height: var h } && w == width && h == height)
            return;

        _graphics?.Dispose();
        _bitmap?.Dispose();
        _bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    public void Dispose()
    {
        _graphics?.Dispose();
        _bitmap?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
