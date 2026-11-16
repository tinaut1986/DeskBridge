using Concentus;
using Concentus.Enums;

namespace DeskBridge.Core.Audio;

/// <summary>How the samples of an audio stream travel (the byte after rate and channels in its format packet).</summary>
public enum AudioCodec : byte
{
    /// <summary>16-bit PCM, interleaved (about 1.5 Mbps for 48 kHz stereo).</summary>
    Pcm = 0,

    /// <summary>Opus, one 10 ms frame per packet (128 kbps stereo: indistinguishable from the original).</summary>
    Opus = 1,
}

/// <summary>Cuts interleaved float samples into 10 ms frames and encodes each with Opus.</summary>
public sealed class OpusEncoder
{
    public const int FrameMs = 10;

    private readonly IOpusEncoder _encoder;
    private readonly int _channels, _frameSamples;
    private readonly float[] _pending;
    private int _filled;
    private readonly byte[] _out = new byte[4000];

    /// <summary>Opus works at these rates only (otherwise the stream goes as PCM).</summary>
    public static bool Supports(int sampleRate) => sampleRate is 8000 or 12000 or 16000 or 24000 or 48000;

    /// <param name="voice">A microphone (tuned for speech) rather than everything a PC plays.</param>
    public OpusEncoder(int sampleRate, int channels, int bitrate, bool voice = false)
    {
        _channels = channels;
        _frameSamples = sampleRate * FrameMs / 1000;
        _pending = new float[_frameSamples * channels];
        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels,
            voice ? OpusApplication.OPUS_APPLICATION_VOIP : OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.Bitrate = bitrate;
        _encoder.Complexity = 5; // plenty for 10 ms frames, light on the CPU
    }

    /// <summary>Adds samples (interleaved, -1..1); every complete frame comes out encoded.</summary>
    public List<byte[]> Add(ReadOnlySpan<float> samples)
    {
        var packets = new List<byte[]>();
        while (samples.Length > 0)
        {
            int take = Math.Min(samples.Length, _pending.Length - _filled);
            samples[..take].CopyTo(_pending.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];
            if (_filled == _pending.Length)
            {
                int length = _encoder.Encode(_pending, _frameSamples, _out, _out.Length);
                packets.Add(_out.AsSpan(0, length).ToArray());
                _filled = 0;
            }
        }
        return packets;
    }
}

/// <summary>Decodes Opus frames back to interleaved 16-bit PCM.</summary>
public sealed class OpusDecoder
{
    private readonly IOpusDecoder _decoder;
    private readonly int _channels;
    private readonly short[] _out;

    public OpusDecoder(int sampleRate, int channels)
    {
        _channels = channels;
        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        _out = new short[sampleRate / 1000 * 120 * channels]; // the longest Opus frame (120 ms)
    }

    /// <summary>One packet to 16-bit little-endian PCM bytes.</summary>
    public byte[] Decode(ReadOnlySpan<byte> packet)
    {
        int frames = _decoder.Decode(packet, _out, _out.Length / _channels, false);
        var bytes = new byte[frames * _channels * 2];
        Buffer.BlockCopy(_out, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
