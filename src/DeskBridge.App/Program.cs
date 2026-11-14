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

      deskbridge keygen                       Generate a new shared key
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

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
