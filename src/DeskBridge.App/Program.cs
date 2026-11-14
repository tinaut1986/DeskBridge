using DeskBridge.App;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

const string Usage = """
    DeskBridge: comparte y reasigna pantallas entre equipos.

    Uso:
      deskbridge validar                      Comprueba la configuración
      deskbridge perfiles                     Lista los perfiles
      deskbridge plan <perfil>                Muestra qué haría cada equipo
      deskbridge aplicar <perfil> <equipo>    Aplica el perfil en este equipo (de momento, simulado)

    Opciones:
      --config <ruta>   Fichero de configuración (por defecto: deskbridge.json)
    """;

var argList = args.ToList();
string configPath = "deskbridge.json";
int configIndex = argList.IndexOf("--config");
if (configIndex >= 0)
{
    if (configIndex + 1 >= argList.Count) return Fail("Falta la ruta después de --config.");
    configPath = argList[configIndex + 1];
    argList.RemoveRange(configIndex, 2);
}

if (argList.Count == 0 || argList[0] is "-h" or "--help" or "ayuda")
{
    Console.WriteLine(Usage);
    return 0;
}

DeskBridgeConfig config;
try
{
    config = ConfigLoader.Load(configPath);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
{
    return Fail($"No se pudo leer '{configPath}': {ex.Message}");
}

try
{
    switch (argList[0])
    {
        case "validar":
        {
            var errors = LayoutPlanner.Validate(config);
            if (errors.Count > 0) throw new ConfigException(errors);
            Console.WriteLine($"Configuración correcta: {config.Machines.Count} equipos, {config.Profiles.Count} perfiles.");
            return 0;
        }
        case "perfiles":
            foreach (var p in config.Profiles)
                Console.WriteLine($"{p.Name}  ({p.Assignments.Count(a => a.UsedBy != a.Machine)} pantallas prestadas)");
            return 0;

        case "plan" when argList.Count == 2:
            PrintPlan(config, LayoutPlanner.Plan(config, argList[1]));
            return 0;

        case "aplicar" when argList.Count == 3:
        {
            var plan = LayoutPlanner.Plan(config, argList[1]);
            var applier = new PlanApplier(new DryRunVirtualDisplays(), new DryRunSender(), new DryRunReceiver(), new DryRunInputRouter());
            await applier.ApplyAsync(config, plan, argList[2]);
            return 0;
        }
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is ConfigException or ArgumentException)
{
    return Fail(ex.Message);
}

static void PrintPlan(DeskBridgeConfig config, LayoutPlan plan)
{
    Console.WriteLine($"Perfil '{plan.Profile}' (teclado y ratón en '{plan.Coordinator}')");
    foreach (var machine in config.Machines)
    {
        Console.WriteLine();
        Console.WriteLine($"== {machine.Name} ({machine.Id}) ==");
        var vds = plan.VirtualDisplaysOf(machine.Id).ToList();
        Console.WriteLine($"  Pantallas virtuales a crear: {vds.Count}");
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

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
