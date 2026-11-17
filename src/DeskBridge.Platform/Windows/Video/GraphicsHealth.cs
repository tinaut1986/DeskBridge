using System.Runtime.Versioning;
using DeskBridge.Core.Localization;
using Vortice.DXGI;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Tells a graphics driver that has stopped working (after a crash Windows falls back to the "Microsoft Basic
/// Display Adapter", which cannot capture or encode video) from an ordinary, passing capture error, so the
/// stream is not retried for ever and the user is told to restart the machine.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GraphicsHealth
{
    /// <summary>DXGI_ERROR_UNSUPPORTED: what a device with video support fails with on a driver that is not working.</summary>
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);

    /// <summary>Microsoft's vendor id: its Basic Display Adapter / Basic Render Driver.</summary>
    private const uint MicrosoftVendorId = 0x1414;

    /// <summary>Whether this error means the GPU cannot capture or encode at all (not worth trying again).</summary>
    public static bool IsBroken(Exception ex) =>
        ex is SharpGen.Runtime.SharpGenException sharpGen && sharpGen.ResultCode.Code == DxgiErrorUnsupported;

    /// <summary>
    /// Why the monitor at (x, y) cannot be sent: its GPU's driver is not working (null when it is, or when that
    /// cannot be told here: the stream itself reports other problems).
    /// </summary>
    public static string? Problem(int x, int y)
    {
        try
        {
            if (DesktopCapture.FindOutput(x, y) is not { } found)
                return null;
            found.Output.Dispose();
            string gpu = found.Adapter.Description1.Description;
            bool basic = found.Adapter.Description1.VendorId == MicrosoftVendorId;
            try
            {
                using var device = VideoDevice.Create(found.Adapter);
                return null;
            }
            catch (Exception ex) when (IsBroken(ex))
            {
                found.Adapter.Dispose(); // (a device that was made owns it)
                return Message(basic ? null : gpu);
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The user's explanation: the driver of <paramref name="gpu"/> (or the one Windows fell back to) stopped working.</summary>
    public static string Message(string? gpu) => gpu is null
        ? L.T("The graphics driver of this machine stopped working (Windows is using the Microsoft Basic Display Adapter), so its screen cannot be sent. Shut this machine down completely (hold Shift while choosing Shut down) and turn it on again: a restart is not always enough. If that does not help, reinstall the graphics driver.")
        : L.T("The graphics driver of this machine ({gpu}) cannot capture or encode video right now, so its screen cannot be sent. Shut this machine down completely (hold Shift while choosing Shut down) and turn it on again: a restart is not always enough. If that does not help, reinstall the graphics driver.", ("gpu", gpu));
}
