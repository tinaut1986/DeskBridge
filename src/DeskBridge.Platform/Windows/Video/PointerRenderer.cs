using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBridge.Core.Video;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Draws the other machine's mouse pointer over the video: its picture as a textured quad with alpha
/// blending, on the swap chain's back buffer, after the video processor has drawn the frame.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PointerRenderer : IDisposable
{
    private const string Shader = """
        cbuffer Placement : register(b0) { float4 rect; }; // left, top, right, bottom in clip space
        Texture2D picture : register(t0);
        SamplerState linearSampler : register(s0);
        struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        Vertex vs(uint id : SV_VertexID)
        {
            float2 uv = float2(id & 1, id >> 1);
            Vertex v;
            v.position = float4(lerp(rect.x, rect.z, uv.x), lerp(rect.y, rect.w, uv.y), 0, 1);
            v.uv = uv;
            return v;
        }
        float4 ps(Vertex v) : SV_Target { return picture.Sample(linearSampler, v.uv); }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;
    private (int W, int H) _size;

    public PointerRenderer(VideoDevice video)
    {
        _device = video.Device;
        _context = video.Context;
        var vsCode = Compiler.Compile(Shader, "vs", "pointer", "vs_4_0");
        var psCode = Compiler.Compile(Shader, "ps", "pointer", "ps_4_0");
        _vs = _device.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);
        _constants = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _blend = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    /// <summary>A new pointer picture (from the receiver's thread).</summary>
    public void SetImage(PointerImage image)
    {
        _view?.Dispose();
        _texture?.Dispose();
        var handle = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
        try
        {
            _texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)image.Width,
                Height = (uint)image.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, new SubresourceData(handle.AddrOfPinnedObject(), (uint)image.Width * 4));
        }
        finally
        {
            handle.Free();
        }
        _view = _device.CreateShaderResourceView(_texture);
        _size = (image.Width, image.Height);
    }

    /// <summary>
    /// Draws the pointer whose top-left corner is at (<paramref name="x"/>, <paramref name="y"/>) of the source
    /// picture, which is shown in <paramref name="dest"/> of a <paramref name="targetWidth"/> x <paramref name="targetHeight"/> target.
    /// </summary>
    public void Draw(ID3D11Texture2D target, int targetWidth, int targetHeight, (int X, int Y, int W, int H) dest,
        int sourceWidth, int sourceHeight, int x, int y)
    {
        if (_view is null)
            return;
        double sx = (double)dest.W / sourceWidth, sy = (double)dest.H / sourceHeight;
        double left = dest.X + x * sx, top = dest.Y + y * sy;
        double right = left + _size.W * sx, bottom = top + _size.H * sy;
        // Pixels to clip space (y up).
        var rect = new System.Numerics.Vector4(
            (float)(left / targetWidth * 2 - 1), (float)(1 - top / targetHeight * 2),
            (float)(right / targetWidth * 2 - 1), (float)(1 - bottom / targetHeight * 2));

        var mapped = _context.Map(_constants, MapMode.WriteDiscard);
        Marshal.StructureToPtr(rect, mapped.DataPointer, false);
        _context.Unmap(_constants);

        using var rtv = _device.CreateRenderTargetView(target);
        _context.OMSetRenderTargets(rtv);
        _context.OMSetBlendState(_blend);
        _context.RSSetViewport(new Viewport(0, 0, targetWidth, targetHeight));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vs);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetShader(_ps);
        _context.PSSetShaderResource(0, _view);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(4, 0);
        _context.OMSetRenderTargets([], null);
    }

    public void Dispose()
    {
        _view?.Dispose();
        _texture?.Dispose();
        _sampler.Dispose();
        _blend.Dispose();
        _constants.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
