using System.IO;
using System.IO.Pipes;
using DeskBridge.Core.Video;
using DeskBridge.Platform.Windows.Video;

namespace DeskBridge.Gui.Secure;

/// <summary>
/// While the secure desktop has the input (lock screen, administrator prompt), DeskBridge cannot capture it: the
/// service's helper does, and this relay forwards its packets over the stream's own encrypted link to the
/// coordinator. DeskBridge's sender stays quiet meanwhile and starts over (format, key frame) when it ends.
/// </summary>
public sealed class SecureVideoRelay : IAsyncDisposable
{
    private readonly SecureLink _link;
    private readonly VideoSender _sender;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    public SecureVideoRelay(SecureLink link, VideoSender sender, Action<string> log)
    {
        _link = link;
        _sender = sender;
        _log = log;
        _run = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        uint session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var (x, y) = _sender.Monitor;
        if (!_link.StartVideo(x, y, _sender.Fps, _sender.BitrateMbps))
            return;
        try
        {
            var pipe = new NamedPipeClientStream(".", SecureHelper.VideoPipeName(session), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, _stop.Token);
            await using var helper = new StreamVideoLink(pipe);
            _sender.Suspended = true;
            _log($"[secure] the display at ({x}, {y}) comes from the service's helper while the secure desktop has the input");
            while (!_stop.IsCancellationRequested && await helper.ReceiveAsync(_stop.Token) is { } packet)
                await _sender.Link.SendAsync(packet.Type, packet.Body, default, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or ObjectDisposedException
                                       or System.Net.Sockets.SocketException)
        {
            _log($"[secure] video from the helper: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _link.StopVideo();
        _stop.Cancel();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
        _sender.Suspended = false; // DeskBridge's own picture again
        _stop.Dispose();
    }
}
