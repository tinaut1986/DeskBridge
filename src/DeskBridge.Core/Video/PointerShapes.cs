using System.Buffers.Binary;

namespace DeskBridge.Core.Video;

/// <summary>A mouse pointer picture: straight-alpha BGRA, <see cref="Width"/> x <see cref="Height"/>.</summary>
public sealed record PointerImage(int Width, int Height, byte[] Bgra)
{
    public byte[] ToBody()
    {
        var body = new byte[8 + Bgra.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, Width);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), Height);
        Bgra.CopyTo(body, 8);
        return body;
    }

    public static PointerImage? FromBody(byte[] body)
    {
        if (body.Length < 8)
            return null;
        int w = BinaryPrimitives.ReadInt32LittleEndian(body), h = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        if (w <= 0 || h <= 0 || w > 512 || h > 512 || body.Length != 8 + w * h * 4)
            return null;
        return new PointerImage(w, h, body[8..]);
    }
}

/// <summary>
/// Turns the pointer shapes Windows reports for a captured screen into plain BGRA pictures that can be drawn
/// over the video. Shapes that invert the screen under them (the text cursor, for example) cannot be
/// reproduced exactly from a picture; they are drawn black, which stays visible on the light backgrounds
/// where those cursors are used.
/// </summary>
public static class PointerShapes
{
    public const int Monochrome = 1, Color = 2, MaskedColor = 4;

    /// <param name="type">DXGI_OUTDUPL_POINTER_SHAPE_TYPE.</param>
    /// <param name="height">As reported: for monochrome shapes, twice the picture's height (AND mask, then XOR mask).</param>
    public static PointerImage? Convert(int type, int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || width > 512 || height > 1024)
            return null;
        switch (type)
        {
            case Color:
            {
                var bgra = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    data.Slice(y * pitch, width * 4).CopyTo(bgra.AsSpan(y * width * 4));
                return new PointerImage(width, height, bgra);
            }

            case MaskedColor:
            {
                // Alpha 0: replace the screen with the colour. 0xFF: XOR it (shown as the colour, or not at all if black).
                var bgra = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int s = y * pitch + x * 4, d = (y * width + x) * 4;
                        bool xor = data[s + 3] == 0xFF;
                        bool black = data[s] == 0 && data[s + 1] == 0 && data[s + 2] == 0;
                        bgra[d] = data[s];
                        bgra[d + 1] = data[s + 1];
                        bgra[d + 2] = data[s + 2];
                        bgra[d + 3] = xor && black ? (byte)0 : (byte)0xFF;
                    }
                }
                return new PointerImage(width, height, bgra);
            }

            case Monochrome:
            {
                int h = height / 2;
                var bgra = new byte[width * h * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int bit = 0x80 >> (x % 8);
                        bool and = (data[y * pitch + x / 8] & bit) != 0;
                        bool xor = (data[(y + h) * pitch + x / 8] & bit) != 0;
                        int d = (y * width + x) * 4;
                        // AND 0: black (XOR 0) or white (XOR 1). AND 1: screen kept (XOR 0) or inverted (XOR 1).
                        byte value = !and && xor ? (byte)0xFF : (byte)0;
                        bgra[d] = bgra[d + 1] = bgra[d + 2] = value;
                        bgra[d + 3] = and && !xor ? (byte)0 : (byte)0xFF;
                    }
                }
                return new PointerImage(width, h, bgra);
            }

            default:
                return null;
        }
    }
}
