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
        bool installed = await Task.Run(IsInstalled, ct);
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

        // Every SETDISPLAYCOUNT reloads the driver (and a few reloads in a row can leave it stopped with a problem):
        // only when the number of its monitors really changes.
        if (VirtualMonitors().Count != displays.Count
            && Send($"SETDISPLAYCOUNT {displays.Count}", wantReply: false) is null)
        {
            log("[virtual displays] the Virtual Display Driver did not take the new monitor count");
            return;
        }
        if (displays.Count == 0)
        {
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
            if (result != 0)
                log($"[virtual displays] {devices[i]}: mode {spec.Width}x{spec.Height}@{spec.RefreshHz} refused ({result})");
        }
        ChangeDisplaySettingsExW(null, 0, 0, 0, 0); // apply everything at once

        for (int i = 0; i < displays.Count; i++)
        {
            var current = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(devices[i], EnumCurrentSettings, ref current))
            {
                Placed[displays[i].Key] = (current.PositionX, current.PositionY);
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
            if (virtualDriver)
                found.Add(device.DeviceName);
            device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        }
        return found;
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
