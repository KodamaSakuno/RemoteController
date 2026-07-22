namespace RemoteController.Shared.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
}

public abstract record RemoteMessage
{
    public abstract MessageType Type { get; }

    public abstract void WritePayload(BinaryWriter writer);

    public static RemoteMessage Read(MessageType type, BinaryReader reader) => type switch
    {
        MessageType.ClientHello => new ClientHelloMessage(reader.ReadInt32()),
        MessageType.ServerHello => new ServerHelloMessage(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
        MessageType.Frame => ReadFrame(reader),
        MessageType.MouseMove => new MouseMoveMessage(reader.ReadInt32(), reader.ReadInt32()),
        MessageType.MouseButton => new MouseButtonMessage((RemoteMouseButton)reader.ReadByte(), reader.ReadBoolean()),
        MessageType.MouseWheel => new MouseWheelMessage(reader.ReadInt32()),
        MessageType.KeyEvent => new KeyEventMessage(reader.ReadInt32(), reader.ReadBoolean()),
        _ => throw new InvalidDataException($"Unknown message type: 0x{(byte)type:X2}"),
    };

    private static FrameMessage ReadFrame(BinaryReader reader)
    {
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var data = reader.ReadBytes(reader.ReadInt32());
        return new FrameMessage(width, height, data);
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

public sealed record FrameMessage(int Width, int Height, byte[] JpegData) : RemoteMessage
{
    public override MessageType Type => MessageType.Frame;

    public override void WritePayload(BinaryWriter writer)
    {
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(JpegData.Length);
        writer.Write(JpegData);
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
