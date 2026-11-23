using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>A file or folder behind a shared link, by the service's own key.</summary>
public sealed record LinkNode(string Key, string Name, bool IsFolder, long? Size = null, DateTimeOffset? Modified = null, DateTimeOffset? Created = null);

/// <summary>
/// Browses what someone shared by link, without an account: a temporary, read-only "drive" whose root is the shared
/// folder. Paths are "/sub/file" built from names (shown in the breadcrumb); each maps to the service's key, learned
/// while listing. Files can be opened, previewed, downloaded or copied to another account; nothing can be changed.
/// </summary>
public abstract class ReadOnlyLinkProvider : ICloudStorageProvider, IFileContentProvider
{
    private readonly ConcurrentDictionary<string, LinkNode> _nodes = new(StringComparer.OrdinalIgnoreCase);

    protected ReadOnlyLinkProvider(CloudProviderType type, string serviceName, LinkNode root)
    {
        ServiceName = serviceName;
        Account = new CloudAccount { Type = type, DisplayName = root.Name };
        Type = type;
        _nodes[string.Empty] = root;
    }

    public string ServiceName { get; }

    public CloudAccount Account { get; }

    public CloudProviderType Type { get; }

    public string Id => Account.ProviderId;

    public string DisplayName => Account.DisplayName;

    public StorageCapabilities Capabilities => StorageCapabilities.Search;

    public string RootPath => string.Empty;

    /// <summary>Children of a folder node.</summary>
    protected abstract IAsyncEnumerable<IReadOnlyList<LinkNode>> ListChildrenAsync(LinkNode folder, CancellationToken cancellationToken);

    /// <summary>The contents of a file node.</summary>
    protected abstract Task<Stream> OpenNodeAsync(LinkNode file, CancellationToken cancellationToken);

    /// <summary>Records a node found outside a listing (the file a file-link points to).</summary>
    protected string Remember(string parentPath, LinkNode node)
    {
        string path = CombinePath(parentPath, node.Name);
        _nodes.TryAdd(path, node);
        return path;
    }

    // ------------------------------------------------------------------ browsing

    public async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var folder = await NodeAsync(path, cancellationToken).ConfigureAwait(false) ?? throw NotFound(path);
        if (!folder.IsFolder) throw new StorageException(StorageErrorKind.NotFound, $"\"{folder.Name}\" is a file, not a folder.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var page in ListChildrenAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            var items = new List<StorageItem>(page.Count);
            foreach (var child in page)
            {
                // Two children with one name (allowed by some services) would share a path: keep the first.
                if (!seen.Add(child.Name)) continue;
                string childPath = CombinePath(path, child.Name);
                _nodes[childPath] = child;
                items.Add(ToItem(childPath, child));
            }
            yield return items;
        }
    }

    public async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        var node = await NodeAsync(path, cancellationToken).ConfigureAwait(false);
        return node is null ? null : ToItem(path, node);
    }

    private async Task<LinkNode?> NodeAsync(string path, CancellationToken cancellationToken)
    {
        if (_nodes.TryGetValue(path, out var known)) return known;
        // Not listed yet (typed path, restored tab): list the parents down to it.
        if (GetParentPath(path) is not { } parent) return null;
        await foreach (var _ in ListAsync(parent, cancellationToken).ConfigureAwait(false))
        {
        }
        return _nodes.GetValueOrDefault(path);
    }

    public async IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!query.HasCriteria) yield break;
        var matcher = query.CreateMatcher(DateTimeOffset.Now);
        await foreach (var found in SearchWalker.WalkAsync(this, Normalize(path), matcher, cancellationToken).ConfigureAwait(false))
            yield return found;
    }

    public Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken) => Task.FromResult<StorageQuota?>(null);

    // ------------------------------------------------------------------ contents

    public async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(Normalize(file.Path), cancellationToken).ConfigureAwait(false) ?? throw NotFound(file.Path);
        return await OpenNodeAsync(node, cancellationToken).ConfigureAwait(false);
    }

    public Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken) =>
        throw ReadOnly();

    public Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult<StorageItem?>(null);

    public Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult<StorageItem?>(null);

    public Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken) => throw ReadOnly();

    public Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken) => throw ReadOnly();

    // ------------------------------------------------------------------ paths ("" = the shared folder)

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
        return Normalize(text.StartsWith('/') ? text : "/" + text);
    }

    protected static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return string.Empty;
        path = path.Replace('\\', '/').TrimEnd('/');
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static StorageItem ToItem(string path, LinkNode node) => new()
    {
        Name = node.Name,
        Path = path,
        Kind = node.IsFolder ? StorageItemKind.Folder : StorageItemKind.File,
        Size = node.IsFolder ? null : node.Size,
        Modified = node.Modified,
        Created = node.Created,
    };

    private StorageException NotFound(string path) =>
        new(StorageErrorKind.NotFound, $"Can't find \"{GetDisplayName(path)}\" in this {ServiceName} link. It may have been removed.");

    private StorageException ReadOnly() =>
        new(StorageErrorKind.AccessDenied, $"This is someone else's {ServiceName} link: it can be opened and copied, not changed.");
}
