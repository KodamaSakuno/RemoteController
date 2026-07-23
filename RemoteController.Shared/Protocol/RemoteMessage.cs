namespace RemoteController.Shared.Protocol;

public static class ProtocolInfo
{
    public const int Version = 2;
}

public abstract record RemoteMessage
{
    public abstract MessageType Type { get; }

    public abstract void WritePayload(BinaryWriter writer);

    public static RemoteMessage Read(MessageType type, BinaryReader reader) => type switch
    {
        MessageType.ClientHello => new ClientHelloMessage(reader.ReadInt32()),
        MessageType.ServerHello => new ServerHelloMessage(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
        MessageType.VideoFrame => ReadVideoFrame(reader),
        MessageType.MouseMove => new MouseMoveMessage(reader.ReadInt32(), reader.ReadInt32()),
        MessageType.MouseButton => new MouseButtonMessage((RemoteMouseButton)reader.ReadByte(), reader.ReadBoolean()),
        MessageType.MouseWheel => new MouseWheelMessage(reader.ReadInt32()),
        MessageType.KeyEvent => new KeyEventMessage(reader.ReadInt32(), reader.ReadBoolean()),
        _ => throw new InvalidDataException($"Unknown message type: 0x{(byte)type:X2}"),
    };

    private static VideoFrameMessage ReadVideoFrame(BinaryReader reader)
    {
        var timestamp = reader.ReadInt64();
        var keyframe = reader.ReadBoolean();
        var data = reader.ReadBytes(reader.ReadInt32());
        return new VideoFrameMessage(timestamp, keyframe, data);
    }
}

public sealed record ClientHelloMessage(int Version) : RemoteMessage
{
    public override MessageType Type => MessageType.ClientHello;

    public override void WritePayload(BinaryWriter writer) => writer.Write(Version);
}

public sealed record ServerHelloMessage(int Version, int ScreenWidth, int ScreenHeight) : RemoteMessage
{
    public override MessageType Type => MessageType.ServerHello;

    public override void WritePayload(BinaryWriter writer)
    {
        writer.Write(Version);
        writer.Write(ScreenWidth);
        writer.Write(ScreenHeight);
    }
}

/// <summary>
/// One H.264 access unit (Annex B byte stream). The first sample of a session is always
/// a keyframe and has the encoder's sequence header (SPS/PPS) prepended.
/// </summary>
public sealed record VideoFrameMessage(long Timestamp, bool Keyframe, byte[] Data) : RemoteMessage
{
    public override MessageType Type => MessageType.VideoFrame;

    public override void WritePayload(BinaryWriter writer)
    {
        writer.Write(Timestamp);
        writer.Write(Keyframe);
        writer.Write(Data.Length);
        writer.Write(Data);
    }
}

public sealed record MouseMoveMessage(int X, int Y) : RemoteMessage
{
    public override MessageType Type => MessageType.MouseMove;

    public override void WritePayload(BinaryWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
    }
}

public enum RemoteMouseButton : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

public sealed record MouseButtonMessage(RemoteMouseButton Button, bool Down) : RemoteMessage
{
    public override MessageType Type => MessageType.MouseButton;

    public override void WritePayload(BinaryWriter writer)
    {
        writer.Write((byte)Button);
        writer.Write(Down);
    }
}

public sealed record MouseWheelMessage(int Delta) : RemoteMessage
{
    public override MessageType Type => MessageType.MouseWheel;

    public override void WritePayload(BinaryWriter writer) => writer.Write(Delta);
}

public sealed record KeyEventMessage(int VirtualKey, bool Down) : RemoteMessage
{
    public override MessageType Type => MessageType.KeyEvent;

    public override void WritePayload(BinaryWriter writer)
    {
        writer.Write(VirtualKey);
        writer.Write(Down);
    }
}
