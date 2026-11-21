using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Copies/moves inside one cloud account on the server (Drive files.copy, Dropbox copy_v2, Graph copy):
/// no bytes travel through this PC, so it is as fast as the service allows.
/// </summary>
public sealed class ServerSideTransferExecutor : ITransferExecutor
{
    private readonly StreamingTransferExecutor _fallback = new();

    public bool CanTransfer(IStorageProvider source, IStorageProvider destination) =>
        source.Id == destination.Id && source is IFileContentProvider &&
        (source.Capabilities & StorageCapabilities.ServerSideCopy) != 0;

    public async Task CopyFileAsync(IStorageProvider source, StorageItem file, IStorageProvider destination, string destinationPath,
        bool overwrite, ITransferProgress progress, CancellationToken cancellationToken)
    {
        var content = (IFileContentProvider)source;
        string parent = destination.GetParentPath(destinationPath) ?? destination.RootPath;
        string name = destination.GetDisplayName(destinationPath);

        await progress.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        if (overwrite && await destination.GetItemAsync(destinationPath, cancellationToken).ConfigureAwait(false) is { IsContainer: false } existing)
            await destination.DeleteAsync([existing.Path], permanent: false, cancellationToken).ConfigureAwait(false);

        if (await content.CopyWithinAsync(file, parent, name, cancellationToken).ConfigureAwait(false) is null)
        {
            // Item type the service can't copy server-side (e.g. exported formats): stream it instead.
            await _fallback.CopyFileAsync(source, file, destination, destinationPath, overwrite: false, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        progress.Report(file.Size ?? 0);
    }

    public async Task<bool> TryMoveAsync(IStorageProvider source, StorageItem item, IStorageProvider destination, string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!CanTransfer(source, destination)) return false;
        string parent = destination.GetParentPath(destinationPath) ?? destination.RootPath;
        string name = destination.GetDisplayName(destinationPath);
        return await ((IFileContentProvider)source).MoveWithinAsync(item, parent, name, cancellationToken).ConfigureAwait(false) is not null;
    }
}

/// <summary>
/// Streams bytes from any provider to any other: upload (PC → cloud), download (cloud → PC) and cloud → cloud
/// across services/accounts. Nothing is written to disk on the way, except when the source can't tell the size up
/// front (e.g. a Google Doc exported on the fly) and the destination needs it – then a temporary file is used and
/// deleted immediately afterwards.
/// </summary>
public sealed class StreamingTransferExecutor : ITransferExecutor
{
    public bool CanTransfer(IStorageProvider source, IStorageProvider destination) =>
        source is IFileContentProvider && destination is IFileContentProvider;

    public async Task CopyFileAsync(IStorageProvider source, StorageItem file, IStorageProvider destination, string destinationPath,
        bool overwrite, ITransferProgress progress, CancellationToken cancellationToken)
    {
        var reader = (IFileContentProvider)source;
        var writer = (IFileContentProvider)destination;
        string parent = destination.GetParentPath(destinationPath) ?? destination.RootPath;
        string name = destination.GetDisplayName(destinationPath);

        await progress.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        Stream input = await reader.OpenReadAsync(file, cancellationToken).ConfigureAwait(false);
        try
        {
            long length = file.Size ?? -1;
            if (length < 0)
            {
                var spool = new FileStream(Path.Combine(Path.GetTempPath(), "akio-" + Guid.NewGuid().ToString("N") + ".tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await input.CopyToAsync(spool, 1 << 20, cancellationToken).ConfigureAwait(false);
                await input.DisposeAsync().ConfigureAwait(false);
                spool.Position = 0;
                input = spool;
                length = spool.Length;
            }

            await using var tracked = new TransferStream(input, progress, cancellationToken, leaveOpen: true);
            await writer.WriteFileAsync(parent, name, tracked, length, overwrite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<bool> TryMoveAsync(IStorageProvider source, StorageItem item, IStorageProvider destination, string destinationPath,
        CancellationToken cancellationToken) => Task.FromResult(false);
}
