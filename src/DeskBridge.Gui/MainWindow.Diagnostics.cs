using System.IO;
using System.Text.Json;
using System.Windows;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Gui.Updates;

namespace DeskBridge.Gui;

/// <summary>
/// Troubleshooting a machine from another one: each machine answers with its version, settings (never the
/// shared key), configuration and recent log; whoever asks saves it as diagnostics-&lt;machine&gt;.json in
/// its own log folder. Collected automatically whenever a machine connects, or from Help.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<ControlSession> _diagnosed = [];

    /// <summary>This machine's state (called on a thread-pool thread).</summary>
    private DiagnosticsMessage OwnDiagnostics() => Dispatcher.Invoke(() => new DiagnosticsMessage(
        _vm.LocalMachine,
        AppInfo.Version + (AppInfo.Channel is { } c ? $" ({c})" : " (local build)"),
        string.IsNullOrWhiteSpace(_vm.LocalConnectTo) ? null : _vm.LocalConnectTo.Trim(),
        int.TryParse(_vm.LocalPort, out var port) ? port : 0,
        IsCoordinatorHere(),
        _vm.Model.ToConfig([]),
        _vm.Log.TakeLast(300).ToList()));

    /// <summary>On the UI thread: asks every newly connected machine for its diagnostics.</summary>
    private void CollectNewDiagnostics()
    {
        if (_agent is null)
            return;
        _diagnosed.RemoveWhere(s => !s.IsOpen);
        foreach (var id in _agent.Directory.Connected)
        {
            if (id != _vm.LocalMachine.Trim() && _agent.Directory.Find(id) is { } session && _diagnosed.Add(session))
                _ = SaveDiagnosticsAsync(session);
        }
    }

    private async Task<string?> SaveDiagnosticsAsync(ControlSession session)
    {
        try
        {
            if (await session.RequestAsync(new GetDiagnosticsMessage()) is not DiagnosticsMessage diagnostics)
                return null; // an older version: it does not know the request
            return WriteDiagnostics(diagnostics);
        }
        catch (ControlException)
        {
            return null;
        }
    }

    private static string WriteDiagnostics(DiagnosticsMessage diagnostics)
    {
        Directory.CreateDirectory(FileLog.Directory);
        var path = Path.Combine(FileLog.Directory, $"diagnostics-{FileReceiverSafe(diagnostics.Machine)}.json");
        var json = JsonSerializer.Serialize(new { collected = DateTime.Now, diagnostics },
            new JsonSerializerOptions(ConfigLoader.JsonOptions) { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private static string FileReceiverSafe(string name) => Core.Transfer.FileReceiver.SafeName(name);

    private async void CollectDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var saved = new List<string> { WriteDiagnostics(OwnDiagnostics()) };
        if (_agent is not null)
        {
            foreach (var id in _agent.Directory.Connected.Where(id => id != _vm.LocalMachine.Trim()))
            {
                if (_agent.Directory.Find(id) is { } session && await SaveDiagnosticsAsync(session) is { } path)
                    saved.Add(path);
            }
        }
        _vm.Status = L.T("Diagnostics of {count} machines saved in the log folder.", ("count", saved.Count));
        AddLog(_vm.Status);
    }
}
