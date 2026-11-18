using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeskBridge.Core.Localization;
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
    private int _width = 1920, _height = 1080, _refreshHz = 60, _x, _y, _scale = 100;

    public string Id { get => _id; set => Set(ref _id, value); }
    public int Width { get => _width; set { if (Set(ref _width, value)) Raise(nameof(Summary)); } }
    public int Height { get => _height; set { if (Set(ref _height, value)) Raise(nameof(Summary)); } }
    public int RefreshHz { get => _refreshHz; set { if (Set(ref _refreshHz, value)) Raise(nameof(Summary)); } }
    public int X { get => _x; set => Set(ref _x, value); }
    public int Y { get => _y; set => Set(ref _y, value); }

    /// <summary>Windows' scale for it, in percent.</summary>
    public int Scale { get => _scale; set => Set(ref _scale, value); }

    private bool _disconnected;

    /// <summary>Not listed by Windows the last time it was detected: kept for when it comes back, left out of plans.</summary>
    public bool Disconnected { get => _disconnected; set => Set(ref _disconnected, value); }

    public string Summary => $"{Width}x{Height} @ {RefreshHz} Hz";

    public static DisplayVm From(DisplayConfig d) =>
        new() { Id = d.Id, Width = d.Width, Height = d.Height, RefreshHz = d.RefreshHz, X = d.X, Y = d.Y, Scale = d.Scale };

    public void CopyFrom(DisplayConfig d)
    {
        Width = d.Width;
        Height = d.Height;
        RefreshHz = d.RefreshHz;
        X = d.X;
        Y = d.Y;
        Scale = d.Scale;
    }

    public DisplayConfig ToConfig() => new(Id.Trim(), Width, Height, RefreshHz, X, Y, Scale is > 0 ? Scale : 100);
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

    private string? _color;

    /// <summary>Its colour chosen on the Layout map ("#RRGGBB"); null: by its place in the list.</summary>
    public string? Color { get => _color; set => Set(ref _color, value); }

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

    private IReadOnlyDictionary<string, VirtualPosition> _deskPositions = new Dictionary<string, VirtualPosition>();

    /// <summary>Its displays placed on the coordinator's desk (Layout tab), by display id; the others stay apart.</summary>
    public IReadOnlyDictionary<string, VirtualPosition> DeskPositions { get => _deskPositions; set => Set(ref _deskPositions, value); }

    private IReadOnlyDictionary<string, int> _deskWidths = new Dictionary<string, int>();

    /// <summary>Width chosen for a display on the desk (to line its edges up with a neighbour); none: its visual size.</summary>
    public IReadOnlyDictionary<string, int> DeskWidths { get => _deskWidths; set => Set(ref _deskWidths, value); }

    public void SetDeskWidth(string display, int? width)
    {
        var copy = new Dictionary<string, int>(DeskWidths);
        if (width is { } w)
            copy[display] = w;
        else
            copy.Remove(display);
        DeskWidths = copy;
    }

    /// <summary>Places one display on the desk (or takes it off with null).</summary>
    public void PlaceOnDesk(string display, VirtualPosition? position)
    {
        var copy = new Dictionary<string, VirtualPosition>(DeskPositions);
        if (position is null)
            copy.Remove(display);
        else
            copy[display] = position;
        DeskPositions = copy;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>One physical display inside a profile: who uses it and how it is streamed.</summary>
public sealed class AssignmentVm : ObservableObject
{
    // Keywords are stored as shown (translated) and recognised in any language, so switching
    // the interface language never turns a valid value into a parse error.
    public static string Native => L.T("Native");
    public static string Auto => L.T("Auto");

    public static bool IsNative(string text) => IsKeyword(text, "Native");
    public static bool IsAuto(string text) => IsKeyword(text, "Auto");

    private static bool IsKeyword(string text, string key)
    {
        var t = text.Trim();
        return t.Length == 0 || L.AllTranslations(key).Any(k => string.Equals(k, t, StringComparison.CurrentCultureIgnoreCase));
    }

    private MachineVm _usedBy;
    private string _resolution, _refresh, _bitrate;

    public AssignmentVm(MachineVm owner, DisplayVm display)
    {
        Owner = owner;
        Display = display;
        _usedBy = owner;
        _resolution = _refresh = Native;
        _bitrate = Auto;
    }

    public MachineVm Owner { get; }
    public DisplayVm Display { get; }

    public string DisplayLabel => L.T("Display {id}", ("id", Display.Id));

    private VirtualPosition? _position;

    /// <summary>Where the virtual display goes in the borrower's desktop when dragged by hand; null = automatic.</summary>
    public VirtualPosition? Position { get => _position; set => Set(ref _position, value); }

    /// <summary>After a language change: keywords shown in the new language, labels refreshed.</summary>
    public void Relabel()
    {
        if (IsNative(Resolution)) Resolution = Native;
        if (IsNative(Refresh)) Refresh = Native;
        if (IsAuto(Bitrate)) Bitrate = Auto;
        Raise(nameof(DisplayLabel));
    }

    public MachineVm UsedBy
    {
        get => _usedBy;
        set
        {
            if (Set(ref _usedBy, value ?? Owner))
            {
                Raise(nameof(IsLent));
                Position = null; // a position only makes sense in the desktop it was dragged in
            }
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
        string where = L.T("{machine}, display {display}", ("machine", Owner), ("display", Display.Id));
        int? width = null, height = null;
        if (!IsNative(Resolution))
        {
            var parts = Resolution.Trim().ToLowerInvariant().Replace('×', 'x').Split('x', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                (width, height) = (w, h);
            else
                errors.Add(L.T("{where}: resolution '{value}' is not like 1920x1080.", ("where", where), ("value", Resolution)));
        }

        int? refresh = IsNative(Refresh) ? null : ParseNumber(Refresh, () =>
            errors.Add(L.T("{where}: refresh rate '{value}' is not a number.", ("where", where), ("value", Refresh))));
        int? bitrate = IsAuto(Bitrate) ? null : ParseNumber(Bitrate, () =>
            errors.Add(L.T("{where}: bitrate '{value}' is not a number.", ("where", where), ("value", Bitrate))));

        var settings = new StreamSettings(width, height, refresh, bitrate);
        return settings == StreamSettings.Default ? null : settings;
    }

    private static int? ParseNumber(string text, Action onError)
    {
        if (int.TryParse(text.Trim().Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        onError();
        return null;
    }
}

public sealed class ProfileVm : ObservableObject
{
    private string _name = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// Per machine id, the display made primary there while this profile is applied: one of its own by id, or
    /// "shows:N" (the virtual display shown on the coordinator's display N). Absent: automatic.
    /// </summary>
    public Dictionary<string, string> PrimaryDisplays { get; } = new();

    public void SetPrimary(string machine, string? display)
    {
        if (display is null ? !PrimaryDisplays.Remove(machine) : PrimaryDisplays.GetValueOrDefault(machine) == display)
            return;
        if (display is not null)
            PrimaryDisplays[machine] = display;
        NotifyAnyChanged();
    }

    public ObservableCollection<AssignmentVm> Assignments { get; } = Tracked<AssignmentVm>();

    /// <summary>Where each borrowing machine puts its virtual displays (absent = to the right).</summary>
    public Dictionary<MachineVm, PlacementSide> Placement { get; } = new();

    public PlacementSide PlacementOf(MachineVm machine) => Placement.GetValueOrDefault(machine, PlacementSide.Right);

    public void SetPlacement(MachineVm machine, PlacementSide side)
    {
        if (PlacementOf(machine) == side)
            return;
        Placement[machine] = side;
        NotifyAnyChanged();
    }

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
        foreach (var gone in Placement.Keys.Where(m => !machines.Contains(m)).ToList())
            Placement.Remove(gone);
    }
}

public sealed record PlacementOption(PlacementSide Side, string Label)
{
    public static IReadOnlyList<PlacementOption> All =>
    [
        new(PlacementSide.Right, L.T("to the right of its physical displays")),
        new(PlacementSide.Left, L.T("to the left of its physical displays")),
        new(PlacementSide.Above, L.T("above its physical displays")),
        new(PlacementSide.Below, L.T("below its physical displays")),
    ];
}

/// <summary>One "where do the virtual displays of this machine go" choice in the Layout tab.</summary>
public sealed class PlacementRowVm(ProfileVm profile, MachineVm machine)
{
    public MachineVm Machine { get; } = machine;
    public string Label => L.T("Virtual displays of {machine}:", ("machine", Machine));
    public IReadOnlyList<PlacementOption> Options { get; } = PlacementOption.All;

    public PlacementOption Selected
    {
        get => Options.First(o => o.Side == profile.PlacementOf(Machine));
        set => profile.SetPlacement(Machine, value.Side);
    }
}

/// <summary>Editable copy of a <see cref="DeskBridgeConfig"/>.</summary>
public sealed class EditorModel
{
    public static IReadOnlyList<string> ResolutionOptions =>
        [AssignmentVm.Native, "1280x720", "1600x900", "1920x1080", "1920x1200", "2560x1080", "2560x1440", "3440x1440", "3840x2160"];

    public static IReadOnlyList<string> RefreshOptions => [AssignmentVm.Native, "30", "50", "60", "75", "90", "120", "144"];

    public static IReadOnlyList<string> BitrateOptions => [AssignmentVm.Auto, "10", "15", "20", "30", "40", "50", "80"];

    public void Relabel()
    {
        foreach (var a in Profiles.SelectMany(p => p.Assignments))
            a.Relabel();
    }

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
            var vm = new MachineVm { Id = m.Id, Name = m.Name, Address = m.Address, ControlPort = m.ControlPort, Color = m.Color };
            foreach (var d in m.Displays)
            {
                var display = DisplayVm.From(d);
                display.Disconnected = m.Disconnected?.Contains(d.Id) == true;
                vm.Displays.Add(display);
            }
            model.Machines.Add(vm);
            vm.HasInput = m.HasInput;
        }

        // Displays on the coordinator's desk (from older versions: where the machine was placed for the mouse).
        var coordinator = config.Machines.FirstOrDefault(m => m.HasInput)?.Displays
            .Select(d => new Core.Planning.DisplayRect(d.X, d.Y, d.Width, d.Height)).ToList() ?? [];
        foreach (var (m, vm) in config.Machines.Zip(model.Machines))
        {
            if (!m.HasInput)
            {
                vm.DeskPositions = Core.Input.InputLayout.DeskPositions(m, coordinator);
                vm.DeskWidths = m.DeskWidths is { } widths ? new Dictionary<string, int>(widths) : new Dictionary<string, int>();
            }
        }

        foreach (var p in config.Profiles)
        {
            var profile = new ProfileVm { Name = p.Name };
            foreach (var (machine, display) in p.PrimaryDisplays ?? new Dictionary<string, string>())
                profile.PrimaryDisplays[machine] = display;
            profile.Sync(model.Machines);
            foreach (var a in p.Assignments)
            {
                var row = profile.Assignments.FirstOrDefault(r => r.Owner.Id == a.Machine && r.Display.Id == a.Display);
                if (row is null)
                    continue; // dangling reference in the file: dropped, the user sees the display as "own"
                row.UsedBy = model.Machines.FirstOrDefault(m => m.Id == a.UsedBy) ?? row.Owner;
                row.Load(a.Stream);
                row.Position = a.Position;
            }
            foreach (var (machineId, side) in p.Placement ?? new Dictionary<string, PlacementSide>())
            {
                if (model.Machines.FirstOrDefault(m => m.Id == machineId) is { } machine)
                    profile.Placement[machine] = side;
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
            m.HasInput, m.ControlPort,
            DeskPositions: m.HasInput || m.DeskPositions.Count == 0
                ? null
                : m.DeskPositions.Where(p => m.Displays.Any(d => d.Id.Trim() == p.Key)).ToDictionary(),
            DeskWidths: m.HasInput || !m.DeskWidths.Any(w => m.DeskPositions.ContainsKey(w.Key))
                ? null
                : m.DeskWidths.Where(w => m.DeskPositions.ContainsKey(w.Key)).ToDictionary(),
            Color: string.IsNullOrWhiteSpace(m.Color) ? null : m.Color.Trim(),
            Disconnected: m.Displays.Where(d => d.Disconnected).Select(d => d.Id.Trim()).ToList() is { Count: > 0 } off ? off : null)).ToList();

        var profiles = Profiles.Select(p => new ProfileConfig(
            p.Name.Trim(),
            p.Assignments
                .Where(a => a.IsLent)
                .Select(a => new DisplayAssignment(a.Owner.Id.Trim(), a.Display.Id.Trim(), a.UsedBy.Id.Trim(), a.ToStreamSettings(errors), a.Position))
                .ToList(),
            PlacementOf(p),
            p.PrimaryDisplays.Where(kv => Machines.Any(m => m.Id.Trim() == kv.Key)).ToDictionary() is { Count: > 0 } primary ? primary : null)).ToList();

        return new DeskBridgeConfig(machines, profiles, ControlPort);
    }

    /// <summary>Only non-default sides of machines still in the config; null when there are none.</summary>
    private Dictionary<string, PlacementSide>? PlacementOf(ProfileVm profile)
    {
        var map = profile.Placement
            .Where(kv => kv.Value != PlacementSide.Right && Machines.Contains(kv.Key))
            .ToDictionary(kv => kv.Key.Id.Trim(), kv => kv.Value);
        return map.Count == 0 ? null : map;
    }
}
