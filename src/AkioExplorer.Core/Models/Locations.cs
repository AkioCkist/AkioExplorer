namespace AkioExplorer.Core.Models;

/// <summary>Address of a folder in any provider: local disk or a cloud account.</summary>
public sealed record StorageLocation(string ProviderId, string Path)
{
    public bool Equals(StorageLocation? other) =>
        other is not null &&
        string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal) &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(ProviderId, StringComparer.OrdinalIgnoreCase.GetHashCode(Path));
}

public sealed record BreadcrumbSegment(string Name, string Path)
{
    /// <summary>BreadcrumbBar renders items via ToString (its overflow item has no data, so no x:Bind template).</summary>
    public override string ToString() => Name;
}

[Flags]
public enum StorageCapabilities
{
    None = 0,
    CreateFolder = 1,
    Rename = 2,
    Delete = 4,
    RecycleBin = 8,
    Search = 16,
    Watch = 32,
    OpenWithShell = 64,
    ServerSideCopy = 128,

    /// <summary>The root is a virtual listing (e.g. This PC) that can't hold files.</summary>
    VirtualRoot = 256,

    /// <summary>Search can look inside files (<see cref="SearchQuery.Content"/>, "Has the words").</summary>
    ContentSearch = 512,

    /// <summary>Search filters by owner, "shared with" and "shared with me".</summary>
    SearchSharing = 1024,

    /// <summary>Search can look in the trash.</summary>
    SearchTrash = 2048,

    /// <summary>Search can return only starred items.</summary>
    SearchStarred = 4096,

    /// <summary>Search always covers the whole account (the folder can't narrow it), e.g. Google Drive.</summary>
    AccountWideSearch = 8192,
}

public enum SortField
{
    Name,
    Modified,
    Type,
    Size,
}

/// <param name="FoldersFirst">Explorer convention (folders above files). Off: folders and files are mixed and
/// ordered purely by the field, e.g. an old file above a newer folder when sorting by date.</param>
public sealed record SortOptions(SortField Field = SortField.Name, bool Descending = false, bool FoldersFirst = true);
