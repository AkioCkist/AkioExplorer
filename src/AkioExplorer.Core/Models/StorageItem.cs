using System.Text.Json.Serialization;

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
    private readonly string _name = string.Empty;

    // Derived from Name only (so record equality is unaffected). Computed once: Extension is read on every icon
    // lookup, type-name lookup and sort comparison, and Path.GetExtension allocates a new string each call.
    private readonly string _nameExtension = string.Empty;

    public required string Name
    {
        get => _name;
        init
        {
            _name = value;
            _nameExtension = System.IO.Path.GetExtension(value);
        }
    }

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

    [JsonIgnore]
    public bool IsContainer => Kind != StorageItemKind.File;

    [JsonIgnore]
    public bool IsHidden => (Attributes & StorageItemAttributes.Hidden) != 0;

    [JsonIgnore]
    public string Extension => Kind == StorageItemKind.File ? _nameExtension : string.Empty;
}
