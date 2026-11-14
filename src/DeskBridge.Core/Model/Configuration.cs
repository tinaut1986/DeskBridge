namespace DeskBridge.Core.Model;

/// <summary>A physical monitor attached to a machine.</summary>
/// <param name="Id">Identifier unique within its machine, e.g. "1", "2", "3".</param>
public sealed record DisplayConfig(string Id, int Width, int Height, int RefreshHz = 60);

/// <summary>A computer taking part in DeskBridge.</summary>
/// <param name="Id">Stable identifier, e.g. "sobremesa".</param>
/// <param name="Address">Host name or IP the other agents use to reach it.</param>
/// <param name="HasInput">True for the machine whose keyboard and mouse drive everything (the coordinator).</param>
public sealed record MachineConfig(
    string Id,
    string Name,
    string Address,
    IReadOnlyList<DisplayConfig> Displays,
    bool HasInput = false);

/// <summary>"Physical display <paramref name="Display"/> of <paramref name="Machine"/> shows a screen of <paramref name="UsedBy"/>".</summary>
public sealed record DisplayAssignment(string Machine, string Display, string UsedBy);

/// <summary>A named layout. Displays not listed stay with their own machine.</summary>
public sealed record ProfileConfig(string Name, IReadOnlyList<DisplayAssignment> Assignments);

public sealed record DeskBridgeConfig(
    IReadOnlyList<MachineConfig> Machines,
    IReadOnlyList<ProfileConfig> Profiles,
    int ControlPort = 47900);
