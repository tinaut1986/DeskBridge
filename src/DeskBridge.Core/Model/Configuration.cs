namespace DeskBridge.Core.Model;

/// <summary>A physical monitor attached to a machine.</summary>
/// <param name="Id">Identifier unique within its machine, e.g. "1", "2", "3".</param>
/// <param name="X">Left edge in the machine's desktop coordinates (as arranged in Windows display settings).</param>
/// <param name="Y">Top edge in the machine's desktop coordinates.</param>
public sealed record DisplayConfig(string Id, int Width, int Height, int RefreshHz = 60, int X = 0, int Y = 0);

/// <summary>A computer taking part in DeskBridge.</summary>
/// <param name="Id">Stable identifier, e.g. "desktop".</param>
/// <param name="Address">Host name or IP the coordinator uses to reach its agent.</param>
/// <param name="HasInput">True for the machine whose keyboard and mouse drive everything (the coordinator).</param>
/// <param name="ControlPort">Overrides <see cref="DeskBridgeConfig.ControlPort"/> for this machine (e.g. two agents on one PC for testing).</param>
public sealed record MachineConfig(
    string Id,
    string Name,
    string Address,
    IReadOnlyList<DisplayConfig> Displays,
    bool HasInput = false,
    int? ControlPort = null);

/// <summary>
/// How a lent display is streamed. Every value is optional: by default the virtual display
/// copies the physical one it is shown on and the bitrate is derived from the resolution.
/// </summary>
public sealed record StreamSettings(int? Width = null, int? Height = null, int? RefreshHz = null, int? BitrateMbps = null)
{
    public static readonly StreamSettings Default = new();
}

/// <summary>Where a virtual display goes in the borrowing machine's desktop, when placed by hand.</summary>
public sealed record VirtualPosition(int X, int Y);

/// <summary>"Physical display <paramref name="Display"/> of <paramref name="Machine"/> shows a screen of <paramref name="UsedBy"/>".</summary>
/// <param name="Position">Hand-placed position of the virtual display in <paramref name="UsedBy"/>'s desktop; null = automatic.</param>
public sealed record DisplayAssignment(
    string Machine, string Display, string UsedBy, StreamSettings? Stream = null, VirtualPosition? Position = null);

/// <summary>Where a machine's borrowed (virtual) displays go, relative to the displays it keeps.</summary>
public enum PlacementSide { Right, Left, Above, Below }

/// <summary>A named layout. Displays not listed stay with their own machine.</summary>
/// <param name="Placement">
/// Per borrowing machine, where its virtual displays go relative to its own displays
/// (default: to the right). Displays lent by the same machine keep their arrangement there.
/// </param>
public sealed record ProfileConfig(
    string Name,
    IReadOnlyList<DisplayAssignment> Assignments,
    IReadOnlyDictionary<string, PlacementSide>? Placement = null);

public sealed record DeskBridgeConfig(
    IReadOnlyList<MachineConfig> Machines,
    IReadOnlyList<ProfileConfig> Profiles,
    int ControlPort = 47900);
