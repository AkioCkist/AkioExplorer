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

        if (_options.FoldersFirst)
        {
            int group = ((int)x.Kind).CompareTo((int)y.Kind);
            if (group != 0) return group;
        }

        int result = _options.Field switch
        {
            SortField.Size => Nullable.Compare(x.Size ?? x.TotalSize, y.Size ?? y.TotalSize),
            SortField.Modified => Nullable.Compare(x.Modified, y.Modified),
            SortField.Type => string.Compare(TypeOf(x), TypeOf(y), StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };

        if (result == 0) result = _names.Compare(x.Name, y.Name);
        return _options.Descending ? -result : result;
    }

    private string TypeOf(StorageItem item) =>
        _typeNameResolver?.Invoke(item) ?? item.TypeDescription ?? item.Extension;
}
