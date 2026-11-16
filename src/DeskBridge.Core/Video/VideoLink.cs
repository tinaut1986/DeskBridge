using System.Buffers.Binary;

namespace DeskBridge.Core.Video;

/// <summary>Where a video sender writes its packets and reads the receiver's: the network, or a local relay.</summary>
public interface IVideoLink : IAsyncDisposable
{
    /// <summary>Sends one packet: <paramref name="header"/> then <paramref name="data"/> as its body.</summary>
    Task SendAsync(VideoPacket type, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>The next packet, or null when the other side closed.</summary>
    Task<(VideoPacket Type, byte[] Body)?> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Video packets over a local stream (a pipe between two processes of the same PC), with no encryption: type,
/// length, body. The relay on the other end forwards them over the encrypted network channel.
/// </summary>
public sealed class StreamVideoLink(Stream stream) : IVideoLink
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _header = new byte[5];

    public async Task SendAsync(VideoPacket type, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        int length = header.Length + data.Length;
        if (length > VideoChannel.MaxPacket)
            throw new InvalidDataException($"Video packet too large ({length} bytes).");
        var packet = new byte[5 + length];
        packet[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), length);
        header.Span.CopyTo(packet.AsSpan(5));
        data.Span.CopyTo(packet.AsSpan(5 + header.Length));
        await _sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(packet, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<(VideoPacket Type, byte[] Body)?> ReceiveAsync(CancellationToken ct = default)
    {
        if (!await ReadAsync(_header, ct))
            return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(1));
        if (length < 0 || length > VideoChannel.MaxPacket)
            throw new InvalidDataException($"Invalid video packet length {length}.");
        var body = new byte[length];
        if (length > 0 && !await ReadAsync(body, ct))
            throw new EndOfStreamException("The video link closed in the middle of a packet.");
        return ((VideoPacket)_header[0], body);
    }

    private async Task<bool> ReadAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("The video link closed in the middle of a packet.");
            }
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        _sendLock.Dispose();
    }
}
