using System.Text.Json;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Core.Simulation;
using DeskBridge.Platform;
using DeskBridge.Platform.Windows;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string usage = $"""
    DeskBridge command line (the GUI is DeskBridge.exe).

    Usage:
      deskbridge validate                     Check the configuration
      deskbridge profiles                     List profiles
      deskbridge plan <profile>               Show what every machine would do
      deskbridge simulate <profile> <machine> Simulate applying a profile on one machine, no network
      deskbridge displays [<machine>]         List this PC's monitors, or ask a machine's agent

      deskbridge remote <machine> log [<lines>]
                                              Version, path and recent log of a machine connected to this PC's agent
      deskbridge remote <machine> update <exe> [<version>]
                                              Install a DeskBridge build there (it must allow updates)
      deskbridge probe <machine> [<seconds>]  Receive a stream from a machine here, without showing it; report
      deskbridge keygen                       Generate a new shared key
      deskbridge video-test [<x> <y>] [<seconds>] [<bmp>]
                                              Capture the monitor at (x, y), encode and decode it on the GPU, report
      deskbridge agent                        Run this machine's agent and wait for orders
      deskbridge switch <profile>             (coordinator) Apply a profile on every machine

    Options:
      --config <path>   Shared configuration (default: {AppPaths.Config})
      --local <path>    This machine's settings (default: {AppPaths.Local})

    For now the agent only simulates: it logs what it would do.
    """;

var argList = args.ToList();
string configPath, localPath;
try
{
    configPath = TakeOption(argList, "--config") ?? AppPaths.Config;
    localPath = TakeOption(argList, "--local") ?? AppPaths.Local;
}
catch (ArgumentException ex)
{
    return Fail(ex.Message);
}

if (argList.Count == 0 || argList[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(usage);
    return 0;
}

try
{
    switch (argList[0], argList.Count)
    {
        case ("validate", 1):
        {
            var config = LoadConfig(configPath);
            var errors = LayoutPlanner.Validate(config);
            if (errors.Count > 0) throw new ConfigException(errors);
            Console.WriteLine($"Configuration OK: {config.Machines.Count} machines, {config.Profiles.Count} profiles.");
            return 0;
        }
        case ("profiles", 1):
            foreach (var p in LoadConfig(configPath).Profiles)
                Console.WriteLine($"{p.Name}  ({p.Assignments.Count(a => a.UsedBy != a.Machine)} lent displays)");
            return 0;

        case ("plan", 2):
        {
            var config = LoadConfig(configPath);
            PrintPlan(config, LayoutPlanner.Plan(config, argList[1]));
            return 0;
        }
        case ("simulate", 3):
        {
            var config = LoadConfig(configPath);
            await Simulated.Applier(Console.WriteLine).ApplyAsync(config, LayoutPlanner.Plan(config, argList[1]), argList[2]);
            return 0;
        }
        case ("displays", 1) when OperatingSystem.IsWindows():
            foreach (var d in WindowsDisplayDetector.DetectDetailed())
                Console.WriteLine($"{d.Config.Id,-3} {d.Config.Width}x{d.Config.Height}@{d.Config.RefreshHz}  at ({d.Config.X},{d.Config.Y})"
                    + $"{(d.IsPrimary ? "  primary" : "")}  {d.DeviceName}  {d.AdapterName}");
            return 0;

        case ("displays", 1):
            foreach (var d in PlatformServices.DisplayDetector().DetectDisplays())
                Console.WriteLine($"{d.Id,-3} {d.Width}x{d.Height}@{d.RefreshHz}  at ({d.X},{d.Y})");
            return 0;

        case ("displays", 2):
            foreach (var d in await Coordinator.GetDisplaysAsync(LoadConfig(configPath), LoadLocal(localPath), argList[1]))
                Console.WriteLine($"{d.Id,-3} {d.Width}x{d.Height}@{d.RefreshHz}  at ({d.X},{d.Y})");
            return 0;

        case ("video-test", >= 1) when OperatingSystem.IsWindows():
        {
            int x = argList.Count > 2 ? int.Parse(argList[1]) : 0, y = argList.Count > 2 ? int.Parse(argList[2]) : 0;
            double seconds = argList.Count > 3 ? double.Parse(argList[3], System.Globalization.CultureInfo.InvariantCulture) : 5;
            string? bmp = argList.Count > 4 ? argList[4] : null;
            SetProcessDpiAwarenessContext(-4); // per-monitor v2: physical pixels, as desktop duplication needs
            int fps = int.TryParse(Environment.GetEnvironmentVariable("DESKBRIDGE_FPS"), out int f) ? f : 60;
            var r = DeskBridge.Platform.Windows.Video.LoopbackTest.Run(x, y, seconds, fps, 20, bmp, Console.WriteLine);
            Console.WriteLine($"{r.Width}x{r.Height}: {r.Encoded / r.Seconds:F1} fps encoded ({r.Captured} screen changes), {r.Decoded} decoded, "
                + $"{r.AverageKiB:F1} KiB/frame, {r.Mbps:F1} Mbps, {r.KeyFrames} key frames, encode {r.EncodeMs:F1} ms, decode {r.DecodeMs:F1} ms");
            if (r.Picture is not null)
                Console.WriteLine($"Last decoded picture: {r.Picture}");
            return 0;
        }

        case ("video-net-test", >= 1) when OperatingSystem.IsWindows():
        {
            // Sender and receiver on this PC over a real (loopback) video connection: handshake, encryption, decoding.
            int x = argList.Count > 2 ? int.Parse(argList[1]) : 0, y = argList.Count > 2 ? int.Parse(argList[2]) : 0;
            double seconds = argList.Count > 3 ? double.Parse(argList[3], System.Globalization.CultureInfo.InvariantCulture) : 5;
            bool show = argList.Contains("--show");
            SetProcessDpiAwarenessContext(-4);
            var key = ControlAuth.NewKey();
            var keyBytes = ControlAuth.DecodeKey(key);
            DeskBridge.Platform.Windows.Video.VideoReceiver? receiver = null;
            await using var server = new ControlServer("receiver", keyBytes, (_, _, _) => Task.FromResult<ControlMessage>(new OkMessage()), 0,
                System.Net.IPAddress.Loopback, Console.WriteLine);
            server.VideoOpened += channel => receiver = !OperatingSystem.IsWindows() ? null : new DeskBridge.Platform.Windows.Video.VideoReceiver(
                channel, show ? "DeskBridge video test" : null, null, Console.WriteLine);
            server.Start();
            var channel = await ControlClient.OpenVideoAsync("127.0.0.1", server.Port, "sender", "receiver", keyBytes, "test");
            var sender = new DeskBridge.Platform.Windows.Video.VideoSender(channel, x, y, 60, 20, Console.WriteLine);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var stats = receiver?.Stats;
            await sender.DisposeAsync();
            if (receiver is not null)
                await receiver.DisposeAsync();
            Console.WriteLine(stats is { } s
                ? $"Received {s.Frames} frames ({s.Frames / seconds:F1} fps), {s.Bytes * 8 / 1e6 / seconds:F1} Mbps, failure: {sender.Failure ?? "none"}"
                : "No video connection arrived.");
            return 0;
        }

        case ("remote", >= 3):
        {
            // Through this PC's running agent, over the session the machine keeps with it.
            var local = LoadLocal(localPath);
            string target = argList[1];
            await using var session = await OpenOwnAgentAsync(local);
            // This PC itself is asked directly; any other machine through the agent.
            ControlMessage Via(ControlMessage m) => target == local.Machine ? m : new RelayMessage(target, m);
            switch (argList[2])
            {
                case "log":
                {
                    int count = argList.Count > 3 ? int.Parse(argList[3]) : 60;
                    var reply = await session.RequestAsync(Via(new GetDiagnosticsMessage()));
                    if (reply is not DiagnosticsMessage d)
                        return Fail(reply is ErrorMessage e ? e.Message : $"Unexpected reply: {reply.GetType().Name}");
                    Console.WriteLine($"{d.Machine} {d.Version}  {d.AppPath}  accepts updates: {d.AcceptsUpdates}");
                    foreach (var line in d.Log.TakeLast(count))
                        Console.WriteLine(line);
                    return 0;
                }
                case "displays":
                {
                    var reply = await session.RequestAsync(Via(new GetDisplaysMessage()));
                    if (reply is not DisplaysMessage list)
                        return Fail(reply is ErrorMessage e ? e.Message : reply.GetType().Name);
                    foreach (var d in list.Displays)
                        Console.WriteLine($"{d.Id}: {d.Width}x{d.Height}@{d.RefreshHz} at ({d.X},{d.Y}) scale {d.Scale}%");
                    return 0;
                }
                case "probe-vdd":
                {
                    var reply = await session.RequestAsync(Via(new ProbeMessage("vdd")));
                    Console.WriteLine(reply is ReportMessage r ? r.Text : reply is ErrorMessage e ? e.Message : reply.GetType().Name);
                    return 0;
                }
                case "update" when argList.Count >= 4:
                {
                    var file = argList[3];
                    var version = argList.Count > 4 ? argList[4]
                        : System.Diagnostics.FileVersionInfo.GetVersionInfo(file).ProductVersion?.Split('+')[0]
                          ?? throw new ArgumentException("Give the version: the file has none.");
                    var bytes = await File.ReadAllBytesAsync(file);
                    var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                    async Task Send(ControlMessage m)
                    {
                        if (await session.RequestAsync(Via(m)) is ErrorMessage e)
                            throw new ControlException(e.Message);
                    }
                    Log($"Sending DeskBridge {version} ({bytes.Length / 1048576.0:F1} MiB) to {target}...");
                    await Send(new UpdateStartMessage(version, bytes.Length, sha));
                    const int chunk = 1024 * 1024;
                    for (int offset = 0; offset < bytes.Length; offset += chunk)
                        await Send(new UpdateChunkMessage(offset, Convert.ToBase64String(bytes, offset, Math.Min(chunk, bytes.Length - offset))));
                    await Send(new UpdateEndMessage());
                    Log($"{target} is restarting with {version}.");
                    return 0;
                }
                default:
                    return Fail(usage);
            }
        }

        case ("probe", >= 2):
        {
            var local = LoadLocal(localPath);
            double seconds = argList.Count > 2 ? double.Parse(argList[2], System.Globalization.CultureInfo.InvariantCulture) : 10;
            await using var session = await OpenOwnAgentAsync(local);
            var reply = await session.RequestAsync(new VideoProbeMessage(argList[1], seconds));
            if (reply is not ReportMessage report)
                return Fail(reply is ErrorMessage e ? e.Message : $"Unexpected reply: {reply.GetType().Name}");
            Console.WriteLine(report.Text);
            return 0;
        }

        case ("keygen", 1):
            Console.WriteLine(ControlAuth.NewKey());
            Console.Error.WriteLine("Put this same key in \"sharedKey\" of deskbridge.local.json on every machine.");
            return 0;

        case ("agent", 1):
            return await RunAgentAsync(LoadLocal(localPath));

        case ("switch", 2):
        {
            var config = LoadConfig(configPath);
            var results = await Coordinator.SwitchProfileAsync(config, LoadLocal(localPath), argList[1]);
            foreach (var r in results)
            {
                Console.WriteLine($"{r.Machine}: {(r.Ok ? "" : "ERROR ")}{r.Message}");
                foreach (var w in r.Warnings)
                    Console.WriteLine($"    warning: {w}");
            }
            return results.All(r => r.Ok) ? 0 : 1;
        }
        default:
            Console.Error.WriteLine(usage);
            return 2;
    }
}
catch (Exception ex) when (ex is ConfigException or ControlException or ArgumentException)
{
    return Fail(ex.Message);
}

static async Task<int> RunAgentAsync(LocalSettings local)
{
    var stopped = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopped.TrySetResult();
    };

    await using var agent = new AgentHost(local, Simulated.Applier(Log), PlatformServices.DisplayDetector(), Log);
    try
    {
        agent.Start();
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        return Fail($"Could not listen on port {local.ControlPort}: {ex.Message}");
    }

    Log("Press Ctrl+C to quit.");
    await stopped.Task;
    return 0;
}

/// <summary>A session with this PC's own running agent (it accepts its own machine id from this PC only).</summary>
static Task<ControlSession> OpenOwnAgentAsync(LocalSettings local) =>
    ControlClient.OpenAsync("127.0.0.1", local.ControlPort, local.Machine, local.Machine, ControlAuth.DecodeKey(local.SharedKey));

static DeskBridgeConfig LoadConfig(string path)
{
    try
    {
        return ConfigLoader.Load(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
    {
        throw new ArgumentException($"Could not read '{path}': {ex.Message}");
    }
}

static LocalSettings LoadLocal(string path)
{
    try
    {
        return LocalSettings.Load(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
    {
        throw new ArgumentException($"Could not read '{path}': {ex.Message} (start the GUI once, or copy deskbridge.local.example.json)");
    }
}

static string? TakeOption(List<string> list, string name)
{
    int i = list.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= list.Count) throw new ArgumentException($"Missing value after {name}.");
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static void PrintPlan(DeskBridgeConfig config, LayoutPlan plan)
{
    Console.WriteLine($"Profile '{plan.Profile}' (keyboard and mouse on '{plan.Coordinator}')");
    foreach (var machine in config.Machines)
    {
        Console.WriteLine();
        Console.WriteLine($"== {machine.Name} ({machine.Id}) ==");
        Console.WriteLine($"  Virtual displays to create: {plan.VirtualDisplaysOf(machine.Id).Count()}");
        foreach (var s in plan.StreamsFrom(machine.Id))
            Console.WriteLine($"  Sends {s.SourceVirtualDisplay} ({s.Width}x{s.Height}@{s.RefreshHz}, {s.BitrateMbps} Mbps) -> {s.SinkMachine} display {s.SinkDisplay}");
        foreach (var s in plan.StreamsTo(machine.Id))
            Console.WriteLine($"  Shows {s.SourceVirtualDisplay} on its display {s.SinkDisplay}");
    }
    Console.WriteLine();
    Console.WriteLine($"Total streams: {plan.Streams.Count}");
    foreach (var w in plan.Warnings)
        Console.WriteLine($"Warning: {w}");
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(nint context);

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
