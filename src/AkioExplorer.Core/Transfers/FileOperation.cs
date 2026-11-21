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

    /// <summary>The app closed while the operation was running (history only).</summary>
    Interrupted,
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

public sealed class TransferOptions
{
    /// <summary>Retry mode: files that already exist at the destination with the same size are treated as done.</summary>
    public bool SkipIdentical { get; init; }

    /// <summary>Forces a conflict answer without asking (e.g. refreshing the "open file" cache).</summary>
    public ConflictResolution? Conflict { get; init; }

    /// <summary>Hidden from history and undo (internal jobs such as downloading a file to open it).</summary>
    public bool Internal { get; init; }
}

/// <summary>Byte progress + pause point handed to transfer executors.</summary>
public interface ITransferProgress
{
    void Report(long bytes);

    /// <summary>Awaits while the operation is paused (async code paths).</summary>
    ValueTask WaitIfPausedAsync(CancellationToken cancellationToken);

    /// <summary>Blocks while the operation is paused (OS callbacks that can't await).</summary>
    void WaitIfPaused(CancellationToken cancellationToken);
}

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
    private TaskCompletionSource? _resumeSignal;

    public FileOperation(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination,
        string sourceName, string destinationName, TransferOptions? options = null)
    {
        Kind = kind;
        SourceProviderId = sourceProviderId;
        Items = items;
        Destination = destination;
        SourceName = sourceName;
        DestinationName = destinationName;
        Options = options ?? new TransferOptions();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public OperationKind Kind { get; }

    public string SourceProviderId { get; }

    public IReadOnlyList<StorageItem> Items { get; }

    public StorageLocation Destination { get; }

    /// <summary>Folder the items came from (display only).</summary>
    public string SourceName { get; }

    public string DestinationName { get; }

    public TransferOptions Options { get; }

    public DateTimeOffset Created { get; } = DateTimeOffset.Now;

    public DateTimeOffset? FinishedAt { get; private set; }

    internal CancellationTokenSource Cancellation { get; } = new();

    public CancellationToken CancellationToken => Cancellation.Token;

    public OperationStatus Status => _status;

    public bool IsFinished => _status is OperationStatus.Completed or OperationStatus.CompletedWithErrors
        or OperationStatus.Canceled or OperationStatus.Failed or OperationStatus.Interrupted;

    public bool IsPaused
    {
        get { lock (_gate) return _resumeSignal is not null; }
    }

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

    public string What => Items.Count == 1 ? $"\"{Items[0].Name}\"" : $"{Items.Count:N0} items";

    public string Title
    {
        get
        {
            string verb = Kind == OperationKind.Copy ? "Copying" : "Moving";
            return IsFinished
                ? $"{(Kind == OperationKind.Copy ? "Copied" : "Moved")} {What} to {DestinationName}"
                : $"{verb} {What} to {DestinationName}";
        }
    }

    public event EventHandler? Finished;

    public void Cancel()
    {
        Resume(); // a paused job must wake up to observe the cancellation
        Cancellation.Cancel();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (IsFinished || _resumeSignal is not null) return;
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            signal = _resumeSignal;
            _resumeSignal = null;
        }
        signal?.TrySetResult();
    }

    internal async ValueTask WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate) wait = _resumeSignal?.Task;
        if (wait is not null) await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void WaitIfPaused(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate) wait = _resumeSignal?.Task;
        wait?.Wait(cancellationToken);
    }

    internal void SetStatus(OperationStatus status)
    {
        _status = status;
        if (IsFinished)
        {
            Resume();
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
