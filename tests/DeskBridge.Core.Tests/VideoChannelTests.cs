using System.Net;
using System.Text.Json;
using DeskBridge.Core.Control;
using DeskBridge.Core.Video;

namespace DeskBridge.Core.Tests;

public class VideoChannelTests
{
    private static ControlServer Server(byte[] key, Action<VideoChannel> opened)
    {
        var server = new ControlServer("desktop", key, (_, _, _) => Task.FromResult<ControlMessage>(new OkMessage()), 0, IPAddress.Loopback);
        server.VideoOpened += opened;
        server.Start();
        return server;
    }

    [Fact]
    public async Task Frames_go_one_way_in_order_and_key_frame_requests_come_back()
    {
        var key = ControlAuth.DecodeKey(ControlAuth.NewKey());
        var arrived = new TaskCompletionSource<VideoChannel>();
        await using var server = Server(key, c => arrived.TrySetResult(c));

        await using var laptop = await ControlClient.OpenVideoAsync("127.0.0.1", server.Port, "laptop", "desktop", key, "s1");
        await using var desktop = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("laptop", "s1"), (desktop.RemoteMachine, desktop.StreamKey));

        var format = new VideoFormat(1920, 1080, 60, 20, "GPU", "Encoder");
        var frames = Enumerable.Range(0, 50).Select(i => Enumerable.Repeat((byte)i, 1000 + i * 3000).ToArray()).ToList();
        // Sent while the other side reads (several MB: more than the sockets buffer).
        var sending = Task.Run(async () =>
        {
            await laptop.SendAsync(VideoPacket.Format, JsonSerializer.SerializeToUtf8Bytes(format), default);
            foreach (var (data, i) in frames.Select((d, i) => (d, i)))
                await laptop.SendAsync(VideoPacket.Frame, VideoChannel.FrameHeader(i * 166_667L, i == 0), data);
        });

        var first = await desktop.ReceiveAsync();
        Assert.Equal(VideoPacket.Format, first!.Value.Type);
        Assert.Equal(format, JsonSerializer.Deserialize<VideoFormat>(first.Value.Body));
        for (int i = 0; i < frames.Count; i++)
        {
            var p = await desktop.ReceiveAsync();
            Assert.Equal(VideoPacket.Frame, p!.Value.Type);
            var (time, keyFrame, data) = VideoChannel.ParseFrame(p.Value.Body);
            Assert.Equal((i * 166_667L, i == 0), (time, keyFrame));
            Assert.Equal(frames[i], data.ToArray());
        }
        await sending.WaitAsync(TimeSpan.FromSeconds(5));

        await desktop.SendAsync(VideoPacket.KeyFrameRequest);
        Assert.Equal(VideoPacket.KeyFrameRequest, (await laptop.ReceiveAsync())!.Value.Type);
    }

    [Fact]
    public async Task A_wrong_key_gets_no_video_connection()
    {
        bool opened = false;
        await using var server = Server(ControlAuth.DecodeKey(ControlAuth.NewKey()), _ => opened = true);
        await Assert.ThrowsAsync<ControlException>(() =>
            ControlClient.OpenVideoAsync("127.0.0.1", server.Port, "laptop", "desktop", ControlAuth.DecodeKey(ControlAuth.NewKey()), "s1"));
        Assert.False(opened);
    }
}
