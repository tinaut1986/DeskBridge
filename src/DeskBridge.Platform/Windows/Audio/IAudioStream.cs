using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows.Audio;

/// <summary>An audio stream over a connection, whichever way it goes.</summary>
[SupportedOSPlatform("windows")]
public interface IAudioStream
{
    /// <summary>Ends when the stream stops (either side, or an error: <see cref="Failure"/>).</summary>
    Task Completion { get; }

    string? Failure { get; }

    /// <summary>Why the other side stopped the stream, if it said so.</summary>
    string? StopReason { get; }

    /// <summary>Stops it; <paramref name="reason"/> tells the other side why.</summary>
    ValueTask DisposeAsync(string reason);
}
