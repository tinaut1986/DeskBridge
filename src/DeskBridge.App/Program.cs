using System.Text.Json;
using DeskBridge.App;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

const string Usage = """
    DeskBridge: comparte y reasigna pantallas entre equipos.

    Uso:
      deskbridge validar                      Comprueba la configuración
      deskbridge perfiles                     Lista los perfiles
      deskbridge plan <perfil>                Muestra qué haría cada equipo
      deskbridge aplicar <perfil> <equipo>    Simula aplicar el perfil en un equipo, sin red

      deskbridge clave                        Genera una clave compartida nueva
      deskbridge agente                       Arranca el agente de este equipo y espera órdenes
      deskbridge cambiar <perfil>             (coordinador) Aplica el perfil en todos los equipos

    Opciones:
      --config <ruta>   Configuración común (por defecto: deskbridge.json)
      --local <ruta>    Ajustes de este equipo (por defecto: deskbridge.local.json)

    De momento el agente simula las acciones: solo dice lo que haría.
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var argList = args.ToList();
string configPath = TakeOption(argList, "--config") ?? "deskbridge.json";
string localPath = TakeOption(argList, "--local") ?? "deskbridge.local.json";

if (argList.Count == 0 || argList[0] is "-h" or "--help" or "ayuda")
{
    Console.WriteLine(Usage);
    return 0;
}

try
{
    switch (argList[0], argList.Count)
    {
        case ("validar", 1):
        {
            var config = LoadConfig(configPath);
            var errors = LayoutPlanner.Validate(config);
            if (errors.Count > 0) throw new ConfigException(errors);
            Console.WriteLine($"Configuración correcta: {config.Machines.Count} equipos, {config.Profiles.Count} perfiles.");
            return 0;
        }
        case ("perfiles", 1):
            foreach (var p in LoadConfig(configPath).Profiles)
                Console.WriteLine($"{p.Name}  ({p.Assignments.Count(a => a.UsedBy != a.Machine)} pantallas prestadas)");
            return 0;

        case ("plan", 2):
        {
            var config = LoadConfig(configPath);
            PrintPlan(config, LayoutPlanner.Plan(config, argList[1]));
            return 0;
        }
        case ("aplicar", 3):
        {
            var config = LoadConfig(configPath);
            await NewApplier().ApplyAsync(config, LayoutPlanner.Plan(config, argList[1]), argList[2]);
            return 0;
        }
        case ("clave", 1):
            Console.WriteLine(ControlAuth.NewKey());
            Console.Error.WriteLine("Pon esta misma clave en \"sharedKey\" de deskbridge.local.json en todos los equipos.");
            return 0;

        case ("agente", 1):
            return await RunAgentAsync(LoadLocal(localPath));

        case ("cambiar", 2):
            return await SwitchProfileAsync(LoadConfig(configPath), LoadLocal(localPath), argList[1]);

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is ConfigException or ControlException or ArgumentException)
{
    return Fail(ex.Message);
}

static async Task<int> RunAgentAsync(LocalSettings local)
{
    var key = ControlAuth.DecodeKey(local.SharedKey);
    var applier = NewApplier();
    var stopped = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopped.TrySetResult();
    };

    await using var server = new ControlServer(
        local.Machine,
        key,
        async (message, ct) =>
        {
            Log($"Aplicando perfil '{message.Profile}'...");
            var plan = LayoutPlanner.Plan(message.Config, message.Profile);
            await applier.ApplyAsync(message.Config, plan, local.Machine, ct);
            Log($"Perfil '{plan.Profile}' aplicado.");
            return new AppliedMessage(local.Machine, plan.Profile, plan.Warnings);
        },
        local.ControlPort,
        log: Log);

    try
    {
        server.Start();
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        return Fail($"No se pudo escuchar en el puerto {local.ControlPort}: {ex.Message}");
    }

    Log($"Agente '{local.Machine}' escuchando en el puerto {server.Port}. Ctrl+C para salir.");
    await stopped.Task;
    Log("Parando agente.");
    return 0;
}

static async Task<int> SwitchProfileAsync(DeskBridgeConfig config, LocalSettings local, string profile)
{
    var plan = LayoutPlanner.Plan(config, profile); // validates before touching any machine
    if (plan.Coordinator != local.Machine)
        return Fail($"Este equipo es '{local.Machine}', pero el coordinador es '{plan.Coordinator}'. Cambia de perfil desde él.");

    var key = ControlAuth.DecodeKey(local.SharedKey);
    var request = new ApplyProfileMessage(config, profile);

    var results = await Task.WhenAll(config.Machines.Select(async machine =>
    {
        var host = machine.Id == local.Machine ? "127.0.0.1" : machine.Address;
        try
        {
            var reply = await ControlClient.SendAsync(host, machine.ControlPort ?? config.ControlPort, local.Machine, machine.Id, key, request);
            return reply switch
            {
                AppliedMessage applied => (true, $"{machine.Name}: perfil aplicado"
                    + string.Concat(applied.Warnings.Select(w => $"{Environment.NewLine}    aviso: {w}"))),
                ErrorMessage error => (false, $"{machine.Name}: ERROR {error.Message}"),
                _ => (false, $"{machine.Name}: respuesta inesperada"),
            };
        }
        catch (ControlException ex)
        {
            return (false, $"{machine.Name}: ERROR {ex.Message}");
        }
    }));

    foreach (var (_, line) in results)
        Console.WriteLine(line);
    return results.All(r => r.Item1) ? 0 : 1;
}

static PlanApplier NewApplier() =>
    new(new DryRunVirtualDisplays(), new DryRunSender(), new DryRunReceiver(), new DryRunInputRouter());

static DeskBridgeConfig LoadConfig(string path)
{
    try
    {
        return ConfigLoader.Load(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
    {
        throw new ArgumentException($"No se pudo leer '{path}': {ex.Message}");
    }
}

static LocalSettings LoadLocal(string path)
{
    try
    {
        var local = LocalSettings.Load(path);
        if (string.IsNullOrWhiteSpace(local.Machine) || string.IsNullOrWhiteSpace(local.SharedKey))
            throw new InvalidDataException("faltan \"machine\" o \"sharedKey\".");
        return local;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
    {
        throw new ArgumentException($"No se pudo leer '{path}': {ex.Message} (copia deskbridge.local.example.json)");
    }
}

static string? TakeOption(List<string> list, string name)
{
    int i = list.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= list.Count) throw new ArgumentException($"Falta el valor después de {name}.");
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

static void PrintPlan(DeskBridgeConfig config, LayoutPlan plan)
{
    Console.WriteLine($"Perfil '{plan.Profile}' (teclado y ratón en '{plan.Coordinator}')");
    foreach (var machine in config.Machines)
    {
        Console.WriteLine();
        Console.WriteLine($"== {machine.Name} ({machine.Id}) ==");
        Console.WriteLine($"  Pantallas virtuales a crear: {plan.VirtualDisplaysOf(machine.Id).Count()}");
        foreach (var s in plan.StreamsFrom(machine.Id))
            Console.WriteLine($"  Emite   {s.SourceVirtualDisplay} ({s.Width}x{s.Height}@{s.RefreshHz}) -> {s.SinkMachine} pantalla {s.SinkDisplay}");
        foreach (var s in plan.StreamsTo(machine.Id))
            Console.WriteLine($"  Muestra {s.SourceVirtualDisplay} en su pantalla {s.SinkDisplay}");
    }
    Console.WriteLine();
    Console.WriteLine($"Streams en total: {plan.Streams.Count}");
    foreach (var w in plan.Warnings)
        Console.WriteLine($"Aviso: {w}");
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
