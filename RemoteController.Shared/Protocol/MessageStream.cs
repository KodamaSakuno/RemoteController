using System.Buffers.Binary;
using System.Text;

namespace RemoteController.Shared.Protocol;

/// <summary>
/// Length-prefixed message framing over a stream:
/// [int32 bodyLength][byte messageType][payload], little-endian.
/// </summary>
public sealed class MessageStream
{
    private const int MaxBodyLength = 32 * 1024 * 1024; // 32 MB safety cap

    private readonly Stream _stream;
    private readonly SemaphoreSlim _asyncWriteLock = new(1, 1);
    private readonly object _syncWriteLock = new();

    public MessageStream(Stream stream) => _stream = stream;

    public async Task WriteAsync(RemoteMessage message, CancellationToken cancellationToken = default)
    {
        var packet = BuildPacket(message);
        await _asyncWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _asyncWriteLock.Release();
        }
    }

    /// <summary>Synchronous write, intended for small control messages.</summary>
    public void Write(RemoteMessage message)
    {
        var packet = BuildPacket(message);
        lock (_syncWriteLock)
        {
            _stream.Write(packet);
        }
    }

    /// <summary>Reads one message. Returns null on clean EOF before a new message starts.</summary>
    public async Task<RemoteMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var header = await ReadExactAsync(4, cancellationToken).ConfigureAwait(false);
        if (header is null)
            return null;

        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (bodyLength <= 0 || bodyLength > MaxBodyLength)
            throw new InvalidDataException($"Invalid message length: {bodyLength}");

        var body = await ReadExactAsync(bodyLength, cancellationToken).ConfigureAwait(false)
                   ?? throw new EndOfStreamException("Connection closed mid-message.");

        using var ms = new MemoryStream(body, 1, body.Length - 1);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        return RemoteMessage.Read((MessageType)body[0], reader);
    }

    private static byte[] BuildPacket(RemoteMessage message)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)message.Type);
            message.WritePayload(writer);
        }

        var body = ms.ToArray();
        var packet = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, body.Length);
        body.CopyTo(packet, sizeof(int));
        return packet;
    }

    private async Task<byte[]?> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                    return null;
                throw new EndOfStreamException("Connection closed mid-message.");
            }

            offset += read;
        }

        return buffer;
    }
}
