using System.Collections.Concurrent;
using System.Security.Cryptography;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Transfer;

/// <summary>
/// Receives files sent by other machines into a folder: written to a hidden .part file, checked
/// against the sender's SHA-256 and only then given its (sanitised, never overwriting) name.
/// </summary>
/// <param name="folder">Where files go; null while this machine does not accept files.</param>
public sealed class FileReceiver(Func<string?> folder)
{
    private sealed class Incoming(string from, string name, long size, string partPath, FileStream stream)
    {
        public string From { get; } = from;
        public string Name { get; } = name;
        public long Size { get; } = size;
        public string PartPath { get; } = partPath;
        public FileStream Stream { get; } = stream;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Written { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Incoming> _incoming = new();

    /// <summary>Raised after a file is saved: (from machine, full path).</summary>
    public event Action<string, string>? FileReceived;

    public ControlMessage Handle(string from, ControlMessage message) => message switch
    {
        FileStartMessage start => Start(from, start),
        FileChunkMessage chunk => Chunk(from, chunk),
        FileEndMessage end => End(from, end),
        _ => new ErrorMessage(L.T("Unexpected request: {request}.", ("request", message.GetType().Name))),
    };

    private ControlMessage Start(string from, FileStartMessage m)
    {
        var dir = folder();
        if (dir is null)
            return new ErrorMessage(L.T("This machine does not accept files from other machines. It can be enabled in its This PC tab."));
        if (m.Size < 0)
            return new ErrorMessage(L.T("Invalid file size."));

        Directory.CreateDirectory(dir);
        var name = SafeName(m.Name);
        var part = Path.Combine(dir, $".{m.Transfer:N}.part");
        var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
        File.SetAttributes(part, FileAttributes.Hidden);
        if (!_incoming.TryAdd(m.Transfer, new Incoming(from, name, m.Size, part, stream)))
        {
            stream.Dispose();
            return new ErrorMessage(L.T("Invalid file transfer."));
        }
        return new OkMessage();
    }

    private ControlMessage Chunk(string from, FileChunkMessage m)
    {
        if (!_incoming.TryGetValue(m.Transfer, out var t) || t.From != from)
            return new ErrorMessage(L.T("Invalid file transfer."));
        byte[] data;
        try
        {
            data = Convert.FromBase64String(m.DataBase64);
        }
        catch (FormatException)
        {
            return Abort(m.Transfer, L.T("Invalid file transfer."));
        }
        if (m.Offset != t.Written || t.Written + data.Length > t.Size)
            return Abort(m.Transfer, L.T("Invalid file transfer."));

        t.Stream.Write(data);
        t.Hash.AppendData(data);
        t.Written += data.Length;
        return new OkMessage();
    }

    private ControlMessage End(string from, FileEndMessage m)
    {
        if (!_incoming.TryGetValue(m.Transfer, out var t) || t.From != from)
            return new ErrorMessage(L.T("Invalid file transfer."));
        if (t.Written != t.Size)
            return Abort(m.Transfer, L.T("The file arrived incomplete."));
        var hash = Convert.ToHexStringLower(t.Hash.GetHashAndReset());
        if (!string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
            return Abort(m.Transfer, L.T("The file arrived damaged (checksum mismatch)."));

        _incoming.TryRemove(m.Transfer, out _);
        t.Stream.Dispose();
        t.Hash.Dispose();
        var target = UniquePath(Path.GetDirectoryName(t.PartPath)!, t.Name);
        File.SetAttributes(t.PartPath, FileAttributes.Normal);
        File.Move(t.PartPath, target);
        FileReceived?.Invoke(from, target);
        return new FileSavedMessage(target);
    }

    private ErrorMessage Abort(Guid transfer, string error)
    {
        if (_incoming.TryRemove(transfer, out var t))
        {
            t.Stream.Dispose();
            t.Hash.Dispose();
            try
            {
                File.Delete(t.PartPath);
            }
            catch (IOException)
            {
                // left behind as a hidden .part file
            }
        }
        return new ErrorMessage(error);
    }

    /// <summary>Drops any folder part and characters Windows does not allow in names.</summary>
    public static string SafeName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/').Split('/').LastOrDefault() ?? "");
        var invalid = Path.GetInvalidFileNameChars();
        file = new string(file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return file.Length == 0 || file.All(c => c == '.') ? "file" : file;
    }

    /// <summary>"name.ext", or "name (2).ext", "name (3).ext"... if it already exists.</summary>
    public static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }
}
