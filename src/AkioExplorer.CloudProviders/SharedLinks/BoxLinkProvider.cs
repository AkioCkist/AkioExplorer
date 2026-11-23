using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>
/// A Box shared link (app.box.com/s/…) opened with a signed-in Box account: every request carries the
/// "BoxApi: shared_link=…" header, which lets the account read what the link shares. Read-only; keys are Box ids.
/// </summary>
internal sealed class BoxLinkProvider : ReadOnlyLinkProvider
{
    private const string Api = "https://api.box.com/2.0";
    private const string Fields = "id,type,name,size,modified_at,content_modified_at,created_at";
    private const int PageSize = 1000;

    private readonly LinkRest _rest;
    private readonly LinkNode? _single;

    private BoxLinkProvider(LinkRest rest, LinkNode root, LinkNode? single)
        : base(CloudProviderType.Box, "Box", root)
    {
        _rest = rest;
        _single = single;
        if (single is not null) FilePath = Remember(string.Empty, single);
    }

    public string? FilePath { get; }

    public static async Task<SharedLinkTarget> OpenAsync(Uri link, HttpClient http, IAccessTokenProvider tokens, CancellationToken cancellationToken)
    {
        var rest = new LinkRest(http, tokens, "Box", new Dictionary<string, string> { ["BoxApi"] = "shared_link=" + link.OriginalString });
        using var json = await rest.GetJsonAsync($"{Api}/shared_items?fields={Fields}", cancellationToken).ConfigureAwait(false);
        var item = Node(json.RootElement);
        if (item.IsFolder) return new SharedLinkTarget(new BoxLinkProvider(rest, item, null), string.Empty) { IsTemporary = true };

        var provider = new BoxLinkProvider(rest, new LinkNode("file", item.Name, IsFolder: true), item);
        return new SharedLinkTarget(provider, string.Empty, provider.FilePath) { IsTemporary = true };
    }

    protected override async IAsyncEnumerable<IReadOnlyList<LinkNode>> ListChildrenAsync(LinkNode folder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_single is not null)
        {
            yield return [_single];
            yield break;
        }
        for (int offset = 0; ; offset += PageSize)
        {
            using var json = await _rest.GetJsonAsync($"{Api}/folders/{Uri.EscapeDataString(folder.Key)}/items?fields={Fields}&limit={PageSize}&offset={offset}",
                cancellationToken).ConfigureAwait(false);
            var entries = json.RootElement.GetProperty("entries");
            var page = new List<LinkNode>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type) && type.GetString() is "file" or "folder") page.Add(Node(entry));
            }
            yield return page;
            int total = json.RootElement.TryGetProperty("total_count", out var count) && count.TryGetInt32(out int n) ? n : 0;
            if (entries.GetArrayLength() < PageSize || offset + PageSize >= total) break;
        }
    }

    protected override Task<Stream> OpenNodeAsync(LinkNode file, CancellationToken cancellationToken) =>
        _rest.OpenAsync($"{Api}/files/{Uri.EscapeDataString(file.Key)}/content", cancellationToken);

    private static LinkNode Node(JsonElement e)
    {
        bool folder = e.GetProperty("type").GetString() == "folder";
        long? size = !folder && e.TryGetProperty("size", out var s) && s.TryGetInt64(out long bytes) ? bytes : null;
        return new LinkNode(e.GetProperty("id").GetString()!, e.GetProperty("name").GetString() ?? "Item", folder, size,
            Date(e, "content_modified_at") ?? Date(e, "modified_at"), Date(e, "created_at"));
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToLocalTime()
            : null;
}
