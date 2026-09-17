using System.Buffers.Binary;

namespace RemoteController.Server;

/// <summary>
/// 把一帧 BGRA 像素落成 32 位 BMP，供 --dump-frame 诊断直接查看，绕过 JPEG 编码与网络链路。
/// 属旋转矫正联调的临时诊断设施，问题解决后移除。
/// </summary>
internal static class FrameDump
{
    public static void WriteBmp(string path, ReadOnlySpan<byte> bgra, int width, int height)
    {
        const int headerSize = 14 + 40; // BITMAPFILEHEADER + BITMAPINFOHEADER
        var pixelBytes = width * height * 4;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[headerSize];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], headerSize + pixelBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], width);
        // 负高度表示自顶向下的行序，与内存帧一致，省一次整帧翻转
        BinaryPrimitives.WriteInt32LittleEndian(header[22..], -height);
        BinaryPrimitives.WriteInt16LittleEndian(header[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[28..], 32);
        stream.Write(header);

        // BGRA 字节序与 BI_RGB 32 位位图一致，像素区可原样落盘
        stream.Write(bgra);
    }
}
