using System.Runtime.CompilerServices;
using AkioExplorer.CloudProviders.Mega;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using CG.Web.MegaApiClient;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>
/// mega.nz/folder/…#KEY and mega.nz/file/…#KEY links, browsed with an anonymous MEGA session: no account needed.
/// (A signed-in MEGA account still offers "Open a link…" to save a copy into it.)
/// </summary>
public sealed class MegaLinkSource : ISharedLinkSource
{
    public bool CanOpenSharedLink(Uri link) => MegaPublicLink.TryParse(link.OriginalString, out _);

    public async Task<SharedLinkTarget> OpenSharedLinkAsync(Uri link, CancellationToken cancellationToken)
    {
        if (!MegaPublicLink.TryParse(link.OriginalString, out var parsed))
            throw new StorageException(StorageErrorKind.NotSupported, "That isn't a MEGA link.");

        var client = new MegaApiClient(new Options(), new MegaWebClient());
        try
        {
            await client.LoginAnonymousAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (parsed.IsFolder)
            {
                var nodes = (await client.GetNodesFromLinkAsync(new Uri(parsed.Url)).WaitAsync(cancellationToken).ConfigureAwait(false)).ToList();
                var top = nodes.FirstOrDefault(n => n.Type == NodeType.Directory && nodes.All(o => o.Id != n.ParentId))
                          ?? throw new StorageException(StorageErrorKind.NotFound, "The MEGA folder link is empty or no longer works.");
                return new SharedLinkTarget(new MegaLinkProvider(client, top, nodes, fileLink: null), string.Empty) { IsTemporary = true };
            }

            var file = await client.GetNodeFromLinkAsync(new Uri(parsed.Url)).WaitAsync(cancellationToken).ConfigureAwait(false);
            var provider = new MegaLinkProvider(client, top: null, [], fileLink: (new Uri(parsed.Url), file));
            return new SharedLinkTarget(provider, string.Empty, provider.FilePath) { IsTemporary = true };
        }
        catch (Exception ex) when (ex is ApiException or ArgumentException or InvalidOperationException or HttpRequestException)
        {
            throw new StorageException(StorageErrorKind.NotFound, "The MEGA link doesn't work: it may have been removed, or the key after # is missing.", ex);
        }
    }

    private sealed class MegaLinkProvider : ReadOnlyLinkProvider
    {
        private readonly MegaApiClient _client;
        private readonly Dictionary<string, INode> _byId;
        private readonly ILookup<string, INode> _byParent;
        private readonly Uri? _fileUri;

        public MegaLinkProvider(MegaApiClient client, INode? top, List<INode> nodes, (Uri Uri, INode Node)? fileLink)
            : base(CloudProviderType.Mega, "MEGA", top is null
                ? new LinkNode("link", fileLink!.Value.Node.Name, IsFolder: true)
                : new LinkNode(top.Id, top.Name, IsFolder: true))
        {
            _client = client;
            _byId = nodes.ToDictionary(n => n.Id);
            _byParent = nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId);
            if (fileLink is { } single)
            {
                _fileUri = single.Uri;
                FilePath = Remember(string.Empty, ToNode("file", single.Node));
            }
        }

        public string? FilePath { get; }

        protected override async IAsyncEnumerable<IReadOnlyList<LinkNode>> ListChildrenAsync(LinkNode folder, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (_fileUri is not null)
            {
                // A file link: its "folder" holds just that file.
                yield return FilePath is null ? [] : [new LinkNode("file", GetDisplayName(FilePath), false)];
                yield break;
            }
            yield return _byParent[folder.Key]
                .Where(n => n.Type is NodeType.File or NodeType.Directory)
                .OrderByDescending(n => n.Type == NodeType.Directory).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(n => ToNode(n.Id, n))
                .ToList();
        }

        protected override async Task<Stream> OpenNodeAsync(LinkNode file, CancellationToken cancellationToken)
        {
            try
            {
                return _fileUri is not null
                    ? await _client.DownloadAsync(_fileUri, null, cancellationToken).ConfigureAwait(false)
                    : await _client.DownloadAsync(_byId[file.Key], null, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                throw new StorageException(StorageErrorKind.Unavailable, $"MEGA refused the download ({ex.ApiResultCode}). The transfer quota may be used up; try again later.", ex);
            }
        }

        private static LinkNode ToNode(string key, INode node) => new(
            key, node.Name, node.Type == NodeType.Directory,
            node.Type == NodeType.Directory ? null : node.Size,
            node.ModificationDate is { } modified ? new DateTimeOffset(modified) : null);
    }
}
