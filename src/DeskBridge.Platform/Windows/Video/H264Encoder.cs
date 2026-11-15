using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>One encoded frame: H.264 Annex B bytes.</summary>
public sealed record EncodedFrame(byte[] Data, long Time, bool KeyFrame);

/// <summary>
/// H.264 encoding on the GPU's hardware encoder (Intel Quick Sync, NVIDIA NVENC, AMD VCE) through its
/// Media Foundation transform, fed with NV12 textures straight from the GPU. Low latency: no B-frames,
/// constant bitrate, one frame in, one frame out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class H264Encoder : IDisposable
{
    private const int PoolSize = 4;

    private readonly VideoDevice _video;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly ID3D11Texture2D[] _pool;
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly Thread _eventThread;
    private readonly Action<EncodedFrame> _output;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private volatile bool _stopping;
    private int _next;
    private int _refused;
    private const int NotAccepting = unchecked((int)0xC00D36B5); // MF_E_NOTACCEPTING
    private bool _forceKey;

    public string Name { get; }

    /// <summary>The settings applied, and those the encoder refused (for logs).</summary>
    public string Settings { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Set when the encoder failed on its own thread; the stream has to be restarted.</summary>
    public Exception? Error { get; private set; }

    public H264Encoder(VideoDevice video, int width, int height, int fps, int bitrateMbps, Action<EncodedFrame> output)
    {
        _video = video;
        _output = output;
        Width = width;
        Height = height;

        (_transform, Name) = CreateHardwareEncoder(video);
        using (var attributes = _transform.Attributes)
        {
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u); // MF_LOW_LATENCY
        }
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)video.Manager.NativePointer);

        var settings = new List<string>();
        void Note(string name, bool ok) => settings.Add(ok ? name : name + " (not supported)");
        Note("low latency", CodecApi.SetBool(_transform, CodecApi.LowLatencyMode, true));
        Note("CBR", CodecApi.SetUInt(_transform, CodecApi.RateControlMode, CodecApi.RateControlCbr));
        Note($"{bitrateMbps} Mbps", CodecApi.SetUInt(_transform, CodecApi.MeanBitRate, (uint)(bitrateMbps * 1_000_000)));
        Note("no B-frames", CodecApi.SetUInt(_transform, CodecApi.BPictureCount, 0));
        Note($"GOP {fps * 2}", CodecApi.SetUInt(_transform, CodecApi.GopSize, (uint)(fps * 2)));
        Settings = string.Join(", ", settings);

        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(bitrateMbps * 1_000_000));
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            type.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u); // High, without B-frames
            _transform.SetOutputType(0, type, 0);
        }
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            _transform.SetInputType(0, type, 0);
        }

        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, width * height);

        _pool = new ID3D11Texture2D[PoolSize];
        for (int i = 0; i < PoolSize; i++)
            _pool[i] = video.CreateTexture(width, height, Format.NV12, BindFlags.RenderTarget);

        _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "H.264 encoder events" };
        _eventThread.Start();
    }

    /// <summary>MFT_ENUM_ADAPTER_LUID: only encoders of this GPU.</summary>
    private static readonly Guid EnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>The first hardware H.264 encoder of the GPU that owns <paramref name="video"/>'s device.</summary>
    private static (IMFTransform Transform, string Name) CreateHardwareEncoder(VideoDevice video)
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);

        // Restricted to the adapter with the device (a laptop with two GPUs must encode on the one it captures from).
        using var attributes = MediaFactory.MFCreateAttributes(1);
        var luid = video.Adapter.Description1.Luid;
        attributes.Set(EnumAdapterLuid, ((ulong)(uint)luid.HighPart << 32) | luid.LowPart);
        MediaFactory.MFTEnum2(TransformCategoryGuids.VideoEncoder, flags, input, output, attributes, out nint array, out uint count);
        if (count == 0)
            throw new NotSupportedException("This GPU has no hardware H.264 encoder.");

        var activates = new IMFActivate[count];
        for (int i = 0; i < count; i++)
            activates[i] = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
        Marshal.FreeCoTaskMem(array);
        try
        {
            var first = activates[0];
            string name = first.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return (first.ActivateObject<IMFTransform>(), name);
        }
        finally
        {
            foreach (var a in activates)
                a.Dispose();
        }
    }

    /// <summary>A texture to convert the next frame into (owned by the encoder, reused in turn).</summary>
    public ID3D11Texture2D NextInput() => _pool[_next];

    /// <summary>Asks for the next frame to be a key frame (a receiver joined or recovered).</summary>
    public void RequestKeyFrame() => _forceKey = true;

    /// <summary>Encodes the texture last returned by <see cref="NextInput"/>; waits while the encoder is busy.</summary>
    public bool Encode(long time100ns, TimeSpan wait)
    {
        if (Error is { } error)
            throw new InvalidOperationException("The H.264 encoder stopped.", error);
        if (!_needInput.Wait(wait))
            return false; // the encoder is behind: drop this frame

        var texture = _pool[_next];
        _next = (_next + 1) % PoolSize;
        if (_forceKey)
        {
            _forceKey = false;
            CodecApi.SetUInt(_transform, CodecApi.ForceKeyFrame, 1);
        }

        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        using var length = buffer.QueryInterface<IMF2DBuffer>();
        buffer.CurrentLength = length.ContiguousLength;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time100ns;
        sample.SampleDuration = 166_667;
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == NotAccepting)
        {
            // Some encoders (Intel Quick Sync) ask for input before they can take it: drop this frame, the
            // next request comes with the next event. Only a long run of refusals is a real failure.
            if (++_refused >= 300)
                throw new InvalidOperationException("The H.264 encoder keeps refusing frames.", ex);
            return false;
        }
        _refused = 0;
        return true;
    }

    private void EventLoop()
    {
        try
        {
            while (!_stopping)
            {
                using var ev = _events.GetEvent(0);
                switch (ev.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _needInput.Release();
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        ReadOutput();
                        break;
                }
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Error = ex;
            _needInput.Release();
        }
        catch (Exception)
        {
            // shutting down
        }
    }

    private void ReadOutput()
    {
        IMFSample? own = null;
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            using var memory = MediaFactory.MFCreateMemoryBuffer(_outputSize);
            own.AddBuffer(memory);
        }
        var buffers = new OutputDataBuffer { StreamID = 0, Sample = own };
        _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers, out _).CheckError();
        using var sample = buffers.Sample;
        buffers.Events?.Dispose();
        if (sample is null)
            return;

        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out nint data, out _, out int size);
        byte[] bytes;
        try
        {
            bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
        }
        finally
        {
            contiguous.Unlock();
        }
        bool key = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint clean).Success && clean != 0;
        _output(new EncodedFrame(bytes, sample.SampleTime, key));
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
            using (var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>())
                shutdown?.Shutdown();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
        }
        _eventThread.Join(1000);
        _events.Dispose();
        _transform.Dispose();
        foreach (var t in _pool)
            t.Dispose();
        _needInput.Dispose();
    }
}
