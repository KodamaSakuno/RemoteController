using System.Drawing;
using System.Drawing.Imaging;

namespace RemoteController.Server;

/// <summary>BGRA 帧 → JPEG 编码器。输出缓冲复用，非线程安全；调用方须保证上一帧发送完毕后再编码下一帧。</summary>
internal sealed unsafe class FrameEncoder : IDisposable
{
    public FrameEncoder(long quality)
    {
        _parameters = new EncoderParameters(1);
        _parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
    }

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo
        .GetImageEncoders()
        .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly MemoryStream _output = new();
    private readonly EncoderParameters _parameters;

    public byte[] Buffer => _output.GetBuffer();
    public int Length => (int)_output.Length;

    public void Encode(byte[] frame, int width, int height)
    {
        _output.SetLength(0);
        fixed (byte* ptr = frame)
        {
            // Format32bppArgb 内存布局即 BGRA，与采集帧一致；此构造只包装不复制，编码期间 frame 必须保持固定
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, (IntPtr)ptr);
            bitmap.Save(_output, JpegCodec, _parameters);
        }
    }

    public void Dispose()
    {
        _parameters.Dispose();
        _output.Dispose();
    }
}
