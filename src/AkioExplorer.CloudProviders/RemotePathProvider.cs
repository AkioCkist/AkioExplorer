using System.Runtime.CompilerServices;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders;

/// <summary>
/// Base for path-addressed remotes without a server-side search or recycle bin (WebDAV, FTP, SFTP, S3, Azure…):
/// search walks the folders, deletes are permanent, and error messages name the service.
/// </summary>
public abstract class RemotePathProvider : PathCloudProvider, IFileContentProvider
{
    protected RemotePathProvider(CloudAccount account, CloudProviderType type, string serviceName)
        : base(account, type)
    {
        ServiceName = serviceName;
    }

    public string ServiceName { get; }

    public override StorageCapabilities Capabilities =>
        StorageCapabilities.CreateFolder | StorageCapabilities.Rename | StorageCapabilities.Delete | StorageCapabilities.Search;

    public override async IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!query.HasCriteria) yield break;
        var matcher = query.CreateMatcher(DateTimeOffset.Now);
        await foreach (var found in SearchWalker.WalkAsync(this, Normalize(path), matcher, cancellationToken).ConfigureAwait(false))
            yield return found;
    }

    public override Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken) => Task.FromResult<StorageQuota?>(null);

    public abstract Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken);

    public abstract Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken);

    public virtual Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult<StorageItem?>(null);

    public virtual Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult<StorageItem?>(null);

    /// <summary>Rename = move within the same folder for most remotes.</summary>
    public override async Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        EnsureValidName(newName);
        path = Normalize(path);
        if (path.Length == 0) throw new StorageException(StorageErrorKind.NotSupported, $"The root of {DisplayName} can't be renamed.");
        var item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false) ?? throw NotFound(path);
        return await MoveWithinAsync(item, GetParentPath(path) ?? string.Empty, newName, cancellationToken).ConfigureAwait(false)
               ?? throw new StorageException(StorageErrorKind.NotSupported, $"{ServiceName} can't rename items.");
    }

    // ------------------------------------------------------------------ errors written for people

    protected string Describe(string path) => Normalize(path).Length == 0 ? $"\"{DisplayName}\"" : $"\"{GetDisplayName(path)}\"";

    protected StorageException NotFound(string path) =>
        new(StorageErrorKind.NotFound, $"Can't find {Describe(path)} in {ServiceName}. It may have been moved or deleted.");

    protected StorageException Exists(string path) =>
        new(StorageErrorKind.AlreadyExists, $"There's already an item named {Describe(path)} here.");

    protected StorageException Denied(string path) =>
        new(StorageErrorKind.AccessDenied, $"You don't have permission to change {Describe(path)} in {ServiceName}.");

    protected StorageException SignInFailed() =>
        new(StorageErrorKind.AuthenticationRequired, $"{ServiceName} rejected the sign-in for \"{DisplayName}\". Reconnect the account to continue.");

    protected StorageException Unreachable(Exception? inner = null) =>
        new(StorageErrorKind.Unavailable, $"Can't reach {ServiceName}. Check your internet connection.", inner);

    protected StorageException Busy() =>
        new(StorageErrorKind.Throttled, $"{ServiceName} is busy right now. Wait a moment and try again.");

    protected StorageException Full() => new(StorageErrorKind.Unknown, $"\"{DisplayName}\" is full.");

    protected static void EnsureValidName(string name)
    {
        if (FileNameValidator.Validate(name) is { } error) throw new StorageException(StorageErrorKind.InvalidName, error);
    }

    protected StorageException RootChange(string what) =>
        new(StorageErrorKind.NotSupported, $"The root of {DisplayName} can't be {what}.");
}
