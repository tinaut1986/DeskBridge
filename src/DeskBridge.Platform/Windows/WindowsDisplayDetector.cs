using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Model;

namespace DeskBridge.Platform.Windows;

/// <summary>A monitor as Windows reports it, with extra details for the UI.</summary>
public sealed record DetectedDisplay(DisplayConfig Config, string DeviceName, string AdapterName, bool IsPrimary);

/// <summary>Lists the monitors attached to the desktop with EnumDisplayDevices / EnumDisplaySettings.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayDetector : IDisplayDetector
{
    /// <summary>
    /// This PC's displays, without the Virtual Display Driver's monitors: those exist only while a profile shows
    /// them on another machine, which manages them (they are not displays of this PC to lend or place).
    /// </summary>
    public IReadOnlyList<DisplayConfig> DetectDisplays() => DetectDetailed().Select(d => d.Config).ToList();

    /// <summary>Every display on the desktop, virtual monitors included.</summary>
    public static IReadOnlyList<DisplayConfig> DetectAll() => DetectDetailed(includeVirtual: true).Select(d => d.Config).ToList();

    public static IReadOnlyList<DetectedDisplay> DetectDetailed(bool includeVirtual = false)
    {
        var result = new List<DetectedDisplay>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(null, i, ref device, 0))
                break;
            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;
            bool virtualMonitor = device.DeviceID.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
                                  || device.DeviceString.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase);
            if (virtualMonitor && !includeVirtual)
                continue;

            var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!NativeMethods.EnumDisplaySettings(device.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode))
                continue;

            var config = new DisplayConfig(
                IdFromDeviceName(device.DeviceName, i),
                (int)mode.dmPelsWidth,
                (int)mode.dmPelsHeight,
                mode.dmDisplayFrequency is 0 or 1 ? 60 : (int)mode.dmDisplayFrequency,
                mode.dmPositionX,
                mode.dmPositionY,
                ScaleOf(mode.dmPositionX, mode.dmPositionY));
            bool primary = (device.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            result.Add(new DetectedDisplay(config, device.DeviceName, device.DeviceString, primary));
        }

        return result.OrderBy(d => int.TryParse(d.Config.Id, out var n) ? n : int.MaxValue).ToList();
    }

    /// <summary>
    /// Makes the display with that id Windows' primary one (Start menu, taskbar, the Windows key, new windows):
    /// every display moves by the same amount so that one is at (0,0), the arrangement between them unchanged.
    /// Returns whether it is the primary display now.
    /// </summary>
    public static bool MakePrimary(string id, Action<string> log)
    {
        var displays = DetectDetailed(includeVirtual: true);
        if (displays.FirstOrDefault(d => d.Config.Id == id) is not { } target)
        {
            log($"[displays] display {id} is not connected: the primary display stays as it is");
            return false;
        }
        if (target.IsPrimary)
            return true;
        int dx = -target.Config.X, dy = -target.Config.Y;

        // Windows' display configuration (what Settings > Display uses): the primary display is the one at (0,0).
        // The older per-display API below is refused by some drivers (DISP_CHANGE_FAILED).
        if (ShiftWithDisplayConfig(target.Config.X, target.Config.Y, dx, dy, log))
        {
            bool done = DetectDetailed(includeVirtual: true).Any(d => d.Config.Id == id && d.IsPrimary);
            if (done)
            {
                log($"[displays] display {id} is now the primary display");
                return true;
            }
        }

        foreach (var display in displays)
        {
            var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!NativeMethods.EnumDisplaySettings(display.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode))
                continue;
            mode.dmPositionX += dx;
            mode.dmPositionY += dy;
            mode.dmFields = NativeMethods.DM_POSITION;
            uint flags = NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET | (display == target ? NativeMethods.CDS_SET_PRIMARY : 0);
            int result = NativeMethods.ChangeDisplaySettingsEx(display.DeviceName, ref mode, 0, flags, 0);
            if (result != 0)
            {
                log($"[displays] {display.DeviceName}: moving it refused ({result}); the primary display stays as it is");
                NativeMethods.ChangeDisplaySettingsEx(null, 0, 0, 0, 0);
                return false;
            }
        }
        int applied = NativeMethods.ChangeDisplaySettingsEx(null, 0, 0, 0, 0); // everything at once
        bool ok = applied == 0 && DetectDetailed(includeVirtual: true).Any(d => d.Config.Id == id && d.IsPrimary);
        log(ok ? $"[displays] display {id} is now the primary display" : $"[displays] display {id} could not be made the primary display ({applied})");
        return ok;
    }

    /// <summary>
    /// Moves every active display by (dx, dy) with QueryDisplayConfig / SetDisplayConfig, so the one whose top-left
    /// corner is at (x, y) ends up at (0,0). False when it could not be done (the caller tries the older API).
    /// </summary>
    private static bool ShiftWithDisplayConfig(int x, int y, int dx, int dy, Action<string> log)
    {
        const uint QdcOnlyActivePaths = 0x2;
        const uint SdcApply = 0x80, SdcUseSuppliedDisplayConfig = 0x20, SdcSaveToDatabase = 0x200, SdcAllowChanges = 0x400;
        const int PathSize = 72, ModeSize = 64;
        const uint ModeTypeSource = 1;
        // DISPLAYCONFIG_MODE_INFO: infoType at 0; for a source mode, position.x at 28 and position.y at 32.
        const int InfoType = 0, PositionX = 28, PositionY = 32;

        nint paths = 0, modes = 0;
        try
        {
            int result = 0;
            uint pathCount = 0, modeCount = 0;
            for (int attempt = 0; attempt < 3; attempt++) // the configuration may change between the two calls
            {
                if (NativeMethods.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount) != 0)
                    return false;
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
                paths = Marshal.AllocHGlobal((int)pathCount * PathSize);
                modes = Marshal.AllocHGlobal((int)modeCount * ModeSize);
                result = NativeMethods.QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0);
                if (result != 122 /* ERROR_INSUFFICIENT_BUFFER */)
                    break;
            }
            if (result != 0)
                return false;

            bool found = false;
            for (int i = 0; i < modeCount; i++)
            {
                nint mode = modes + i * ModeSize;
                if ((uint)Marshal.ReadInt32(mode, InfoType) == ModeTypeSource
                    && Marshal.ReadInt32(mode, PositionX) == x && Marshal.ReadInt32(mode, PositionY) == y)
                    found = true;
            }
            if (!found)
                return false;
            for (int i = 0; i < modeCount; i++)
            {
                nint mode = modes + i * ModeSize;
                if ((uint)Marshal.ReadInt32(mode, InfoType) != ModeTypeSource)
                    continue;
                Marshal.WriteInt32(mode, PositionX, Marshal.ReadInt32(mode, PositionX) + dx);
                Marshal.WriteInt32(mode, PositionY, Marshal.ReadInt32(mode, PositionY) + dy);
            }
            result = NativeMethods.SetDisplayConfig(pathCount, paths, modeCount, modes,
                SdcApply | SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcAllowChanges);
            if (result != 0)
                log($"[displays] moving the displays refused ({result}); trying the older way");
            return result == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    /// <summary>The id of Windows' primary display.</summary>
    public static string? PrimaryId() => DetectDetailed(includeVirtual: true).FirstOrDefault(d => d.IsPrimary)?.Config.Id;

    /// <summary>Windows' scale of the monitor at that desktop position, in percent (100 when it cannot be read).</summary>
    private static int ScaleOf(int x, int y)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = x + 1, Y = y + 1 }, 2 /* nearest */);
            if (monitor != 0 && NativeMethods.GetDpiForMonitor(monitor, 0 /* effective */, out uint dpi, out _) == 0 && dpi > 0)
                return (int)Math.Round(dpi * 100.0 / 96);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return 100;
    }

    /// <summary>
    /// Windows names monitors like "\\.\DISPLAY3"; the trailing number usually matches the one shown
    /// in Settings > Display (not guaranteed: "Identify" there is the source of truth).
    /// </summary>
    private static string IdFromDeviceName(string deviceName, uint index)
    {
        var digits = new string(deviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 ? digits : (index + 1).ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>DEVMODEW, display variant of the union (position, orientation, fixed output).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private static class NativeMethods
    {
        public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const uint DM_POSITION = 0x20;
        public const uint CDS_UPDATEREGISTRY = 0x1;
        public const uint CDS_SET_PRIMARY = 0x10;
        public const uint CDS_NORESET = 0x10000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, nint window, uint flags, nint param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        public static extern int ChangeDisplaySettingsEx(string? deviceName, nint mode, nint window, uint flags, nint param);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        public static extern nint MonitorFromPoint(Point point, uint flags);

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint pathCount, nint paths, ref uint modeCount, nint modes, nint topologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint pathCount, nint paths, uint modeCount, nint modes, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);
    }
}
