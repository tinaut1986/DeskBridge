using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using DeskBridge.Core.Control;

namespace DeskBridge.Gui.Secure;

/// <summary>
/// DeskBridge's side of the service's helper (<see cref="SecureHelper"/>): knows which desktop has the input and,
/// while it is the secure one (lock screen, administrator prompt), passes the keyboard and mouse to the helper,
/// which can play them there. Keeps trying to connect while the option is on; only an elevated DeskBridge can.
/// </summary>
public sealed class SecureLink : IDisposable
{
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private volatile string? _desktop;

    /// <summary>The desktop with the input as the helper sees it ("Default", "Winlogon"...), or null without a helper.</summary>
    public string? Desktop => _writer is null ? null : _desktop;

    /// <summary>The secure desktop has the input and the helper can play the keyboard and mouse there.</summary>
    public bool OnSecureDesktop => Desktop is { } d && !string.Equals(d, "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The desktop with the input changed (on the link's thread).</summary>
    public event Action<string>? DesktopChanged;

    public SecureLink(Action<string> log)
    {
        _log = log;
        new Thread(Run) { IsBackground = true, Name = "Secure link" }.Start();
    }

    private void Run()
    {
        uint session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        bool warned = false;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", SecureHelper.PipeName(session), PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(2000);
                using var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                var hello = reader.ReadLine()?.Split(' ');
                if (hello is not ["hello", var version, ..] || version != SecureHelper.ProtocolVersion.ToString())
                {
                    _log($"[secure] the helper speaks another version ({string.Join(' ', hello ?? [])}): update the service");
                    _stop.Token.WaitHandle.WaitOne(30_000);
                    continue;
                }
                lock (_writeLock)
                    _writer = writer;
                _log($"[secure] connected to the service's helper (DeskBridge {(hello.Length > 2 ? hello[2] : "?")})");
                warned = false;
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith("desktop ", StringComparison.Ordinal))
                    {
                        _desktop = line[8..];
                        DesktopChanged?.Invoke(_desktop);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    _log(ex is UnauthorizedAccessException
                        ? "[secure] the service's helper only answers DeskBridge running as administrator"
                        : $"[secure] the service's helper is not reachable ({ex.Message}); trying again");
                }
            }
            finally
            {
                lock (_writeLock)
                    _writer = null;
            }
            _stop.Token.WaitHandle.WaitOne(3000);
        }
    }

    private bool Send(string line)
    {
        lock (_writeLock)
        {
            if (_writer is null)
                return false;
            try
            {
                _writer.WriteLine(line);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>Asks the helper to send the display at (x, y) (see <see cref="SecureVideoRelay"/>).</summary>
    public bool StartVideo(int x, int y, int fps, int mbps) => Send($"video {x} {y} {fps} {mbps}");

    public bool StopVideo() => Send("video-stop");

    /// <summary>Puts the cursor at (x, y) on the secure desktop.</summary>
    public bool MoveTo(int x, int y) => Send($"move {x} {y}");

    /// <summary>A key, button or wheel event on the secure desktop.</summary>
    public bool Play(InputEvent e) => Send("event " + JsonSerializer.Serialize(e));

    public void ReleaseAll() => Send("release");

    public void Dispose()
    {
        _stop.Cancel();
        lock (_writeLock)
        {
            try
            {
                _writer?.BaseStream.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
