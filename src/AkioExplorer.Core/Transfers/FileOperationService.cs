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

    /// <summary>Disk to disk: small files copy 8 at a time (like robocopy /MT), so per-file overhead overlaps.</summary>
    private const int LocalSmallFileSlots = 8;

    /// <summary>Files at least this big copy one at a time between disks: parallel big writes only make the disk seek.</summary>
    private const long LocalLargeFile = 8L * 1024 * 1024;

    private readonly IStorageProviderRegistry _registry;
    private readonly IReadOnlyList<ITransferExecutor> _executors;
    private readonly SemaphoreSlim _conflictGate = new(1);
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

    public FileOperation Retry(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination) =>
        Enqueue(kind, sourceProviderId, items, destination, new TransferOptions { SkipIdentical = true });

    public FileOperation Enqueue(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, StorageLocation destination,
        TransferOptions? options = null)
    {
        var destinationProvider = _registry.Get(destination.ProviderId);
        var sourceProvider = _registry.Find(sourceProviderId);
        string sourceName = sourceProvider is null || items.Count == 0
            ? sourceProviderId
            : sourceProvider.GetParentPath(items[0].Path) is { } parent ? sourceProvider.GetDisplayName(parent) : sourceProvider.DisplayName;
        var operation = new FileOperation(kind, sourceProviderId, items, destination, sourceName,
            destinationProvider.GetDisplayName(destination.Path), options);
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

    /// <summary>
    /// Concurrency for one operation. Cloud: <c>Files</c> slots for every file. Disk to disk: small files share the
    /// many slots, large files also take the single <c>Large</c> slot so only one big file streams at a time.
    /// </summary>
    private sealed class Slots(int files, bool splitLarge) : IDisposable
    {
        public SemaphoreSlim Files { get; } = new(files);

        public SemaphoreSlim? Large { get; } = splitLarge ? new SemaphoreSlim(1) : null;

        public void Dispose()
        {
            Files.Dispose();
            Large?.Dispose();
        }
    }

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
        // Several files at once: per-file latency (cloud session setup and confirmations; on disks, opening, creating
        // and closing each small file) dominates otherwise. Big local files still go one at a time (see Slots).
        bool bothLocal = (src.Capabilities & dst.Capabilities & StorageCapabilities.OpenWithShell) != 0;
        int parallel = bothLocal ? LocalSmallFileSlots : Math.Clamp(_settings.Current.ParallelFileTransfers, 1, 8);
        using var slots = new Slots(parallel, splitLarge: bothLocal);
        if (parallel == 1)
        {
            foreach (var top in plan)
            {
                ct.ThrowIfCancellationRequested();
                await op.WaitIfPausedAsync(ct).ConfigureAwait(false);
                await TransferTopLevelAsync(op, executor, src, dst, destRoot, top, slots, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await Task.WhenAll(plan.Select(top => TransferTopLevelAsync(op, executor, src, dst, destRoot, top, slots, ct))).ConfigureAwait(false);
        }

        op.SetCurrent(null);
        op.SetStatus(op.Errors.Count > 0 ? OperationStatus.CompletedWithErrors : OperationStatus.Completed);
    }

    private static string TransferName(IStorageProvider src, IStorageProvider dst, StorageItem file) =>
        DestinationName(dst, src is IFileContentProvider content && !file.IsContainer ? content.GetTransferName(file) : file.Name);

    /// <summary>A name the destination accepts ("A | B" from Google Drive becomes "A _ B" on disk/OneDrive/Dropbox).</summary>
    private static string DestinationName(IStorageProvider dst, string name) =>
        dst is IFileContentProvider content ? content.MakeValidName(name) : name;

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
        string destRoot, TopLevel top, Slots slots, CancellationToken ct)
    {
        await op.WaitIfPausedAsync(ct).ConfigureAwait(false);
        var item = top.Item;
        string topName = DestinationName(dst, item.Name);
        string topTarget = dst.CombinePath(destRoot, topName);
        bool anyWritten = false;

        if (item.IsContainer)
        {
            var existing = await dst.GetItemAsync(topTarget, ct).ConfigureAwait(false);
            if (top.SameFolder)
                topTarget = await UniqueNameAsync(dst, destRoot, topName, copySuffix: true, ct).ConfigureAwait(false);
            else if (existing is { IsContainer: false })
                topTarget = await UniqueNameAsync(dst, destRoot, topName, copySuffix: false, ct).ConfigureAwait(false);
            // An existing folder with the same name is merged; conflicts are handled per file inside it.

            var folderPaths = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = topTarget };
            // Folders this operation creates start empty: nothing inside can clash, so their files skip the per-file
            // "does it exist?" lookup and their subfolders skip it too (thousands of round trips saved).
            var fresh = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relative in top.Folders)
            {
                string path = ResolveFolder(dst, folderPaths, relative);
                bool parentFresh = relative.Length > 0 && fresh.Contains(ParentRelative(relative));
                if (await EnsureFolderAsync(dst, path, knownMissing: parentFresh, ct).ConfigureAwait(false)) fresh.Add(relative);
            }

            // Folder paths are all resolved above, so the cache is only read from here on (safe in parallel).
            await Task.WhenAll(top.Files.Select(async file =>
            {
                string folder = ResolveFolder(dst, folderPaths, file.RelativeFolder);
                bool empty = fresh.Contains(file.RelativeFolder);
                if (await InSlotAsync(slots, file.Item,
                            () => TransferFileAsync(op, executor, src, dst, file.Item, folder, inPlaceCopy: false, ct, targetKnownFree: empty), ct)
                        .ConfigureAwait(false) is null)
                    top.Incomplete = true;
            })).ConfigureAwait(false);

            anyWritten = true;
        }
        else
        {
            string? written = await InSlotAsync(slots, item, () => TransferFileAsync(op, executor, src, dst, item, destRoot, top.SameFolder, ct), ct)
                .ConfigureAwait(false);
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
                // Local moves delete the original like Explorer; cloud originals go to the service's trash so a
                // mistaken move stays recoverable there.
                bool permanent = (src.Capabilities & StorageCapabilities.OpenWithShell) != 0;
                await src.DeleteAsync([item.Path], permanent, ct).ConfigureAwait(false);
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

    private static async Task<T> InSlotAsync<T>(Slots slots, StorageItem file, Func<Task<T>> work, CancellationToken ct)
    {
        var large = slots.Large is { } gate && (file.Size ?? 0) >= LocalLargeFile ? gate : null;
        if (large is not null) await large.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await slots.Files.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                slots.Files.Release();
            }
        }
        finally
        {
            large?.Release();
        }
    }

    private static string ParentRelative(string relative)
    {
        int cut = relative.LastIndexOf('\u0000');
        return cut < 0 ? string.Empty : relative[..cut];
    }

    /// <summary>Copies one file into <paramref name="folder"/>. Returns the written path, or null if skipped/failed.</summary>
    /// <param name="targetKnownFree">The folder was created by this operation, so the name can't be taken yet.</param>
    private async Task<string?> TransferFileAsync(FileOperation op, ITransferExecutor executor, IStorageProvider src, IStorageProvider dst,
        StorageItem file, string folder, bool inPlaceCopy, CancellationToken ct, bool targetKnownFree = false)
    {
        await op.WaitIfPausedAsync(ct).ConfigureAwait(false);
        long size = file.Size ?? 0;
        string name = TransferName(src, dst, file);
        string target = inPlaceCopy
            ? await UniqueNameAsync(dst, folder, name, copySuffix: true, ct).ConfigureAwait(false)
            : dst.CombinePath(folder, name);
        bool overwrite = false;

        if (!inPlaceCopy && !targetKnownFree && await dst.GetItemAsync(target, ct).ConfigureAwait(false) is { } existing)
        {
            if (op.Options.SkipIdentical && !existing.IsContainer && existing.Size == file.Size && file.Size is not null)
            {
                op.FileSkipped(size); // retry: this one already arrived
                return target;
            }

            var resolution = existing.IsContainer
                ? ConflictResolution.KeepBoth
                : op.Options.Conflict ?? await DecideAsync(op, file, existing, dst.GetDisplayName(folder), ct).ConfigureAwait(false);

            switch (resolution)
            {
                case ConflictResolution.Skip:
                    op.FileSkipped(size);
                    return null;
                case ConflictResolution.KeepBoth:
                    target = await UniqueNameAsync(dst, folder, name, copySuffix: false, ct).ConfigureAwait(false);
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
            var progress = new FileProgress(op);
            long reported = 0;
            try
            {
                await executor.CopyFileAsync(src, file, dst, target, overwrite, progress, ct).ConfigureAwait(false);
                op.FileDone();
                return target;
            }
            catch (OperationCanceledException)
            {
                op.AddTransferred(-progress.Reported);
                throw;
            }
            catch (StorageException ex) when (attempt < MaxAttempts && ex.Kind is StorageErrorKind.InUse or StorageErrorKind.Unavailable or StorageErrorKind.Throttled)
            {
                op.AddTransferred(-progress.Reported);
                _logger.LogInformation("Retrying {File} after transient error: {Message}", file.Name, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
            }
            catch (StorageException ex)
            {
                reported = progress.Reported;
                op.AddTransferred(size - reported); // keep the progress bar honest: the file is "done" (failed)
                op.FileDone();
                op.AddError($"{file.Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Per-file progress: feeds the operation totals and remembers what to roll back on retry.</summary>
    private sealed class FileProgress(FileOperation op) : ITransferProgress
    {
        private long _reported;

        public long Reported => Interlocked.Read(ref _reported);

        public void Report(long bytes)
        {
            Interlocked.Add(ref _reported, bytes);
            op.AddTransferred(bytes);
        }

        public ValueTask WaitIfPausedAsync(CancellationToken cancellationToken) => op.WaitIfPausedAsync(cancellationToken);

        public void WaitIfPaused(CancellationToken cancellationToken) => op.WaitIfPaused(cancellationToken);

        public void ReportParts(PartedFileProgress? parts) => op.SetParts(this, parts);
    }

    private async Task<ConflictResolution> DecideAsync(FileOperation op, StorageItem source, StorageItem existing, string folderName, CancellationToken ct)
    {
        if (op.ConflictAnswerForAll is { } forAll) return forAll;

        // Files run in parallel: one question at a time, and "do this for all" answers the ones waiting.
        await _conflictGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DecideLockedAsync(op, source, existing, folderName, ct).ConfigureAwait(false);
        }
        finally
        {
            _conflictGate.Release();
        }
    }

    private async Task<ConflictResolution> DecideLockedAsync(FileOperation op, StorageItem source, StorageItem existing, string folderName, CancellationToken ct)
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
        path = dst.CombinePath(ResolveFolder(dst, cache, parentRelative), DestinationName(dst, name));
        cache[relative] = path;
        return path;
    }

    /// <summary>Creates the folder unless it exists. Returns true when this call created it (so it is empty).</summary>
    /// <param name="knownMissing">The parent was just created, so there is no need to look first.</param>
    private static async Task<bool> EnsureFolderAsync(IStorageProvider dst, string path, bool knownMissing, CancellationToken ct)
    {
        if (!knownMissing && await dst.GetItemAsync(path, ct).ConfigureAwait(false) is { IsContainer: true }) return false;
        string parent = dst.GetParentPath(path) ?? string.Empty;
        string name = dst.GetDisplayName(path);
        try
        {
            await dst.CreateFolderAsync(parent, name, ct).ConfigureAwait(false);
            return true;
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.AlreadyExists)
        {
            return false;
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
