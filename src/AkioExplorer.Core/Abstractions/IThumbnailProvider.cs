using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Abstractions;

/// <summary>
/// Server-rendered thumbnails (photos, videos, PDFs, documents) for cloud providers, so grid view shows the picture
/// instead of the app icon without downloading the file. Local files use the Windows thumbnail cache instead.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>Cheap check (no network): whether asking for a thumbnail of <paramref name="file"/> is worthwhile.</summary>
    bool MayHaveThumbnail(StorageItem file);

    /// <summary>Image bytes (JPEG/PNG) about <paramref name="size"/> pixels on the long side, or null when there is none.</summary>
    Task<Stream?> OpenThumbnailAsync(StorageItem file, int size, CancellationToken cancellationToken);
}
