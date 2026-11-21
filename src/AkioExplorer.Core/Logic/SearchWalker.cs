using System.Runtime.CompilerServices;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Logic;

/// <summary>
/// Search by listing folders breadth-first. Used for filter-only searches (e.g. "all videos") on providers whose
/// server search needs a search term. Nearest folders come first, so useful results show up early.
/// </summary>
public static class SearchWalker
{
    public static async IAsyncEnumerable<StorageItem> WalkAsync(
        IStorageProvider provider, string path, SearchMatcher matcher, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Queue<string>();
        pending.Enqueue(path);
        bool isRoot = true;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = pending.Dequeue();
            var pages = provider.ListAsync(folder, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    IReadOnlyList<StorageItem> page;
                    try
                    {
                        if (!await pages.MoveNextAsync().ConfigureAwait(false)) break;
                        page = pages.Current;
                    }
                    catch (StorageException) when (!isRoot)
                    {
                        break; // an unreadable subfolder must not end the whole search
                    }

                    foreach (var item in page)
                    {
                        if (item.IsContainer) pending.Enqueue(item.Path);
                        if (matcher.Matches(item)) yield return item;
                    }
                }
            }
            finally
            {
                await pages.DisposeAsync().ConfigureAwait(false);
            }

            isRoot = false;
        }
    }
}
