namespace RemoteController.Shared.Protocol;

public enum MessageType : byte
{
    // Client -> Host
    ClientHello = 0x01,
    MouseMove = 0x10,
    MouseButton = 0x11,
    MouseWheel = 0x12,
    KeyEvent = 0x13,

    // Host -> Client
    ServerHello = 0x02,
    VideoFrame = 0x04,
}
