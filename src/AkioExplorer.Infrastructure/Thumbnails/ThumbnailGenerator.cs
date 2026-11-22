using System.Runtime.InteropServices.WindowsRuntime;
using AkioExplorer.Core.Abstractions;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace AkioExplorer.Infrastructure.Thumbnails;

/// <summary>
/// Thumbnail (120 px square, centre-cropped) and preview (longest side ≤ 1000 px) JPEGs, as MEGA's apps make them
/// when uploading. Pictures are decoded with Windows Imaging (EXIF rotation applied); anything else (videos, PDFs)
/// goes through the shell's thumbnail, the same picture File Explorer shows.
/// </summary>
public sealed class ThumbnailGenerator : IThumbnailGenerator
{
    private const uint ThumbnailSize = 120;
    private const uint PreviewSize = 1000;

    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff" };

    public async Task<GeneratedThumbnails?> CreateAsync(string? localPath, byte[]? imageBytes, CancellationToken cancellationToken)
    {
        try
        {
            using IRandomAccessStream? source = await OpenAsync(localPath, imageBytes).ConfigureAwait(false);
            if (source is null) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var rotation = await OrientationAsync(decoder).ConfigureAwait(false);
            byte[] preview = await EncodeAsync(decoder, rotation, PreviewSize, square: false).ConfigureAwait(false);
            byte[] thumbnail = await EncodeAsync(decoder, rotation, ThumbnailSize, square: true).ConfigureAwait(false);
            return new GeneratedThumbnails(thumbnail, preview);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // unsupported format, missing codec, damaged file
        }
    }

    private static async Task<IRandomAccessStream?> OpenAsync(string? localPath, byte[]? imageBytes)
    {
        if (localPath is not null)
        {
            var file = await StorageFile.GetFileFromPathAsync(localPath);
            if (Pictures.Contains(Path.GetExtension(localPath))) return await file.OpenReadAsync();
            var shell = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, PreviewSize, ThumbnailOptions.ResizeThumbnail);
            return shell is { Size: > 0 } ? shell : null;
        }
        if (imageBytes is null) return null;
        var memory = new InMemoryRandomAccessStream();
        await memory.WriteAsync(imageBytes.AsBuffer());
        memory.Seek(0);
        return memory;
    }

    /// <summary>EXIF orientation → the rotation that shows the picture upright (mirrored orientations are rare and left as is).</summary>
    private static async Task<BitmapRotation> OrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            var properties = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
            if (properties.TryGetValue("System.Photo.Orientation", out var value) && value.Value is ushort orientation)
                return orientation switch
                {
                    3 => BitmapRotation.Clockwise180Degrees,
                    6 => BitmapRotation.Clockwise90Degrees,
                    8 => BitmapRotation.Clockwise270Degrees,
                    _ => BitmapRotation.None,
                };
        }
        catch (Exception)
        {
            // No metadata (PNG, shell thumbnails).
        }
        return BitmapRotation.None;
    }

    /// <summary>BitmapTransform applies scale, then rotation, then crop – so the crop is in upright coordinates.</summary>
    private static async Task<byte[]> EncodeAsync(BitmapDecoder decoder, BitmapRotation rotation, uint size, bool square)
    {
        uint width = decoder.PixelWidth, height = decoder.PixelHeight;
        double scale = square ? (double)size / Math.Min(width, height) : Math.Min(1.0, (double)size / Math.Max(width, height));
        uint scaledWidth = Math.Max(1, (uint)Math.Round(width * scale));
        uint scaledHeight = Math.Max(1, (uint)Math.Round(height * scale));
        bool sideways = rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;
        uint uprightWidth = sideways ? scaledHeight : scaledWidth;
        uint uprightHeight = sideways ? scaledWidth : scaledHeight;

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            Rotation = rotation,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        if (square)
        {
            uint side = Math.Min(size, Math.Min(uprightWidth, uprightHeight));
            transform.Bounds = new BitmapBounds
            {
                X = (uprightWidth - side) / 2, Y = (uprightHeight - side) / 2, Width = side, Height = side,
            };
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
