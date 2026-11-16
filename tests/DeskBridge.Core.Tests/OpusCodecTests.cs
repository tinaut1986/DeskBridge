using DeskBridge.Core.Audio;

namespace DeskBridge.Core.Tests;

public class OpusCodecTests
{
    [Fact]
    public void A_tone_survives_encoding_and_takes_a_fraction_of_the_PCM_bandwidth()
    {
        const int rate = 48000, channels = 2;
        var encoder = new OpusEncoder(rate, channels, 128_000);
        var decoder = new OpusDecoder(rate, channels);

        // One second of a 440 Hz tone, fed in odd-sized chunks as the capture delivers them.
        var tone = new float[rate * channels];
        for (int i = 0; i < rate; i++)
            tone[2 * i] = tone[2 * i + 1] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        var packets = new List<byte[]>();
        for (int at = 0; at < tone.Length; at += 1234)
            packets.AddRange(encoder.Add(tone.AsSpan(at, Math.Min(1234, tone.Length - at))));

        Assert.Equal(100, packets.Count); // 10 ms frames
        long encoded = packets.Sum(p => (long)p.Length);
        Assert.InRange(encoded * 8, 50_000, 200_000); // about 128 kbps instead of 1536

        var pcm = packets.SelectMany(p => decoder.Decode(p)).ToArray();
        Assert.Equal(rate * channels * 2, pcm.Length);
        // Past the codec's start-up, the decoded tone is as loud as the original (about half full scale).
        short peak = 0;
        for (int i = pcm.Length / 2; i < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(pcm, i)));
        Assert.InRange(peak, 14000, 18500);
    }

    [Theory]
    [InlineData(48000, true)]
    [InlineData(16000, true)]
    [InlineData(44100, false)]
    public void Only_Opus_rates_are_encoded(int rate, bool supported) => Assert.Equal(supported, OpusEncoder.Supports(rate));
}
