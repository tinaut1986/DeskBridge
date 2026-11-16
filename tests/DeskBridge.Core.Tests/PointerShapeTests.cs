using DeskBridge.Core.Video;

namespace DeskBridge.Core.Tests;

public class PointerShapeTests
{
    [Fact]
    public void Monochrome_masks_become_black_white_and_transparent_pixels()
    {
        // 8x1 picture: AND row then XOR row (one byte each, pitch 1).
        // Pixels: 0 AND0/XOR0 black, 1 AND0/XOR1 white, 2 AND1/XOR0 transparent, 3 AND1/XOR1 inverted (black).
        byte and = 0b0011_0000, xor = 0b0101_0000;
        var image = PointerShapes.Convert(PointerShapes.Monochrome, 8, 2, 1, [and, xor])!;
        Assert.Equal((8, 1), (image.Width, image.Height));
        byte[] Pixel(int i) => image.Bgra[(i * 4)..(i * 4 + 4)];
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(0));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(1));
        Assert.Equal(0, Pixel(2)[3]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(3));
    }

    [Fact]
    public void Colour_shapes_keep_their_alpha_and_round_trip_through_a_packet()
    {
        // 2x1 with a padded pitch of 12 bytes.
        byte[] data = [10, 20, 30, 128, 40, 50, 60, 0, 99, 99, 99, 99];
        var image = PointerShapes.Convert(PointerShapes.Color, 2, 1, 12, data)!;
        Assert.Equal(new byte[] { 10, 20, 30, 128, 40, 50, 60, 0 }, image.Bgra);

        var back = PointerImage.FromBody(image.ToBody())!;
        Assert.Equal((image.Width, image.Height), (back.Width, back.Height));
        Assert.Equal(image.Bgra, back.Bgra);
        Assert.Null(PointerImage.FromBody([1, 0, 0, 0, 1, 0, 0, 0])); // size and data disagree
    }

    [Fact]
    public void Masked_colour_xor_black_is_transparent()
    {
        byte[] data = [1, 2, 3, 0, 0, 0, 0, 255, 9, 9, 9, 255];
        var image = PointerShapes.Convert(PointerShapes.MaskedColor, 3, 1, 12, data)!;
        Assert.Equal(255, image.Bgra[3]);   // replaces the screen
        Assert.Equal(0, image.Bgra[7]);     // XOR with black: no change
        Assert.Equal(255, image.Bgra[11]);  // XOR with a colour: shown as that colour
    }
}
