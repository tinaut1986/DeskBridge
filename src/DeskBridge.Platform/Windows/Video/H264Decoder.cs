using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// H.264 decoding with Windows' decoder, accelerated by the GPU (DXVA: NVIDIA NVDEC, Intel Quick Sync...):
/// frames come out as NV12 textures on the GPU, ready to be converted for display.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class H264Decoder : IDisposable
{
    private static readonly Guid ClsidH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);

    private readonly IMFTransform _transform;
    private readonly bool _providesSamples;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>True when the GPU decodes (otherwise Windows decodes on the CPU into system memory, not supported here).</summary>
    public bool Hardware { get; }

    public H264Decoder(VideoDevice video)
    {
        VideoDevice.StartMediaFoundation();
        _transform = CreateDecoder();
        using (var attributes = _transform.Attributes)
        {
            Hardware = attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out uint aware).Success && aware != 0;
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u); // MF_LOW_LATENCY
        }
        if (!Hardware)
            throw new NotSupportedException("Windows' H.264 decoder cannot use the GPU here.");
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)video.Manager.NativePointer);

        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            _transform.SetInputType(0, type, 0);
        }
        PickOutputType();
        _providesSamples = (_transform.GetOutputStreamInfo(0).Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        if (!_providesSamples)
            throw new NotSupportedException("Windows' H.264 decoder does not give GPU frames here.");
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    private static IMFTransform CreateDecoder()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidH264Decoder, 0, 1 /* in process */, typeof(IMFTransform).GUID, out nint ptr));
        return new IMFTransform(ptr);
    }

    private void PickOutputType()
    {
        for (int i = 0; ; i++)
        {
            using var type = _transform.GetOutputAvailableType(0, i);
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                continue;
            _transform.SetOutputType(0, type, 0);
            ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            return;
        }
    }

    /// <summary>
    /// Decodes one frame of Annex B bytes; <paramref name="frame"/> is called for every picture that comes out
    /// (the NV12 texture, the slice of it, and the picture size), valid only during the call.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> data, long time100ns, Action<ID3D11Texture2D, uint, int, int> frame)
    {
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length))
        {
            buffer.Lock(out nint ptr, out _, out _);
            try
            {
                data.CopyTo(new Span<byte>((void*)ptr, data.Length));
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.CurrentLength = data.Length;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time100ns;
            _transform.ProcessInput(0, sample, 0);
        }

        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0 };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result.Code == NeedMoreInput)
                return;
            if (result.Code == StreamChange)
            {
                output.Sample?.Dispose();
                PickOutputType(); // the first frame tells the real size
                continue;
            }
            result.CheckError();

            using var sample = output.Sample;
            if (sample is null)
                continue;
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
            using var texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
            frame(texture, dxgi.SubresourceIndex, Width, Height);
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
        }
        catch (SharpGenException)
        {
        }
        _transform.Dispose();
    }
}
