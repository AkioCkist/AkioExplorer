using System.Runtime.CompilerServices;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Dropbox.Api;
using Dropbox.Api.Files;

namespace AkioExplorer.CloudProviders.Dropbox;

/// <summary>
/// Dropbox through the official Dropbox .NET SDK. Path addressed ("/Photos/2026"); the SDK refreshes short-lived
/// access tokens itself from the refresh token kept in Windows Credential Manager.
/// </summary>
public sealed class DropboxProvider : PathCloudProvider, IFileContentProvider, IThumbnailProvider, IDirectDownloadProvider, ISharedLinkSource
{
    public const string ServiceName = "Dropbox";
    private const uint PageSize = 2000;
    private const int MaxChunkAttempts = 4;

    private readonly Func<DropboxClient> _clientFactory;
    private DropboxClient? _client;

    public DropboxProvider(CloudAccount account, Func<DropboxClient> clientFactory)
        : base(account, CloudProviderType.Dropbox)
    {
        _clientFactory = clientFactory;
    }

    private DropboxClient Client => _client ??= _clientFactory();

    public bool CanOpenSharedLink(Uri link) => SharedLinks.TryParseDropbox(link, out _);

    /// <summary>A dropbox.com/scl/… shared link, browsed read-only with this account.</summary>
    public Task<SharedLinkTarget> OpenSharedLinkAsync(Uri link, CancellationToken cancellationToken) =>
        Links.DropboxLinkProvider.OpenAsync(link, () => Client, cancellationToken);

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var page = await CallAsync(() => Client.Files.ListFolderAsync(new ListFolderArg(path, limit: PageSize)), path).ConfigureAwait(false);
        yield return page.Entries.Select(ToItem).ToList();

        while (page.HasMore)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string cursor = page.Cursor;
            page = await CallAsync(() => Client.Files.ListFolderContinueAsync(new ListFolderContinueArg(cursor)), path).ConfigureAwait(false);
            yield return page.Entries.Select(ToItem).ToList();
        }
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        try
        {
            var metadata = await CallAsync(() => Client.Files.GetMetadataAsync(new GetMetadataArg(path)), path).ConfigureAwait(false);
            return metadata.IsDeleted ? null : ToItem(metadata);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var result = await CallAsync(() => Client.Files.CreateFolderV2Async(new CreateFolderArg(path, autorename: false)), path).ConfigureAwait(false);
        return ToItem(result.Metadata);
    }

    public override async Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        EnsureValidName(newName);
        path = Normalize(path);
        string target = CombinePath(GetParentPath(path) ?? string.Empty, newName);
        if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, target, StringComparison.Ordinal))
        {
            // Case-only rename: Dropbox paths are case-insensitive, so hop through a temporary name.
            string temp = CombinePath(GetParentPath(path) ?? string.Empty, "~akio-" + Guid.NewGuid().ToString("N")[..8]);
            await CallAsync(() => Client.Files.MoveV2Async(new RelocationArg(path, temp)), path).ConfigureAwait(false);
            path = temp;
        }

        var result = await CallAsync(() => Client.Files.MoveV2Async(new RelocationArg(path, target, autorename: false)), path).ConfigureAwait(false);
        return ToItem(result.Metadata);
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Normalize(raw);
            if (path.Length == 0) throw new StorageException(StorageErrorKind.NotSupported, "The root of Dropbox can't be deleted.");

            if (permanent)
            {
                try
                {
                    // Dropbox Business only; personal accounts keep deleted files restorable (handled below).
                    await CallAsync(async () => { await Client.Files.PermanentlyDeleteAsync(new DeleteArg(path)).ConfigureAwait(false); return true; }, path).ConfigureAwait(false);
                    continue;
                }
                catch (StorageException ex) when (ex.Kind is StorageErrorKind.AccessDenied or StorageErrorKind.NotSupported or StorageErrorKind.Unknown)
                {
                }
            }

            await CallAsync(() => Client.Files.DeleteV2Async(new DeleteArg(path)), path).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (!query.HasCriteria) yield break;
        var matcher = query.CreateMatcher(DateTimeOffset.Now);

        if (query.NameText.Length == 0 || query.HasWildcards)
        {
            // Dropbox search needs a plain term. For filter-only or wildcard searches, list the tree recursively
            // (one paged server call, not a folder-by-folder walk) and filter here.
            var page = await CallAsync(() => Client.Files.ListFolderAsync(new ListFolderArg(path, recursive: true, limit: PageSize)), path).ConfigureAwait(false);
            while (true)
            {
                foreach (var entry in page.Entries)
                {
                    if (entry.IsDeleted) continue;
                    var item = ToItem(entry);
                    if (!item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && matcher.Matches(item)) yield return item;
                }

                if (!page.HasMore) yield break;
                cancellationToken.ThrowIfCancellationRequested();
                string next = page.Cursor;
                page = await CallAsync(() => Client.Files.ListFolderContinueAsync(new ListFolderContinueArg(next)), path).ConfigureAwait(false);
            }
        }

        var options = new SearchOptions(path: path.Length == 0 ? null : path, maxResults: 200);
        var result = await CallAsync(() => Client.Files.SearchV2Async(new SearchV2Arg(query.NameText, options)), path).ConfigureAwait(false);
        while (true)
        {
            foreach (var match in result.Matches)
            {
                if (!match.Metadata.IsMetadata) continue;
                var item = ToItem(match.Metadata.AsMetadata.Value);
                if (matcher.Matches(item)) yield return item;
            }

            if (!result.HasMore || result.Cursor is null) yield break;
            cancellationToken.ThrowIfCancellationRequested();
            string cursor = result.Cursor;
            result = await CallAsync(() => Client.Files.SearchContinueV2Async(new SearchV2ContinueArg(cursor)), path).ConfigureAwait(false);
        }
    }

    public override async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        var usage = await CallAsync(() => Client.Users.GetSpaceUsageAsync(), string.Empty).ConfigureAwait(false);
        ulong? allocated = usage.Allocation.IsIndividual ? usage.Allocation.AsIndividual.Value.Allocated
            : usage.Allocation.IsTeam ? usage.Allocation.AsTeam.Value.Allocated : null;
        return new StorageQuota((long)usage.Used, allocated is { } a ? (long)a : null);
    }

    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.ServerSideCopy;

    // ------------------------------------------------------------------ content

    // ------------------------------------------------------------------ thumbnails

    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".gif", ".webp", ".ppm", ".bmp", ".heic",
    };

    /// <summary>Dropbox renders thumbnails for photos only (the formats its API lists).</summary>
    public bool MayHaveThumbnail(StorageItem file) =>
        file.Kind == StorageItemKind.File && ThumbnailExtensions.Contains(file.Extension) && file.Size is null or < 20L * 1024 * 1024;

    public async Task<Stream?> OpenThumbnailAsync(StorageItem file, int size, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        var thumbnailSize = size <= 64 ? ThumbnailSize.W64h64.Instance
            : size <= 128 ? ThumbnailSize.W128h128.Instance
            : size <= 256 ? (ThumbnailSize)ThumbnailSize.W256h256.Instance
            : ThumbnailSize.W640h480.Instance;
        try
        {
            using var response = await Client.Files.GetThumbnailV2Async(new ThumbnailV2Arg(
                new PathOrLink.Path(path), ThumbnailFormat.Jpeg.Instance, thumbnailSize, ThumbnailMode.FitoneBestfit.Instance)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new MemoryStream(await response.GetContentAsByteArrayAsync().ConfigureAwait(false));
        }
        catch (ApiException<ThumbnailV2Error>)
        {
            return null; // unsupported or corrupt image: keep the icon
        }
    }

    /// <summary><c>get_temporary_link</c>: a 4-hour direct link on Dropbox's content servers, Range supported.</summary>
    public async Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        var result = await CallAsync(() => Client.Files.GetTemporaryLinkAsync(new GetTemporaryLinkArg(path)), path).ConfigureAwait(false);
        return string.IsNullOrEmpty(result.Link)
            ? null
            : new DirectDownload(result.Link, new Dictionary<string, string> { ["User-Agent"] = "AkioExplorer" }) { MaxConnections = 6 };
    }

    public async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        var response = await CallAsync(() => Client.Files.DownloadAsync(new DownloadArg(path)), path).ConfigureAwait(false);
        try
        {
            return new OwnedStream(await response.GetContentAsStreamAsync().ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Small files in one request; larger ones through an upload session, chunk by chunk with retries.</summary>
    public async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var mode = overwrite ? (WriteMode)WriteMode.Overwrite.Instance : WriteMode.Add.Instance;
        int chunkSize = Core.Transfers.UploadChunking.Dropbox(length); // bigger chunks for big files: fewer idle round trips
        var buffer = new byte[chunkSize];

        int first = await content.ReadFullAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (first < chunkSize)
        {
            var single = await ChunkAsync(() => Client.Files.UploadAsync(
                new UploadArg(path, mode, autorename: false, mute: true), new MemoryStream(buffer, 0, first, writable: false)), path, cancellationToken)
                .ConfigureAwait(false);
            return ToItem(single);
        }

        var start = await ChunkAsync(() => Client.Files.UploadSessionStartAsync(
            new UploadSessionStartArg(), new MemoryStream(buffer, 0, first, writable: false)), path, cancellationToken).ConfigureAwait(false);
        ulong offset = (ulong)first;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await content.ReadFullAsync(buffer, cancellationToken).ConfigureAwait(false);
            var cursor = new UploadSessionCursor(start.SessionId, offset);
            if (read < chunkSize)
            {
                var done = await ChunkAsync(() => Client.Files.UploadSessionFinishAsync(
                    new UploadSessionFinishArg(cursor, new CommitInfo(path, mode, autorename: false, mute: true)),
                    new MemoryStream(buffer, 0, read, writable: false)), path, cancellationToken).ConfigureAwait(false);
                return ToItem(done);
            }

            await ChunkAsync(async () =>
            {
                await Client.Files.UploadSessionAppendV2Async(new UploadSessionAppendArg(cursor), new MemoryStream(buffer, 0, read, writable: false))
                    .ConfigureAwait(false);
                return true;
            }, path, cancellationToken).ConfigureAwait(false);
            offset += (ulong)read;
        }
    }

    public async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        string from = Normalize(source.Path);
        string to = CombinePath(parentPath, name);
        var result = await CallAsync(() => Client.Files.CopyV2Async(new RelocationArg(from, to, autorename: false)), from).ConfigureAwait(false);
        return ToItem(result.Metadata);
    }

    public async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        string from = Normalize(source.Path);
        string to = CombinePath(parentPath, name);
        var result = await CallAsync(() => Client.Files.MoveV2Async(new RelocationArg(from, to, autorename: false)), from).ConfigureAwait(false);
        return ToItem(result.Metadata);
    }

    /// <summary>One upload request with retries for throttling and network hiccups (the chunk is in memory, so it can be resent).</summary>
    private async Task<T> ChunkAsync<T>(Func<Task<T>> call, string path, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await CallAsync(call, path).ConfigureAwait(false);
            }
            catch (StorageException ex) when (attempt < MaxChunkAttempts && ex.Kind is StorageErrorKind.Throttled or StorageErrorKind.Unavailable)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static StorageItem ToItem(Metadata metadata)
    {
        bool folder = metadata.IsFolder;
        var file = metadata.IsFile ? metadata.AsFile : null;
        return new StorageItem
        {
            Name = metadata.Name,
            Path = Normalize(metadata.PathDisplay ?? metadata.PathLower ?? "/" + metadata.Name),
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = file is null ? null : (long)file.Size,
            Modified = file is null ? null : new DateTimeOffset(DateTime.SpecifyKind(file.ServerModified, DateTimeKind.Utc)).ToLocalTime(),
        };
    }

    private async Task<T> CallAsync<T>(Func<Task<T>> call, string path)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DropboxException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
        {
            throw Map(ex, path);
        }
    }

    internal StorageException Map(Exception ex, string path)
    {
        string name = Normalize(path).Length == 0 ? DisplayName : $"\"{GetDisplayName(path)}\"";
        string detail = ex.Message ?? string.Empty;
        return ex switch
        {
            AuthException or BadInputException when detail.Contains("invalid_grant", StringComparison.Ordinal) || ex is AuthException =>
                new StorageException(StorageErrorKind.AuthenticationRequired, $"The sign-in for \"{DisplayName}\" has expired or was revoked. Reconnect the account to continue.", ex),
            RateLimitException => new StorageException(StorageErrorKind.Throttled, "Dropbox is limiting requests right now. Wait a moment and try again.", ex),
            AccessException => new StorageException(StorageErrorKind.AccessDenied, $"You don't have permission to access {name}.", ex),
            HttpRequestException or TaskCanceledException => new StorageException(StorageErrorKind.Unavailable, "Can't reach Dropbox. Check your internet connection.", ex),
            HttpException http when http.StatusCode >= 500 => new StorageException(StorageErrorKind.Unavailable, "Dropbox is having trouble right now. Try again in a moment.", ex),
            _ when detail.Contains("not_found", StringComparison.Ordinal) =>
                new StorageException(StorageErrorKind.NotFound, $"Can't find {name} in Dropbox. It may have been moved or deleted.", ex),
            _ when detail.Contains("conflict", StringComparison.Ordinal) =>
                new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named {name} here.", ex),
            _ when detail.Contains("insufficient_space", StringComparison.Ordinal) =>
                new StorageException(StorageErrorKind.Unknown, "Your Dropbox is full.", ex),
            _ when detail.Contains("no_write_permission", StringComparison.Ordinal) || detail.Contains("restricted_content", StringComparison.Ordinal) =>
                new StorageException(StorageErrorKind.AccessDenied, $"You don't have permission to change {name}.", ex),
            _ when detail.Contains("too_many_write_operations", StringComparison.Ordinal) =>
                new StorageException(StorageErrorKind.Throttled, "Dropbox is busy with other changes. Try again in a moment.", ex),
            _ when detail.Contains("malformed_path", StringComparison.Ordinal) || detail.Contains("disallowed_name", StringComparison.Ordinal) =>
                new StorageException(StorageErrorKind.InvalidName, "Dropbox doesn't allow that name.", ex),
            _ => new StorageException(StorageErrorKind.Unknown, $"Dropbox error: {detail}", ex),
        };
    }

    private static void EnsureValidName(string name)
    {
        if (FileNameValidator.Validate(name) is { } error)
            throw new StorageException(StorageErrorKind.InvalidName, error);
    }
}
