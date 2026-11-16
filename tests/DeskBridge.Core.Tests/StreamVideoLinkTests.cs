using DeskBridge.Core.Video;

namespace DeskBridge.Core.Tests;

public class StreamVideoLinkTests
{
    [Fact]
    public async Task Packets_arrive_whole_and_in_order()
    {
        var stream = new MemoryStream();
        var writer = new StreamVideoLink(stream);
        await writer.SendAsync(VideoPacket.Frame, new byte[] { 1, 2 }, new byte[] { 3, 4, 5 });
        await writer.SendAsync(VideoPacket.KeyFrameRequest, default, default);

        stream.Position = 0;
        var reader = new StreamVideoLink(stream);
        var first = await reader.ReceiveAsync();
        Assert.Equal(VideoPacket.Frame, first!.Value.Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, first.Value.Body);
        var second = await reader.ReceiveAsync();
        Assert.Equal(VideoPacket.KeyFrameRequest, second!.Value.Type);
        Assert.Empty(second.Value.Body);
        Assert.Null(await reader.ReceiveAsync()); // closed
    }
}
