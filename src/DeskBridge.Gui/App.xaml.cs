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
        DeskBridge.Platform.Windows.Priority.RaiseProcess(); // keyboard, mouse and video stay smooth on a busy PC
        DeskBridge.Platform.Windows.Input.CursorHider.Show(); // in case the app ended while the pointer was hidden
        DeskBridge.Platform.Windows.Audio.MutedDevices.RestoreLeftOver(); // and with this PC's output muted
        DeskBridge.Platform.Windows.Audio.DefaultMicrophone.Restore(null); // or VB-CABLE as its microphone

        // A crash leaves its story behind (crash.txt in the log folder), shown in the log at the next start, so it
        // can be read remotely; and the pointer is never left hidden.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => RecordCrash(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) => RecordCrash(args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Write("[error] unobserved task exception: " + args.Exception);
            args.SetObserved();
        };
        var options = StartupOptions.Parse(e.Args);

        // Settings, Apps, Uninstall.
        if (e.Args.Contains("--uninstall"))
        {
            Installation.Uninstall();
            Shutdown();
            return;
        }
        // A published build started from a download: it runs from its install folder.
        if (options.SnapshotDir is null && Installation.MoveIntoPlace(e.Args))
        {
            Shutdown();
            return;
        }

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
        if (options.SnapshotDir is null)
            _ = Task.Run(Installation.Register); // the Start menu shortcut and the Apps entry, with this version

        if (options.Minimized && options.SnapshotDir is null)
            new WindowInteropHelper(window).EnsureHandle(); // started with Windows: agent and clipboard run, window stays hidden
        else
            window.Show();
    }

    private static void RecordCrash(Exception? ex)
    {
        try
        {
            DeskBridge.Platform.Windows.Input.CursorHider.Show();
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} DeskBridge {AppInfo.Version} ended with an error:\n{ex}";
            System.IO.Directory.CreateDirectory(FileLog.Directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(FileLog.Directory, "crash.txt"), text);
            FileLog.Write(text);
        }
        catch
        {
            // nothing more can be done while crashing
        }
    }

    /// <summary>The story of the last crash, if any (shown once in the log, then removed).</summary>
    public static string? TakeLastCrash()
    {
        var path = System.IO.Path.Combine(FileLog.Directory, "crash.txt");
        try
        {
            if (!System.IO.File.Exists(path))
                return null;
            var text = System.IO.File.ReadAllText(path);
            System.IO.File.Delete(path);
            return text;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
