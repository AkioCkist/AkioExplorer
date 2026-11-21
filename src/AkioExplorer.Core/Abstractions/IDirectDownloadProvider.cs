using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Abstractions;

/// <summary>A plain HTTP URL (plus headers) serving a file's bytes with Range support.</summary>
public sealed record DirectDownload(string Url, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Connections worth opening for one file on this service.</summary>
    public int MaxConnections { get; init; } = 8;
}

/// <summary>
/// Cloud providers that can hand out a direct, range-capable download URL. The transfer engine then downloads
/// with several connections at once (IDM-style segmentation) instead of one stream.
/// </summary>
public interface IDirectDownloadProvider
{
    /// <summary>
    /// A URL for <paramref name="file"/>, or null when the file can't be fetched by range (e.g. a Google Doc that is
    /// exported on the fly). Called again with <paramref name="refresh"/> = true when the previous URL or token expired.
    /// </summary>
    Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken);
}
