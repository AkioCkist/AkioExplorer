using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Provider-agnostic copy/move engine: bounded concurrency, recursive planning, conflict handling,
/// retries with backoff for transient errors, instant same-volume moves, and safe moves (the source is only
/// removed when every file inside it was transferred).
/// </summary>
public sealed class FileOperationService : IFileOperationService
{
    private const int MaxAttempts = 3;

    private readonly IStorageProviderRegistry _registry;
    private readonly IReadOnlyList<ITransferExecutor> _executors;
    private readonly IConflictResolver _resolver;
    private readonly ISettingsService _settings;
    private readonly ILogger<FileOperationService> _logger;
    private readonly List<FileOperation> _operations = new();
    private readonly AsyncLimiter _limiter;

    public FileOperationService(
        IStorageProviderRegistry registry,
        IEnumerable<ITransferExecutor> executors,
        IConflictResolver resolver,
        ISettingsService settings,
        ILogger<FileOperationService> logger)
    {
        _registry = registry;
        _executors = executors.ToList();
        _resolver = resolver;
        _settings = settings;
        _logger = logger;
        _limiter = new AsyncLimiter(() => Math.Clamp(_settings.Current.MaxSimultaneousOperations, 1, 8));
    }

    public IReadOnlyList<FileOperation> Operations
    {
        get { lock (_operations) return _operations.ToArray(); }
    }

    public event EventHandler<FileOperation>? OperationQueued;

    public event EventHandler<FileOperation>? OperationFinished;

    public FileOperation Enqueue(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination)
    {
        var destinationProvider = _registry.Get(destination.ProviderId);
        var operation = new FileOperation(kind, sourceProviderId, items, destination, destinationProvider.GetDisplayName(destination.Path));
        lock (_operations) _operations.Add(operation);
        OperationQueued?.Invoke(this, operation);
        _ = Task.Run(() => RunAsync(operation));
        return operation;
    }

    public bool CanTransfer(string sourceProviderId, string destinationProviderId) =>
        _registry.Find(sourceProviderId) is { } src && _registry.Find(destinationProviderId) is { } dst &&
        _executors.Any(e => e.CanTransfer(src, dst));

    public void Remove(FileOperation operation)
    {
        if (!operation.IsFinished) operation.Cancel();
        lock (_operations) _operations.Remove(operation);
    }

    private async Task RunAsync(FileOperation op)
    {
        bool acquired = false;
        try
        {
            await _limiter.WaitAsync(op.CancellationToken).ConfigureAwait(false);
            acquired = true;
            await ExecuteAsync(op).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            op.SetStatus(OperationStatus.Canceled);
        }
        catch (StorageException ex)
        {
            op.AddError(ex.Message);
            op.SetStatus(OperationStatus.Failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File operation {Id} failed", op.Id);
            op.AddError($"Something went wrong: {ex.Message}");
            op.SetStatus(OperationStatus.Failed);
        }
        finally
        {
            if (acquired) _limiter.Release();
            OperationFinished?.Invoke(this, op);
        }
    }

    // ------------------------------------------------------------------ execution

    private sealed record PlannedFile(StorageItem Item, string RelativeFolder);

    private sealed class TopLevel(StorageItem item, bool sameFolder)
    {
        public StorageItem Item { get; } = item;
        public bool SameFolder { get; } = sameFolder;
        public List<PlannedFile> Files { get; } = new();
        public List<string> Folders { get; } = new();
        public bool Incomplete { get; set; }
    }

    private async Task ExecuteAsync(FileOperation op)
    {
        var ct = op.CancellationToken;
        var src = _registry.Get(op.SourceProviderId);
        var dst = _registry.Get(op.Destination.ProviderId);
        var executor = _executors.FirstOrDefault(e => e.CanTransfer(src, dst))
            ?? throw new StorageException(StorageErrorKind.NotSupported, "Transfers between these two locations aren't available yet.");
        string destRoot = op.Destination.Path;
        string verb = op.Kind == OperationKind.Copy ? "copy" : "move";

        if (destRoot == dst.RootPath && (dst.Capabilities & StorageCapabilities.VirtualRoot) != 0)
            throw new StorageException(StorageErrorKind.NotSupported, "Items can't be placed directly in This PC. Open a drive or folder first.");

        foreach (var item in op.Items)
        {
            if (item.Kind == StorageItemKind.Drive)
                throw new StorageException(StorageErrorKind.NotSupported, $"Drives can't be {(op.Kind == OperationKind.Copy ? "copied" : "moved")} as a whole. Open \"{item.Name}\" and select its contents.");
            if (item.IsContainer && src.Id == dst.Id && dst.IsSameOrDescendant(destRoot, item.Path))
                throw new StorageException(StorageErrorKind.InvalidName, $"Can't {verb} \"{item.Name}\" into itself or one of its own subfolders.");
        }

        op.SetStatus(OperationStatus.Preparing);
        var plan = new List<TopLevel>();
        foreach (var item in op.Items)
        {
            ct.ThrowIfCancellationRequested();
            bool sameFolder = src.Id == dst.Id &&
                              string.Equals(src.GetParentPath(item.Path), destRoot, StringComparison.OrdinalIgnoreCase);
            if (op.Kind == OperationKind.Move && sameFolder) continue; // moving into the folder it is already in

            if (op.Kind == OperationKind.Move)
            {
                // Fast path: same volume rename, no conflict – instant regardless of size.
                string target = dst.CombinePath(destRoot, item.Name);
                if (await dst.GetItemAsync(target, ct).ConfigureAwait(false) is null &&
                    await executor.TryMoveAsync(src, item, dst, target, ct).ConfigureAwait(false))
                {
                    op.AddPlanned(item.Size ?? 0, 1);
                    op.FileDone();
                    op.AddTransferred(item.Size ?? 0);
                    op.AddCreated(item, target);
                    continue;
                }
            }

            var top = new TopLevel(item, sameFolder);
            if (item.IsContainer)
                await ExpandAsync(src, item.Path, string.Empty, top, ct).ConfigureAwait(false);
            else
                top.Files.Add(new PlannedFile(item, string.Empty));

            op.AddPlanned(top.Files.Sum(f => f.Item.Size ?? 0), top.Files.Count);
            plan.Add(top);
        }

        op.SetStatus(OperationStatus.Running);
        foreach (var top in plan)
        {
            ct.ThrowIfCancellationRequested();
            await TransferTopLevelAsync(op, executor, src, dst, destRoot, top, ct).ConfigureAwait(false);
        }

        op.SetCurrent(null);
        op.SetStatus(op.Errors.Count > 0 ? OperationStatus.CompletedWithErrors : OperationStatus.Completed);
    }

    private static async Task ExpandAsync(IStorageProvider src, string folderPath, string relative, TopLevel top, CancellationToken ct)
    {
        top.Folders.Add(relative);
        await foreach (var page in src.ListAsync(folderPath, ct).ConfigureAwait(false))
        {
            foreach (var child in page)
            {
                string childRelative = relative.Length == 0 ? child.Name : relative + "\u0000" + child.Name;
                if (child.IsContainer)
                {
                    if ((child.Attributes & StorageItemAttributes.ReparsePoint) != 0) continue; // don't follow junctions
                    await ExpandAsync(src, child.Path, childRelative, top, ct).ConfigureAwait(false);
                }
                else
                {
                    top.Files.Add(new PlannedFile(child, relative));
                }
            }
        }
    }

    private async Task TransferTopLevelAsync(FileOperation op, ITransferExecutor executor, IStorageProvider src, IStorageProvider dst,
        string destRoot, TopLevel top, CancellationToken ct)
    {
        var item = top.Item;
        string topTarget = dst.CombinePath(destRoot, item.Name);
        bool anyWritten = false;

        if (item.IsContainer)
        {
            var existing = await dst.GetItemAsync(topTarget, ct).ConfigureAwait(false);
            if (top.SameFolder)
                topTarget = await UniqueNameAsync(dst, destRoot, item.Name, copySuffix: true, ct).ConfigureAwait(false);
            else if (existing is { IsContainer: false })
                topTarget = await UniqueNameAsync(dst, destRoot, item.Name, copySuffix: false, ct).ConfigureAwait(false);
            // An existing folder with the same name is merged; conflicts are handled per file inside it.

            var folderPaths = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = topTarget };
            foreach (var relative in top.Folders)
            {
                string path = ResolveFolder(dst, folderPaths, relative);
                await EnsureFolderAsync(dst, path, ct).ConfigureAwait(false);
            }

            foreach (var file in top.Files)
            {
                ct.ThrowIfCancellationRequested();
                string folder = ResolveFolder(dst, folderPaths, file.RelativeFolder);
                if (await TransferFileAsync(op, executor, src, dst, file.Item, folder, inPlaceCopy: false, ct).ConfigureAwait(false) is null)
                    top.Incomplete = true;
            }

            anyWritten = true;
        }
        else
        {
            string? written = await TransferFileAsync(op, executor, src, dst, item, destRoot, top.SameFolder, ct).ConfigureAwait(false);
            if (written is null) top.Incomplete = true;
            else
            {
                topTarget = written;
                anyWritten = true;
            }
        }

        if (anyWritten) op.AddCreated(item, topTarget);

        if (op.Kind == OperationKind.Move && !top.Incomplete)
        {
            try
            {
                await src.DeleteAsync([item.Path], permanent: true, ct).ConfigureAwait(false);
            }
            catch (StorageException ex)
            {
                op.AddError($"\"{item.Name}\" was copied but the original couldn't be removed: {ex.Message}");
            }
        }
        else if (op.Kind == OperationKind.Move && top.Incomplete && item.IsContainer)
        {
            op.AddError($"\"{item.Name}\" was only partly moved; the original folder was kept so nothing is lost.");
        }
    }

    /// <summary>Copies one file into <paramref name="folder"/>. Returns the written path, or null if skipped/failed.</summary>
    private async Task<string?> TransferFileAsync(FileOperation op, ITransferExecutor executor, IStorageProvider src, IStorageProvider dst,
        StorageItem file, string folder, bool inPlaceCopy, CancellationToken ct)
    {
        long size = file.Size ?? 0;
        string target = inPlaceCopy
            ? await UniqueNameAsync(dst, folder, file.Name, copySuffix: true, ct).ConfigureAwait(false)
            : dst.CombinePath(folder, file.Name);
        bool overwrite = false;

        if (!inPlaceCopy && await dst.GetItemAsync(target, ct).ConfigureAwait(false) is { } existing)
        {
            var resolution = existing.IsContainer
                ? ConflictResolution.KeepBoth
                : await DecideAsync(op, file, existing, dst.GetDisplayName(folder), ct).ConfigureAwait(false);

            switch (resolution)
            {
                case ConflictResolution.Skip:
                    op.FileSkipped(size);
                    return null;
                case ConflictResolution.KeepBoth:
                    target = await UniqueNameAsync(dst, folder, file.Name, copySuffix: false, ct).ConfigureAwait(false);
                    break;
                case ConflictResolution.Replace:
                    if (src.Id == dst.Id && string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        op.FileSkipped(size); // replacing a file with itself is a no-op
                        return null;
                    }
                    overwrite = true;
                    break;
            }
        }

        op.SetCurrent(file.Name);
        for (int attempt = 1; ; attempt++)
        {
            long reported = 0;
            try
            {
                await executor.CopyFileAsync(src, file, dst, target, overwrite, delta =>
                {
                    reported += delta;
                    op.AddTransferred(delta);
                }, ct).ConfigureAwait(false);
                op.FileDone();
                return target;
            }
            catch (OperationCanceledException)
            {
                op.AddTransferred(-reported);
                throw;
            }
            catch (StorageException ex) when (attempt < MaxAttempts && ex.Kind is StorageErrorKind.InUse or StorageErrorKind.Unavailable)
            {
                op.AddTransferred(-reported);
                _logger.LogInformation("Retrying {File} after transient error: {Message}", file.Name, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
            }
            catch (StorageException ex)
            {
                op.AddTransferred(size - reported); // keep the progress bar honest: the file is "done" (failed)
                op.FileDone();
                op.AddError($"{file.Name}: {ex.Message}");
                return null;
            }
        }
    }

    private async Task<ConflictResolution> DecideAsync(FileOperation op, StorageItem source, StorageItem existing, string folderName, CancellationToken ct)
    {
        if (op.ConflictAnswerForAll is { } forAll) return forAll;

        switch (_settings.Current.ConflictBehavior)
        {
            case ConflictBehavior.Replace: return ConflictResolution.Replace;
            case ConflictBehavior.Skip: return ConflictResolution.Skip;
            case ConflictBehavior.KeepBoth: return ConflictResolution.KeepBoth;
        }

        int remaining = Math.Max(0, op.TotalFiles - op.CompletedFiles - 1);
        var decision = await _resolver.ResolveAsync(op, new ConflictInfo(source, existing, folderName, remaining), ct).ConfigureAwait(false);
        if (decision.ApplyToAll) op.ConflictAnswerForAll = decision.Resolution;
        return decision.Resolution;
    }

    private static string ResolveFolder(IStorageProvider dst, Dictionary<string, string> cache, string relative)
    {
        if (cache.TryGetValue(relative, out var path)) return path;
        int cut = relative.LastIndexOf('\u0000');
        string parentRelative = cut < 0 ? string.Empty : relative[..cut];
        string name = cut < 0 ? relative : relative[(cut + 1)..];
        path = dst.CombinePath(ResolveFolder(dst, cache, parentRelative), name);
        cache[relative] = path;
        return path;
    }

    private static async Task EnsureFolderAsync(IStorageProvider dst, string path, CancellationToken ct)
    {
        if (await dst.GetItemAsync(path, ct).ConfigureAwait(false) is { IsContainer: true }) return;
        string parent = dst.GetParentPath(path) ?? string.Empty;
        string name = dst.GetDisplayName(path);
        try
        {
            await dst.CreateFolderAsync(parent, name, ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.AlreadyExists)
        {
        }
    }

    /// <summary>"a.txt" → "a (2).txt" (keep both) or "a - Copy.txt" (copy into the same folder).</summary>
    private static async Task<string> UniqueNameAsync(IStorageProvider dst, string folder, string name, bool copySuffix, CancellationToken ct)
    {
        foreach (var candidate in ConflictNaming.Candidates(name, copySuffix))
        {
            string path = dst.CombinePath(folder, candidate);
            if (await dst.GetItemAsync(path, ct).ConfigureAwait(false) is null) return path;
        }

        throw new StorageException(StorageErrorKind.AlreadyExists, $"Couldn't find a free name for \"{name}\".");
    }
}

public static class ConflictNaming
{
    /// <summary>Name candidates in the order Windows Explorer uses.</summary>
    public static IEnumerable<string> Candidates(string name, bool copySuffix)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(stem))
        {
            stem = name;
            ext = string.Empty;
        }

        if (copySuffix)
        {
            yield return $"{stem} - Copy{ext}";
            for (int i = 2; i < 10_000; i++) yield return $"{stem} - Copy ({i}){ext}";
        }
        else
        {
            for (int i = 2; i < 10_000; i++) yield return $"{stem} ({i}){ext}";
        }
    }
}

/// <summary>Semaphore whose limit can change at runtime (Settings → simultaneous operations).</summary>
internal sealed class AsyncLimiter(Func<int> limit)
{
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private readonly object _gate = new();
    private int _running;

    public Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_running < limit())
            {
                _running++;
                return Task.CompletedTask;
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }

        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public void Release()
    {
        lock (_gate)
        {
            _running--;
            while (_running < limit() && _waiters.Count > 0)
            {
                if (_waiters.Dequeue().TrySetResult()) _running++;
            }
        }
    }
}
