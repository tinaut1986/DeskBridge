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
    public IReadOnlyList<DisplayConfig> DetectDisplays() => DetectDetailed().Select(d => d.Config).ToList();

    public static IReadOnlyList<DetectedDisplay> DetectDetailed()
    {
        var result = new List<DetectedDisplay>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(null, i, ref device, 0))
                break;
            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
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
                mode.dmPositionY);
            bool primary = (device.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            result.Add(new DetectedDisplay(config, device.DeviceName, device.DeviceString, primary));
        }

        return result.OrderBy(d => int.TryParse(d.Config.Id, out var n) ? n : int.MaxValue).ToList();
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);
    }
}
