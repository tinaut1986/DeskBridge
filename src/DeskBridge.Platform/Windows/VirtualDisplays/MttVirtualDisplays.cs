using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Model;

namespace DeskBridge.Platform.Windows.VirtualDisplays;

/// <summary>
/// Virtual monitors through the "Virtual Display Driver" (MttVDD, github.com/VirtualDrivers/Virtual-Display-Driver,
/// MIT, signed; install with <c>winget install --id=VirtualDrivers.Virtual-Display-Driver -e</c>). The driver
/// takes a monitor count over its named pipe; each monitor is then given the planned mode and position with
/// the Windows display settings API. Without the driver, every planned virtual display falls back to this
/// PC's main display (so streaming can still be tried).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MttVirtualDisplays(Action<string> log) : IVirtualDisplayDriver
{
    private const string PipeName = "MTTVirtualDisplayPipe";

    /// <summary>Where each planned virtual display ended up (its top-left corner in this PC's desktop), by key.</summary>
    public static ConcurrentDictionary<string, (int X, int Y)> Placed { get; } = new();

    /// <summary>The monitor (a \\.\DISPLAYn name) of each planned virtual display, by key.</summary>
    private static ConcurrentDictionary<string, string> PlacedDevices { get; } = new();

    /// <summary>
    /// Where the planned virtual display <paramref name="key"/> ("machine/vN") is: as placed when the profile was
    /// applied, or else the N-th of the driver's monitors on the desktop (after a restart of the app, for example).
    /// </summary>
    public static (int X, int Y)? Find(string key)
    {
        // Where its monitor is now: the whole desktop moves when the primary display changes (after the profile
        // placed it), so the position it was given may be stale.
        if (PlacedDevices.TryGetValue(key, out var device) && IsAttached(device))
        {
            var now = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(device, EnumCurrentSettings, ref now))
                return (now.PositionX, now.PositionY);
        }
        if (Placed.TryGetValue(key, out var placed))
            return placed;
        int slash = key.LastIndexOf("/v", StringComparison.Ordinal);
        if (slash < 0 || !int.TryParse(key[(slash + 2)..], out int n) || n < 1)
            return null;
        var attached = VirtualMonitors().Where(IsAttached).ToList();
        if (attached.Count < n)
            return null;
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettingsW(attached[n - 1], EnumCurrentSettings, ref mode) ? (mode.PositionX, mode.PositionY) : null;
    }

    /// <summary>
    /// True when the driver's pipe takes a connection (some versions do not answer PING, so the connection itself
    /// is the sign that the driver is installed and running).
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task SetDisplaysAsync(IReadOnlyList<VirtualDisplaySpec> displays, CancellationToken ct = default)
    {
        Placed.Clear();
        PlacedDevices.Clear();
        bool installed = await Task.Run(IsInstalled, ct);
        if (!installed && displays.Count > 0 && await Task.Run(RestartStoppedDriver, ct))
        {
            for (int i = 0; i < 20 && !(installed = await Task.Run(IsInstalled, ct)); i++)
                await Task.Delay(500, ct);
            log(installed ? "[virtual displays] the Virtual Display Driver is running again"
                          : "[virtual displays] the Virtual Display Driver did not come back after restarting it");
        }
        if (!installed)
        {
            if (displays.Count > 0)
            {
                log($"[virtual displays] the Virtual Display Driver is not installed: {displays.Count} planned virtual display(s) "
                    + "will show this PC's main display instead");
                foreach (var d in displays)
                    Placed[d.Key] = (0, 0);
            }
            return;
        }

        if (displays.Count > 0 && Input.PointerOverlay.Needed)
        {
            // A Remote Desktop session only gets Remote Desktop's own displays: virtual monitors appear on this PC's
            // own (console) session, once the remote one is handed back to it (tscon <session> /dest:console).
            log("[virtual displays] this is a Remote Desktop session, where Windows shows no virtual monitors: "
                + "its main display is sent instead until the session is back on this PC's own screen");
            foreach (var d in displays)
                Placed[d.Key] = (0, 0);
            return;
        }

        // Every SETDISPLAYCOUNT reloads the driver, and a few reloads in a row can leave it stopped with a problem
        // (code 43, seen going from one monitor to none and back): only when it needs more monitors than it has.
        // With none needed they are only taken off the desktop (they stay with the driver, ready for the next
        // profile). With fewer but some needed, the count goes down to that: a monitor taken off the desktop is still
        // listed by Windows' display settings (at the driver's 800x600), as if a display were left over.
        // A size the driver does not list cannot be given to its monitors (e.g. a laptop's 2880x1800): added to its
        // settings once, then the driver is reloaded to read them.
        bool reload = await Task.Run(() => AddMissingResolutions(displays), ct);
        int have = VirtualMonitors().Count;
        int want = displays.Count == 0 ? have : displays.Count;
        bool recount = want != have;
        if ((reload || recount) && Send($"SETDISPLAYCOUNT {want}", wantReply: false) is null)
        {
            log("[virtual displays] the Virtual Display Driver did not take the new monitor count");
            return;
        }
        if (want < have)
            log($"[virtual displays] {have - want} virtual monitor(s) no profile display needs: the driver keeps {want}");
        if (reload || want < have)
            await Task.Delay(2000, ct); // its monitors go away and come back (with new names) as it reloads
        if (displays.Count == 0)
        {
            await DetachLeftOversAsync(0, ct);
            log("[virtual displays] none");
            return;
        }

        // The new monitors take a moment to appear.
        List<string> devices = [];
        for (int i = 0; i < 60 && (devices = VirtualMonitors()).Count < displays.Count; i++)
            await Task.Delay(250, ct);
        if (devices.Count < displays.Count)
        {
            log($"[virtual displays] only {devices.Count} of {displays.Count} virtual monitors appeared");
            return;
        }

        // A virtual monitor that is Windows' primary display (a profile made it so) cannot be moved away from (0,0):
        // its whole new mode was refused and it stayed at the driver's default (800x600). A physical display of this
        // PC is made primary first; the profile makes the virtual one primary again once it is in place.
        var detailed = WindowsDisplayDetector.DetectDetailed(includeVirtual: true);
        if (detailed.FirstOrDefault(d => d.IsPrimary) is { } primary && devices.Contains(primary.DeviceName)
            && detailed.FirstOrDefault(d => !devices.Contains(d.DeviceName)) is { } physical)
        {
            log($"[virtual displays] {primary.DeviceName} is the primary display: display {physical.Config.Id} is made primary while they are set up");
            WindowsDisplayDetector.MakePrimary(physical.Config.Id, log);
        }

        // A monitor that is not part of the desktop cannot be given a mode directly: extend the desktop onto
        // every connected display first, as Windows' display settings do.
        if (devices.Take(displays.Count).Any(d => !IsAttached(d)))
        {
            int extended = SetDisplayConfig(0, 0, 0, 0, SdcApply | SdcTopologyExtend);
            if (extended != 0)
                log($"[virtual displays] extending the desktop onto the virtual monitors failed ({extended})");
            await Task.Delay(1000, ct);
        }

        for (int i = 0; i < displays.Count; i++)
        {
            var spec = displays[i];
            var mode = new DevMode
            {
                Size = (ushort)Marshal.SizeOf<DevMode>(),
                Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency | DmPosition,
                PelsWidth = (uint)spec.Width,
                PelsHeight = (uint)spec.Height,
                DisplayFrequency = (uint)spec.RefreshHz,
                PositionX = spec.X,
                PositionY = spec.Y,
            };
            int result = ChangeDisplaySettingsExW(devices[i], ref mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
            if (result != 0)
            {
                // Not every refresh rate is in the driver's list: let Windows pick one for that size.
                mode.Fields &= ~DmDisplayFrequency;
                result = ChangeDisplaySettingsExW(devices[i], ref mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
            }
            if (result != 0 && ClosestMode(devices[i], spec.Width, spec.Height) is { } closest)
            {
                // Still not a size it has: the nearest one it has, so it is a display of its own (extended), not
                // a copy of the main one.
                mode.PelsWidth = closest.Width;
                mode.PelsHeight = closest.Height;
                result = ChangeDisplaySettingsExW(devices[i], ref mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
                if (result == 0)
                    log($"[virtual displays] {devices[i]}: {spec.Width}x{spec.Height} is not one of its sizes; {closest.Width}x{closest.Height} instead");
            }
            if (result != 0)
                log($"[virtual displays] {devices[i]}: mode {spec.Width}x{spec.Height}@{spec.RefreshHz} refused ({result})");
        }
        ChangeDisplaySettingsExW(null, 0, 0, 0, 0); // apply everything at once
        await DetachLeftOversAsync(displays.Count, ct);

        for (int i = 0; i < displays.Count; i++)
        {
            var current = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(devices[i], EnumCurrentSettings, ref current))
            {
                Placed[displays[i].Key] = (current.PositionX, current.PositionY);
                PlacedDevices[displays[i].Key] = devices[i];
                log($"[virtual displays] {displays[i].Key} = {devices[i]} {current.PelsWidth}x{current.PelsHeight}@{current.DisplayFrequency} at ({current.PositionX},{current.PositionY})");
            }
        }
    }

    /// <summary>What can be seen of the driver from here (troubleshooting): pipes, its answer, display devices.</summary>
    public static string Report()
    {
        var lines = new List<string>();
        try
        {
            var pipes = Directory.GetFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .Where(n => n is not null && (n.Contains("MTT", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("VDD", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            lines.Add("pipes: " + (pipes.Count == 0 ? "none matching MTT/Virtual/VDD" : string.Join(", ", pipes)));
        }
        catch (Exception ex)
        {
            lines.Add("pipes: " + ex.Message);
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            lines.Add($"connect {PipeName}: ok");
            try
            {
                pipe.ReadMode = PipeTransmissionMode.Message;
            }
            catch (Exception ex)
            {
                lines.Add("message mode: " + ex.Message);
            }
            var bytes = Encoding.Unicode.GetBytes("PING");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            var buffer = new byte[1024];
            var read = pipe.ReadAsync(buffer, 0, buffer.Length);
            if (read.Wait(2000))
                lines.Add($"PING -> {read.Result} bytes: '{Encoding.Unicode.GetString(buffer, 0, read.Result)}' / ascii '{Encoding.ASCII.GetString(buffer, 0, read.Result)}'");
            else
                lines.Add("PING -> no answer in 2 s");
        }
        catch (Exception ex)
        {
            lines.Add($"connect {PipeName}: {ex.GetType().Name}: {ex.Message}");
        }

        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            lines.Add($"display {device.DeviceName}: '{device.DeviceString}' flags 0x{device.StateFlags:X} id '{device.DeviceId}'");
            device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        }
        // Display devices Windows knows of (installed drivers, even when not part of this session).
        try
        {
            using var pnp = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pnputil.exe", "/enum-devices /class Display")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = pnp.StandardOutput.ReadToEnd();
            pnp.WaitForExit(5000);
            foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Microsoft PnP", StringComparison.Ordinal)))
                lines.Add("pnp: " + line);
        }
        catch (Exception ex)
        {
            lines.Add("pnputil: " + ex.Message);
        }
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver"))
            lines.Add("registry HKLM\\SOFTWARE\\MikeTheTech\\VirtualDisplayDriver: " + (key is null ? "missing" : string.Join(", ", key.GetValueNames().Select(v => $"{v}={key.GetValue(v)}"))));
        foreach (var path in new[] { @"C:\VirtualDisplayDriver\vdd_settings.xml", @"C:\Program Files\Virtual Display Driver" })
            lines.Add($"{path}: {(File.Exists(path) || Directory.Exists(path) ? "exists" : "missing")}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The driver's monitors (\\.\DISPLAYn names), in order, whether or not they are part of the desktop yet:
    /// giving one a mode and a position attaches it.
    /// </summary>
    private static List<string> VirtualMonitors()
    {
        var found = new List<string>();
        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            bool virtualDriver = device.DeviceString.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
                                 || device.DeviceId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);
            if (virtualDriver && HasMonitor(device.DeviceName))
                found.Add(device.DeviceName);
            device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        }
        return found;
    }

    /// <summary>
    /// Whether an output of the driver has a monitor: an output can stay listed after the driver's monitor count
    /// went down, and only the monitors count (or can be given a mode).
    /// </summary>
    private static bool HasMonitor(string output)
    {
        var monitor = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevicesW(output, 0, ref monitor, 0);
    }

    private static DateTime _lastRestart;

    /// <summary>Where the driver reads its settings (the monitor count and the sizes its monitors offer).</summary>
    private static string SettingsPath
    {
        get
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
            var folder = key?.GetValue("VDDPATH") as string;
            return Path.Combine(string.IsNullOrWhiteSpace(folder) ? @"C:\VirtualDisplayDriver" : folder, "vdd_settings.xml");
        }
    }

    /// <summary>
    /// Adds to the driver's settings every planned size it does not list yet (at 60 Hz; its global refresh rates
    /// apply too). True when the file changed, so the driver has to be reloaded. Needs administrator rights.
    /// </summary>
    private bool AddMissingResolutions(IReadOnlyList<VirtualDisplaySpec> displays)
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path) || displays.Count == 0)
                return false;
            var doc = System.Xml.Linq.XDocument.Load(path, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            if (doc.Descendants("resolutions").FirstOrDefault() is not { } list)
                return false;
            static int Value(System.Xml.Linq.XElement e, string name) =>
                int.TryParse((string?)e.Element(name), out var v) ? v : 0;
            var have = list.Elements("resolution").Select(r => (Value(r, "width"), Value(r, "height"))).ToHashSet();
            var missing = displays.Select(d => (d.Width, d.Height)).Where(size => !have.Contains(size)).Distinct().ToList();
            if (missing.Count == 0)
                return false;
            foreach (var (width, height) in missing)
            {
                list.Add(new System.Xml.Linq.XElement("resolution",
                    new System.Xml.Linq.XElement("width", width),
                    new System.Xml.Linq.XElement("height", height),
                    new System.Xml.Linq.XElement("refresh_rate", 60)));
            }
            doc.Save(path);
            log($"[virtual displays] added to the driver's sizes: {string.Join(", ", missing.Select(m => $"{m.Width}x{m.Height}"))}; reloading it");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            log($"[virtual displays] the driver's sizes could not be updated: {ex.Message}");
            return false;
        }
    }

    /// <summary>The size the monitor offers that is closest to the wanted one (same shape first, not larger).</summary>
    private static (uint Width, uint Height)? ClosestMode(string device, int width, int height)
    {
        var sizes = new HashSet<(uint Width, uint Height)>();
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        for (int i = 0; EnumDisplaySettingsW(device, i, ref mode); i++)
        {
            sizes.Add((mode.PelsWidth, mode.PelsHeight));
            mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        }
        if (sizes.Count == 0)
            return null;
        double shape = (double)width / height;
        return sizes
            .OrderBy(s => s.Width > width || s.Height > height ? 1 : 0)                 // not larger than wanted
            .ThenBy(s => Math.Abs((double)s.Width / s.Height - shape) < 0.02 ? 0 : 1)  // same shape
            .ThenByDescending(s => (long)s.Width * s.Height)                            // as large as possible
            .First();
    }

    /// <summary>
    /// The driver's device is there but its pipe does not answer (stopped with a problem, e.g. code 43 after a
    /// reload): restart the device, as disabling and enabling it in Device Manager does. At most once every few
    /// minutes, so a driver that keeps failing is not restarted in a loop. Needs administrator rights.
    /// </summary>
    private bool RestartStoppedDriver()
    {
        if (DateTime.UtcNow - _lastRestart < TimeSpan.FromMinutes(5))
            return false;
        try
        {
            string? instance = null, candidate = null;
            foreach (var line in RunPnpUtil("/enum-devices /class Display").Split('\n'))
            {
                // Each device starts with its instance id (e.g. ROOT\DISPLAY\0000); the labels are in Windows' language.
                int colon = line.IndexOf(':');
                var value = colon < 0 ? "" : line[(colon + 1)..].Trim();
                if (value.Contains('\\') && !value.Contains(' '))
                    candidate = value;
                else if (value.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase) && candidate is not null)
                {
                    instance = candidate;
                    break;
                }
            }
            if (instance is null)
                return false;
            _lastRestart = DateTime.UtcNow;
            log($"[virtual displays] the Virtual Display Driver does not answer: restarting its device ({instance})");
            RunPnpUtil($"/restart-device \"{instance}\"");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log($"[virtual displays] restarting the Virtual Display Driver failed: {ex.Message}");
            return false;
        }
    }

    private static string RunPnpUtil(string arguments)
    {
        using var pnp = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pnputil.exe", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = pnp.StandardOutput.ReadToEnd();
        pnp.WaitForExit(15000);
        return output;
    }

    /// <summary>
    /// Virtual monitors past the first <paramref name="keep"/> that are still part of the desktop (a profile that
    /// used more of them before: the driver keeps them, see SetDisplaysAsync) are taken off it, so the cursor and
    /// windows cannot go onto a display nobody shows.
    /// </summary>
    private Task DetachLeftOversAsync(int keep, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var extra = VirtualMonitors().Skip(keep).Where(IsAttached).ToList();
        if (extra.Count == 0)
            return Task.CompletedTask;
        DetachAll(extra);
        return Task.CompletedTask;
    }

    private void DetachAll(List<string> extra)
    {
        if (extra.Count == 0)
            return;
        foreach (var device in extra)
        {
            // A position and a size of zero take a display off the desktop.
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>(), Fields = DmPelsWidth | DmPelsHeight | DmPosition };
            int result = ChangeDisplaySettingsExW(device, ref mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
            if (result != 0)
                log($"[virtual displays] {device}: taking it off the desktop refused ({result})");
        }
        ChangeDisplaySettingsExW(null, 0, 0, 0, 0);
        log($"[virtual displays] taken off the desktop (not in the profile any more): {string.Join(", ", extra)}");
    }

    private static bool IsAttached(string deviceName)
    {
        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            if (device.DeviceName == deviceName)
                return (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
            device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        }
        return false;
    }

    /// <summary>One command per connection, UTF-16, as the driver expects; null when it could not be sent.</summary>
    private static string? Send(string command, bool wantReply)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            pipe.ReadMode = PipeTransmissionMode.Message;
            var bytes = Encoding.Unicode.GetBytes(command);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            if (!wantReply)
                return "";
            var buffer = new byte[1024];
            int read = pipe.Read(buffer, 0, buffer.Length);
            return Encoding.Unicode.GetString(buffer, 0, read).TrimEnd('\0');
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Win32.

    private const uint DmPosition = 0x20, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1, CdsNoReset = 0x10000000;
    private const uint SdcTopologyExtend = 0x4, SdcApply = 0x80;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, nint paths, uint modeCount, nint modes, uint flags);
    private const uint DisplayDeviceAttachedToDesktop = 0x1;
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string device, ref DevMode devMode, nint hwnd, uint flags, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, nint devMode, nint hwnd, uint flags, nint param);
}
