using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>
/// A OneDrive / SharePoint sharing link opened with a signed-in Microsoft account through Graph's shares API:
/// read-only browsing of the shared folder (or the one shared file). Node keys are "driveId|itemId".
/// </summary>
internal sealed class OneDriveLinkProvider : ReadOnlyLinkProvider
{
    private const string Select = "id,name,size,lastModifiedDateTime,createdDateTime,folder,file,package,parentReference,remoteItem";

    private readonly LinkRest _rest;
    private readonly string _graph;
    private readonly LinkNode? _single;

    private OneDriveLinkProvider(LinkRest rest, string graph, LinkNode root, LinkNode? single, CloudProviderType type, string service)
        : base(type, service, root)
    {
        _rest = rest;
        _graph = graph;
        _single = single;
        if (single is not null) FilePath = Remember(string.Empty, single);
    }

    public string? FilePath { get; }

    public static async Task<SharedLinkTarget> OpenAsync(Uri link, HttpClient http, IAccessTokenProvider tokens, string graph,
        CloudProviderType type, string service, CancellationToken cancellationToken)
    {
        var rest = new LinkRest(http, tokens, service);
        using var json = await rest.GetJsonAsync($"{graph}/shares/{SharedLinks.GraphShareId(link)}/driveItem?$select={Select}",
            cancellationToken).ConfigureAwait(false);
        var item = Node(json.RootElement, driveId: null);
        if (item.IsFolder)
            return new SharedLinkTarget(new OneDriveLinkProvider(rest, graph, item, null, type, service), string.Empty) { IsTemporary = true };

        var provider = new OneDriveLinkProvider(rest, graph, new LinkNode("file", item.Name, IsFolder: true), item, type, service);
        return new SharedLinkTarget(provider, string.Empty, provider.FilePath) { IsTemporary = true };
    }

    protected override async IAsyncEnumerable<IReadOnlyList<LinkNode>> ListChildrenAsync(LinkNode folder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_single is not null)
        {
            yield return [_single];
            yield break;
        }
        var (drive, id) = Split(folder.Key);
        string? url = $"{_graph}/drives/{drive}/items/{id}/children?$top=999&$select={Select}";
        while (url is not null)
        {
            using var json = await _rest.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            var page = new List<LinkNode>();
            foreach (var element in json.RootElement.GetProperty("value").EnumerateArray()) page.Add(Node(element, drive));
            url = json.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            yield return page;
        }
    }

    protected override Task<Stream> OpenNodeAsync(LinkNode file, CancellationToken cancellationToken)
    {
        var (drive, id) = Split(file.Key);
        return _rest.OpenAsync($"{_graph}/drives/{drive}/items/{id}/content", cancellationToken);
    }

    private static LinkNode Node(JsonElement e, string? driveId)
    {
        // An item added to "My files" from someone else's drive points there through remoteItem.
        if (e.TryGetProperty("remoteItem", out var remote) && remote.ValueKind == JsonValueKind.Object)
        {
            var target = Node(remote, null);
            return target with { Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? target.Name : target.Name };
        }
        string drive = e.TryGetProperty("parentReference", out var parent) && parent.TryGetProperty("driveId", out var d)
            ? d.GetString() ?? driveId ?? string.Empty
            : driveId ?? string.Empty;
        bool folder = e.TryGetProperty("folder", out _);
        long? size = !folder && e.TryGetProperty("size", out var s) && s.TryGetInt64(out long bytes) ? bytes : null;
        return new LinkNode(drive + "|" + e.GetProperty("id").GetString(), e.GetProperty("name").GetString() ?? "Item", folder, size,
            Date(e, "lastModifiedDateTime"), Date(e, "createdDateTime"));
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToLocalTime()
            : null;

    private static (string Drive, string Id) Split(string key)
    {
        int bar = key.IndexOf('|');
        return (Uri.EscapeDataString(key[..bar]), Uri.EscapeDataString(key[(bar + 1)..]));
    }
}
