using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DeskBridge.Core.Control;
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
/// DeskBridge to helper: <c>move &lt;x&gt; &lt;y&gt;</c>, <c>event &lt;InputEvent as JSON&gt;</c>, <c>release</c>.
/// </summary>
public static class SecureHelper
{
    public const int ProtocolVersion = 1;

    public static string PipeName(uint session) => $"DeskBridge.Secure.{session}";

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
                Serve(pipe, input);
                Log("DeskBridge disconnected");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"pipe: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(uint session)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Administrators only as an elevated token has them: a normal (filtered) token holds the group as deny-only.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName(session), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, 0, 0, security);
    }

    private static void Serve(NamedPipeServerStream pipe, BlockingCollection<string> input)
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

        // The desktop with the input, now and whenever it changes.
        using var stop = new CancellationTokenSource();
        var watch = new Thread(() =>
        {
            string? last = null;
            while (!stop.IsCancellationRequested)
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
                stop.Token.WaitHandle.WaitOne(150);
            }
        }) { IsBackground = true, Name = "Secure desktop watch" };
        watch.Start();
        try
        {
            while (reader.ReadLine() is { } line)
                input.Add(line);
        }
        finally
        {
            stop.Cancel();
            input.Add("release");
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
