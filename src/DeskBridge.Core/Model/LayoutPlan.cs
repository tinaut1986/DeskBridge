namespace DeskBridge.Core.Model;

/// <summary>
/// A virtual monitor that <paramref name="Machine"/> must create, placed at (<paramref name="X"/>, <paramref name="Y"/>)
/// in that machine's desktop coordinates. It stands for physical display <paramref name="ForDisplay"/> of
/// <paramref name="ForMachine"/>, which shows it.
/// </summary>
public sealed record VirtualDisplaySpec(
    string Key, string Machine, int Width, int Height, int RefreshHz,
    int X = 0, int Y = 0, string ForMachine = "", string ForDisplay = "");

/// <summary>
/// One video stream: <paramref name="SourceMachine"/> captures its virtual display
/// <paramref name="SourceVirtualDisplay"/> and <paramref name="SinkMachine"/> shows it full screen
/// on its physical display <paramref name="SinkDisplay"/>.
/// </summary>
public sealed record StreamSpec(
    string Key,
    string SourceMachine,
    string SourceVirtualDisplay,
    string SinkMachine,
    string SinkDisplay,
    int Width,
    int Height,
    int RefreshHz,
    int BitrateMbps);

/// <summary>When the coordinator's cursor is on <paramref name="Display"/>, input goes to <paramref name="ToMachine"/>.</summary>
public sealed record InputRoute(string Display, string ToMachine);

/// <summary>Everything every agent has to do to put a profile in place.</summary>
public sealed record LayoutPlan(
    string Profile,
    string Coordinator,
    IReadOnlyList<VirtualDisplaySpec> VirtualDisplays,
    IReadOnlyList<StreamSpec> Streams,
    IReadOnlyList<InputRoute> InputRoutes,
    IReadOnlyList<string> Warnings)
{
    public IEnumerable<VirtualDisplaySpec> VirtualDisplaysOf(string machine) =>
        VirtualDisplays.Where(v => v.Machine == machine);

    public IEnumerable<StreamSpec> StreamsFrom(string machine) =>
        Streams.Where(s => s.SourceMachine == machine);

    public IEnumerable<StreamSpec> StreamsTo(string machine) =>
        Streams.Where(s => s.SinkMachine == machine);
}
