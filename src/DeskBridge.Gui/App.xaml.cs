using System.Windows;
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

        var window = new MainWindow(options, settings);
        MainWindow = window;
        window.Show();
    }
}
