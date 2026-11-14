using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui.ViewModels;

public sealed record SummaryLine(string Text, Brush Brush);

/// <summary>
/// Window state. Deliberately not an <see cref="ObservableObject"/>: changes here (status, summary)
/// must not trigger a layout redraw, or redrawing would loop.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private EditorModel _model = new();
    private ProfileVm? _selectedProfile;
    private MachineVm? _selectedMachine;
    private IReadOnlyList<SummaryLine> _summary = [];
    private string _status = "", _agentStatus = "";
    private string _localMachine = "", _localKey = "", _localPort = "47900", _localConnectTo = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public EditorModel Model { get => _model; set => Set(ref _model, value); }

    public ProfileVm? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value))
                ObservableObject.NotifyAnyChanged();
        }
    }

    public MachineVm? SelectedMachine
    {
        get => _selectedMachine;
        set
        {
            if (Set(ref _selectedMachine, value))
                Raise(nameof(DisplaysTitle));
        }
    }

    public string DisplaysTitle => SelectedMachine is null
        ? L.T("Physical displays")
        : L.T("Physical displays of {machine}", ("machine", SelectedMachine));

    public IReadOnlyList<string> ResolutionOptions => EditorModel.ResolutionOptions;
    public IReadOnlyList<string> RefreshOptions => EditorModel.RefreshOptions;
    public IReadOnlyList<string> BitrateOptions => EditorModel.BitrateOptions;

    public void RaiseDisplaysTitle() => Raise(nameof(DisplaysTitle));

    /// <summary>After a language change: texts built in code.</summary>
    public void RaiseComputedTexts()
    {
        Raise(nameof(DisplaysTitle));
        Raise(nameof(ResolutionOptions));
        Raise(nameof(RefreshOptions));
        Raise(nameof(BitrateOptions));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public IReadOnlyList<SummaryLine> Summary { get => _summary; set => Set(ref _summary, value); }

    private IReadOnlyList<PlacementRowVm> _placementRows = [];

    /// <summary>One row per machine that borrows displays in the selected profile.</summary>
    public IReadOnlyList<PlacementRowVm> PlacementRows { get => _placementRows; set => Set(ref _placementRows, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string AgentStatus { get => _agentStatus; set => Set(ref _agentStatus, value); }

    public ObservableCollection<string> Log { get; } = new();

    public IEnumerable<string> MachineIds => Model.Machines.Select(m => m.Id);

    public string LocalMachine { get => _localMachine; set => Set(ref _localMachine, value); }
    public string LocalKey { get => _localKey; set => Set(ref _localKey, value); }
    public string LocalPort { get => _localPort; set => Set(ref _localPort, value); }

    /// <summary>Coordinator address this machine connects to (empty: it only listens).</summary>
    public string LocalConnectTo { get => _localConnectTo; set => Set(ref _localConnectTo, value); }

    public void RaiseMachineIds() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MachineIds)));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
