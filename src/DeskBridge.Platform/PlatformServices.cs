using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Model;
using DeskBridge.Platform.Linux;
using DeskBridge.Platform.Windows;

namespace DeskBridge.Platform;

/// <summary>The implementation of each OS-specific part for the OS the app runs on.</summary>
public static class PlatformServices
{
    public static IDisplayDetector DisplayDetector()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDisplayDetector();
        if (OperatingSystem.IsLinux())
            return new KScreenDisplayDetector();
        return new NoDisplays();
    }

    public static IAutostart Autostart()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsAutostart();
        if (OperatingSystem.IsLinux())
            return new XdgAutostart();
        return new NoAutostart();
    }

    private sealed class NoDisplays : IDisplayDetector
    {
        public IReadOnlyList<DisplayConfig> DetectDisplays() => [];
    }

    private sealed class NoAutostart : IAutostart
    {
        public bool IsEnabled(string executable) => false;
        public void Set(string executable, bool enabled) => throw new PlatformNotSupportedException();
    }
}

/// <summary>Starting the app (minimized) when the user logs in.</summary>
public interface IAutostart
{
    bool IsEnabled(string executable);

    void Set(string executable, bool enabled);
}
