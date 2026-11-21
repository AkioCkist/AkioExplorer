using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Transfers;

public enum OperationKind
{
    Copy,
    Move,
}

public enum OperationStatus
{
    Queued,
    Preparing,
    Running,
    Completed,
    CompletedWithErrors,
    Canceled,
    Failed,
}

public enum ConflictResolution
{
    Replace,
    Skip,
    KeepBoth,
}

/// <summary>What the user configured for name conflicts (Settings).</summary>
public enum ConflictBehavior
{
    Ask,
    Replace,
    Skip,
    KeepBoth,
}

public sealed record ConflictInfo(StorageItem Source, StorageItem Existing, string DestinationFolderName, int RemainingItems);

public sealed record ConflictDecision(ConflictResolution Resolution, bool ApplyToAll);

public sealed record TransferResult(StorageItem Source, string CreatedPath);

/// <summary>
/// One copy/move job. Progress fields are written by the worker and read by the UI on a timer
/// (no per-byte events crossing threads – keeps the UI thread free during big transfers).
/// </summary>
public sealed class FileOperation
{
    private readonly object _gate = new();
    private readonly List<string> _errors = new();
    private readonly List<TransferResult> _results = new();
    private long _totalBytes;
    private long _transferredBytes;
    private int _totalFiles;
    private int _completedFiles;
    private int _skipped;
    private volatile OperationStatus _status = OperationStatus.Queued;
    private volatile string? _currentItem;

    public FileOperation(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination, string destinationName)
    {
        Kind = kind;
        SourceProviderId = sourceProviderId;
        Items = items;
        Destination = destination;
        DestinationName = destinationName;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public OperationKind Kind { get; }

    public string SourceProviderId { get; }

    public IReadOnlyList<StorageItem> Items { get; }

    public StorageLocation Destination { get; }

    public string DestinationName { get; }

    public DateTimeOffset Created { get; } = DateTimeOffset.Now;

    public DateTimeOffset? FinishedAt { get; private set; }

    internal CancellationTokenSource Cancellation { get; } = new();

    public CancellationToken CancellationToken => Cancellation.Token;

    public OperationStatus Status => _status;

    public bool IsFinished => _status is OperationStatus.Completed or OperationStatus.CompletedWithErrors or OperationStatus.Canceled or OperationStatus.Failed;

    public string? CurrentItem => _currentItem;

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public int TotalFiles => Volatile.Read(ref _totalFiles);

    public int CompletedFiles => Volatile.Read(ref _completedFiles);

    public int SkippedFiles => Volatile.Read(ref _skipped);

    /// <summary>Conflict answer the user chose with "apply to all".</summary>
    public ConflictResolution? ConflictAnswerForAll { get; internal set; }

    public IReadOnlyList<string> Errors
    {
        get { lock (_gate) return _errors.ToArray(); }
    }

    /// <summary>Top-level items created at the destination (used to select them).</summary>
    public IReadOnlyList<string> CreatedPaths
    {
        get { lock (_gate) return _results.Select(r => r.CreatedPath).ToArray(); }
    }

    /// <summary>Source → destination pairs of top-level items (used for undo, incl. renamed "keep both" copies).</summary>
    public IReadOnlyList<TransferResult> Results
    {
        get { lock (_gate) return _results.ToArray(); }
    }

    public string Title
    {
        get
        {
            string verb = Kind == OperationKind.Copy ? "Copying" : "Moving";
            string what = Items.Count == 1 ? $"\"{Items[0].Name}\"" : $"{Items.Count:N0} items";
            return IsFinished
                ? $"{(Kind == OperationKind.Copy ? "Copied" : "Moved")} {what} to {DestinationName}"
                : $"{verb} {what} to {DestinationName}";
        }
    }

    public event EventHandler? Finished;

    public void Cancel() => Cancellation.Cancel();

    internal void SetStatus(OperationStatus status)
    {
        _status = status;
        if (IsFinished)
        {
            FinishedAt = DateTimeOffset.Now;
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetCurrent(string? name) => _currentItem = name;

    internal void AddPlanned(long bytes, int files)
    {
        Interlocked.Add(ref _totalBytes, bytes);
        Interlocked.Add(ref _totalFiles, files);
    }

    internal void AddTransferred(long bytes) => Interlocked.Add(ref _transferredBytes, bytes);

    internal void FileDone() => Interlocked.Increment(ref _completedFiles);

    internal void FileSkipped(long bytes)
    {
        Interlocked.Increment(ref _skipped);
        Interlocked.Increment(ref _completedFiles);
        Interlocked.Add(ref _transferredBytes, bytes);
    }

    internal void AddError(string message)
    {
        lock (_gate) _errors.Add(message);
    }

    internal void AddCreated(StorageItem source, string createdPath)
    {
        lock (_gate) _results.Add(new TransferResult(source, createdPath));
    }
}
