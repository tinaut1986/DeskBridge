using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBridge.Core.Configuration;

namespace DeskBridge.Core.Control;

/// <summary>
/// One JSON message per line over a stream, with a size limit so a peer cannot exhaust memory.
/// After <see cref="Encrypt"/>, each line is base64(AES-GCM(json)) with a per-direction counter as
/// nonce: a reordered, replayed or modified message fails to decrypt and closes the connection.
/// </summary>
internal sealed class LineChannel(Stream stream)
{
    /// <summary>Before authentication only small messages are accepted.</summary>
    public const int HandshakeLimit = 64 * 1024;

    /// <summary>After it, room for a clipboard image or a file chunk.</summary>
    public const int SessionLimit = 64 * 1024 * 1024;

    private const byte NewLine = 0x0A;
    private const int TagBytes = 16;

    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private int _maxLineBytes = HandshakeLimit;
    private AesGcm? _send, _receive;
    private ulong _sendCounter, _receiveCounter;

    public void Encrypt(byte[] sendKey, byte[] receiveKey)
    {
        _send = new AesGcm(sendKey, TagBytes);
        _receive = new AesGcm(receiveKey, TagBytes);
        _maxLineBytes = SessionLimit;
    }

    public async Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ConfigLoader.JsonOptions);
        byte[] line;
        if (_send is null)
        {
            line = json;
        }
        else
        {
            var sealedBytes = new byte[json.Length + TagBytes];
            _send.Encrypt(Nonce(_sendCounter++), json, sealedBytes.AsSpan(0, json.Length), sealedBytes.AsSpan(json.Length));
            line = Encoding.ASCII.GetBytes(Convert.ToBase64String(sealedBytes));
        }
        await stream.WriteAsync(line, ct);
        stream.WriteByte(NewLine);
        await stream.FlushAsync(ct);
    }

    /// <returns>The next message, or null if the peer closed the connection cleanly.</returns>
    public async Task<ControlMessage?> ReceiveAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        if (line is null)
            return null;

        byte[] json;
        if (_receive is null)
        {
            json = line;
        }
        else
        {
            byte[] sealedBytes;
            try
            {
                sealedBytes = Convert.FromBase64String(Encoding.ASCII.GetString(line));
            }
            catch (FormatException)
            {
                throw new InvalidDataException("Malformed encrypted message.");
            }
            if (sealedBytes.Length < TagBytes)
                throw new InvalidDataException("Malformed encrypted message.");
            json = new byte[sealedBytes.Length - TagBytes];
            try
            {
                _receive.Decrypt(Nonce(_receiveCounter++), sealedBytes.AsSpan(0, json.Length), sealedBytes.AsSpan(json.Length), json);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException("A message failed authentication (altered, replayed or wrong key).");
            }
        }
        return JsonSerializer.Deserialize<ControlMessage>(json, ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty message.");
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
        return nonce;
    }

    private async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            int index = Array.IndexOf(_buffer, NewLine, _start, _end - _start);
            if (index >= 0)
            {
                line.Write(_buffer, _start, index - _start);
                _start = index + 1;
                return line.ToArray();
            }

            line.Write(_buffer, _start, _end - _start);
            _start = _end = 0;
            if (line.Length > _maxLineBytes)
                throw new InvalidDataException("Message too large.");

            int read = await stream.ReadAsync(_buffer, ct);
            if (read == 0)
                return line.Length == 0 ? null : throw new EndOfStreamException("Connection closed in the middle of a message.");
            _end = read;
        }
    }
}
