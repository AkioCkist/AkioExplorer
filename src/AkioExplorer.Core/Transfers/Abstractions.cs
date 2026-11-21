using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Moves bytes between two providers. Implementations pick the best method for the pair:
/// OS copy engine for local↔local (Phase 2), server-side copy or streaming for clouds (Phase 4).
/// </summary>
public interface ITransferExecutor
{
    bool CanTransfer(IStorageProvider source, IStorageProvider destination);

    /// <summary>
    /// Copies one file to <paramref name="destinationPath"/> (a <see cref="IStorageProvider.CombinePath"/> result).
    /// Reports byte deltas and honours pause through <paramref name="progress"/>. Must leave no partial file on cancel/failure.
    /// </summary>
    Task CopyFileAsync(IStorageProvider source, StorageItem file, IStorageProvider destination, string destinationPath,
        bool overwrite, ITransferProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Instant move (rename) when both paths live on the same volume/account. Returns false if not possible,
    /// in which case the engine falls back to copy + delete.
    /// </summary>
    Task<bool> TryMoveAsync(IStorageProvider source, StorageItem item, IStorageProvider destination, string destinationPath, CancellationToken cancellationToken);
}

/// <summary>Asks the user what to do when a destination name already exists. Called from worker threads.</summary>
public interface IConflictResolver
{
    Task<ConflictDecision> ResolveAsync(FileOperation operation, ConflictInfo conflict, CancellationToken cancellationToken);
}

public interface IFileOperationService
{
    IReadOnlyList<FileOperation> Operations { get; }

    /// <summary>Queues a copy/move. Returns immediately; work runs in the background with bounded concurrency.</summary>
    FileOperation Enqueue(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination,
        TransferOptions? options = null);

    /// <summary>Runs a finished/failed operation again; files that already arrived (same name and size) are skipped.</summary>
    FileOperation Retry(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination);

    /// <summary>Raised (on a worker thread) when an operation is queued.</summary>
    event EventHandler<FileOperation>? OperationQueued;

    /// <summary>Raised (on a worker thread) when an operation finishes in any state.</summary>
    event EventHandler<FileOperation>? OperationFinished;

    void Remove(FileOperation operation);

    /// <summary>True when an executor exists for copying from one provider to another.</summary>
    bool CanTransfer(string sourceProviderId, string destinationProviderId);
}
