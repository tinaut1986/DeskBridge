using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace DeskBridge.Platform.Windows.Audio;

/// <summary>
/// Makes VB-CABLE's microphone ("CABLE Output") this PC's default microphone while the coordinator's microphone
/// comes out of it, and gives back the one the user had afterwards: apps that use the default microphone hear
/// the coordinator's, and the PC used on its own keeps its own. The one to give back is kept in a file, so it
/// is put back at the next start if the app ended without doing it.
/// Windows has no public API to change default devices; this uses the same internal one as the Sound control
/// panel and tools like EarTrumpet or SoundSwitch (IPolicyConfig).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DefaultMicrophone
{
    private const string CableOutput = "CABLE Output";
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "default-microphone.txt");

    /// <summary>Makes CABLE Output the default microphone (all roles), remembering the current one.</summary>
    public static void UseCable(Action<string> log)
    {
        lock (Gate)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var cable = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Contains(CableOutput, StringComparison.OrdinalIgnoreCase));
                if (cable is null)
                    return;
                string? current = DefaultId(enumerator, Role.Communications);
                string? currentConsole = DefaultId(enumerator, Role.Console);
                if (current == cable.ID && currentConsole == cable.ID)
                    return; // already (the file, if any, keeps the user's own)
                if (!File.Exists(FilePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllLines(FilePath, [currentConsole ?? "", current ?? ""]);
                }
                SetDefault(cable.ID);
                log($"[audio] '{cable.FriendlyName}' is now the default microphone of this PC (until the coordinator's microphone stops)");
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                log($"[audio] could not make {CableOutput} the default microphone: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gives back the default microphone remembered by <see cref="UseCable"/>, if CABLE Output is still the
    /// default (a microphone the user chose meanwhile stays).
    /// </summary>
    public static void Restore(Action<string>? log)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                var lines = File.ReadAllLines(FilePath);
                File.Delete(FilePath);
                using var enumerator = new MMDeviceEnumerator();
                var cable = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.Contains(CableOutput, StringComparison.OrdinalIgnoreCase));
                string? console = lines.ElementAtOrDefault(0), communications = lines.ElementAtOrDefault(1);
                if (cable is not null && DefaultId(enumerator, Role.Console) == cable.ID && Exists(enumerator, console))
                    SetDefault(console!, Role.Console, Role.Multimedia);
                if (cable is not null && DefaultId(enumerator, Role.Communications) == cable.ID && Exists(enumerator, communications))
                    SetDefault(communications!, Role.Communications);
                if (Exists(enumerator, console))
                    log?.Invoke($"[audio] default microphone of this PC given back: '{enumerator.GetDevice(console).FriendlyName}'");
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                log?.Invoke($"[audio] could not give back the default microphone: {ex.Message}");
            }
        }
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            return device.ID;
        }
        catch (COMException)
        {
            return null; // no microphone at all
        }
    }

    private static bool Exists(MMDeviceEnumerator enumerator, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        try
        {
            using var device = enumerator.GetDevice(id);
            return device.State == DeviceState.Active;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static void SetDefault(string id, params Role[] roles)
    {
        if (roles.Length == 0)
            roles = [Role.Console, Role.Multimedia, Role.Communications];
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in roles)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    /// <summary>Undocumented (Windows 7 and later); only SetDefaultEndpoint is used, the others keep the vtable order.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod, nint defaultPeriodOut, nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
