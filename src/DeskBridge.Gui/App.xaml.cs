using System.Windows;
using System.Windows.Interop;
using DeskBridge.Core.Localization;
using DeskBridge.Gui.Updates;

namespace DeskBridge.Gui;

public partial class App : Application
{
    private SingleInstance? _instance;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        Updater.CleanUpAfterUpdate(e.Args);
        DeskBridge.Platform.Windows.Input.CursorHider.Show(); // in case the app ended while the pointer was hidden
        var options = StartupOptions.Parse(e.Args);

        // Snapshots (development) may run next to a real instance; everything else is one per session.
        if (options.SnapshotDir is null)
        {
            _instance = SingleInstance.Acquire(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()));
            if (_instance is null)
            {
                Shutdown();
                return;
            }
        }

        var settings = UserSettings.Load(options.SettingsPath);
        L.SetLanguage(L.Resolve(options.Language ?? settings.Language, L.SystemLocales()));
        ThemeManager.Apply(options.Theme ?? settings.Theme);

        // The app lives in the notification area: closing the window does not end it, Quit does.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow(options, settings);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();

        if (options.Minimized && options.SnapshotDir is null)
            new WindowInteropHelper(window).EnsureHandle(); // started with Windows: agent and clipboard run, window stays hidden
        else
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
