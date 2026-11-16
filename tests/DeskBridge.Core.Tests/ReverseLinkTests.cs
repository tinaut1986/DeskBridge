using System.Net;
using System.Net.Sockets;
using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Simulation;
using DeskBridge.Core.Transfer;

namespace DeskBridge.Core.Tests;

/// <summary>
/// A laptop whose firewall blocks every incoming connection: its agent does not listen at all, connects
/// to the desktop (coordinator) and is reached over that connection.
/// </summary>
public class ReverseLinkTests
{
    private sealed class FixedDetector(params DisplayConfig[] displays) : IDisplayDetector
    {
        public IReadOnlyList<DisplayConfig> DetectDisplays() => displays;
    }

    private sealed class Setup : IAsyncDisposable
    {
        public readonly DeskBridgeConfig Config;
        public readonly LocalSettings Desktop, Laptop;
        public AgentHost DesktopAgent;
        public readonly AgentHost LaptopAgent;
        public readonly string Inbox = Path.Combine(Path.GetTempPath(), $"deskbridge-reverse-{Guid.NewGuid():N}");
        public readonly List<string> LaptopLog = [];

        public Setup()
        {
            var key = ControlAuth.NewKey();
            int desktopPort = FreePort();
            int unreachable = FreePort(); // nothing listens there: the laptop "cannot be dialled"
            var example = LayoutPlannerTests.Example();
            Config = example with
            {
                Machines = example.Machines
                    .Select(m => m with { Address = "127.0.0.1", ControlPort = m.Id == "desktop" ? desktopPort : unreachable })
                    .ToList(),
            };
            Desktop = new LocalSettings("desktop", key, desktopPort);
            Laptop = new LocalSettings("laptop", key, unreachable, ConnectTo: $"127.0.0.1:{desktopPort}");

            DesktopAgent = NewDesktopAgent();
            DesktopAgent.Start();
            LaptopAgent = new AgentHost(Laptop, Simulated.Applier(_ => { }), new FixedDetector(new DisplayConfig("1", 2560, 1600)),
                s => { lock (LaptopLog) LaptopLog.Add(s); }, new FileReceiver(() => Inbox));
            LaptopAgent.Start(listen: false);
        }

        public AgentHost NewDesktopAgent() =>
            new(Desktop, Simulated.Applier(_ => { }), new FixedDetector(new DisplayConfig("1", 1920, 1080)), _ => { });

        public async Task WaitConnectedAsync()
        {
            for (int i = 0; i < 100 && DesktopAgent.Directory.Find("laptop") is null; i++)
                await Task.Delay(50);
            Assert.NotNull(DesktopAgent.Directory.Find("laptop"));
            Assert.Equal("desktop", LaptopAgent.Reverse!.ConnectedTo);
        }

        public async ValueTask DisposeAsync()
        {
            await LaptopAgent.DisposeAsync();
            await DesktopAgent.DisposeAsync();
            if (Directory.Exists(Inbox))
                Directory.Delete(Inbox, recursive: true);
        }
    }

    [Fact]
    public async Task Coordinator_reaches_a_laptop_that_only_connects_out()
    {
        await using var s = new Setup();
        await s.WaitConnectedAsync();

        // Without the directory the laptop is unreachable...
        var direct = await Coordinator.SwitchProfileAsync(s.Config, s.Desktop, "work");
        Assert.False(direct.Single(r => r.Machine == "laptop").Ok);

        // ...with it, everything goes over the laptop's own connection.
        var results = await Coordinator.SwitchProfileAsync(s.Config, s.Desktop, "work", s.DesktopAgent.Directory);
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal("work", s.LaptopAgent.ActiveProfile);

        var displays = await Coordinator.GetDisplaysAsync(s.Config, s.Desktop, "laptop", s.DesktopAgent.Directory);
        Assert.Equal(2560, Assert.Single(displays).Width);
    }

    [Fact]
    public async Task Clipboard_and_files_flow_both_ways_over_the_laptop_connection()
    {
        await using var s = new Setup();
        await s.WaitConnectedAsync();
        string? laptopGot = null, desktopGot = null;
        s.LaptopAgent.ClipboardHandler = (_, c) => { laptopGot = c.Text; return Task.FromResult(true); };
        s.DesktopAgent.ClipboardHandler = (_, c) => { desktopGot = c.Text; return Task.FromResult(true); };

        // Desktop -> laptop, over the session the laptop opened.
        Assert.True(Assert.Single(await Peers.SendClipboardAsync(s.Config, s.Desktop, new ClipboardMessage("to laptop", null), s.DesktopAgent.Directory)).Ok);
        // Laptop -> desktop, over the same session (found in the laptop's directory).
        Assert.True(Assert.Single(await Peers.SendClipboardAsync(s.Config, s.Laptop, new ClipboardMessage("to desktop", null), s.LaptopAgent.Directory)).Ok);
        Assert.Equal(("to laptop", "to desktop"), (laptopGot, desktopGot));

        var source = Path.Combine(Path.GetTempPath(), $"deskbridge-reverse-src-{Guid.NewGuid():N}.bin");
        var data = new byte[Peers.ChunkBytes + 777];
        new Random(7).NextBytes(data);
        await File.WriteAllBytesAsync(source, data);
        try
        {
            var saved = Assert.Single(await Peers.SendFilesAsync(s.Config, s.Desktop, "laptop", [source], directory: s.DesktopAgent.Directory));
            Assert.Equal(data, await File.ReadAllBytesAsync(saved));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task The_laptop_reconnects_after_the_coordinator_restarts()
    {
        await using var s = new Setup();
        await s.WaitConnectedAsync();

        await s.DesktopAgent.DisposeAsync();
        for (int i = 0; i < 100 && s.LaptopAgent.Reverse!.ConnectedTo is not null; i++)
            await Task.Delay(50);
        Assert.Null(s.LaptopAgent.Reverse!.ConnectedTo);

        s.DesktopAgent = s.NewDesktopAgent();
        s.DesktopAgent.Start();
        // First retry comes after ~2 s of backoff.
        for (int i = 0; i < 200 && s.DesktopAgent.Directory.Find("laptop") is null; i++)
            await Task.Delay(50);
        await s.WaitConnectedAsync();
    }

    [Fact]
    public async Task Only_the_coordinator_the_laptop_connects_to_can_replace_configurations()
    {
        await using var s = new Setup();
        await s.WaitConnectedAsync();
        DeskBridgeConfig? laptopGot = null, desktopGot = null;
        s.LaptopAgent.ConfigReceived += c => laptopGot = c;
        s.DesktopAgent.ConfigReceived += c => desktopGot = c;

        // The laptop's own first-run config names itself as the coordinator; it must not impose it on the desktop.
        var laptopAsCoordinator = s.Config with
        {
            Machines = s.Config.Machines.Select(m => m with { HasInput = m.Id == "laptop" }).ToList(),
        };
        await using (var toDesktop = await Peers.ConnectAsync(s.Config, s.Laptop, "desktop", s.LaptopAgent.Directory))
        {
            Assert.IsType<ErrorMessage>(await toDesktop.RequestAsync(new ConfigSyncMessage(laptopAsCoordinator)));
            Assert.IsType<ErrorMessage>(await toDesktop.RequestAsync(new ApplyProfileMessage(laptopAsCoordinator, "personal")));
        }
        Assert.Null(desktopGot);

        // The coordinator the laptop connects to can.
        await Coordinator.SyncConfigAsync(s.Config, s.DesktopAgent.Directory);
        for (int i = 0; i < 50 && laptopGot is null; i++)
            await Task.Delay(20);
        Assert.Equal(s.Config.Machines.Count, laptopGot?.Machines.Count);
    }

    [Fact]
    public async Task Input_notifications_arrive_in_order_and_the_leave_comes_back()
    {
        await using var s = new Setup();
        await s.WaitConnectedAsync();
        var received = new List<int>();
        var allIn = new TaskCompletionSource();
        s.LaptopAgent.InputHandler = async (from, message) =>
        {
            if (message is InputEventsMessage events)
            {
                lock (received)
                {
                    received.AddRange(events.Events.Select(e => e.Code));
                    if (received.Count == 500)
                        allIn.TrySetResult();
                }
                // The laptop gives the cursor back over the same session, as a notification too.
                if (received.Count == 500 && s.LaptopAgent.Directory.Find(from) is { } back)
                    await back.NotifyAsync(new InputLeaveMessage(0.25));
            }
        };
        var leave = new TaskCompletionSource<double>();
        s.DesktopAgent.InputHandler = (_, message) =>
        {
            if (message is InputLeaveMessage l)
                leave.TrySetResult(l.Fraction);
            return Task.CompletedTask;
        };

        var session = s.DesktopAgent.Directory.Find("laptop")!;
        for (int i = 0; i < 500; i++)
            await session.NotifyAsync(new InputEventsMessage([new InputEvent(InputKind.Key, Code: i, Down: true)]));

        await allIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Enumerable.Range(0, 500), received);
        Assert.Equal(0.25, await leave.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_command_line_on_the_coordinator_reaches_the_laptop_through_its_agent()
    {
        await using var s = new Setup();
        await s.WaitConnectedAsync();
        var key = ControlAuth.DecodeKey(s.Desktop.SharedKey);

        // The command line talks to its own agent with its own id (accepted from this PC only)...
        await using (var cli = await ControlClient.OpenAsync("127.0.0.1", s.Desktop.ControlPort, "desktop", "desktop", key))
        {
            var reply = await cli.RequestAsync(new RelayMessage("laptop", new GetDisplaysMessage()));
            Assert.Equal(2560, Assert.Single(Assert.IsType<DisplaysMessage>(reply).Displays).Width);
            Assert.IsType<ErrorMessage>(await cli.RequestAsync(new RelayMessage("nobody", new GetDisplaysMessage())));
        }

        // ...while another machine cannot use the desktop to reach a third one.
        await using var other = await ControlClient.OpenAsync("127.0.0.1", s.Desktop.ControlPort, "intruder", "desktop", key);
        Assert.IsType<ErrorMessage>(await other.RequestAsync(new RelayMessage("laptop", new GetDisplaysMessage())));
    }

    [Theory]
    [InlineData("192.168.1.52", "192.168.1.52", 47900)]
    [InlineData("192.168.1.52:5000", "192.168.1.52", 5000)]
    [InlineData(" desktop.local ", "desktop.local", 47900)]
    [InlineData("desktop:notaport", "desktop:notaport", 47900)]
    public void Coordinator_address_parses_with_an_optional_port(string address, string host, int port)
    {
        Assert.Equal((host, port), ReverseLink.ParseAddress(address, 47900));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
