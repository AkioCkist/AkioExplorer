namespace AkioExplorer.Core.Models;

public enum StorageItemKind
{
    Folder = 0,
    Drive = 1,
    File = 2,
}

[Flags]
public enum StorageItemAttributes
{
    None = 0,
    Hidden = 1,
    System = 2,
    ReadOnly = 4,
    ReparsePoint = 8,
}

/// <summary>
/// Provider-neutral description of a file, folder or drive. Immutable; providers create new instances on change.
/// </summary>
public sealed record StorageItem
{
    public required string Name { get; init; }

    /// <summary>Provider-specific path / id that the owning provider understands.</summary>
    public required string Path { get; init; }

    public required StorageItemKind Kind { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? Modified { get; init; }

    public DateTimeOffset? Created { get; init; }

    public StorageItemAttributes Attributes { get; init; }

    /// <summary>Drive capacity in bytes (drives only).</summary>
    public long? TotalSize { get; init; }

    /// <summary>Drive free space in bytes (drives only).</summary>
    public long? FreeSpace { get; init; }

    /// <summary>Optional human readable type (e.g. "Local Disk"). Files usually leave this null and the UI resolves it.</summary>
    public string? TypeDescription { get; init; }

    public bool IsContainer => Kind != StorageItemKind.File;

    public bool IsHidden => (Attributes & StorageItemAttributes.Hidden) != 0;

    public string Extension => Kind == StorageItemKind.File ? System.IO.Path.GetExtension(Name) : string.Empty;
}
