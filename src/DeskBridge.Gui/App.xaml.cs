using System.Windows;

namespace DeskBridge.Gui;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow(StartupOptions.Parse(e.Args));
        MainWindow = window;
        window.Show();
    }
}
