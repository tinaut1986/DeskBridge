namespace DeskBridge.Core.Model;

/// <summary>A physical monitor attached to a machine.</summary>
/// <param name="Id">Identifier unique within its machine, e.g. "1", "2", "3".</param>
/// <param name="X">Left edge in the machine's desktop coordinates (as arranged in Windows display settings).</param>
/// <param name="Y">Top edge in the machine's desktop coordinates.</param>
/// <param name="Scale">Windows' scale for it, in percent (a small high-resolution panel is often at 150 or 200).</param>
public sealed record DisplayConfig(string Id, int Width, int Height, int RefreshHz = 60, int X = 0, int Y = 0, int Scale = 100);

/// <summary>A computer taking part in DeskBridge.</summary>
/// <param name="Id">Stable identifier, e.g. "desktop".</param>
/// <param name="Address">Host name or IP the coordinator uses to reach its agent.</param>
/// <param name="HasInput">True for the machine whose keyboard and mouse drive everything (the coordinator).</param>
/// <param name="ControlPort">Overrides <see cref="DeskBridgeConfig.ControlPort"/> for this machine (e.g. two agents on one PC for testing).</param>
/// <param name="InputSide">
/// For keyboard and mouse sharing: on which side of the coordinator's displays this machine is (the cursor
/// moves to it past that edge). Null = to the right. Ignored on the coordinator itself.
/// </param>
/// <param name="InputPosition">
/// Older versions: where the top-left corner of this machine's displays went in the coordinator's desktop for
/// the mouse. Read once into <paramref name="DeskPositions"/>.
/// </param>
/// <param name="DeskPositions">
/// This machine's displays placed on the coordinator's desk (the Layout tab), by display id: the top-left corner
/// of each in the coordinator's desktop coordinates. A display on the desk is used from the coordinator like one
/// more of its monitors (the cursor moves onto it past the edge where they touch); one that is not stays apart.
/// </param>
/// <param name="DeskWidths">
/// Width chosen for a display on the desk (by display id), e.g. the same as the monitor it sits under so the
/// edges line up; without one it takes its visual size (its width at Windows' scale). The height follows.
/// </param>
public sealed record MachineConfig(
    string Id,
    string Name,
    string Address,
    IReadOnlyList<DisplayConfig> Displays,
    bool HasInput = false,
    int? ControlPort = null,
    PlacementSide? InputSide = null,
    VirtualPosition? InputPosition = null,
    IReadOnlyDictionary<string, VirtualPosition>? DeskPositions = null,
    IReadOnlyDictionary<string, int>? DeskWidths = null);

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
/// <param name="PrimaryDisplays">
/// Per machine, the display that is Windows' primary one there while the profile is applied: one of its own by
/// id, or "shows:N" for the virtual display it shows on the coordinator's display N. A machine not listed keeps
/// its own (the coordinator: or the nearest free one while its own shows another machine).
/// </param>
public sealed record ProfileConfig(
    string Name,
    IReadOnlyList<DisplayAssignment> Assignments,
    IReadOnlyDictionary<string, PlacementSide>? Placement = null,
    IReadOnlyDictionary<string, string>? PrimaryDisplays = null)
{
    /// <summary>The reference to a virtual display by the coordinator's display that shows it.</summary>
    public const string ShowsPrefix = "shows:";
}

public sealed record DeskBridgeConfig(
    IReadOnlyList<MachineConfig> Machines,
    IReadOnlyList<ProfileConfig> Profiles,
    int ControlPort = 47900);
