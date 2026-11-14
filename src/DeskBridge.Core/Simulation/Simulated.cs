using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Simulation;

/// <summary>Stand-ins that only log what the real implementations would do.</summary>
public static class Simulated
{
    public static PlanApplier Applier(Action<string> log) =>
        new(new VirtualDisplays(log), new Sender(log), new Receiver(log), new InputRouter(log));

    private sealed class VirtualDisplays(Action<string> log) : IVirtualDisplayDriver
    {
        public Task SetDisplaysAsync(IReadOnlyList<VirtualDisplaySpec> displays, CancellationToken ct = default)
        {
            log($"[virtual displays] keep {displays.Count}"
                + string.Concat(displays.Select(d => $"{Environment.NewLine}    {d.Key}  {d.Width}x{d.Height}@{d.RefreshHz}")));
            return Task.CompletedTask;
        }
    }

    private sealed class Sender(Action<string> log) : IStreamSender
    {
        public Task StartAsync(StreamSpec s, CancellationToken ct = default)
        {
            log($"[send] {s.SourceVirtualDisplay} {s.Width}x{s.Height}@{s.RefreshHz} {s.BitrateMbps} Mbps -> {s.SinkMachine} display {s.SinkDisplay}");
            return Task.CompletedTask;
        }

        public Task StopAllAsync(CancellationToken ct = default)
        {
            log("[send] stop all");
            return Task.CompletedTask;
        }
    }

    private sealed class Receiver(Action<string> log) : IStreamReceiver
    {
        public Task StartAsync(StreamSpec s, string sourceAddress, CancellationToken ct = default)
        {
            log($"[receive] {s.SourceVirtualDisplay} from {sourceAddress} -> display {s.SinkDisplay}, full screen");
            return Task.CompletedTask;
        }

        public Task StopAllAsync(CancellationToken ct = default)
        {
            log("[receive] stop all");
            return Task.CompletedTask;
        }
    }

    private sealed class InputRouter(Action<string> log) : IInputRouter
    {
        public Task ApplyRoutesAsync(IReadOnlyList<InputRoute> routes, IReadOnlyDictionary<string, string> machineAddresses, CancellationToken ct = default)
        {
            foreach (var r in routes)
                log($"[input] cursor on display {r.Display} -> keyboard/mouse to '{r.ToMachine}'");
            return Task.CompletedTask;
        }
    }
}
