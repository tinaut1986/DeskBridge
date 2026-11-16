using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeskBridge.Core.Video;

/// <summary>What a video packet carries.</summary>
public enum VideoPacket : byte
{
    /// <summary>Sender → receiver: picture size and settings (JSON <see cref="VideoFormat"/>), before the first frame and on changes.</summary>
    Format = 1,

    /// <summary>Sender → receiver: one H.264 frame: time (int64, 100 ns), flags (1 = key frame), Annex B bytes.</summary>
    Frame = 2,

    /// <summary>Sender → receiver: pointer position on the captured display: x, y (int32), visible (byte).</summary>
    Pointer = 3,

    /// <summary>Sender → receiver: a new pointer picture (<see cref="PointerImage"/>), drawn at the Pointer position.</summary>
    PointerShape = 4,

    /// <summary>Sender → receiver, on an audio stream: sample rate (int32) and channels (int16) of the PCM that follows.</summary>
    AudioFormat = 6,

    /// <summary>Sender → receiver, on an audio stream: 16-bit little-endian PCM, interleaved.</summary>
    Audio = 7,

    /// <summary>Receiver → sender: the next frame must be a key frame (decoder (re)started).</summary>
    KeyFrameRequest = 10,

    /// <summary>
    /// Receiver → sender: the frame with this time (int64) is on screen now. The sender measures the
    /// round trip from capture to display and back (the network part is a millisecond or two on a LAN).
    /// </summary>
    Shown = 11,

    /// <summary>Either way: the stream ends, with the reason (UTF-8 text).</summary>
    Stop = 20,
}

/// <summary>The stream's picture as the sender produces it.</summary>
public sealed record VideoFormat(int Width, int Height, int Fps, int BitrateMbps, string Gpu, string Encoder);

/// <summary>
/// A video connection: after the same authentication as the control channel, binary packets
/// (4-byte length, AES-GCM ciphertext and tag, a per-direction counter as nonce). No JSON and no base64,
/// so a 1080p60 stream costs next to nothing to frame and encrypt.
/// </summary>
public sealed class VideoChannel : IVideoLink
{
    /// <summary>Largest packet accepted (a key frame of a 4K picture at a high bitrate fits easily).</summary>
    public const int MaxPacket = 16 * 1024 * 1024;

    private const int TagBytes = 16;

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly AesGcm _send, _receive;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ulong _sendCounter, _receiveCounter;
    private readonly byte[] _header = new byte[4];

    /// <summary>
    /// Bytes already received and not read yet: more than nothing right after reading a frame means the next
    /// one is already here, i.e. the reader is behind.
    /// </summary>
    public int Pending
    {
        get
        {
            try
            {
                return _tcp.Available;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                return 0;
            }
        }
    }

    /// <summary>The other machine's id.</summary>
    public string RemoteMachine { get; }

    /// <summary>Which stream of the plan this connection is for.</summary>
    public string StreamKey { get; }

    public VideoChannel(TcpClient tcp, Stream stream, string remoteMachine, string streamKey, byte[] sendKey, byte[] receiveKey)
    {
        _tcp = tcp;
        _stream = stream;
        RemoteMachine = remoteMachine;
        StreamKey = streamKey;
        _send = new AesGcm(sendKey, TagBytes);
        _receive = new AesGcm(receiveKey, TagBytes);
        tcp.NoDelay = true;
        // Small socket buffers: when the network cannot keep up, frames wait in the sender's own queue (where
        // they are dropped and a key frame follows) instead of piling up unseen in the socket (a growing delay).
        tcp.SendBufferSize = 128 * 1024;
        tcp.ReceiveBufferSize = 256 * 1024;
    }

    private static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
    }

    /// <summary>Sends one packet: <paramref name="header"/> then <paramref name="data"/> as its body.</summary>
    public async Task SendAsync(VideoPacket type, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        int length = 1 + header.Length + data.Length;
        if (length > MaxPacket)
            throw new InvalidDataException($"Video packet too large ({length} bytes).");
        var packet = new byte[4 + length + TagBytes];
        BinaryPrimitives.WriteInt32LittleEndian(packet, length);
        var plain = new byte[length];
        plain[0] = (byte)type;
        header.Span.CopyTo(plain.AsSpan(1));
        data.Span.CopyTo(plain.AsSpan(1 + header.Length));

        await _sendLock.WaitAsync(ct);
        try
        {
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _sendCounter++);
            _send.Encrypt(nonce, plain, packet.AsSpan(4, length), packet.AsSpan(4 + length, TagBytes));
            await _stream.WriteAsync(packet, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendAsync(VideoPacket type, CancellationToken ct = default) => SendAsync(type, default, default, ct);

    /// <summary>The next packet, or null when the other side closed the connection.</summary>
    public async Task<(VideoPacket Type, byte[] Body)?> ReceiveAsync(CancellationToken ct = default)
    {
        if (!await ReadExactlyOrEndAsync(_header, ct))
            return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(_header);
        if (length < 1 || length > MaxPacket)
            throw new InvalidDataException($"Invalid video packet length {length}.");
        var cipher = new byte[length + TagBytes];
        if (!await ReadExactlyOrEndAsync(cipher, ct))
            return null;
        var plain = new byte[length];
        var nonce = new byte[12];
        Nonce(nonce, _receiveCounter++);
        try
        {
            _receive.Decrypt(nonce, cipher.AsSpan(0, length), cipher.AsSpan(length, TagBytes), plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("A video packet failed authentication.");
        }
        return ((VideoPacket)plain[0], plain[1..]);
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("The video connection closed in the middle of a packet.");
            }
            read += n;
        }
        return true;
    }

    // Packet bodies.

    public static byte[] FrameHeader(long time, bool keyFrame)
    {
        var h = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(h, time);
        h[8] = keyFrame ? (byte)1 : (byte)0;
        return h;
    }

    public static (long Time, bool KeyFrame, ReadOnlyMemory<byte> Data) ParseFrame(byte[] body) =>
        (BinaryPrimitives.ReadInt64LittleEndian(body), (body[8] & 1) != 0, body.AsMemory(9));

    public static byte[] PointerBody(int x, int y, bool visible)
    {
        var b = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(b, x);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), y);
        b[8] = visible ? (byte)1 : (byte)0;
        return b;
    }

    public static (int X, int Y, bool Visible) ParsePointer(byte[] body) =>
        (BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)), body[8] != 0);

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        _send.Dispose();
        _receive.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
