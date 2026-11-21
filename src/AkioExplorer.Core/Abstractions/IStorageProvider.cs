using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Abstractions;

/// <summary>
/// A browsable storage: the local machine or one connected cloud account.
/// All members are safe to call from any thread and never block the caller on I/O.
/// </summary>
public interface IStorageProvider
{
    /// <summary>Stable id used in <see cref="StorageLocation.ProviderId"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    StorageCapabilities Capabilities { get; }

    /// <summary>Path of the provider root (e.g. "" for "This PC").</summary>
    string RootPath { get; }

    /// <summary>Lists a folder. Yields pages so large / remote folders render progressively.</summary>
    IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, CancellationToken cancellationToken);

    Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken);

    Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken);

    Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken);

    /// <summary>Recursive name search under <paramref name="path"/>. Results stream in as they are found.</summary>
    IAsyncEnumerable<StorageItem> SearchAsync(string path, string query, CancellationToken cancellationToken);

    IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path);

    /// <summary>Parent folder path, or null at the root.</summary>
    string? GetParentPath(string path);

    /// <summary>Human readable name of a folder path (for titles / favorites).</summary>
    string GetDisplayName(string path);

    /// <summary>Watches a folder for changes. Returns null if the provider cannot watch.</summary>
    IDisposable? Watch(string path, Action onChanged);

    /// <summary>Path of a child named <paramref name="name"/> inside <paramref name="parentPath"/>.</summary>
    string CombinePath(string parentPath, string name);

    /// <summary>True if <paramref name="path"/> is <paramref name="ancestor"/> or lies inside it.</summary>
    bool IsSameOrDescendant(string path, string ancestor);

    /// <summary>Normalises user typed input (e.g. an address bar entry) to a provider path, or null if invalid.</summary>
    string? TryNormalizePath(string input);
}

public interface IStorageProviderRegistry
{
    IReadOnlyList<IStorageProvider> Providers { get; }

    IStorageProvider? Find(string providerId);

    IStorageProvider Get(string providerId);

    void Register(IStorageProvider provider);

    event EventHandler? ProvidersChanged;
}
