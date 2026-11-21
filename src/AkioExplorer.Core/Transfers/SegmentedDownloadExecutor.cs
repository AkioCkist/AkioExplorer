using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers.Segmented;

namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Cloud → This PC downloads with several connections per file (IDM-style dynamic segmentation, see
/// <see cref="SegmentedDownloader"/>): a single HTTPS stream is usually capped well below the line speed by the
/// service or by TCP over distance; 8 ranges in parallel fill the line. Partial data lives in
/// <c>&lt;name&gt;.akiodl</c> + <c>.akiodl.json</c> next to the target, so a failed or interrupted download resumes
/// from where it stopped on Retry instead of starting over. Small files and files without a direct URL use the
/// plain streaming path.
/// </summary>
public sealed class SegmentedDownloadExecutor : ITransferExecutor
{
    /// <summary>Below this, connection setup costs more than parallelism gains.</summary>
    public const long MinSegmentedSize = 8L * 1024 * 1024;

    private readonly StreamingTransferExecutor _fallback = new();
    private readonly Func<int> _maxConnections;

    public SegmentedDownloadExecutor(Func<int>? maxConnections = null)
    {
        _maxConnections = maxConnections ?? (() => 8);
    }

    public bool CanTransfer(IStorageProvider source, IStorageProvider destination) =>
        source is IDirectDownloadProvider && source is IFileContentProvider && destination is IFileContentProvider &&
        (destination.Capabilities & StorageCapabilities.OpenWithShell) != 0; // destination is a real disk path

    public async Task CopyFileAsync(IStorageProvider source, StorageItem file, IStorageProvider destination, string destinationPath,
        bool overwrite, ITransferProgress progress, CancellationToken cancellationToken)
    {
        await progress.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        var direct = (IDirectDownloadProvider)source;
        DirectDownload? link = file.Size is >= MinSegmentedSize
            ? await direct.GetDirectDownloadAsync(file, refresh: false, cancellationToken).ConfigureAwait(false)
            : null;
        if (link is null)
        {
            await _fallback.CopyFileAsync(source, file, destination, destinationPath, overwrite, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        string target = destinationPath;
        if (!overwrite && (File.Exists(target) || Directory.Exists(target)))
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{Path.GetFileName(target)}\" here.");

        var options = new SegmentedDownloadOptions
        {
            MaxConnections = Math.Clamp(Math.Min(link.MaxConnections, _maxConnections()), 1, 32),
            PauseGate = progress.WaitIfPausedAsync,
            MaxSourceRefreshes = 50, // access tokens last ~1 h: a long download refreshes several times
        };

        long reported = 0;
        var tracker = new Progress(p =>
        {
            progress.ReportParts(new PartedFileProgress(file.Name, p.Total, p.ActiveConnections, p.Segments));
            long delta = p.Downloaded - Interlocked.Read(ref reported);
            if (delta == 0) return;
            Interlocked.Add(ref reported, delta);
            progress.Report(delta);
        });

        try
        {
            // Off the caller's context: every connection copies bytes; none of that belongs on a UI thread.
            await Task.Run(() => new SegmentedDownloader(options).DownloadAsync(
                ToSource(link), target, tracker,
                refreshSource: async ct => await direct.GetDirectDownloadAsync(file, refresh: true, ct).ConfigureAwait(false) is { } fresh
                    ? ToSource(fresh)
                    : null,
                ct: cancellationToken), cancellationToken).ConfigureAwait(false);

            // Final numbers: the last progress tick may not have fired before completion.
            long rest = (file.Size ?? reported) - Interlocked.Read(ref reported);
            if (rest > 0) progress.Report(rest);
        }
        // Progress roll-back on failure/cancel is the caller's job (it knows what this file reported).
        // The .akiodl pair stays on disk so a retry – automatic or the user's – continues where it stopped;
        // nothing ever appears under the target name until the download is complete.
        catch (Exception ex) when (ex is not StorageException and not OperationCanceledException)
        {
            throw ex switch
            {
                IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => new StorageException(StorageErrorKind.Unknown, io.Message, io),
                DownloadUrlExpiredException => new StorageException(StorageErrorKind.Unavailable, "The service refused the download (expired link or download quota). Retry later; it continues where it stopped.", ex),
                _ => new StorageException(StorageErrorKind.Unavailable, $"Download failed: {ex.Message} Retry continues where it stopped.", ex),
            };
        }
        finally
        {
            progress.ReportParts(null);
        }
    }

    public Task<bool> TryMoveAsync(IStorageProvider source, StorageItem item, IStorageProvider destination, string destinationPath,
        CancellationToken cancellationToken) => Task.FromResult(false);

    private static DownloadSource ToSource(DirectDownload link) => new(link.Url, link.Headers);

    /// <summary>Synchronous IProgress: the engine reports from worker threads; no context hop needed.</summary>
    private sealed class Progress(Action<SegmentedProgress> report) : IProgress<SegmentedProgress>
    {
        public void Report(SegmentedProgress value) => report(value);
    }
}
