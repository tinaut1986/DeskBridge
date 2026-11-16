using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// One Direct3D 11 device (on the GPU that drives the given output, or the default one) shared by capture,
/// colour conversion, the hardware encoder/decoder (through a Media Foundation device manager) and display.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoDevice : IDisposable
{
    private static int _mfStarted;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIAdapter1 Adapter { get; }
    public IMFDXGIDeviceManager Manager { get; }

    /// <summary>Description of the GPU, for logs ("Intel(R) UHD Graphics", "NVIDIA GeForce RTX 3060").</summary>
    public string GpuName => Adapter.Description1.Description;

    private VideoDevice(IDXGIAdapter1 adapter, ID3D11Device device, ID3D11DeviceContext context, IMFDXGIDeviceManager manager)
    {
        Adapter = adapter;
        Device = device;
        Context = context;
        Manager = manager;
    }

    /// <param name="adapter">The GPU to use (the one with the output to capture); null = the first one.</param>
    public static VideoDevice Create(IDXGIAdapter1? adapter = null)
    {
        StartMediaFoundation();
        if (adapter is null)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(0, out adapter).CheckError();
        }

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();

        // The encoder/decoder use the device from their own threads.
        using (var multithread = device!.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        // Ahead of other programs' work on the GPU (an emulator, a game): ignored where Windows does not allow it.
        using (var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice>())
            dxgiDevice?.SetGPUThreadPriority(7);

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        return new VideoDevice(adapter!, device, context!, manager);
    }

    public static void StartMediaFoundation()
    {
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            MediaFactory.MFStartup(false).CheckError();
    }

    /// <summary>A GPU texture (default usage).</summary>
    public ID3D11Texture2D CreateTexture(int width, int height, Format format, BindFlags bind = BindFlags.RenderTarget, int arraySize = 1,
        ResourceOptionFlags options = ResourceOptionFlags.None) =>
        Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = (uint)arraySize,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = bind,
            MiscFlags = options,
        });

    public void Dispose()
    {
        Manager.Dispose();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
