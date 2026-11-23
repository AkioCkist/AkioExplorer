using System.Globalization;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Logic;

/// <summary>
/// Explorer-style ordering: folders, then drives, then files; within a group by the chosen field,
/// ties broken by natural name order. Direction applies to the field, not to the grouping.
/// With <see cref="SortOptions.FoldersFirst"/> off, folders and files are one list ordered by the field.
/// </summary>
public sealed class StorageItemComparer : IComparer<StorageItem>
{
    private readonly SortOptions _options;
    private readonly Func<StorageItem, string>? _typeNameResolver;
    private readonly NaturalStringComparer _names = NaturalStringComparer.Instance;

    public StorageItemComparer(SortOptions options, Func<StorageItem, string>? typeNameResolver = null)
    {
        _options = options;
        _typeNameResolver = typeNameResolver;
    }

    public int Compare(StorageItem? x, StorageItem? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return CompareCore(x, y, 0, 0, typesRanked: false);
    }

    /// <summary>
    /// Sorts <paramref name="rows"/> in this order. Sorting by type looks each item's type name up once and compares
    /// precomputed ranks, instead of resolving and culture-comparing two type names on every comparison.
    /// </summary>
    public void Sort<T>(Span<T> rows, Func<T, StorageItem> itemOf)
    {
        if (rows.Length < 2) return;
        if (_options.Field != SortField.Type)
        {
            rows.Sort((a, b) => Compare(itemOf(a), itemOf(b)));
            return;
        }

        var keys = new TypeKey[rows.Length];
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Length; i++)
        {
            var item = itemOf(rows[i]);
            string type = TypeOf(item);
            ranks.TryAdd(type, 0);
            keys[i] = new TypeKey(item, type);
        }

        // Rank = position in culture order; names the culture calls equal share a rank, as string.Compare would.
        var names = ranks.Keys.ToArray();
        var culture = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        Array.Sort(names, culture);
        int rank = 0;
        for (int i = 0; i < names.Length; i++)
        {
            if (i > 0 && culture.Compare(names[i - 1], names[i]) != 0) rank++;
            ranks[names[i]] = rank;
        }
        for (int i = 0; i < keys.Length; i++) keys[i] = keys[i] with { Rank = ranks[keys[i].Type] };

        keys.AsSpan().Sort(rows, Comparer<TypeKey>.Create((a, b) =>
            ReferenceEquals(a.Item, b.Item) ? 0 : CompareCore(a.Item, b.Item, a.Rank, b.Rank, typesRanked: true)));
    }

    private int CompareCore(StorageItem x, StorageItem y, int xTypeRank, int yTypeRank, bool typesRanked)
    {
        if (_options.FoldersFirst)
        {
            int group = ((int)x.Kind).CompareTo((int)y.Kind);
            if (group != 0) return group;
        }

        int result = _options.Field switch
        {
            SortField.Size => Nullable.Compare(x.Size ?? x.TotalSize, y.Size ?? y.TotalSize),
            SortField.Modified => Nullable.Compare(x.Modified, y.Modified),
            SortField.Type => typesRanked
                ? xTypeRank.CompareTo(yTypeRank)
                : string.Compare(TypeOf(x), TypeOf(y), StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };

        if (result == 0) result = _names.Compare(x.Name, y.Name);
        return _options.Descending ? -result : result;
    }

    private string TypeOf(StorageItem item) =>
        _typeNameResolver?.Invoke(item) ?? item.TypeDescription ?? item.Extension;

    private readonly record struct TypeKey(StorageItem Item, string Type, int Rank = 0);
}
