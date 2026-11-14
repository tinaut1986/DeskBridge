using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeskBridge.Core.Model;

namespace DeskBridge.Gui.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any editable value anywhere changes; the window redraws the layout on it.</summary>
    public static event Action? AnyChanged;

    public static void NotifyAnyChanged() => AnyChanged?.Invoke();

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name!);
        NotifyAnyChanged();
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected static ObservableCollection<T> Tracked<T>(IEnumerable<T>? items = null)
    {
        var collection = new ObservableCollection<T>(items ?? []);
        collection.CollectionChanged += (_, _) => NotifyAnyChanged();
        return collection;
    }
}

public sealed class DisplayVm : ObservableObject
{
    private string _id = "1";
    private int _width = 1920, _height = 1080, _refreshHz = 60, _x, _y;

    public string Id { get => _id; set => Set(ref _id, value); }
    public int Width { get => _width; set { if (Set(ref _width, value)) Raise(nameof(Summary)); } }
    public int Height { get => _height; set { if (Set(ref _height, value)) Raise(nameof(Summary)); } }
    public int RefreshHz { get => _refreshHz; set { if (Set(ref _refreshHz, value)) Raise(nameof(Summary)); } }
    public int X { get => _x; set => Set(ref _x, value); }
    public int Y { get => _y; set => Set(ref _y, value); }

    public string Summary => $"{Width}x{Height} @ {RefreshHz} Hz";

    public static DisplayVm From(DisplayConfig d) =>
        new() { Id = d.Id, Width = d.Width, Height = d.Height, RefreshHz = d.RefreshHz, X = d.X, Y = d.Y };

    public void CopyFrom(DisplayConfig d)
    {
        Width = d.Width;
        Height = d.Height;
        RefreshHz = d.RefreshHz;
        X = d.X;
        Y = d.Y;
    }

    public DisplayConfig ToConfig() => new(Id.Trim(), Width, Height, RefreshHz, X, Y);
}

public sealed class MachineVm : ObservableObject
{
    private string _id = "", _name = "", _address = "";
    private int? _controlPort;
    private bool _hasInput;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public int? ControlPort { get => _controlPort; set => Set(ref _controlPort, value); }

    /// <summary>The coordinator. Setting it on one machine clears it on the others (see <see cref="EditorModel"/>).</summary>
    public bool HasInput
    {
        get => _hasInput;
        set
        {
            if (Set(ref _hasInput, value) && value)
                HasInputSet?.Invoke(this);
        }
    }

    internal Action<MachineVm>? HasInputSet;

    public ObservableCollection<DisplayVm> Displays { get; } = Tracked<DisplayVm>();

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>One physical display inside a profile: who uses it and how it is streamed.</summary>
public sealed class AssignmentVm : ObservableObject
{
    public const string Native = "Native";
    public const string Auto = "Auto";

    private MachineVm _usedBy;
    private string _resolution = Native, _refresh = Native, _bitrate = Auto;

    public AssignmentVm(MachineVm owner, DisplayVm display)
    {
        Owner = owner;
        Display = display;
        _usedBy = owner;
    }

    public MachineVm Owner { get; }
    public DisplayVm Display { get; }

    public MachineVm UsedBy
    {
        get => _usedBy;
        set
        {
            if (Set(ref _usedBy, value ?? Owner))
                Raise(nameof(IsLent));
        }
    }

    public bool IsLent => UsedBy != Owner;

    /// <summary>"Native" or "WIDTHxHEIGHT".</summary>
    public string Resolution { get => _resolution; set => Set(ref _resolution, value); }

    /// <summary>"Native" or a number of Hz.</summary>
    public string Refresh { get => _refresh; set => Set(ref _refresh, value); }

    /// <summary>"Auto" or a number of Mbps.</summary>
    public string Bitrate { get => _bitrate; set => Set(ref _bitrate, value); }

    public void Load(StreamSettings? s)
    {
        Resolution = s is { Width: { } w, Height: { } h } ? $"{w}x{h}" : Native;
        Refresh = s?.RefreshHz?.ToString(CultureInfo.InvariantCulture) ?? Native;
        Bitrate = s?.BitrateMbps?.ToString(CultureInfo.InvariantCulture) ?? Auto;
    }

    public StreamSettings? ToStreamSettings(List<string> errors)
    {
        string where = $"{Owner} display {Display.Id}";
        int? width = null, height = null;
        var res = Resolution.Trim();
        if (!res.Equals(Native, StringComparison.OrdinalIgnoreCase) && res.Length > 0)
        {
            var parts = res.ToLowerInvariant().Replace('×', 'x').Split('x', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                (width, height) = (w, h);
            else
                errors.Add($"{where}: resolution '{Resolution}' is not like 1920x1080.");
        }

        int? refresh = ParseOptional(Refresh, Native, where, "refresh rate", errors);
        int? bitrate = ParseOptional(Bitrate, Auto, where, "bitrate", errors);

        var settings = new StreamSettings(width, height, refresh, bitrate);
        return settings == StreamSettings.Default ? null : settings;
    }

    private static int? ParseOptional(string text, string keyword, string where, string what, List<string> errors)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return null;
        if (int.TryParse(t.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        errors.Add($"{where}: {what} '{text}' is not a number.");
        return null;
    }
}

public sealed class ProfileVm : ObservableObject
{
    private string _name = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    public ObservableCollection<AssignmentVm> Assignments { get; } = Tracked<AssignmentVm>();

    /// <summary>One row per physical display, in machine order; keeps existing rows (and their settings).</summary>
    public void Sync(IReadOnlyList<MachineVm> machines)
    {
        var wanted = machines.SelectMany(m => m.Displays.Select(d => (m, d))).ToList();
        foreach (var stale in Assignments.Where(a => !wanted.Contains((a.Owner, a.Display))).ToList())
            Assignments.Remove(stale);

        for (int i = 0; i < wanted.Count; i++)
        {
            var (machine, display) = wanted[i];
            var existing = Assignments.FirstOrDefault(a => a.Owner == machine && a.Display == display);
            if (existing is null)
                Assignments.Insert(i, new AssignmentVm(machine, display));
            else if (Assignments.IndexOf(existing) != i)
                Assignments.Move(Assignments.IndexOf(existing), i);
        }

        foreach (var a in Assignments.Where(a => !machines.Contains(a.UsedBy)))
            a.UsedBy = a.Owner;
    }
}

/// <summary>Editable copy of a <see cref="DeskBridgeConfig"/>.</summary>
public sealed class EditorModel
{
    public static readonly string[] ResolutionOptions =
        [AssignmentVm.Native, "1280x720", "1600x900", "1920x1080", "1920x1200", "2560x1080", "2560x1440", "3440x1440", "3840x2160"];

    public static readonly string[] RefreshOptions = [AssignmentVm.Native, "30", "50", "60", "75", "90", "120", "144"];

    public static readonly string[] BitrateOptions = [AssignmentVm.Auto, "10", "15", "20", "30", "40", "50", "80"];

    public ObservableCollection<MachineVm> Machines { get; } = new();
    public ObservableCollection<ProfileVm> Profiles { get; } = new();
    public int ControlPort { get; set; } = 47900;

    public EditorModel()
    {
        Machines.CollectionChanged += OnMachinesChanged;
        Profiles.CollectionChanged += (_, _) => ObservableObject.NotifyAnyChanged();
    }

    private void OnMachinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var m in e.NewItems?.OfType<MachineVm>() ?? [])
        {
            m.HasInputSet = OnHasInputSet;
            m.Displays.CollectionChanged += (_, _) => SyncProfiles();
        }
        SyncProfiles();
    }

    private void OnHasInputSet(MachineVm coordinator)
    {
        foreach (var other in Machines.Where(m => m != coordinator))
            other.HasInput = false;
    }

    public void SyncProfiles()
    {
        foreach (var p in Profiles)
            p.Sync(Machines);
        ObservableObject.NotifyAnyChanged();
    }

    public static EditorModel FromConfig(DeskBridgeConfig config)
    {
        var model = new EditorModel { ControlPort = config.ControlPort };
        foreach (var m in config.Machines)
        {
            var vm = new MachineVm { Id = m.Id, Name = m.Name, Address = m.Address, ControlPort = m.ControlPort };
            foreach (var d in m.Displays)
                vm.Displays.Add(DisplayVm.From(d));
            model.Machines.Add(vm);
            vm.HasInput = m.HasInput;
        }

        foreach (var p in config.Profiles)
        {
            var profile = new ProfileVm { Name = p.Name };
            profile.Sync(model.Machines);
            foreach (var a in p.Assignments)
            {
                var row = profile.Assignments.FirstOrDefault(r => r.Owner.Id == a.Machine && r.Display.Id == a.Display);
                if (row is null)
                    continue; // dangling reference in the file: dropped, the user sees the display as "own"
                row.UsedBy = model.Machines.FirstOrDefault(m => m.Id == a.UsedBy) ?? row.Owner;
                row.Load(a.Stream);
            }
            model.Profiles.Add(profile);
        }
        return model;
    }

    /// <summary>Builds the config; values that cannot be parsed are reported in <paramref name="errors"/>.</summary>
    public DeskBridgeConfig ToConfig(List<string> errors)
    {
        var machines = Machines.Select(m => new MachineConfig(
            m.Id.Trim(), m.Name.Trim(), m.Address.Trim(),
            m.Displays.Select(d => d.ToConfig()).ToList(),
            m.HasInput, m.ControlPort)).ToList();

        var profiles = Profiles.Select(p => new ProfileConfig(
            p.Name.Trim(),
            p.Assignments
                .Where(a => a.IsLent)
                .Select(a => new DisplayAssignment(a.Owner.Id.Trim(), a.Display.Id.Trim(), a.UsedBy.Id.Trim(), a.ToStreamSettings(errors)))
                .ToList())).ToList();

        return new DeskBridgeConfig(machines, profiles, ControlPort);
    }
}
