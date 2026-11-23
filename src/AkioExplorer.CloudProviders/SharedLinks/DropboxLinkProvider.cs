using System.Runtime.CompilerServices;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Sharing;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>
/// A Dropbox shared link (dropbox.com/scl/fo/…, /scl/fi/…) opened with a signed-in Dropbox account: folders are listed
/// with list_folder + shared_link, files fetched with get_shared_link_file. Keys are paths inside the link ("/sub/a.txt").
/// Needs the app's "sharing.read" permission; accounts connected before it was added must be reconnected.
/// </summary>
internal sealed class DropboxLinkProvider : ReadOnlyLinkProvider
{
    private const string ScopeHint = "Opening Dropbox links needs the \"sharing.read\" permission: tick it for your Dropbox app, then reconnect the account.";

    private readonly Func<DropboxClient> _client;
    private readonly string _url;
    private readonly LinkNode? _single;

    private DropboxLinkProvider(Func<DropboxClient> client, string url, LinkNode root, LinkNode? single)
        : base(CloudProviderType.Dropbox, "Dropbox", root)
    {
        _client = client;
        _url = url;
        _single = single;
        if (single is not null) FilePath = Remember(string.Empty, single);
    }

    public string? FilePath { get; }

    public static async Task<SharedLinkTarget> OpenAsync(Uri link, Func<DropboxClient> client, CancellationToken cancellationToken)
    {
        if (!SharedLinks.TryParseDropbox(link, out bool isFolder)) throw new StorageException(StorageErrorKind.NotSupported, "That isn't a Dropbox link.");
        string url = link.OriginalString;

        if (isFolder)
        {
            string name = "Dropbox folder";
            try
            {
                name = (await Call(() => client().Sharing.GetSharedLinkMetadataAsync(url), cancellationToken).ConfigureAwait(false)).Name;
            }
            catch (StorageException ex) when (ex.Kind == StorageErrorKind.AccessDenied)
            {
                // No sharing.read yet: the folder still lists (files.metadata.read), only its name is unknown.
            }
            var provider = new DropboxLinkProvider(client, url, new LinkNode(string.Empty, name, IsFolder: true), null);
            // List the top level now, so a link this account can't open falls back to the browser right away.
            await foreach (var _ in provider.ListChildrenAsync(new LinkNode(string.Empty, name, true), cancellationToken).ConfigureAwait(false)) break;
            return new SharedLinkTarget(provider, string.Empty) { IsTemporary = true };
        }

        var metadata = await Call(() => client().Sharing.GetSharedLinkMetadataAsync(url), cancellationToken).ConfigureAwait(false);
        var file = metadata.AsFile;
        var single = new LinkNode(string.Empty, metadata.Name, IsFolder: false, file is null ? null : (long)file.Size,
            file is null ? null : new DateTimeOffset(DateTime.SpecifyKind(file.ServerModified, DateTimeKind.Utc)).ToLocalTime());
        var fileProvider = new DropboxLinkProvider(client, url, new LinkNode("link", metadata.Name, IsFolder: true), single);
        return new SharedLinkTarget(fileProvider, string.Empty, fileProvider.FilePath) { IsTemporary = true };
    }

    protected override async IAsyncEnumerable<IReadOnlyList<LinkNode>> ListChildrenAsync(LinkNode folder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_single is not null)
        {
            yield return [_single];
            yield break;
        }
        var page = await Call(() => _client().Files.ListFolderAsync(new ListFolderArg(folder.Key, limit: 2000, sharedLink: new SharedLink(_url))),
            cancellationToken).ConfigureAwait(false);
        yield return page.Entries.Select(e => Node(folder.Key, e)).ToList();
        while (page.HasMore)
        {
            string cursor = page.Cursor;
            page = await Call(() => _client().Files.ListFolderContinueAsync(new ListFolderContinueArg(cursor)), cancellationToken).ConfigureAwait(false);
            yield return page.Entries.Select(e => Node(folder.Key, e)).ToList();
        }
    }

    protected override async Task<Stream> OpenNodeAsync(LinkNode file, CancellationToken cancellationToken)
    {
        string? path = _single is not null ? null : file.Key;
        var response = await Call(() => _client().Sharing.GetSharedLinkFileAsync(_url, path), cancellationToken).ConfigureAwait(false);
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

    private static LinkNode Node(string parentKey, Metadata entry)
    {
        var file = entry.IsFile ? entry.AsFile : null;
        return new LinkNode(parentKey + "/" + entry.Name, entry.Name, entry.IsFolder, file is null ? null : (long)file.Size,
            file is null ? null : new DateTimeOffset(DateTime.SpecifyKind(file.ServerModified, DateTimeKind.Utc)).ToLocalTime());
    }

    private static async Task<T> Call<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await call().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DropboxException or HttpRequestException)
        {
            string detail = ex.Message ?? string.Empty;
            throw detail switch
            {
                _ when detail.Contains("missing_scope", StringComparison.Ordinal) => new StorageException(StorageErrorKind.AccessDenied, ScopeHint, ex),
                _ when ex is HttpRequestException => new StorageException(StorageErrorKind.Unavailable, "Can't reach Dropbox. Check your internet connection.", ex),
                _ when ex is AuthException => new StorageException(StorageErrorKind.AuthenticationRequired, "The Dropbox sign-in has expired. Reconnect the account to continue.", ex),
                _ when detail.Contains("shared_link_access_denied", StringComparison.Ordinal) || detail.Contains("access_denied", StringComparison.Ordinal) =>
                    new StorageException(StorageErrorKind.AccessDenied, "This Dropbox account isn't allowed to open that link.", ex),
                _ when detail.Contains("not_found", StringComparison.Ordinal) || detail.Contains("shared_link_not_found", StringComparison.Ordinal) =>
                    new StorageException(StorageErrorKind.NotFound, "The Dropbox link doesn't work: it was removed or has expired.", ex),
                _ => new StorageException(StorageErrorKind.Unknown, $"Dropbox error: {detail}", ex),
            };
        }
    }
}
