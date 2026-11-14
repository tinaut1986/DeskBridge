using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Simulation;
using DeskBridge.Core.Transfer;

namespace DeskBridge.Core.Tests;

public class ControlChannelTests
{
    private static readonly byte[] Key = ControlAuth.DecodeKey(ControlAuth.NewKey());

    private static ControlServer StartServer(byte[] key, ControlRequestHandler onRequest, string machine = "laptop")
    {
        var server = new ControlServer(machine, key, onRequest, port: 0, IPAddress.Loopback);
        server.Start();
        return server;
    }

    private static Task<ControlMessage> Send(ControlServer server, byte[] key, ControlMessage request, string expected = "laptop") =>
        ControlClient.SendAsync("127.0.0.1", server.Port, "desktop", expected, key, request);

    private static ApplyProfileMessage Request() => new(LayoutPlannerTests.Example(), "work");

    [Fact]
    public async Task Request_reaches_the_agent_with_the_authenticated_sender()
    {
        (string From, ApplyProfileMessage Message)? received = null;
        await using var server = StartServer(Key, (from, m, _) =>
        {
            received = (from, (ApplyProfileMessage)m);
            return Task.FromResult<ControlMessage>(new AppliedMessage("laptop", "work", ["ok"]));
        });

        var applied = Assert.IsType<AppliedMessage>(await Send(server, Key, Request()));

        Assert.Equal(["ok"], applied.Warnings);
        Assert.Equal("desktop", received!.Value.From);
        Assert.Equal(30, received.Value.Message.Config.Profiles.Single(p => p.Name == "work").Assignments.Single(a => a.Display == "3").Stream!.BitrateMbps);
    }

    [Fact]
    public async Task Several_requests_share_one_encrypted_connection()
    {
        int count = 0;
        await using var server = StartServer(Key, (_, _, _) => Task.FromResult<ControlMessage>(new ErrorMessage($"#{Interlocked.Increment(ref count)}")));

        await using var connection = await ControlClient.OpenAsync("127.0.0.1", server.Port, "desktop", "laptop", Key);
        for (int i = 1; i <= 3; i++)
            Assert.Equal($"#{i}", Assert.IsType<ErrorMessage>(await connection.RequestAsync(new GetDisplaysMessage())).Message);
    }

    [Fact]
    public async Task Wrong_key_is_rejected_and_nothing_is_handled()
    {
        bool called = false;
        await using var server = StartServer(Key, (_, _, _) =>
        {
            called = true;
            return Task.FromResult<ControlMessage>(new OkMessage());
        });

        var ex = await Assert.ThrowsAsync<ControlException>(() => Send(server, ControlAuth.DecodeKey(ControlAuth.NewKey()), Request()));

        Assert.Contains("Authentication failed", ex.Message);
        Assert.False(called);
    }

    /// <summary>Someone listening at the agent's address without the key cannot get anything sent to it.</summary>
    [Fact]
    public async Task A_fake_agent_without_the_key_is_detected_before_any_request_is_sent()
    {
        using var fake = new TcpListener(IPAddress.Loopback, 0);
        fake.Start();
        int port = ((IPEndPoint)fake.LocalEndpoint).Port;
        string? secondLine = null;
        var fakeAgent = Task.Run(async () =>
        {
            using var client = await fake.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var nonce = ControlAuth.NewNonce();
            await stream.WriteAsync(Encoding.UTF8.GetBytes($$"""{"type":"challenge","version":{{ControlProtocol.Version}},"machine":"laptop","nonce":"{{nonce}}"}""" + "\n"));
            using var reader = new StreamReader(stream);
            await reader.ReadLineAsync(); // auth
            await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"type":"welcome","proof":"Zm9yZ2Vk"}""" + "\n"));
            secondLine = await reader.ReadLineAsync(); // must be nothing: the client hangs up
        });

        var ex = await Assert.ThrowsAsync<ControlException>(() =>
            ControlClient.SendAsync("127.0.0.1", port, "desktop", "laptop", Key, new ClipboardMessage("secret", null)));

        Assert.Contains("could not prove", ex.Message);
        await fakeAgent;
        Assert.Null(secondLine);
    }

    [Fact]
    public async Task Wrong_machine_at_address_is_detected()
    {
        await using var server = StartServer(Key, (_, _, _) => Task.FromResult<ControlMessage>(new OkMessage()), machine: "other");

        var ex = await Assert.ThrowsAsync<ControlException>(() => Send(server, Key, Request()));

        Assert.Contains("'other'", ex.Message);
    }

    [Fact]
    public async Task Handler_failure_comes_back_as_error_message()
    {
        await using var server = StartServer(Key, (_, _, _) => throw new InvalidOperationException("driver not installed"));

        Assert.Equal("driver not installed", Assert.IsType<ErrorMessage>(await Send(server, Key, Request())).Message);
    }

    [Fact]
    public async Task Garbage_before_auth_does_not_kill_the_server()
    {
        await using var server = StartServer(Key, (_, _, _) => Task.FromResult<ControlMessage>(new OkMessage()));

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port);
            await raw.GetStream().WriteAsync("this is not json\n"u8.ToArray());
        }

        Assert.IsType<OkMessage>(await Send(server, Key, Request()));
    }

    [Fact]
    public async Task Nothing_listening_gives_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<ControlException>(() =>
            ControlClient.SendAsync("127.0.0.1", FreePort(), "desktop", "laptop", Key, Request()));

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

    /// <summary>Two real agents on loopback, driven like the GUI does.</summary>
    private sealed class TwoMachines : IAsyncDisposable
    {
        public readonly DeskBridgeConfig Config;
        public readonly LocalSettings Desktop, Laptop;
        public readonly AgentHost DesktopAgent, LaptopAgent;
        public readonly string LaptopInbox = Path.Combine(Path.GetTempPath(), $"deskbridge-inbox-{Guid.NewGuid():N}");
        public bool LaptopAcceptsFiles = true;

        public TwoMachines()
        {
            var key = ControlAuth.NewKey();
            int desktopPort = FreePort(), laptopPort = FreePort();
            var example = LayoutPlannerTests.Example();
            Config = example with
            {
                Machines = example.Machines
                    .Select(m => m with { Address = "127.0.0.1", ControlPort = m.Id == "desktop" ? desktopPort : laptopPort })
                    .ToList(),
            };
            Desktop = new LocalSettings("desktop", key, desktopPort);
            Laptop = new LocalSettings("laptop", key, laptopPort);
            DesktopAgent = new AgentHost(Desktop, Simulated.Applier(_ => { }), new FixedDetector(), _ => { });
            LaptopAgent = new AgentHost(Laptop, Simulated.Applier(_ => { }), new FixedDetector(
                new DisplayConfig("1", 1920, 1080), new DisplayConfig("2", 2560, 1440, 75, 1920, 0)), _ => { },
                new FileReceiver(() => LaptopAcceptsFiles ? LaptopInbox : null));
            DesktopAgent.Start();
            LaptopAgent.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await DesktopAgent.DisposeAsync();
            await LaptopAgent.DisposeAsync();
            if (Directory.Exists(LaptopInbox))
                Directory.Delete(LaptopInbox, recursive: true);
        }
    }

    [Fact]
    public async Task Coordinator_switches_profile_and_reads_remote_displays()
    {
        await using var m = new TwoMachines();
        DeskBridgeConfig? laptopGot = null;
        m.LaptopAgent.ConfigReceived += c => laptopGot = c;

        var results = await Coordinator.SwitchProfileAsync(m.Config, m.Desktop, "work");
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal("work", m.DesktopAgent.ActiveProfile);
        Assert.Equal("work", m.LaptopAgent.ActiveProfile);
        Assert.Equal(m.Config.Machines.Count, laptopGot?.Machines.Count);

        var displays = await Coordinator.GetDisplaysAsync(m.Config, m.Desktop, "laptop");
        Assert.Equal((2560, 1440, 75, 1920), (displays[1].Width, displays[1].Height, displays[1].RefreshHz, displays[1].X));

        await Assert.ThrowsAsync<ControlException>(() => Coordinator.SwitchProfileAsync(m.Config, m.Laptop, "work"));
    }

    [Fact]
    public async Task Only_the_coordinator_can_apply_a_profile()
    {
        await using var m = new TwoMachines();

        // The laptop has the key but is not the coordinator: it talks to the desktop's agent directly.
        await using var connection = await Peers.ConnectAsync(m.Config, m.Laptop, "desktop");
        var reply = await connection.RequestAsync(new ApplyProfileMessage(m.Config, "work"));

        Assert.Contains("Only the coordinator", Assert.IsType<ErrorMessage>(reply).Message);
        Assert.Null(m.DesktopAgent.ActiveProfile);
    }

    [Fact]
    public async Task Clipboard_goes_to_the_other_machines_that_accept_it()
    {
        await using var m = new TwoMachines();
        (string From, ClipboardMessage Message)? got = null;
        m.DesktopAgent.ClipboardHandler = (from, c) =>
        {
            got = (from, c);
            return Task.FromResult(true);
        };

        var results = await Peers.SendClipboardAsync(m.Config, m.Laptop, new ClipboardMessage("hola ñ 😀", null));

        Assert.True(Assert.Single(results).Ok);
        Assert.Equal(("laptop", "hola ñ 😀"), (got!.Value.From, got.Value.Message.Text));

        // The laptop has no handler: it refuses.
        var refused = Assert.Single(await Peers.SendClipboardAsync(m.Config, m.Desktop, new ClipboardMessage("x", null)));
        Assert.False(refused.Ok);
    }

    [Fact]
    public async Task Files_arrive_intact_with_a_safe_unique_name()
    {
        await using var m = new TwoMachines();
        var source = Path.Combine(Path.GetTempPath(), $"deskbridge-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            var big = Path.Combine(source, "big.bin");
            var data = new byte[Peers.ChunkBytes * 2 + 12345];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(big, data);
            var note = Path.Combine(source, "note.txt");
            await File.WriteAllTextAsync(note, "first");

            var progress = new List<long>();
            var saved = await Peers.SendFilesAsync(m.Config, m.Desktop, "laptop", [big, note], new Progress<(string, long Done, long)>(p => progress.Add(p.Done)));
            Assert.Equal(data, await File.ReadAllBytesAsync(saved[0]));
            Assert.Equal(Path.Combine(m.LaptopInbox, "note.txt"), saved[1]);

            await File.WriteAllTextAsync(note, "second");
            var again = Assert.Single(await Peers.SendFilesAsync(m.Config, m.Desktop, "laptop", [note]));
            Assert.Equal(Path.Combine(m.LaptopInbox, "note (2).txt"), again);
            Assert.Equal("second", await File.ReadAllTextAsync(again));
            Assert.Empty(Directory.GetFiles(m.LaptopInbox, "*.part", new EnumerationOptions { AttributesToSkip = 0 }));

            m.LaptopAcceptsFiles = false;
            var ex = await Assert.ThrowsAsync<ControlException>(() => Peers.SendFilesAsync(m.Config, m.Desktop, "laptop", [note]));
            Assert.Contains("does not accept files", ex.Message);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData(@"..\..\Windows\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a<b>c?.txt", "a_b_c_.txt")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    public void Received_file_names_cannot_escape_the_folder(string sent, string kept)
    {
        Assert.Equal(kept, FileReceiver.SafeName(sent));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
