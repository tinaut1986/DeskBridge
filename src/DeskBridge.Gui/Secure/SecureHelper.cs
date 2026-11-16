using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DeskBridge.Core.Control;
using DeskBridge.Core.Video;
using DeskBridge.Platform.Windows.Video;
using DeskBridge.Gui.Updates;
using DeskBridge.Platform.Windows.Input;
using DeskBridge.Platform.Windows.Secure;

namespace DeskBridge.Gui.Secure;

/// <summary>
/// The service's helper (<c>DeskBridge.exe --secure-helper</c>), running as SYSTEM in the user's session: the only
/// kind of process that can follow the input onto the secure desktop. It tells DeskBridge which desktop has the
/// input and, while the secure one has it, plays there the keyboard and mouse DeskBridge passes on.
/// <para>
/// Its pipe can only be opened by administrators (an elevated DeskBridge) and SYSTEM: a program running without
/// administrator rights cannot use it to click "Yes" on an administrator prompt.
/// </para>
/// Protocol: one line per message. Helper to DeskBridge: <c>hello &lt;version&gt;</c>, <c>desktop &lt;name&gt;</c>.
/// DeskBridge to helper: <c>move &lt;x&gt; &lt;y&gt;</c>, <c>event &lt;InputEvent as JSON&gt;</c>, <c>release</c>,
/// <c>video &lt;x&gt; &lt;y&gt; &lt;fps&gt; &lt;Mbps&gt;</c> (capture that display and send it through the second pipe,
/// <see cref="VideoPipeName"/>, as <see cref="StreamVideoLink"/> packets), <c>video-stop</c>.
/// </summary>
public static class SecureHelper
{
    public const int ProtocolVersion = 1;

    public static string PipeName(uint session) => $"DeskBridge.Secure.{session}";

    public static string VideoPipeName(uint session) => $"DeskBridge.Secure.{session}.video";

    private static readonly object VideoGate = new();
    private static VideoSender? _video;
    private static NamedPipeServerStream? _videoPipe;

    /// <summary>
    /// Captures the display at (x, y) of whichever desktop has the input (this process may open the secure one) and
    /// sends it to DeskBridge through the video pipe, for the coordinator.
    /// </summary>
    private static void StartVideo(uint session, int x, int y, int fps, int mbps)
    {
        StopVideo();
        var pipe = CreatePipe(VideoPipeName(session));
        lock (VideoGate)
            _videoPipe = pipe;
        new Thread(() =>
        {
            try
            {
                var wait = pipe.WaitForConnectionAsync();
                if (!wait.Wait(TimeSpan.FromSeconds(10)))
                {
                    Log("video: DeskBridge did not connect");
                    pipe.Dispose();
                    return;
                }
                var sender = new VideoSender(new StreamVideoLink(pipe), x, y, fps, mbps, Log, followInputDesktop: true);
                lock (VideoGate)
                {
                    if (_videoPipe != pipe)
                    {
                        _ = sender.DisposeAsync().AsTask(); // stopped meanwhile
                        return;
                    }
                    _video = sender;
                }
                Log($"video: sending the display at ({x}, {y}) of the desktop with the input");
            }
            catch (Exception ex) when (ex is IOException or AggregateException or ObjectDisposedException or InvalidOperationException)
            {
                Log($"video: {ex.Message}");
            }
        }) { IsBackground = true, Name = "Secure video start" }.Start();
    }

    private static void StopVideo()
    {
        VideoSender? sender;
        NamedPipeServerStream? pipe;
        lock (VideoGate)
        {
            (sender, pipe) = (_video, _videoPipe);
            (_video, _videoPipe) = (null, null);
        }
        if (sender is not null)
        {
            sender.DisposeAsync().AsTask().Wait(3000);
            Log("video: stopped");
        }
        else
            pipe?.Dispose();
    }

    private static void Log(string line) => FileLog.Write("[helper] " + line);

    /// <summary>Runs until the service ends this process.</summary>
    public static void Run()
    {
        uint session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        Log($"started in session {session} (DeskBridge {AppInfo.Version})");
        var input = new BlockingCollection<string>();
        new Thread(() => InjectLoop(input)) { IsBackground = true, Name = "Secure input" }.Start();

        while (true)
        {
            try
            {
                using var pipe = CreatePipe(session);
                pipe.WaitForConnection();
                Log("DeskBridge connected");
                Serve(pipe, input, session);
                Log("DeskBridge disconnected");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"pipe: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(uint session) => CreatePipe(PipeName(session));

    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Administrators only as an elevated token has them: a normal (filtered) token holds the group as deny-only.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Asynchronous (overlapped): on a synchronous handle Windows runs one operation at a time, so a write (the
        // desktop changing) waited forever behind the read waiting for DeskBridge.
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    private static void Serve(NamedPipeServerStream pipe, BlockingCollection<string> input, uint session)
    {
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var writeLock = new object();
        void Send(string line)
        {
            lock (writeLock)
                writer.WriteLine(line);
        }
        Send($"hello {ProtocolVersion} {AppInfo.Version}");
        Log("hello sent");

        // The desktop with the input, now and whenever it changes.
        using var stop = new CancellationTokenSource();
        var watch = new Thread(() =>
        {
            string? last = null;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                string name = InputDesktop.Name() ?? "?";
                if (name != last)
                {
                    last = name;
                    try
                    {
                        Send($"desktop {name}");
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    Log($"input desktop: {name}");
                }
                }
                catch (Exception ex) when (ex is not IOException)
                {
                    Log($"desktop watch: {ex.Message}");
                }
                stop.Token.WaitHandle.WaitOne(150);
            }
        }) { IsBackground = true, Name = "Secure desktop watch" };
        watch.Start();
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("video ", StringComparison.Ordinal))
                {
                    var v = line.Split(' ');
                    if (v.Length == 5 && int.TryParse(v[1], out int x) && int.TryParse(v[2], out int y)
                        && int.TryParse(v[3], out int fps) && int.TryParse(v[4], out int mbps))
                        StartVideo(session, x, y, fps, mbps);
                }
                else if (line == "video-stop")
                    StopVideo();
                else
                    input.Add(line);
            }
        }
        finally
        {
            stop.Cancel();
            input.Add("release");
            StopVideo();
        }
    }

    /// <summary>On a thread of its own (no windows: it may change desktop), always on the desktop with the input.</summary>
    private static void InjectLoop(BlockingCollection<string> input)
    {
        var injector = new InputInjector();
        string? on = null;
        foreach (var line in input.GetConsumingEnumerable())
        {
            try
            {
                string now = InputDesktop.Name() ?? "?";
                if (now != on && InputDesktop.Follow())
                    on = now;
                int space = line.IndexOf(' ');
                string verb = space < 0 ? line : line[..space], rest = space < 0 ? "" : line[(space + 1)..];
                switch (verb)
                {
                    case "move":
                        var xy = rest.Split(' ');
                        InputInjector.MoveTo(int.Parse(xy[0]), int.Parse(xy[1]));
                        break;
                    case "event" when JsonSerializer.Deserialize<InputEvent>(rest) is { } e:
                        injector.Play(e);
                        break;
                    case "release":
                        injector.ReleaseAll();
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
            {
                Log($"bad message ignored: {ex.Message}");
            }
        }
    }
}
