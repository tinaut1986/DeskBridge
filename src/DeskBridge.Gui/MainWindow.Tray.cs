using System.Windows;

namespace DeskBridge.Gui;

/// <summary>Notification area icon (filled in with the tray step).</summary>
public partial class MainWindow
{
    /// <summary>Tells the user about something that happened while the window may be hidden.</summary>
    private void Notify(string text)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
            FlashTaskbar();
    }

    private void FlashTaskbar() => TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();
}
