using System.Drawing;
using System.Drawing.Imaging;

namespace RemoteController.Server;

/// <summary>BGRA 帧 → JPEG 编码器。输出缓冲复用，非线程安全；调用方须保证上一帧发送完毕后再编码下一帧。</summary>
internal sealed unsafe class FrameEncoder : IDisposable
{
    private const long Quality = 75;

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo
        .GetImageEncoders()
        .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static readonly EncoderParameters Parameters = CreateParameters();

    private readonly MemoryStream _output = new();

    public byte[] Buffer => _output.GetBuffer();
    public int Length => (int)_output.Length;

    public void Encode(byte[] frame, int width, int height)
    {
        _output.SetLength(0);
        fixed (byte* ptr = frame)
        {
            // Format32bppArgb 内存布局即 BGRA，与采集帧一致；此构造只包装不复制，编码期间 frame 必须保持固定
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, (IntPtr)ptr);
            bitmap.Save(_output, JpegCodec, Parameters);
        }
    }

    public void Dispose() => _output.Dispose();

    private static EncoderParameters CreateParameters()
    {
        var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, Quality);
        return parameters;
    }
}
