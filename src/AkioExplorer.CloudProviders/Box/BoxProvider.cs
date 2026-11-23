using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Box;

/// <summary>
/// Box through its v2.0 REST API. Box addresses items by id, so paths are resolved folder by folder and cached.
/// Deletes go to the Box trash; big uploads use chunked upload sessions with SHA-1 digests per part.
/// </summary>
public sealed class BoxProvider : RemotePathProvider, IDirectDownloadProvider, IDisposable, ISharedLinkSource
{
    public const string ServiceName = "Box";
    internal const string Api = "https://api.box.com/2.0";
    private const string UploadApi = "https://upload.box.com/api/2.0";
    private const string Fields = "id,type,name,size,modified_at,content_modified_at,created_at";
    private const long ChunkedUploadMin = 50L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly HttpClient _noRedirect;
    private readonly IAccessTokenProvider _tokens;
    private readonly ConcurrentDictionary<string, (string Id, bool Folder)> _ids = new(StringComparer.OrdinalIgnoreCase);

    public BoxProvider(CloudAccount account, HttpClient http, IAccessTokenProvider tokens)
        : base(account, CloudProviderType.Box, ServiceName)
    {
        _http = http;
        _tokens = tokens;
        _noRedirect = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _ids[string.Empty] = ("0", true);
    }

    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.RecycleBin | StorageCapabilities.ServerSideCopy;

    // ------------------------------------------------------------------ links other people shared

    public bool CanOpenSharedLink(Uri link) => Core.Logic.SharedLinks.TryParseBox(link);

    /// <summary>An app.box.com/s/… shared link, browsed read-only with this account.</summary>
    public Task<SharedLinkTarget> OpenSharedLinkAsync(Uri link, CancellationToken cancellationToken) =>
        Links.BoxLinkProvider.OpenAsync(link, _http, _tokens, cancellationToken);
    // ------------------------------------------------------------------ id resolution

    private async Task<(string Id, bool Folder)?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (_ids.TryGetValue(path, out var known)) return known;
        string? parent = GetParentPath(path);
        if (parent is null) return null;
        var parentRef = await ResolveAsync(parent, cancellationToken).ConfigureAwait(false);
        if (parentRef is not { Folder: true }) return null;
        string name = ItemName(path);
        await foreach (var page in ListByIdAsync(parentRef.Value.Id, parent, cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in page)
                if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return _ids.TryGetValue(item.Path, out var found) ? found : null;
        }
        return null;
    }

    private async Task<(string Id, bool Folder)> RequireAsync(string path, CancellationToken cancellationToken) =>
        await ResolveAsync(path, cancellationToken).ConfigureAwait(false) ?? throw NotFound(path);

    private void Forget(string path)
    {
        path = Normalize(path);
        foreach (var key in _ids.Keys.Where(k => k.Length > 0 && IsSameOrDescendant(k, path)).ToList()) _ids.TryRemove(key, out _);
    }

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var folder = await RequireAsync(path, cancellationToken).ConfigureAwait(false);
        if (!folder.Folder) throw new StorageException(StorageErrorKind.NotFound, $"{Describe(path)} is a file, not a folder.");
        await foreach (var page in ListByIdAsync(folder.Id, path, cancellationToken).ConfigureAwait(false)) yield return page;
    }

    private async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListByIdAsync(string folderId, string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            string url = $"{Api}/folders/{folderId}/items?fields={Fields}&limit=1000&usemarker=true" + (marker is null ? "" : "&marker=" + Uri.EscapeDataString(marker));
            using var json = await SendAsync(HttpMethod.Get, url, null, path, cancellationToken).ConfigureAwait(false);
            var root = json!.RootElement;
            var page = new List<StorageItem>();
            foreach (var entry in root.GetProperty("entries").EnumerateArray())
            {
                if (entry.GetProperty("type").GetString() == "web_link") continue;
                page.Add(ToItem(entry, CombinePath(path, entry.GetProperty("name").GetString()!)));
            }
            marker = root.TryGetProperty("next_marker", out var next) && next.ValueKind == JsonValueKind.String && next.GetString() is { Length: > 0 } m ? m : null;
            yield return page;
        }
        while (marker is not null);
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        if (await ResolveAsync(path, cancellationToken).ConfigureAwait(false) is not { } item) return null;
        try
        {
            using var json = await SendAsync(HttpMethod.Get, $"{Api}/{Kind(item.Folder)}/{item.Id}?fields={Fields}", null, path, cancellationToken).ConfigureAwait(false);
            return ToItem(json!.RootElement, path);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            Forget(path);
            return null;
        }
    }

    private StorageItem ToItem(JsonElement e, string path)
    {
        bool folder = e.GetProperty("type").GetString() == "folder";
        path = Normalize(path);
        _ids[path] = (e.GetProperty("id").GetString()!, folder);
        string name = e.GetProperty("name").GetString()!;
        return new StorageItem
        {
            Name = name,
            Path = path,
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = folder ? null : e.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
            Modified = Date(e, "content_modified_at") ?? Date(e, "modified_at"),
            Created = Date(e, "created_at"),
        };
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date) ? date.ToLocalTime() : null;

    private static string Kind(bool folder) => folder ? "folders" : "files";

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var parent = await RequireAsync(parentPath, cancellationToken).ConfigureAwait(false);
        using var json = await SendAsync(HttpMethod.Post, $"{Api}/folders?fields={Fields}",
            new JsonObject { ["name"] = name, ["parent"] = new JsonObject { ["id"] = parent.Id } }, path, cancellationToken).ConfigureAwait(false);
        return ToItem(json!.RootElement, path);
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            string path = Normalize(raw);
            if (path.Length == 0) throw RootChange("deleted");
            if (await ResolveAsync(path, cancellationToken).ConfigureAwait(false) is not { } item) continue;
            string url = $"{Api}/{Kind(item.Folder)}/{item.Id}" + (item.Folder ? "?recursive=true" : "");
            (await SendAsync(HttpMethod.Delete, url, null, path, cancellationToken).ConfigureAwait(false))?.Dispose();
            if (permanent)
                (await SendAsync(HttpMethod.Delete, $"{Api}/{Kind(item.Folder)}/{item.Id}/trash", null, path, cancellationToken).ConfigureAwait(false))?.Dispose();
            Forget(path);
        }
    }

    public override async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        var item = await RequireAsync(source.Path, cancellationToken).ConfigureAwait(false);
        var parent = await RequireAsync(parentPath, cancellationToken).ConfigureAwait(false);
        string target = CombinePath(parentPath, name);
        using var json = await SendAsync(HttpMethod.Put, $"{Api}/{Kind(item.Folder)}/{item.Id}?fields={Fields}",
            new JsonObject { ["name"] = name, ["parent"] = new JsonObject { ["id"] = parent.Id } }, target, cancellationToken).ConfigureAwait(false);
        Forget(source.Path);
        return ToItem(json!.RootElement, target);
    }

    public override async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        var item = await RequireAsync(source.Path, cancellationToken).ConfigureAwait(false);
        var parent = await RequireAsync(parentPath, cancellationToken).ConfigureAwait(false);
        string target = CombinePath(parentPath, name);
        using var json = await SendAsync(HttpMethod.Post, $"{Api}/{Kind(item.Folder)}/{item.Id}/copy?fields={Fields}",
            new JsonObject { ["name"] = name, ["parent"] = new JsonObject { ["id"] = parent.Id } }, target, cancellationToken).ConfigureAwait(false);
        return ToItem(json!.RootElement, target);
    }

    public override async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, $"{Api}/users/me?fields=space_amount,space_used", null, string.Empty, cancellationToken).ConfigureAwait(false);
        var root = json!.RootElement;
        long? used = root.TryGetProperty("space_used", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : null;
        long? total = root.TryGetProperty("space_amount", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
        return new StorageQuota(used, total is > 0 and < long.MaxValue / 2 ? total : null); // "unlimited" plans report a huge number
    }

    internal static async Task<CloudIdentity> GetIdentityAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/users/me?fields=id,name,login");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = json.RootElement;
        return new CloudIdentity(root.GetProperty("login").GetString(), root.GetProperty("name").GetString(), root.GetProperty("id").GetString());
    }

    // ------------------------------------------------------------------ content

    /// <summary>/content answers 302 with a pre-authenticated download URL (Range supported).</summary>
    public async Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        var item = await RequireAsync(file.Path, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/files/{item.Id}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(cancellationToken, refresh).ConfigureAwait(false));
        using var response = await _noRedirect.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Found && response.Headers.Location is { } location
            ? new DirectDownload(location.AbsoluteUri, new Dictionary<string, string> { ["User-Agent"] = "AkioExplorer" }) { MaxConnections = 6 }
            : null;
    }

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        var item = await RequireAsync(file.Path, cancellationToken).ConfigureAwait(false);
        var response = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/files/{item.Id}/content"), HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (response) throw Map(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), file.Path);
        }
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var parent = await RequireAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var existing = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !overwrite) throw Exists(path);
        if (existing is { Folder: true }) throw Exists(path);

        if (length >= ChunkedUploadMin)
            return await ChunkedUploadAsync(parent.Id, existing?.Id, name, path, content, length, cancellationToken).ConfigureAwait(false);

        var data = new byte[Math.Max(0, length)];
        int read = length > 0 ? await content.ReadFullAsync(data, cancellationToken).ConfigureAwait(false) : 0;
        string url = existing is { } file ? $"{UploadApi}/files/{file.Id}/content?fields={Fields}" : $"{UploadApi}/files/content?fields={Fields}";
        using var response = await SendRawAsync(() =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(JsonSerializer.Serialize(new { name, parent = new { id = parent.Id } })), "attributes" },
                { new ByteArrayContent(data, 0, read), "file", name },
            };
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Map(response.StatusCode, text, path);
        using var json = JsonDocument.Parse(text);
        return ToItem(json.RootElement.GetProperty("entries")[0], path);
    }

    private async Task<StorageItem> ChunkedUploadAsync(string parentId, string? fileId, string name, string path, Stream content, long length,
        CancellationToken cancellationToken)
    {
        var body = fileId is null
            ? new JsonObject { ["folder_id"] = parentId, ["file_size"] = length, ["file_name"] = name }
            : new JsonObject { ["file_size"] = length, ["file_name"] = name };
        string sessionUrl = fileId is null ? $"{UploadApi}/files/upload_sessions" : $"{UploadApi}/files/{fileId}/upload_sessions";
        string sessionId, partsUrl, commitUrl, abortUrl;
        int partSize;
        using (var session = await SendAsync(HttpMethod.Post, sessionUrl, body, path, cancellationToken).ConfigureAwait(false))
        {
            var root = session!.RootElement;
            sessionId = root.GetProperty("id").GetString()!;
            partSize = root.GetProperty("part_size").GetInt32();
            var endpoints = root.GetProperty("session_endpoints");
            partsUrl = endpoints.GetProperty("upload_part").GetString()!;
            commitUrl = endpoints.GetProperty("commit").GetString()!;
            abortUrl = endpoints.GetProperty("abort").GetString()!;
        }
        _ = sessionId;

        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var parts = new JsonArray();
        try
        {
            var buffer = new byte[partSize];
            long offset = 0;
            while (offset < length)
            {
                int read = await content.ReadFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new StorageException(StorageErrorKind.Unknown, $"The source of \"{name}\" ended early.");
                whole.AppendData(buffer, 0, read);
                string digest = Convert.ToBase64String(SHA1.HashData(buffer.AsSpan(0, read)));
                long start = offset;
                using var json = await SendJsonRawAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, partsUrl) { Content = new ByteArrayContent(buffer, 0, read) };
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + read - 1, length);
                    request.Headers.TryAddWithoutValidation("Digest", "sha=" + digest);
                    return request;
                }, path, cancellationToken).ConfigureAwait(false);
                parts.Add(JsonNode.Parse(json.RootElement.GetProperty("part").GetRawText()));
                offset += read;
            }

            string fileDigest = Convert.ToBase64String(whole.GetHashAndReset());
            for (int attempt = 0; ; attempt++)
            {
                using var response = await SendRawAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, commitUrl) { Content = JsonContent.Create(new JsonObject { ["parts"] = parts.DeepClone() }) };
                    request.Headers.TryAddWithoutValidation("Digest", "sha=" + fileDigest);
                    return request;
                }, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Accepted && attempt < 30)
                {
                    // Box is still assembling the parts; it says when to ask again.
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw Map(response.StatusCode, text, path);
                using var json = JsonDocument.Parse(text);
                return ToItem(json.RootElement.GetProperty("entries")[0], path);
            }
        }
        catch
        {
            try
            {
                using var _ = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Delete, abortUrl), HttpCompletionOption.ResponseContentRead, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> build, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0).ConfigureAwait(false));
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                request.Dispose();
                throw Unreachable(ex);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                request.Dispose();
                continue;
            }
            return response;
        }
    }

    private async Task<JsonDocument> SendJsonRawAsync(Func<HttpRequestMessage> build, string path, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(build, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Map(response.StatusCode, text, path);
        return JsonDocument.Parse(text);
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, JsonNode? body, string path, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body);
            return request;
        }, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Map(response.StatusCode, text, path);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private StorageException Map(HttpStatusCode status, string body, string path)
    {
        string code = string.Empty, message = string.Empty;
        try
        {
            using var json = JsonDocument.Parse(body);
            code = json.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            message = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
        }
        return code switch
        {
            "item_name_in_use" or "conflict" => Exists(path),
            "item_name_invalid" or "item_name_too_long" => new StorageException(StorageErrorKind.InvalidName, $"Box doesn't allow the name {Describe(path)}."),
            "storage_limit_exceeded" or "file_size_limit_exceeded" => code == "storage_limit_exceeded" ? Full()
                : new StorageException(StorageErrorKind.NotSupported, $"{Describe(path)} is larger than your Box plan allows."),
            "access_denied_insufficient_permissions" or "forbidden" => Denied(path),
            "not_found" or "trashed" => NotFound(path),
            _ => (int)status switch
            {
                401 => SignInFailed(),
                403 => Denied(path),
                404 => NotFound(path),
                409 => Exists(path),
                429 or 503 => Busy(),
                _ => new StorageException(StorageErrorKind.Unknown, $"Box error ({(int)status}{(code.Length > 0 ? ", " + code : "")}): {message}"),
            },
        };
    }

    public void Dispose() => _noRedirect.Dispose();
}
