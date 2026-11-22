using System.Runtime.InteropServices.WindowsRuntime;
using AkioExplorer.Infrastructure.Thumbnails;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AkioExplorer.Tests;

public class ThumbnailGeneratorTests
{
    /// <summary>A 300×200 24-bit BMP (bottom-up rows, padded to 4 bytes).</summary>
    private static byte[] Bitmap(int width, int height)
    {
        int row = (width * 3 + 3) / 4 * 4;
        var data = new byte[54 + row * height];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                data[54 + y * row + x * 3 + 2] = (byte)(x * 255 / width); // red gradient
        return data;
    }

    private static async Task<(uint Width, uint Height)> SizeOf(byte[] jpeg)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
        return (decoder.PixelWidth, decoder.PixelHeight);
    }

    [Fact]
    public async Task Makes_a_square_thumbnail_and_a_proportional_preview()
    {
        var made = await new ThumbnailGenerator().CreateAsync(null, Bitmap(3000, 2000), CancellationToken.None);

        Assert.NotNull(made);
        Assert.Equal((120u, 120u), await SizeOf(made!.Thumbnail));
        Assert.Equal((1000u, 667u), await SizeOf(made.Preview));
    }

    [Fact]
    public async Task Small_pictures_are_not_enlarged_for_the_preview()
    {
        var made = await new ThumbnailGenerator().CreateAsync(null, Bitmap(300, 200), CancellationToken.None);
        Assert.Equal((300u, 200u), await SizeOf(made!.Preview));
    }

    [Fact]
    public async Task Garbage_gives_no_thumbnail() =>
        Assert.Null(await new ThumbnailGenerator().CreateAsync(null, [1, 2, 3], CancellationToken.None));
}
