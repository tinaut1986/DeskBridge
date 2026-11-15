using System.Runtime.Versioning;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Colour conversion and scaling on the GPU's video engine (D3D11 video processor): BGRA from the screen to
/// NV12 for the encoder, and NV12 from the decoder to the BGRA swap chain, keeping the aspect ratio.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoConverter : IDisposable
{
    private readonly VideoDevice _video;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly Dictionary<(nint, uint), ID3D11VideoProcessorInputView> _inputs = [];
    private readonly Dictionary<nint, ID3D11VideoProcessorOutputView> _outputs = [];

    public int InputWidth { get; }
    public int InputHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    public VideoConverter(VideoDevice video, int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps = 60)
    {
        _video = video;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        _videoDevice = video.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = video.Context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.OptimalSpeed,
        };
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();

        // Whole input, fitted into the output with black bars when the aspect ratios differ.
        double scale = Math.Min((double)outputWidth / inputWidth, (double)outputHeight / inputHeight);
        int w = (int)Math.Round(inputWidth * scale), h = (int)Math.Round(inputHeight * scale);
        int x = (outputWidth - w) / 2, y = (outputHeight - h) / 2;
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, inputWidth, inputHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(x, y, x + w, y + h));
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, outputWidth, outputHeight));
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { A = 1 } });
    }

    /// <summary>Converts <paramref name="input"/> (slice <paramref name="slice"/> of a texture array) into <paramref name="output"/>.</summary>
    public void Convert(ID3D11Texture2D input, uint slice, ID3D11Texture2D output)
    {
        if (!_inputs.TryGetValue((input.NativePointer, slice), out var inView))
        {
            var desc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = slice },
            };
            _videoDevice.CreateVideoProcessorInputView(input, _enumerator, desc, out inView).CheckError();
            _inputs[(input.NativePointer, slice)] = inView;
        }
        if (!_outputs.TryGetValue(output.NativePointer, out var outView))
        {
            var desc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
            _videoDevice.CreateVideoProcessorOutputView(output, _enumerator, desc, out outView).CheckError();
            _outputs[output.NativePointer] = outView;
        }

        var stream = new VideoProcessorStream { Enable = true, InputSurface = inView };
        _videoContext.VideoProcessorBlt(_processor, outView, 0, 1, [stream]).CheckError();
    }

    /// <summary>Views of textures that are going away (swap chain resized, decoder reallocated).</summary>
    public void ForgetViews()
    {
        foreach (var v in _inputs.Values) v.Dispose();
        foreach (var v in _outputs.Values) v.Dispose();
        _inputs.Clear();
        _outputs.Clear();
    }

    public void Dispose()
    {
        ForgetViews();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
