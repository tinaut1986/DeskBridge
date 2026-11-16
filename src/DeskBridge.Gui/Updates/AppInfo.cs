using System.Reflection;
using DeskBridge.Core.Updates;

namespace DeskBridge.Gui.Updates;

/// <summary>Version and channel baked into the build by the release workflow.</summary>
public static class AppInfo
{
    private static readonly Assembly Entry = typeof(AppInfo).Assembly;

    /// <summary>X.Y.Z of this build (0.0.0 if it cannot be read).</summary>
    public static SemVersion Version { get; } =
        SemVersion.TryParse(Entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new(0, 0, 0);

    /// <summary>"release" or "beta" in published builds; null in local builds (which never update themselves).</summary>
    public static string? Channel { get; } =
        Entry.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DeskBridgeChannel")?.Value is { Length: > 0 } c ? c : null;

    public static bool IsReleaseBuild => Channel is not null;

    public static string DisplayVersion => Channel == "beta" ? $"{Version} beta" : Version.ToString();
}
