using System.Collections.ObjectModel;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace AkioExplorer.App.ViewModels;

/// <summary>
/// Live operations: the transfer queue page (<see cref="Queue"/>) and the compact bottom panel (<see cref="Items"/>).
/// Follows the response-time limits: operations that finish within ~0.6 s never appear in the panel (no flashing),
/// longer ones show percent, speed and time left, failures stay until dismissed or retried.
/// </summary>
public sealed partial class OperationsViewModel : ObservableObject
{
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan KeepCompletedFor = TimeSpan.FromSeconds(3);

    private readonly IFileOperationService _service;
    private readonly IStorageProviderRegistry _registry;
    private readonly UndoService _undo;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<OperationItemViewModel> _pending = new();

    public OperationsViewModel(IFileOperationService service, IStorageProviderRegistry registry, UndoService undo, DispatcherQueue dispatcher)
    {
        _service = service;
        _registry = registry;
        _undo = undo;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();

        service.OperationQueued += (_, op) => dispatcher.TryEnqueue(() => OnQueued(op));
        service.OperationFinished += (_, op) => dispatcher.TryEnqueue(() => OnFinished(op));
    }

    /// <summary>Bottom panel: operations running longer than a moment, plus failures.</summary>
    public ObservableCollection<OperationItemViewModel> Items { get; } = new();

    /// <summary>Transfer queue page: everything queued, running or paused.</summary>
    public ObservableCollection<OperationItemViewModel> Queue { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private bool _hasQueue;

    /// <summary>Raised on the UI thread when an operation finishes, so panes can select the new items.</summary>
    public event EventHandler<FileOperation>? OperationCompleted;

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var item in Queue) item.Operation.Pause();
        Tick();
    }

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var item in Queue) item.Operation.Resume();
        Tick();
    }

    private void OnQueued(FileOperation op)
    {
        var item = new OperationItemViewModel(op, Dismiss, Retry);
        _pending.Add(item);
        if (!op.Options.Internal) Queue.Add(item);
        UpdateCounts();
        _timer.Start();
    }

    private void OnFinished(FileOperation op)
    {
        var item = Items.FirstOrDefault(i => i.Operation == op) ?? _pending.FirstOrDefault(i => i.Operation == op)
                   ?? Queue.FirstOrDefault(i => i.Operation == op);
        if (item is not null)
        {
            item.Refresh();
            Queue.Remove(item); // finished jobs live on in Recent transfers
            bool problem = op.Status is OperationStatus.Failed or OperationStatus.CompletedWithErrors;
            if (problem && !Items.Contains(item)) Show(item); // errors are always visible
        }

        UpdateCounts();
        if (!op.Options.Internal) RegisterUndo(op);
        OperationCompleted?.Invoke(this, op);
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var p in _pending.ToList())
        {
            if (p.Operation.IsFinished && p.Operation.Status is OperationStatus.Completed or OperationStatus.Canceled)
            {
                _pending.Remove(p); // fast operation: never shown
                _service.Remove(p.Operation);
            }
            else if (now - p.Operation.Created >= ShowAfter)
            {
                Show(p);
            }
        }

        foreach (var item in Items.Union(Queue).ToList())
        {
            item.Refresh();
            if (Items.Contains(item) && item.Operation.Status is OperationStatus.Completed or OperationStatus.Canceled &&
                item.Operation.FinishedAt is { } finished && now - finished >= KeepCompletedFor)
            {
                Dismiss(item);
            }
        }

        if (_pending.Count == 0 && Queue.Count == 0 && Items.All(i => i.Operation.IsFinished && i.HasProblem)) _timer.Stop();
    }

    private void UpdateCounts()
    {
        ActiveCount = Queue.Count;
        HasQueue = Queue.Count > 0;
    }

    private void Show(OperationItemViewModel item)
    {
        _pending.Remove(item);
        if (!Items.Contains(item)) Items.Add(item);
        HasItems = true;
        _timer.Start();
    }

    private void Dismiss(OperationItemViewModel item)
    {
        if (!item.Operation.IsFinished) item.Operation.Cancel();
        Items.Remove(item);
        _pending.Remove(item);
        if (item.Operation.IsFinished) Queue.Remove(item);
        _service.Remove(item.Operation);
        HasItems = Items.Count > 0;
        UpdateCounts();
    }

    private void Retry(OperationItemViewModel item)
    {
        var op = item.Operation;
        Dismiss(item);
        if (_registry.Find(op.SourceProviderId) is null || _registry.Find(op.Destination.ProviderId) is null) return;
        _service.Retry(op.Kind, op.SourceProviderId, op.Items, op.Destination);
    }

    private void RegisterUndo(FileOperation op)
    {
        var created = op.CreatedPaths;
        var results = op.Results;
        if (created.Count == 0 || op.Status == OperationStatus.Failed) return;
        var destination = _registry.Find(op.Destination.ProviderId);
        var source = _registry.Find(op.SourceProviderId);
        if (destination is null || source is null) return;

        string what = op.What;
        if (op.Kind == OperationKind.Copy)
        {
            _undo.Push($"Copy {what}", () => destination.DeleteAsync(created, permanent: !destination.Capabilities.HasFlag(StorageCapabilities.RecycleBin), CancellationToken.None));
        }
        else if (source.GetParentPath(op.Items[0].Path) is { } originalFolder)
        {
            _undo.Push($"Move {what}", async () =>
            {
                var items = new List<StorageItem>();
                foreach (var path in created)
                {
                    if (await destination.GetItemAsync(path, CancellationToken.None) is { } item) items.Add(item);
                }
                if (items.Count == 0) return;

                var back = _service.Enqueue(OperationKind.Move, destination.Id, items, new StorageLocation(source.Id, originalFolder));
                // Items renamed by "keep both" get their original names back once they're home.
                async Task RestoreNamesAsync()
                {
                    foreach (var r in results)
                    {
                        string createdName = destination.GetDisplayName(r.CreatedPath);
                        if (string.Equals(createdName, r.Source.Name, StringComparison.Ordinal)) continue;
                        string returned = source.CombinePath(originalFolder, createdName);
                        try
                        {
                            if (await source.GetItemAsync(r.Source.Path, CancellationToken.None) is null)
                                await source.RenameAsync(returned, r.Source.Name, CancellationToken.None);
                        }
                        catch (StorageException)
                        {
                            // Name taken again meanwhile: leave the "(2)" name rather than overwrite anything.
                        }
                    }
                }

                int restored = 0;
                void OnBackFinished() { if (Interlocked.Exchange(ref restored, 1) == 0) _ = RestoreNamesAsync(); }
                back.Finished += (_, _) => OnBackFinished();
                if (back.IsFinished) OnBackFinished(); // finished before we subscribed
            });
        }
    }
}

public sealed partial class OperationItemViewModel : ObservableObject
{
    private readonly Action<OperationItemViewModel> _dismiss;
    private readonly Action<OperationItemViewModel> _retry;
    private long _lastBytes;
    private DateTimeOffset _lastSample = DateTimeOffset.Now;
    private double _speed; // bytes/s, smoothed

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private bool _hasProblem;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _canRetry;

    public OperationItemViewModel(FileOperation operation, Action<OperationItemViewModel> dismiss, Action<OperationItemViewModel> retry)
    {
        Operation = operation;
        _dismiss = dismiss;
        _retry = retry;
        Refresh();
    }

    public FileOperation Operation { get; }

    public string Glyph => Operation.Kind == OperationKind.Copy ? "" : "";

    public string Route => $"{Operation.SourceName}  →  {Operation.DestinationName}";

    public string PauseGlyph => IsPaused ? "" : "";

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseGlyph));
        OnPropertyChanged(nameof(PauseLabel));
    }

    [RelayCommand]
    private void CancelOrDismiss() => _dismiss(this);

    [RelayCommand]
    private void PauseOrResume()
    {
        if (Operation.IsPaused) Operation.Resume();
        else Operation.Pause();
        Refresh();
    }

    [RelayCommand]
    private void Retry() => _retry(this);

    public void Refresh()
    {
        var op = Operation;
        Title = op.Title;
        long total = op.TotalBytes;
        long done = op.TransferredBytes;

        var now = DateTimeOffset.Now;
        double seconds = (now - _lastSample).TotalSeconds;
        if (seconds >= 0.2)
        {
            double instant = Math.Max(0, done - _lastBytes) / seconds;
            _speed = op.IsPaused ? 0 : _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
            _lastBytes = done;
            _lastSample = now;
        }

        IsPaused = op.IsPaused;
        IsRunning = !op.IsFinished;
        HasProblem = op.Status is OperationStatus.Failed or OperationStatus.CompletedWithErrors;
        CanRetry = op.Status is OperationStatus.Failed or OperationStatus.CompletedWithErrors;
        IsIndeterminate = !op.IsPaused && op.Status is OperationStatus.Queued or OperationStatus.Preparing;
        Progress = total > 0 ? Math.Min(100, 100.0 * done / total) : op.IsFinished ? 100 : 0;

        Detail = op.IsPaused && !op.IsFinished
            ? $"Paused  ·  {Progress:0}%  ·  {Formatting.FormatSize(done)} of {Formatting.FormatSize(total)}"
            : op.Status switch
            {
                OperationStatus.Queued => "Waiting for other transfers to finish…",
                OperationStatus.Preparing => "Preparing…",
                OperationStatus.Running => RunningText(op, total, done),
                OperationStatus.Completed => op.SkippedFiles > 0 ? $"Done · {op.SkippedFiles:N0} skipped" : "Done",
                OperationStatus.Canceled => "Canceled",
                _ => ProblemText(op),
            };
    }

    private string RunningText(FileOperation op, long total, long done)
    {
        var parts = new List<string> { $"{Progress:0}%", $"{Formatting.FormatSize(done)} of {Formatting.FormatSize(total)}" };
        if (_speed > 1024)
        {
            parts.Add($"{Formatting.FormatSize((long)_speed)}/s");
            double remaining = (total - done) / _speed;
            if (remaining >= 1) parts.Add(FormatEta(remaining) + " left");
        }
        if (op.TotalFiles > 1) parts.Add($"{op.CompletedFiles:N0} of {op.TotalFiles:N0} files");
        if (op.CurrentItem is { } current) parts.Add(current);
        return string.Join("  ·  ", parts);
    }

    private static string ProblemText(FileOperation op)
    {
        var errors = op.Errors;
        if (errors.Count == 0) return "Failed";
        return errors.Count == 1 ? errors[0] : $"{errors.Count:N0} problems. First: {errors[0]}";
    }

    internal static string FormatEta(double seconds) => seconds switch
    {
        < 60 => $"{seconds:0} s",
        < 3600 => $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s",
        _ => $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min",
    };
}

/// <summary>Recent transfers (persisted history) with retry.</summary>
public sealed partial class TransferHistoryViewModel : ObservableObject
{
    private readonly ITransferHistory _history;
    private readonly IFileOperationService _service;
    private readonly IStorageProviderRegistry _registry;
    private readonly DispatcherQueue _dispatcher;

    public TransferHistoryViewModel(ITransferHistory history, IFileOperationService service, IStorageProviderRegistry registry, DispatcherQueue dispatcher)
    {
        _history = history;
        _service = service;
        _registry = registry;
        _dispatcher = dispatcher;
        history.Changed += (_, _) => _dispatcher.TryEnqueue(Rebuild);
        Rebuild();
    }

    public ObservableCollection<TransferRecordViewModel> Records { get; } = new();

    [ObservableProperty] private bool _hasRecords;

    [ObservableProperty] private string _message = string.Empty;

    [RelayCommand]
    private void Clear() => _history.Clear();

    public void Retry(TransferRecordViewModel record)
    {
        var r = record.Record;
        if (_registry.Find(r.SourceProviderId) is null || _registry.Find(r.Destination.ProviderId) is null)
        {
            Message = "One of the accounts in this transfer isn't connected any more.";
            return;
        }
        Message = string.Empty;
        _history.Remove(r.Id);
        _service.Retry(r.Kind, r.SourceProviderId, r.Items, r.Destination);
    }

    public void Remove(TransferRecordViewModel record) => _history.Remove(record.Record.Id);

    private void Rebuild()
    {
        Records.Clear();
        foreach (var r in _history.Records) Records.Add(new TransferRecordViewModel(r));
        HasRecords = Records.Count > 0;
    }
}

public sealed class TransferRecordViewModel(TransferRecord record)
{
    public TransferRecord Record { get; } = record;

    public string Title => Record.Title;

    public string Route => $"{Record.SourceName}  →  {Record.DestinationName}";

    public bool CanRetry => Record.CanRetry;

    public string Glyph => Record.Status switch
    {
        OperationStatus.Completed => "",
        OperationStatus.CompletedWithErrors => "",
        OperationStatus.Canceled => "",
        OperationStatus.Interrupted => "",
        _ => "",
    };

    public string StatusText
    {
        get
        {
            string status = Record.Status switch
            {
                OperationStatus.Completed => Record.Skipped > 0 ? $"Done, {Record.Skipped:N0} skipped" : "Done",
                OperationStatus.CompletedWithErrors => $"Done with {Record.Errors.Count:N0} problem(s)",
                OperationStatus.Canceled => "Canceled",
                OperationStatus.Interrupted => "Interrupted when AkioExplorer closed",
                _ => "Failed",
            };
            string size = Record.TotalBytes > 0 ? $"  ·  {Formatting.FormatSize(Record.TransferredBytes)} of {Formatting.FormatSize(Record.TotalBytes)}" : string.Empty;
            string when = Record.Finished is { } f ? $"  ·  {f.LocalDateTime:g}" : string.Empty;
            return status + size + when;
        }
    }

    public string ErrorText => Record.Errors.Count == 0 ? string.Empty : Record.Errors[0];

    public bool HasError => Record.Errors.Count > 0;
}
