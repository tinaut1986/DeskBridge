using System.Text;
using System.Text.Json;
using DeskBridge.Core.Configuration;

namespace DeskBridge.Core.Control;

/// <summary>One JSON message per line over a stream, with a size limit so a peer cannot exhaust memory.</summary>
internal sealed class LineChannel(Stream stream, int maxLineBytes = 1 << 20)
{
    private const byte NewLine = 0x0A;
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    public async Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ConfigLoader.JsonOptions);
        await stream.WriteAsync(bytes, ct);
        stream.WriteByte(NewLine);
        await stream.FlushAsync(ct);
    }

    /// <returns>The next message, or null if the peer closed the connection cleanly.</returns>
    public async Task<ControlMessage?> ReceiveAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        if (line is null)
            return null;
        return JsonSerializer.Deserialize<ControlMessage>(line, ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty message.");
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            int index = Array.IndexOf(_buffer, NewLine, _start, _end - _start);
            if (index >= 0)
            {
                line.Write(_buffer, _start, index - _start);
                _start = index + 1;
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }

            line.Write(_buffer, _start, _end - _start);
            _start = _end = 0;
            if (line.Length > maxLineBytes)
                throw new InvalidDataException("Message too large.");

            int read = await stream.ReadAsync(_buffer, ct);
            if (read == 0)
                return line.Length == 0 ? null : throw new EndOfStreamException("Connection closed in the middle of a message.");
            _end = read;
        }
    }
}
