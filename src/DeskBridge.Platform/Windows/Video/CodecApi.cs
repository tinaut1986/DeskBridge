using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// The few ICodecAPI settings DeskBridge uses on encoders (Vortice does not wrap ICodecAPI), called through
/// its vtable. Every setting is best effort: an encoder that does not support one keeps its default.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class CodecApi
{
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");

    public const uint RateControlCbr = 0, RateControlPeakConstrainedVbr = 1;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public short Bool;
    }

    private const ushort VtBool = 11, VtUi4 = 19;

    public static bool SetUInt(ComObject encoder, Guid api, uint value) =>
        Set(encoder, api, new Variant { Type = VtUi4, UInt = value });

    public static bool SetBool(ComObject encoder, Guid api, bool value) =>
        Set(encoder, api, new Variant { Type = VtBool, Bool = value ? (short)-1 : (short)0 });

    private static bool Set(ComObject encoder, Guid api, Variant value)
    {
        var iid = IidCodecApi;
        if (Marshal.QueryInterface(encoder.NativePointer, in iid, out nint codecApi) < 0)
            return false;
        try
        {
            // ICodecAPI::SetValue(const GUID*, VARIANT*) is the 10th entry (after IUnknown and 6 others).
            var vtable = *(nint**)codecApi;
            var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)vtable[9];
            return setValue(codecApi, &api, &value) >= 0;
        }
        finally
        {
            Marshal.Release(codecApi);
        }
    }
}
