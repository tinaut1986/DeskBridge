using DeskBridge.Core.Model;

namespace DeskBridge.Core.Abstractions;

/// <summary>Creates and removes virtual monitors on the local machine (Virtual Display Driver).</summary>
public interface IVirtualDisplayDriver
{
    /// <summary>Makes the set of DeskBridge virtual monitors exactly <paramref name="displays"/>.</summary>
    Task SetDisplaysAsync(IReadOnlyList<VirtualDisplaySpec> displays, CancellationToken ct = default);
}

/// <summary>Captures and encodes a local virtual monitor (Sunshine, or our own encoder later).</summary>
public interface IStreamSender
{
    Task StartAsync(StreamSpec stream, CancellationToken ct = default);
    Task StopAllAsync(CancellationToken ct = default);
}

/// <summary>Shows a remote stream full screen on a local physical monitor (Moonlight, or our own player later).</summary>
public interface IStreamReceiver
{
    Task StartAsync(StreamSpec stream, string sourceAddress, CancellationToken ct = default);
    Task StopAllAsync(CancellationToken ct = default);
}

/// <summary>Decides which machine receives keyboard and mouse depending on where the cursor is.</summary>
public interface IInputRouter
{
    Task ApplyRoutesAsync(IReadOnlyList<InputRoute> routes, IReadOnlyDictionary<string, string> machineAddresses, CancellationToken ct = default);
}
