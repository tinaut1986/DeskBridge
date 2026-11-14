using System.Windows;
using System.Windows.Interop;
using DeskBridge.Core.Localization;
using DeskBridge.Gui.Updates;

namespace DeskBridge.Gui;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        Updater.CleanUpAfterUpdate(e.Args);

        var options = StartupOptions.Parse(e.Args);
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
}
