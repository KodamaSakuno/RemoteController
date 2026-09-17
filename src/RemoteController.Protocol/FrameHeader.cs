using System.Buffers.Binary;

namespace RemoteController.Protocol;

/// <summary>图像帧的 16 字节二进制头，负载为 BGRA32 像素。</summary>
public readonly struct FrameHeader
{
    public const int Size = 16;

    // "RCF1" 按小端序写入，与写入端平台无关
    private const uint MagicValue = 0x3146_4352;

    public int Width { get; }
    public int Height { get; }
    public int PayloadLength { get; }

    public FrameHeader(int width, int height, int payloadLength)
    {
        Width = width;
        Height = height;
        PayloadLength = payloadLength;
    }

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, MagicValue);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], PayloadLength);
    }

    public static bool TryParse(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        header = default;
        if (source.Length < Size)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != MagicValue)
            return false;

        header = new FrameHeader(
            BinaryPrimitives.ReadInt32LittleEndian(source[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[12..]));
        return true;
    }
}
