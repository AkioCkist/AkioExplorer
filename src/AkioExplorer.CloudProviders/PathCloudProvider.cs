using System.Runtime.CompilerServices;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders;

/// <summary>
/// Shared path logic for services addressed by "/folder/sub" paths (Dropbox, OneDrive, WebDAV…).
/// Root is "" and comparisons are case-insensitive, matching those services.
/// </summary>
public abstract class PathCloudProvider : ICloudStorageProvider
{
    protected PathCloudProvider(CloudAccount account, CloudProviderType type)
    {
        Account = account;
        Type = type;
    }

    public CloudAccount Account { get; }

    public CloudProviderType Type { get; }

    public string Id => Account.ProviderId;

    public string DisplayName => Account.DisplayName;

    public virtual StorageCapabilities Capabilities =>
        StorageCapabilities.CreateFolder | StorageCapabilities.Rename | StorageCapabilities.Delete |
        StorageCapabilities.RecycleBin | StorageCapabilities.Search;

    public string RootPath => string.Empty;

    public abstract IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, CancellationToken cancellationToken);

    public abstract Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken);

    public abstract Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken);

    public abstract Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken);

    public abstract Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken);

    public abstract IAsyncEnumerable<StorageItem> SearchAsync(string path, string query, CancellationToken cancellationToken);

    public abstract Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken);

    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path)
    {
        var segments = new List<BreadcrumbSegment> { new(DisplayName, string.Empty) };
        string current = string.Empty;
        foreach (var part in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            segments.Add(new BreadcrumbSegment(part, current));
        }
        return segments;
    }

    public string? GetParentPath(string path)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        int cut = path.LastIndexOf('/');
        return cut <= 0 ? string.Empty : path[..cut];
    }

    public string GetDisplayName(string path)
    {
        path = Normalize(path);
        return path.Length == 0 ? DisplayName : path[(path.LastIndexOf('/') + 1)..];
    }

    public IDisposable? Watch(string path, Action onChanged) => null;

    public string CombinePath(string parentPath, string name) => Normalize(parentPath) + "/" + name;

    public bool IsSameOrDescendant(string path, string ancestor)
    {
        string a = Normalize(ancestor), p = Normalize(path);
        return a.Length == 0 || p.Equals(a, StringComparison.OrdinalIgnoreCase) || p.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    public string? TryNormalizePath(string input)
    {
        string text = input.Trim().Trim('"').Replace('\\', '/');
        if (text.Length == 0 || text.Equals(DisplayName, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (!text.StartsWith('/')) text = "/" + text;
        return Normalize(text);
    }

    protected static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return string.Empty;
        path = path.Replace('\\', '/').TrimEnd('/');
        return path.StartsWith('/') ? path : "/" + path;
    }

    protected static string ItemName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
