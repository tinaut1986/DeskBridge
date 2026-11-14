using System.Net;
using System.Net.Sockets;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;

namespace DeskBridge.Core.Tests;

public class ControlChannelTests
{
    private static readonly byte[] Key = ControlAuth.DecodeKey(ControlAuth.NewKey());

    private static ControlServer StartServer(
        byte[] key,
        Func<ApplyProfileMessage, CancellationToken, Task<ControlMessage>> onApply,
        string machine = "portatil")
    {
        var server = new ControlServer(machine, key, onApply, port: 0, IPAddress.Loopback);
        server.Start();
        return server;
    }

    private static Task<ControlMessage> Send(ControlServer server, byte[] key, ControlMessage request, string expected = "portatil") =>
        ControlClient.SendAsync("127.0.0.1", server.Port, "sobremesa", expected, key, request);

    private static ApplyProfileMessage Request() =>
        new(ConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "deskbridge.example.json")), "trabajo");

    [Fact]
    public async Task Apply_request_reaches_the_agent_with_the_whole_config()
    {
        ApplyProfileMessage? received = null;
        await using var server = StartServer(Key, (m, _) =>
        {
            received = m;
            return Task.FromResult<ControlMessage>(new AppliedMessage("portatil", m.Profile, ["ok"]));
        });

        var reply = await Send(server, Key, Request());

        var applied = Assert.IsType<AppliedMessage>(reply);
        Assert.Equal(("portatil", "trabajo"), (applied.Machine, applied.Profile));
        Assert.Equal(["ok"], applied.Warnings);
        Assert.NotNull(received);
        Assert.Equal(2, received.Config.Machines.Count);
        Assert.Equal(3, received.Config.Profiles.Single(p => p.Name == "trabajo").Assignments.Count);
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

        Assert.Contains("Autenticación fallida", ex.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task Wrong_machine_at_address_is_detected()
    {
        await using var server = StartServer(Key, (_, _) => Task.FromResult<ControlMessage>(new ErrorMessage("no")), machine: "otro");

        var ex = await Assert.ThrowsAsync<ControlException>(() => Send(server, Key, Request()));

        Assert.Contains("'otro'", ex.Message);
    }

    [Fact]
    public async Task Handler_failure_comes_back_as_error_message()
    {
        await using var server = StartServer(Key, (_, _) => throw new InvalidOperationException("driver no instalado"));

        var reply = await Send(server, Key, Request());

        Assert.Equal("driver no instalado", Assert.IsType<ErrorMessage>(reply).Message);
    }

    [Fact]
    public async Task Garbage_before_auth_does_not_kill_the_server()
    {
        await using var server = StartServer(Key, (m, _) => Task.FromResult<ControlMessage>(new AppliedMessage("portatil", m.Profile, [])));

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = raw.GetStream();
            await stream.WriteAsync("esto no es json\n"u8.ToArray());
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
            ControlClient.SendAsync("127.0.0.1", port, "sobremesa", "portatil", Key, Request()));

        Assert.Contains("No se pudo conectar", ex.Message);
    }

    [Fact]
    public void Short_or_invalid_keys_are_refused()
    {
        Assert.Throws<ControlException>(() => ControlAuth.DecodeKey("no-es-base64!"));
        Assert.Throws<ControlException>(() => ControlAuth.DecodeKey(Convert.ToBase64String(new byte[8])));
    }
}
