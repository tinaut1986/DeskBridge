using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Video;

namespace DeskBridge.Core.Tests;

/// <summary>
/// The protocol as other implementations (the Android app) must speak it: a fixed key and fixed nonces, the
/// proofs, session keys, JSON messages and encrypted lines and video packets they give. The file lives with the
/// Android tests, which read it; this test fails when the C# side no longer produces the same bytes.
/// Set DESKBRIDGE_WRITE_VECTORS=1 to rewrite it after a deliberate protocol change (and update Android).
/// </summary>
public class ProtocolVectorTests
{
    private static readonly string VectorsPath = Path.Combine(
        RepoRoot(), "android", "protocol", "src", "test", "resources", "protocol-vectors.json");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskBridge.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static string Json(ControlMessage message) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, ConfigLoader.JsonOptions));

    /// <summary>A line as <c>LineChannel</c> writes it once encrypted (without the newline).</summary>
    private static string SealLine(byte[] key, ulong counter, string json)
    {
        var plain = Encoding.UTF8.GetBytes(json);
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
        var sealedBytes = new byte[plain.Length + 16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length));
        return Convert.ToBase64String(sealedBytes);
    }

    private static JsonObject Build()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var serverNonce = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var clientNonce = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray());
        const string client = "tablet", server = "desktop";
        var (c2s, s2c) = ControlAuth.SessionKeys(key, serverNonce, clientNonce);

        var request = Json(new SessionFrame(5, false, new GetDisplaysMessage()));
        var reply = Json(new SessionFrame(5, true, new DisplaysMessage("tablet", [new DisplayConfig("1", 2560, 1600, 120, 0, 0, 200)])));
        var ping = Json(new SessionFrame(6, false, new PingMessage()));

        var messages = new JsonObject
        {
            ["challenge"] = Json(new ChallengeMessage(ControlProtocol.Version, server, serverNonce)),
            ["auth"] = Json(new AuthMessage(client, clientNonce, ControlAuth.ClientProof(key, serverNonce, clientNonce, client, server), Link: true)),
            ["authVideo"] = Json(new AuthMessage(client, clientNonce, "p", Video: "desktop/v1->tablet/1")),
            ["welcome"] = Json(new WelcomeMessage(ControlAuth.ServerProof(key, serverNonce, clientNonce, client, server))),
            ["error"] = Json(new ErrorMessage("no")),
            ["ok"] = Json(new SessionFrame(7, true, new OkMessage())),
            ["applied"] = Json(new AppliedMessage("tablet", "personal", ["w1"])),
            ["startVideo"] = Json(new StartVideoMessage("desktop/v1->tablet/1", false, "1", 60, 20)),
            ["videoStarted"] = Json(new VideoStartedMessage(0, 0, 2560, 1600)),
            ["stopVideo"] = Json(new StopVideoMessage(null)),
            ["diagnostics"] = Json(new DiagnosticsMessage("tablet", "0.1.0", "desktop:47900", 47900, false, null, ["line"])),
            ["inputEvents"] = Json(new InputEventsMessage([new InputEvent(InputKind.MouseMove, 3, -2), new InputEvent(InputKind.Key, Code: 0x1E, Down: true)], 9)),
            ["inputEnter"] = Json(new InputEnterMessage(PlacementSide.Below, 0.5, 10, 0)),
            ["inputLeave"] = Json(new InputLeaveMessage(0.25, 640, 0)),
            ["inputCursor"] = Json(new InputCursorMessage(9, 100, 200)),
        };

        // Video, as the coordinator sends it to the tablet (the tablet opened the connection, so it is the client).
        var video = new MemoryStream();
        var tcp = new TcpClient();
        var channel = new VideoChannel(tcp, video, client, "s", sendKey: s2c, receiveKey: c2s);
        var format = new VideoFormat(2560, 1600, 60, 20, "GPU", "Encoder");
        var frame = new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0x84 };
        channel.SendAsync(VideoPacket.Format, JsonSerializer.SerializeToUtf8Bytes(format), default).GetAwaiter().GetResult();
        channel.SendAsync(VideoPacket.Frame, VideoChannel.FrameHeader(1_234_567, keyFrame: true), frame).GetAwaiter().GetResult();
        channel.SendAsync(VideoPacket.Pointer, VideoChannel.PointerBody(-5, 300, visible: true), default).GetAwaiter().GetResult();
        channel.SendAsync(VideoPacket.PointerShape, new PointerImage(1, 2, [1, 2, 3, 4, 5, 6, 7, 8], 0, 1).ToBody(), default).GetAwaiter().GetResult();
        tcp.Dispose();

        return new JsonObject
        {
            ["protocolVersion"] = ControlProtocol.Version,
            ["key"] = Convert.ToBase64String(key),
            ["serverNonce"] = serverNonce,
            ["clientNonce"] = clientNonce,
            ["client"] = client,
            ["server"] = server,
            ["clientProof"] = ControlAuth.ClientProof(key, serverNonce, clientNonce, client, server),
            ["serverProof"] = ControlAuth.ServerProof(key, serverNonce, clientNonce, client, server),
            ["clientToServer"] = Hex(c2s),
            ["serverToClient"] = Hex(s2c),
            ["messages"] = messages,
            ["serverLines"] = new JsonArray(SealLine(s2c, 0, request), SealLine(s2c, 1, ping)),
            ["serverLinesJson"] = new JsonArray(request, ping),
            ["clientLine"] = SealLine(c2s, 0, reply),
            ["clientLineJson"] = reply,
            ["videoFormatJson"] = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(format)),
            ["videoFrame"] = Hex(frame),
            ["videoServerBytes"] = Hex(video.ToArray()),
        };
    }

    [Fact]
    public void The_vectors_the_Android_tests_read_match_this_implementation()
    {
        var text = Build().ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("DESKBRIDGE_WRITE_VECTORS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VectorsPath)!);
            File.WriteAllText(VectorsPath, text);
        }
        Assert.True(File.Exists(VectorsPath), $"Missing {VectorsPath}: run with DESKBRIDGE_WRITE_VECTORS=1.");
        Assert.Equal(text, File.ReadAllText(VectorsPath).ReplaceLineEndings("\n"));
    }
}
