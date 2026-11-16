using System.Security.Cryptography;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Agent;

/// <summary>What any machine can do with the others (not only the coordinator): clipboard and files.</summary>
public static class Peers
{
    public const int ChunkBytes = 1 << 20;

    public static (string Host, int Port) Endpoint(DeskBridgeConfig config, LocalSettings local, MachineConfig machine) =>
        (machine.Id == local.Machine ? "127.0.0.1" : machine.Address, machine.ControlPort ?? config.ControlPort);

    /// <summary>
    /// A session with a machine: the one already open in <paramref name="directory"/> (e.g. the machine
    /// connected to us because it cannot accept connections), or a new one to its address.
    /// </summary>
    public static async Task<PeerLink> ConnectAsync(
        DeskBridgeConfig config, LocalSettings local, string machineId, PeerDirectory? directory = null, CancellationToken ct = default)
    {
        if (directory?.Find(machineId) is { } open)
            return new PeerLink(open, owned: false);

        var machine = config.Machines.FirstOrDefault(m => m.Id == machineId)
            ?? throw new ControlException(L.T("Machine '{machine}' is not in the configuration.", ("machine", machineId)));
        var (host, port) = Endpoint(config, local, machine);
        var session = await ControlClient.OpenAsync(host, port, local.Machine, machine.Id, ControlAuth.DecodeKey(local.SharedKey), ct: ct);
        return new PeerLink(session, owned: true);
    }

    /// <summary>Sends what was copied here to every other machine, in parallel; each one decides whether to take it.</summary>
    public static async Task<IReadOnlyList<MachineResult>> SendClipboardAsync(
        DeskBridgeConfig config, LocalSettings local, ClipboardMessage clipboard, PeerDirectory? directory = null, CancellationToken ct = default)
    {
        return await Task.WhenAll(config.Machines.Where(m => m.Id != local.Machine).Select(async machine =>
        {
            try
            {
                await using var link = await ConnectAsync(config, local, machine.Id, directory, ct);
                return await link.RequestAsync(clipboard, ct) switch
                {
                    OkMessage => new MachineResult(machine.Id, true, "", []),
                    ErrorMessage e => new MachineResult(machine.Id, false, e.Message, []),
                    var other => new MachineResult(machine.Id, false, L.T("unexpected reply {reply}", ("reply", other.GetType().Name)), []),
                };
            }
            catch (ControlException ex)
            {
                return new MachineResult(machine.Id, false, ex.Message, []);
            }
        }));
    }

    /// <summary>Sends files to one machine over a single session.</summary>
    /// <returns>Where each file was saved on the other machine.</returns>
    public static async Task<IReadOnlyList<string>> SendFilesAsync(
        DeskBridgeConfig config,
        LocalSettings local,
        string machineId,
        IReadOnlyList<string> paths,
        IProgress<(string File, long Done, long Total)>? progress = null,
        PeerDirectory? directory = null,
        CancellationToken ct = default)
    {
        await using var link = await ConnectAsync(config, local, machineId, directory, ct);
        var saved = new List<string>();
        long total = paths.Sum(p => new FileInfo(p).Length), done = 0;
        var buffer = new byte[ChunkBytes];

        foreach (var path in paths)
        {
            var transfer = Guid.NewGuid();
            var name = Path.GetFileName(path);
            await using var file = File.OpenRead(path);
            Expect<OkMessage>(await link.RequestAsync(new FileStartMessage(transfer, name, file.Length), ct));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            int read;
            while ((read = await file.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                Expect<OkMessage>(await link.RequestAsync(new FileChunkMessage(transfer, offset, Convert.ToBase64String(buffer, 0, read)), ct));
                offset += read;
                done += read;
                progress?.Report((name, done, total));
            }
            var end = Expect<FileSavedMessage>(await link.RequestAsync(new FileEndMessage(transfer, Convert.ToHexStringLower(hash.GetHashAndReset())), ct));
            saved.Add(end.Path);
        }
        return saved;
    }

    private static T Expect<T>(ControlMessage reply) where T : ControlMessage => reply switch
    {
        T ok => ok,
        ErrorMessage e => throw new ControlException(e.Message),
        _ => throw new ControlException(L.T("unexpected reply {reply}", ("reply", reply.GetType().Name))),
    };
}
