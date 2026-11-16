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

    /// <summary>True when the driver answered on its pipe.</summary>
    public static bool IsInstalled() => Send("PING", wantReply: true)?.StartsWith("PONG", StringComparison.Ordinal) == true;

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

        if (Send($"SETDISPLAYCOUNT {displays.Count}", wantReply: false) is null)
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

    /// <summary>The driver's monitors that are part of the desktop (\\.\DISPLAYn names), in order.</summary>
    private static List<string> VirtualMonitors()
    {
        var found = new List<string>();
        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            bool attached = (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
            bool virtualDriver = device.DeviceString.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
                                 || device.DeviceId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);
            if (attached && virtualDriver)
                found.Add(device.DeviceName);
            device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        }
        return found;
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
