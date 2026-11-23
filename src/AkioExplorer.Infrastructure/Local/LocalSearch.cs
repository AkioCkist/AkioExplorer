using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.Infrastructure.Local;

/// <summary>
/// Local name search. Prefers Everything's in-memory index (milliseconds for a whole disk); folders it doesn't index
/// fall back to a multi-threaded walk. Every result goes through the same <see cref="SearchMatcher"/>, so both paths
/// return the same kind of answer.
/// </summary>
internal static class LocalSearch
{
    private const int WalkBufferSize = 32 * 1024;

    /// <summary>Runs a search and hands each match to <paramref name="onItem"/> (return false to stop). Blocking.</summary>
    public static void Run(
        IReadOnlyList<string> roots, bool everywhere, SearchQuery query, SearchMatcher matcher, bool useEverything,
        Func<StorageItem, bool> onItem, ILogger logger, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var toWalk = new List<string>(roots);
        int found = 0;
        bool Emit(StorageItem item)
        {
            found++;
            return onItem(item);
        }

        if (useEverything && TrySearchEverything(roots, everywhere, query, matcher, Emit, toWalk, cancellationToken))
        {
            logger.LogInformation("Search via Everything index: {Count} results in {Ms} ms", found, clock.ElapsedMilliseconds);
            if (toWalk.Count == 0) return;
        }

        if (toWalk.Count > 0)
        {
            ParallelWalk(toWalk, query, matcher, Emit, cancellationToken);
            logger.LogInformation("Search via parallel walk of {Roots}: {Count} results in {Ms} ms",
                string.Join(", ", toWalk), found, clock.ElapsedMilliseconds);
        }
    }

    // ------------------------------------------------------------------ Everything

    /// <summary>Whole-drive search through Everything only; false when Everything isn't usable (nothing emitted).</summary>
    internal static bool TryIndexOnly(
        IReadOnlyList<string> roots, SearchQuery query, SearchMatcher matcher, Func<StorageItem, bool> emit,
        CancellationToken cancellationToken)
    {
        var ignored = new List<string>(roots);
        return TrySearchEverything(roots, everywhere: true, query, matcher, emit, ignored, cancellationToken);
    }

    /// <summary>
    /// Searches the roots Everything indexes and removes them from <paramref name="toWalk"/>.
    /// Returns false (leaving <paramref name="toWalk"/> untouched) when Everything isn't usable.
    /// </summary>
    private static bool TrySearchEverything(
        IReadOnlyList<string> roots, bool everywhere, SearchQuery query, SearchMatcher matcher,
        Func<StorageItem, bool> emit, List<string> toWalk, CancellationToken cancellationToken)
    {
        using var session = EverythingSession.TryOpen();
        if (session is null) return false;

        var indexed = new List<string>();
        foreach (var root in roots)
        {
            bool? covered = IsIndexed(session, root, cancellationToken);
            if (covered is null) return false;
            if (covered.Value) indexed.Add(root);
        }
        if (indexed.Count == 0) return true;

        var prefixes = indexed.Select(r => r.EndsWith('\\') ? r : r + "\\").ToArray();
        string search = BuildSearch(query, everywhere ? null : indexed[0], DateTimeOffset.Now);
        bool stopped = false;

        bool ok = session.Query(search, entry =>
        {
            string full = entry.FullPath;
            if (!StartsWithAny(full, prefixes) || IsSystemPath(full)) return true;

            // Attributes come only when Everything indexes them (Options > Indexes > Index attributes); reading them
            // from disk here costs ~170 µs a file, which turned a 0.1 s search into seconds.
            var attributes = entry.Attributes;
            if (attributes is { } a && (a & FileAttributes.System) != 0) return true;

            var modified = entry.ModifiedUtc is { } m ? new DateTimeOffset(m).ToLocalTime() : (DateTimeOffset?)null;
            if (!matcher.Matches(entry.Name, entry.IsFolder, entry.Size, modified)) return true;

            var item = new StorageItem
            {
                Name = entry.Name,
                Path = full,
                Kind = entry.IsFolder ? StorageItemKind.Folder : StorageItemKind.File,
                Size = entry.Size,
                Modified = modified,
                Attributes = attributes is { } attr ? LocalStorageProvider.MapAttributes(attr) : StorageItemAttributes.None,
            };
            if (emit(item)) return true;
            stopped = true;
            return false;
        }, cancellationToken);

        if (!ok) return false;
        foreach (var root in indexed) toWalk.Remove(root);
        if (stopped) toWalk.Clear();
        return true;
    }

    /// <summary>
    /// Everything search text for a query. The matcher re-checks every result, so filters here only have to be
    /// at least as wide as the real ones (dates are widened by a day to dodge time-zone edges).
    /// </summary>
    internal static string BuildSearch(SearchQuery query, string? folder, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (folder is not null) parts.Add(PathTerm(folder));

        string name = query.NameText.Replace("\"", string.Empty);
        if (name.Length > 0) parts.Add('"' + name + '"');

        switch (query.Category)
        {
            case FileCategory.Any:
                break;
            case FileCategory.Folder:
                parts.Add("folder:");
                break;
            default:
                var extensions = query.Category == FileCategory.Media
                    ? FileCategories.ExtensionsOf(FileCategory.Image).Concat(FileCategories.ExtensionsOf(FileCategory.Video))
                    : FileCategories.ExtensionsOf(query.Category);
                parts.Add("file:");
                parts.Add("ext:" + string.Join(';', extensions.Select(e => e.TrimStart('.'))));
                break;
        }

        var (minSize, maxSize) = query.SizeRange();
        if (query.Size != SizeFilter.Any)
        {
            parts.Add("file:");
            if (minSize is { } min) parts.Add("size:>=" + min.ToString(CultureInfo.InvariantCulture));
            if (maxSize is { } max) parts.Add("size:<" + max.ToString(CultureInfo.InvariantCulture));
        }

        var (after, before) = query.ModifiedRange(now);
        if (after is { } a) parts.Add("dm:>=" + a.LocalDateTime.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (before is { } b) parts.Add("dm:<" + b.LocalDateTime.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return string.Join(' ', parts.Distinct());
    }

    private static readonly ConcurrentDictionary<string, (bool Indexed, long Expires)> VolumeCoverage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether Everything indexes the volume holding <paramref name="folder"/>. Asking costs a full-disk count
    /// (~60 ms), so the answer is kept for a few minutes: the quick-search popup searches on every keystroke.
    /// </summary>
    private static bool? IsIndexed(EverythingSession session, string folder, CancellationToken cancellationToken)
    {
        string volume = Path.GetPathRoot(folder) is { Length: > 0 } r ? r : folder;
        long now = Environment.TickCount64;
        if (VolumeCoverage.TryGetValue(volume, out var known) && known.Expires > now) return known.Indexed;

        long? count = session.Count(PathTerm(volume), cancellationToken);
        if (count is null) return null;
        // Zero hits for a volume that has entries on disk means Everything doesn't index it (network share, FAT USB…).
        bool indexed = count > 0 || !HasEntries(volume);
        VolumeCoverage[volume] = (indexed, now + (long)TimeSpan.FromMinutes(5).TotalMilliseconds);
        return indexed;
    }

    /// <summary>A quoted term with a backslash, which Everything matches against the full path.</summary>
    private static string PathTerm(string folder) => '"' + folder.TrimEnd('\\') + "\\\"";

    private static bool HasEntries(string folder)
    {
        try { return Directory.EnumerateFileSystemEntries(folder).Any(); }
        catch { return false; }
    }

    private static bool StartsWithAny(string path, string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Folders the walker never enters (they carry the System attribute); Everything sees them via the MFT.</summary>
    private static bool IsSystemPath(string path) =>
        path.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\System Volume Information", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ parallel walk

    /// <summary>
    /// Several threads share one queue of folders; each lists a folder (no recursion), reports matches and queues
    /// its subfolders. Much faster than one recursive enumerator on SSDs, where per-folder latency dominates.
    /// </summary>
    internal static void ParallelWalk(
        IReadOnlyList<string> roots, SearchQuery query, SearchMatcher matcher, Func<StorageItem, bool> emit,
        CancellationToken cancellationToken)
    {
        string name = query.NameText;
        bool wildcard = query.HasWildcards;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
            BufferSize = WalkBufferSize,
        };

        bool Matches(ref FileSystemEntry e) =>
            (name.Length == 0 || (wildcard
                ? FileSystemName.MatchesSimpleExpression(name, e.FileName, ignoreCase: true)
                : e.FileName.Contains(name, StringComparison.OrdinalIgnoreCase))) &&
            matcher.Matches(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? null : e.Length, e.LastWriteTimeUtc.ToLocalTime());

        var queue = new ConcurrentQueue<string>(roots);
        int pending = roots.Count;
        int workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        using var available = new SemaphoreSlim(roots.Count);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var emitLock = new object();

        void Worker()
        {
            var token = stop.Token;
            while (true)
            {
                available.Wait(token);
                if (!queue.TryDequeue(out var folder)) return; // woken up to finish
                try
                {
                    var entries = new FileSystemEnumerable<(StorageItem? Item, string? Sub)>(folder, (ref FileSystemEntry e) =>
                    {
                        string? sub = e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0 ? e.ToFullPath() : null;
                        // Files only get here when they matched (see the predicate); folders are checked now.
                        StorageItem? item = !e.IsDirectory || Matches(ref e) ? LocalStorageProvider.ToItem(ref e) : null;
                        return (item, sub);
                    }, options)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory || Matches(ref e),
                    };

                    foreach (var (item, sub) in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (sub is not null)
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue(sub);
                            available.Release();
                        }
                        if (item is not null)
                        {
                            bool more;
                            lock (emitLock) more = emit(item);
                            if (!more) stop.Cancel();
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // An unreadable or vanished folder must not end the whole search.
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0) available.Release(workers);
                }
            }
        }

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning)).ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            cancellationToken.ThrowIfCancellationRequested(); // stopped by emit returning false otherwise: not an error
        }
    }
}
