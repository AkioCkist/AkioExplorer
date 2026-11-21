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
/// Bottom progress panel. Follows the response-time limits: operations that finish within ~0.6 s never appear
/// (no flashing), longer ones show percent, speed and time left, failures stay until dismissed.
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

    public ObservableCollection<OperationItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;

    /// <summary>Raised on the UI thread when an operation finishes, so panes can select the new items.</summary>
    public event EventHandler<FileOperation>? OperationCompleted;

    private void OnQueued(FileOperation op)
    {
        _pending.Add(new OperationItemViewModel(op, Dismiss));
        _timer.Start();
    }

    private void OnFinished(FileOperation op)
    {
        var item = Items.FirstOrDefault(i => i.Operation == op) ?? _pending.FirstOrDefault(i => i.Operation == op);
        if (item is not null)
        {
            item.Refresh();
            bool problem = op.Status is OperationStatus.Failed or OperationStatus.CompletedWithErrors;
            if (problem && !Items.Contains(item)) Show(item); // errors are always visible
        }

        RegisterUndo(op);
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

        foreach (var item in Items.ToList())
        {
            item.Refresh();
            if (item.Operation.Status is OperationStatus.Completed or OperationStatus.Canceled &&
                item.Operation.FinishedAt is { } finished && now - finished >= KeepCompletedFor)
            {
                Dismiss(item);
            }
        }

        if (_pending.Count == 0 && Items.All(i => i.Operation.IsFinished && i.HasProblem)) _timer.Stop();
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
        _service.Remove(item.Operation);
        HasItems = Items.Count > 0;
    }

    private void RegisterUndo(FileOperation op)
    {
        var created = op.CreatedPaths;
        var results = op.Results;
        if (created.Count == 0 || op.Status == OperationStatus.Failed) return;
        var destination = _registry.Find(op.Destination.ProviderId);
        var source = _registry.Find(op.SourceProviderId);
        if (destination is null || source is null) return;

        string what = op.Items.Count == 1 ? $"\"{op.Items[0].Name}\"" : $"{op.Items.Count:N0} items";
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
    private long _lastBytes;
    private DateTimeOffset _lastSample = DateTimeOffset.Now;
    private double _speed; // bytes/s, smoothed

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private bool _hasProblem;

    public OperationItemViewModel(FileOperation operation, Action<OperationItemViewModel> dismiss)
    {
        Operation = operation;
        _dismiss = dismiss;
        Refresh();
    }

    public FileOperation Operation { get; }

    public string Glyph => Operation.Kind == OperationKind.Copy ? "" : "";

    [RelayCommand]
    private void CancelOrDismiss() => _dismiss(this);

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
            _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
            _lastBytes = done;
            _lastSample = now;
        }

        IsRunning = !op.IsFinished;
        HasProblem = op.Status is OperationStatus.Failed or OperationStatus.CompletedWithErrors;
        IsIndeterminate = op.Status is OperationStatus.Queued or OperationStatus.Preparing;
        Progress = total > 0 ? Math.Min(100, 100.0 * done / total) : op.IsFinished ? 100 : 0;

        Detail = op.Status switch
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
        if (op.CurrentItem is { } current) parts.Add(current);
        return string.Join("  ·  ", parts);
    }

    private static string ProblemText(FileOperation op)
    {
        var errors = op.Errors;
        if (errors.Count == 0) return "Failed";
        return errors.Count == 1 ? errors[0] : $"{errors.Count:N0} problems. First: {errors[0]}";
    }

    private static string FormatEta(double seconds) => seconds switch
    {
        < 60 => $"{seconds:0} s",
        < 3600 => $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s",
        _ => $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min",
    };
}
