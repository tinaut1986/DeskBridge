using System.Net;
using System.Net.Sockets;
using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Simulation;

namespace DeskBridge.Core.Tests;

public class ControlChannelTests
{
    private static readonly byte[] Key = ControlAuth.DecodeKey(ControlAuth.NewKey());

    private static ControlServer StartServer(
        byte[] key,
        Func<ControlMessage, CancellationToken, Task<ControlMessage>> onRequest,
        string machine = "laptop")
    {
        var server = new ControlServer(machine, key, onRequest, port: 0, IPAddress.Loopback);
        server.Start();
        return server;
    }

    private static Task<ControlMessage> Send(ControlServer server, byte[] key, ControlMessage request, string expected = "laptop") =>
        ControlClient.SendAsync("127.0.0.1", server.Port, "desktop", expected, key, request);

    private static ApplyProfileMessage Request() => new(LayoutPlannerTests.Example(), "work");

    [Fact]
    public async Task Apply_request_reaches_the_agent_with_the_whole_config()
    {
        ApplyProfileMessage? received = null;
        await using var server = StartServer(Key, (m, _) =>
        {
            received = (ApplyProfileMessage)m;
            return Task.FromResult<ControlMessage>(new AppliedMessage("laptop", received.Profile, ["ok"]));
        });

        var reply = await Send(server, Key, Request());

        var applied = Assert.IsType<AppliedMessage>(reply);
        Assert.Equal(("laptop", "work"), (applied.Machine, applied.Profile));
        Assert.Equal(["ok"], applied.Warnings);
        Assert.NotNull(received);
        Assert.Equal(2, received.Config.Machines.Count);
        Assert.Equal(30, received.Config.Profiles.Single(p => p.Name == "work").Assignments.Single(a => a.Display == "3").Stream!.BitrateMbps);
    }

    [Fact]
    public async Task Wrong_key_is_rejected_and_nothing_is_applied()
    {
        bool called = false;
        await using var server = StartServer(Key, (_, _) =>
        {
            called = true;
            return Task.FromResult<ControlMessage>(new ErrorMessage("no"));
        });

        var otherKey = ControlAuth.DecodeKey(ControlAuth.NewKey());
        var ex = await Assert.ThrowsAsync<ControlException>(() => Send(server, otherKey, Request()));

        Assert.Contains("Authentication failed", ex.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task Wrong_machine_at_address_is_detected()
    {
        await using var server = StartServer(Key, (_, _) => Task.FromResult<ControlMessage>(new ErrorMessage("no")), machine: "other");

        var ex = await Assert.ThrowsAsync<ControlException>(() => Send(server, Key, Request()));

        Assert.Contains("'other'", ex.Message);
    }

    [Fact]
    public async Task Handler_failure_comes_back_as_error_message()
    {
        await using var server = StartServer(Key, (_, _) => throw new InvalidOperationException("driver not installed"));

        var reply = await Send(server, Key, Request());

        Assert.Equal("driver not installed", Assert.IsType<ErrorMessage>(reply).Message);
    }

    [Fact]
    public async Task Garbage_before_auth_does_not_kill_the_server()
    {
        await using var server = StartServer(Key, (m, _) => Task.FromResult<ControlMessage>(new AppliedMessage("laptop", "work", [])));

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = raw.GetStream();
            await stream.WriteAsync("this is not json\n"u8.ToArray());
            await stream.FlushAsync();
        }

        Assert.IsType<AppliedMessage>(await Send(server, Key, Request()));
    }

    [Fact]
    public async Task Nothing_listening_gives_a_clear_error()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var ex = await Assert.ThrowsAsync<ControlException>(() =>
            ControlClient.SendAsync("127.0.0.1", port, "desktop", "laptop", Key, Request()));

        Assert.Contains("Could not connect", ex.Message);
    }

    [Fact]
    public void Short_or_invalid_keys_are_refused()
    {
        Assert.Throws<ControlException>(() => ControlAuth.DecodeKey("not-base64!"));
        Assert.Throws<ControlException>(() => ControlAuth.DecodeKey(Convert.ToBase64String(new byte[8])));
    }

    private sealed class FixedDetector(params DisplayConfig[] displays) : IDisplayDetector
    {
        public IReadOnlyList<DisplayConfig> DetectDisplays() => displays;
    }

    /// <summary>Two real agents on loopback, driven through <see cref="Coordinator"/> like the GUI does.</summary>
    [Fact]
    public async Task Coordinator_switches_profile_and_reads_remote_displays_through_real_agents()
    {
        var key = ControlAuth.NewKey();
        var logs = new List<string>();
        void Log(string s) { lock (logs) logs.Add(s); }

        int desktopPort = FreePort(), laptopPort = FreePort();
        var config = LayoutPlannerTests.Example();
        config = config with
        {
            Machines = config.Machines
                .Select(m => m with { Address = "127.0.0.1", ControlPort = m.Id == "desktop" ? desktopPort : laptopPort })
                .ToList(),
        };
        var desktop = new LocalSettings("desktop", key, desktopPort);
        var laptop = new LocalSettings("laptop", key, laptopPort);

        await using var desktopAgent = new AgentHost(desktop, Simulated.Applier(Log), new FixedDetector(), Log);
        await using var laptopAgent = new AgentHost(laptop, Simulated.Applier(Log), new FixedDetector(
            new DisplayConfig("1", 1920, 1080), new DisplayConfig("2", 2560, 1440, 75, 1920, 0)), Log);
        desktopAgent.Start();
        laptopAgent.Start();

        var results = await Coordinator.SwitchProfileAsync(config, desktop, "work");
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal("work", desktopAgent.ActiveProfile);
        Assert.Equal("work", laptopAgent.ActiveProfile);

        var displays = await Coordinator.GetDisplaysAsync(config, desktop, "laptop");
        Assert.Equal(["1", "2"], displays.Select(d => d.Id));
        Assert.Equal((2560, 1440, 75, 1920), (displays[1].Width, displays[1].Height, displays[1].RefreshHz, displays[1].X));

        await Assert.ThrowsAsync<ControlException>(() => Coordinator.SwitchProfileAsync(config, laptop, "work"));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
