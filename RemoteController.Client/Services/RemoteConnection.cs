using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RemoteController.Shared.Protocol;

namespace RemoteController.Client.Services;

/// <summary>
/// TCP connection to a RemoteController.Host: performs the handshake,
/// receives frames on a background loop and sends input events.
/// </summary>
public sealed class RemoteConnection : IDisposable
{
    private TcpClient? _client;
    private MessageStream? _stream;
    private volatile bool _disconnectRequested;

    public event Action<FrameMessage>? FrameReceived;

    /// <summary>Raised when the session ends for any reason; the argument is a human-readable reason.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected { get; private set; }

    public async Task<(int Width, int Height)> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _disconnectRequested = false;

        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            var stream = new MessageStream(client.GetStream());
            await stream.WriteAsync(new ClientHelloMessage(ProtocolInfo.Version), cancellationToken);
            if (await stream.ReadAsync(cancellationToken) is not ServerHelloMessage hello)
                throw new InvalidDataException("Unexpected handshake response from host.");

            _client = client;
            _stream = stream;
            IsConnected = true;
            _ = ReceiveLoopAsync();
            return (hello.ScreenWidth, hello.ScreenHeight);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void SendMouseMove(int x, int y) => SafeWrite(new MouseMoveMessage(x, y));

    public void SendMouseButton(RemoteMouseButton button, bool down) => SafeWrite(new MouseButtonMessage(button, down));

    public void SendMouseWheel(int steps) => SafeWrite(new MouseWheelMessage(steps));

    public void SendKey(int virtualKey, bool down) => SafeWrite(new KeyEventMessage(virtualKey, down));

    public void Disconnect()
    {
        _disconnectRequested = true;
        IsConnected = false;
        _stream = null;

        var client = Interlocked.Exchange(ref _client, null);
        try
        {
            client?.Close();
        }
        catch
        {
            // already gone
        }
    }

    public void Dispose() => Disconnect();

    private void SafeWrite(RemoteMessage message)
    {
        try
        {
            _stream?.Write(message);
        }
        catch
        {
            // connection dropped; the receive loop reports the disconnect
        }
    }

    private async Task ReceiveLoopAsync()
    {
        string reason;
        try
        {
            while (true)
            {
                var message = await _stream!.ReadAsync();
                if (message is null)
                {
                    reason = "远端关闭了连接";
                    break;
                }

                if (message is FrameMessage frame)
                    FrameReceived?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            reason = _disconnectRequested ? "已断开" : ex.Message;
        }

        IsConnected = false;
        Disconnected?.Invoke(reason);
    }
}
