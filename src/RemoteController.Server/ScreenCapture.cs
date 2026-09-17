using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteController.Server;

/// <summary>GDI 全屏采集：一次性创建 DIB section，逐帧 BitBlt 后直接读位图内存。</summary>
internal sealed unsafe class ScreenCapture : IDisposable
{
    private readonly HWND _screenHwnd;
    private readonly HDC _screenDc;
    private readonly HDC _memoryDc;
    private readonly DeleteObjectSafeHandle _bitmap;
    private readonly HGDIOBJ _oldBitmap;
    private readonly byte* _bits;

    public int Width { get; }
    public int Height { get; }
    private readonly int _originX;
    private readonly int _originY;

    public ScreenCapture()
    {
        _originX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        _originY = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        Width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        Height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);

        // GetDC(NULL) 返回覆盖整个虚拟屏幕的 DC，多显示器坐标系一致
        _screenHwnd = new HWND(IntPtr.Zero);
        _screenDc = PInvoke.GetDC(_screenHwnd);
        _memoryDc = PInvoke.CreateCompatibleDC(_screenDc);

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                // 负高度 = 自顶向下 DIB，行序与帧缓冲一致，省一次翻转
                biWidth = Width,
                biHeight = -Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        _bitmap = PInvoke.CreateDIBSection(_memoryDc, &info, DIB_USAGE.DIB_RGB_COLORS, out var bits, null, 0);
        if (_bitmap.IsInvalid)
            throw new InvalidOperationException("CreateDIBSection 失败");

        _bits = (byte*)bits;
        _oldBitmap = PInvoke.SelectObject(_memoryDc, (HGDIOBJ)_bitmap.DangerousGetHandle());
    }

    public int Capture(Span<byte> destination)
    {
        // BitBlt 不设置 LastError，失败只能按返回值判断
        if (!PInvoke.BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, _originX, _originY, ROP_CODE.SRCCOPY))
            throw new Win32Exception("BitBlt 失败");

        var length = Width * Height * 4;
        new ReadOnlySpan<byte>(_bits, length).CopyTo(destination);
        return length;
    }

    public void Dispose()
    {
        PInvoke.SelectObject(_memoryDc, _oldBitmap);
        _bitmap.Dispose();
        PInvoke.DeleteDC(_memoryDc);
        PInvoke.ReleaseDC(_screenHwnd, _screenDc);
    }
}
