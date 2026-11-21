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
}

public enum SortField
{
    Name,
    Modified,
    Type,
    Size,
}

public sealed record SortOptions(SortField Field = SortField.Name, bool Descending = false);
