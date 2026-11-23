using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>
/// mediafire.com/folder/… and mediafire.com/file/… links through MediaFire's public API (no account): folders are
/// listed with folder/get_content, files are fetched from the download page's direct link.
/// </summary>
public sealed partial class MediaFireLinkSource(HttpClient? http = null) : ISharedLinkSource
{
    private const string Api = "https://www.mediafire.com/api/1.5/";
    private readonly HttpClient _http = http ?? CloudHttp.Client;

    public bool CanOpenSharedLink(Uri link) => SharedLinks.TryParseMediaFire(link, out _, out _);

    public async Task<SharedLinkTarget> OpenSharedLinkAsync(Uri link, CancellationToken cancellationToken)
    {
        if (!SharedLinks.TryParseMediaFire(link, out string key, out bool isFolder))
            throw new StorageException(StorageErrorKind.NotSupported, "That isn't a MediaFire link.");

        if (isFolder)
        {
            var info = await CallAsync($"folder/get_info.php?folder_key={key}", cancellationToken).ConfigureAwait(false);
            var folder = info.GetProperty("folder_info");
            string name = Text(folder, "name") is { Length: > 0 } n ? n : "MediaFire folder";
            return new SharedLinkTarget(new MediaFireLinkProvider(this, new LinkNode(key, name, IsFolder: true)), string.Empty) { IsTemporary = true };
        }

        var fileInfo = (await CallAsync($"file/get_info.php?quick_key={key}", cancellationToken).ConfigureAwait(false)).GetProperty("file_info");
        var file = FileNode(fileInfo);
        var provider = new MediaFireLinkProvider(this, new LinkNode("file:" + key, file.Name, IsFolder: true), file);
        return new SharedLinkTarget(provider, string.Empty, provider.FilePath) { IsTemporary = true };
    }

    // ------------------------------------------------------------------ API

    private async Task<JsonElement> CallAsync(string query, CancellationToken cancellationToken)
    {
        string url = Api + query + (query.Contains('?') ? "&" : "?") + "response_format=json";
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement.GetProperty("response");
            if (Text(root, "result") != "Success")
            {
                string message = Text(root, "message") ?? "unknown error";
                var kind = Text(root, "error") is "110" or "112" or "113" ? StorageErrorKind.NotFound : StorageErrorKind.Unknown;
                throw new StorageException(kind, kind == StorageErrorKind.NotFound
                    ? "The MediaFire link doesn't work: the file or folder may have been removed or made private."
                    : $"MediaFire error: {message}");
            }
            return root.Clone();
        }
        catch (HttpRequestException ex)
        {
            throw new StorageException(StorageErrorKind.Unavailable, "Can't reach MediaFire. Check your internet connection.", ex);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new StorageException(StorageErrorKind.Unknown, "MediaFire sent an answer AkioExplorer doesn't understand.", ex);
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<LinkNode>> ListFolderAsync(string folderKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (string type in (string[])["folders", "files"])
        {
            for (int chunk = 1; chunk < 1000; chunk++)
            {
                var content = (await CallAsync($"folder/get_content.php?folder_key={folderKey}&content_type={type}&chunk={chunk}&chunk_size=1000",
                    cancellationToken).ConfigureAwait(false)).GetProperty("folder_content");
                var page = new List<LinkNode>();
                if (content.TryGetProperty(type, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        if (type == "folders")
                        {
                            if (Text(entry, "folderkey") is { Length: > 0 } key)
                                page.Add(new LinkNode(key, Text(entry, "name") ?? key, IsFolder: true, Created: Date(entry, "created_utc", "created")));
                        }
                        else
                        {
                            page.Add(FileNode(entry));
                        }
                    }
                }
                yield return page;
                if (Text(content, "more_chunks") != "yes") break;
            }
        }
    }

    private static LinkNode FileNode(JsonElement file)
    {
        string key = Text(file, "quickkey") ?? throw new StorageException(StorageErrorKind.Unknown, "MediaFire sent a file without a key.");
        long? size = long.TryParse(Text(file, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : null;
        var created = Date(file, "created_utc", "created");
        return new LinkNode(key, Text(file, "filename") ?? key, IsFolder: false, size, created, created);
    }

    /// <summary>The direct link sits on the download page (the "Download" button); newer pages scramble it in base64.</summary>
    private async Task<Stream> OpenFileAsync(string quickKey, CancellationToken cancellationToken)
    {
        string page;
        try
        {
            page = await _http.GetStringAsync($"https://www.mediafire.com/file/{quickKey}/file", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new StorageException(StorageErrorKind.Unavailable, "Can't reach MediaFire. Check your internet connection.", ex);
        }

        string? direct = DirectHref().Match(page) is { Success: true } href ? WebUtility.HtmlDecode(href.Groups[1].Value) : null;
        if (direct is null && ScrambledHref().Match(page) is { Success: true } scrambled)
        {
            try { direct = Encoding.UTF8.GetString(Convert.FromBase64String(scrambled.Groups[1].Value)); }
            catch (FormatException) { }
        }
        if (direct is null || !direct.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new StorageException(StorageErrorKind.Unavailable, "MediaFire didn't give a download link for this file (it may need a captcha in the browser).");

        var response = await _http.GetAsync(direct, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType == "text/html")
        {
            response.Dispose();
            throw new StorageException(StorageErrorKind.Unavailable, "MediaFire refused the download. Try again later, or open the link in the browser.");
        }
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static DateTimeOffset? Date(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (Text(element, name) is { } text &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return date.ToLocalTime();
        }
        return null;
    }

    [GeneratedRegex("href=\"(https?://download[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex DirectHref();

    [GeneratedRegex("data-scrambled-url=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ScrambledHref();

    private sealed class MediaFireLinkProvider : ReadOnlyLinkProvider
    {
        private readonly MediaFireLinkSource _source;
        private readonly LinkNode? _single;

        public MediaFireLinkProvider(MediaFireLinkSource source, LinkNode root, LinkNode? single = null)
            : base(CloudProviderType.MediaFire, "MediaFire", root)
        {
            _source = source;
            _single = single;
            if (single is not null) FilePath = Remember(string.Empty, single);
        }

        public string? FilePath { get; }

        protected override async IAsyncEnumerable<IReadOnlyList<LinkNode>> ListChildrenAsync(LinkNode folder, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_single is not null && folder.Key.StartsWith("file:", StringComparison.Ordinal))
            {
                yield return [_single];
                yield break;
            }
            await foreach (var page in _source.ListFolderAsync(folder.Key, cancellationToken).ConfigureAwait(false))
                yield return page;
        }

        protected override Task<Stream> OpenNodeAsync(LinkNode file, CancellationToken cancellationToken) =>
            _source.OpenFileAsync(file.Key, cancellationToken);
    }
}
