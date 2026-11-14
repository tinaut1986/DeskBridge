using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Model;

namespace DeskBridge.App;

/// <summary>Stand-ins that only print what the real implementations would do.</summary>
internal sealed class DryRunVirtualDisplays : IVirtualDisplayDriver
{
    public Task SetDisplaysAsync(IReadOnlyList<VirtualDisplaySpec> displays, CancellationToken ct = default)
    {
        Console.WriteLine($"[pantallas virtuales] dejar {displays.Count}:");
        foreach (var d in displays)
            Console.WriteLine($"    {d.Key}  {d.Width}x{d.Height}@{d.RefreshHz}");
        return Task.CompletedTask;
    }
}

internal sealed class DryRunSender : IStreamSender
{
    public Task StartAsync(StreamSpec s, CancellationToken ct = default)
    {
        Console.WriteLine($"[emitir] {s.SourceVirtualDisplay} -> {s.SinkMachine} (pantalla {s.SinkDisplay})");
        return Task.CompletedTask;
    }

    public Task StopAllAsync(CancellationToken ct = default)
    {
        Console.WriteLine("[emitir] parar todo");
        return Task.CompletedTask;
    }
}

internal sealed class DryRunReceiver : IStreamReceiver
{
    public Task StartAsync(StreamSpec s, string sourceAddress, CancellationToken ct = default)
    {
        Console.WriteLine($"[recibir] {s.SourceVirtualDisplay} desde {sourceAddress} -> pantalla {s.SinkDisplay} a pantalla completa");
        return Task.CompletedTask;
    }

    public Task StopAllAsync(CancellationToken ct = default)
    {
        Console.WriteLine("[recibir] parar todo");
        return Task.CompletedTask;
    }
}

internal sealed class DryRunInputRouter : IInputRouter
{
    public Task ApplyRoutesAsync(IReadOnlyList<InputRoute> routes, IReadOnlyDictionary<string, string> machineAddresses, CancellationToken ct = default)
    {
        foreach (var r in routes)
            Console.WriteLine($"[entrada] cursor en pantalla {r.Display} -> teclado/ratón a '{r.ToMachine}'");
        return Task.CompletedTask;
    }
}
