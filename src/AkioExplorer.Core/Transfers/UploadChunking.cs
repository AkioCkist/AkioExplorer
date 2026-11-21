namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Chunk size for resumable uploads. Every chunk ends with a round trip (the service confirms it before the next
/// one may start), during which the line sits idle: 8 MB chunks at 10 MB/s with a 300 ms round trip waste about a
/// quarter of the time. Big files therefore use bigger chunks (about an eighth of the file, within the service's
/// limits and its required granularity); small files keep small chunks so a retry resends little.
/// </summary>
public static class UploadChunking
{
    public const int GoogleGranularity = 256 * 1024;
    public const int OneDriveGranularity = 320 * 1024;
    public const int DropboxGranularity = 4 * 1024 * 1024;

    /// <param name="length">File size, or -1 when unknown.</param>
    /// <param name="granularity">Chunks must be a multiple of this.</param>
    /// <param name="min">Smallest chunk (rounded up to <paramref name="granularity"/>).</param>
    /// <param name="max">Largest chunk (rounded down to <paramref name="granularity"/>); bounds memory per upload.</param>
    public static int ChunkSize(long length, int granularity, int min, int max)
    {
        long lo = RoundUp(min, granularity);
        long hi = Math.Max(lo, max / granularity * (long)granularity);
        if (length <= 0) return (int)lo;
        long wanted = RoundUp((length + 7) / 8, granularity);
        return (int)Math.Clamp(wanted, lo, hi);
    }

    public static int Google(long length) => ChunkSize(length, GoogleGranularity, 8 << 20, 32 << 20);

    /// <summary>Graph: multiples of 320 KiB, at most 60 MiB per request.</summary>
    public static int OneDrive(long length) => ChunkSize(length, OneDriveGranularity, 10 << 20, 32 * 320 * 1024 * 3);

    /// <summary>Dropbox: multiples of 4 MiB recommended, at most 150 MB per request.</summary>
    public static int Dropbox(long length) => ChunkSize(length, DropboxGranularity, 8 << 20, 32 << 20);

    private static long RoundUp(long value, int granularity) => (value + granularity - 1) / granularity * granularity;
}
