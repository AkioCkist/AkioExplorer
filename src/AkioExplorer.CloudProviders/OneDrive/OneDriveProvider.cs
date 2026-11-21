using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.OneDrive;

/// <summary>
/// OneDrive (personal and work/school) through the Microsoft Graph v1.0 REST API, path addressed
/// ("/Documents/Report.docx"). Large folders are paged (999 items per request) and streamed to the UI.
/// </summary>
public sealed class OneDriveProvider : PathCloudProvider, IFileContentProvider, IThumbnailProvider, IDirectDownloadProvider
{
    public const string ServiceName = "OneDrive";
    internal const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string Select = "id,name,size,lastModifiedDateTime,createdDateTime,folder,file,package,remoteItem,parentReference";

    private const int SimpleUploadLimit = 4 * 1024 * 1024;
    private const int MaxChunkAttempts = 5;
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly ConcurrentDictionary<string, string> _pathById = new();

    public OneDriveProvider(CloudAccount account, HttpClient http, IAccessTokenProvider tokens)
        : base(account, CloudProviderType.OneDrive)
    {
        _http = http;
        _tokens = tokens;
    }

    // ------------------------------------------------------------------ addressing

    internal static string ItemRef(string path)
    {
        path = Normalize(path);
        if (path.Length == 0) return "/me/drive/root";
        var escaped = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"/me/drive/root:/{escaped}:";
    }

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        string? url = $"{GraphBase}{ItemRef(path)}/children?$top=999&$select={Select}";
        bool any = false;
        while (url is not null)
        {
            using var json = await SendAsync(HttpMethod.Get, url, null, cancellationToken, path).ConfigureAwait(false);
            var root = json!.RootElement;
            var page = new List<StorageItem>();
            foreach (var element in root.GetProperty("value").EnumerateArray())
                page.Add(ToItem(element, CombinePath(path, element.GetProperty("name").GetString()!)));
            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            any = true;
            yield return page;
        }
        if (!any) yield return Array.Empty<StorageItem>();
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        try
        {
            using var json = await SendAsync(HttpMethod.Get, $"{GraphBase}{ItemRef(path)}?$select={Select}", null, cancellationToken, path).ConfigureAwait(false);
            return path.Length == 0 ? null : ToItem(json!.RootElement, path);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        var body = new JsonObject
        {
            ["name"] = name,
            ["folder"] = new JsonObject(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        };
        string path = CombinePath(parentPath, name);
        using var json = await SendAsync(HttpMethod.Post, $"{GraphBase}{ItemRef(parentPath)}/children", body, cancellationToken, path).ConfigureAwait(false);
        return ToItem(json!.RootElement, path);
    }

    public override async Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        EnsureValidName(newName);
        path = Normalize(path);
        using var json = await SendAsync(HttpMethod.Patch, $"{GraphBase}{ItemRef(path)}", new JsonObject { ["name"] = newName }, cancellationToken, path)
            .ConfigureAwait(false);
        return ToItem(json!.RootElement, CombinePath(GetParentPath(path) ?? string.Empty, newName));
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Normalize(raw);
            if (path.Length == 0) throw new StorageException(StorageErrorKind.NotSupported, "The root of OneDrive can't be deleted.");

            if (permanent)
            {
                try
                {
                    // Work/school accounts only; personal OneDrive falls back to the recycle bin below.
                    (await SendAsync(HttpMethod.Post, $"{GraphBase}{ItemRef(path)}/permanentDelete", null, cancellationToken, path).ConfigureAwait(false))?.Dispose();
                    continue;
                }
                catch (StorageException ex) when (ex.Kind is StorageErrorKind.NotSupported or StorageErrorKind.Unknown or StorageErrorKind.AccessDenied)
                {
                }
            }

            (await SendAsync(HttpMethod.Delete, $"{GraphBase}{ItemRef(path)}", null, cancellationToken, path).ConfigureAwait(false))?.Dispose();
        }
    }

    // ------------------------------------------------------------------ search & quota

    public override async IAsyncEnumerable<StorageItem> SearchAsync(string path, string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        query = query.Trim();
        if (query.Length == 0) yield break;

        string q = Uri.EscapeDataString(query.Replace("'", "''"));
        string? url = $"{GraphBase}{ItemRef(path)}/search(q='{q}')?$top=200&$select={Select}";
        while (url is not null)
        {
            using var json = await SendAsync(HttpMethod.Get, url, null, cancellationToken, path).ConfigureAwait(false);
            var root = json!.RootElement;
            foreach (var element in root.GetProperty("value").EnumerateArray())
            {
                string? itemPath = await ResolvePathAsync(element, cancellationToken).ConfigureAwait(false);
                if (itemPath is null || !IsSameOrDescendant(itemPath, path) || itemPath.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                yield return ToItem(element, itemPath);
            }
            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }

    public override async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        using var json = await SendAsync(HttpMethod.Get, $"{GraphBase}/me/drive?$select=quota", null, cancellationToken, string.Empty).ConfigureAwait(false);
        if (!json!.RootElement.TryGetProperty("quota", out var quota)) return null;
        return new StorageQuota(
            quota.TryGetProperty("used", out var used) ? used.GetInt64() : null,
            quota.TryGetProperty("total", out var total) ? total.GetInt64() : null);
    }

    /// <summary>Signed-in user (used right after authentication).</summary>
    internal static async Task<CloudIdentity> GetIdentityAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/me?$select=id,displayName,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = json.RootElement;
        string? mail = root.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        string? upn = root.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null;
        string? name = root.TryGetProperty("displayName", out var d) ? d.GetString() : null;
        string? id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        return new CloudIdentity(mail ?? upn, name, id);
    }

    // ------------------------------------------------------------------ content

    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.ServerSideCopy;

    /// <summary>"/drive/root:/A/B" form used in parentReference.path.</summary>
    internal static string GraphParentPath(string parentPath) => "/drive/root:" + Normalize(parentPath);

    // ------------------------------------------------------------------ thumbnails

    private static readonly HashSet<string> DocumentThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".ods", ".odp", ".rtf",
    };

    public bool MayHaveThumbnail(StorageItem file) =>
        Core.Logic.PreviewClassifier.IsImageOrVideo(file) || DocumentThumbnailExtensions.Contains(file.Extension);

    /// <summary>Graph renders "medium" (176 px) and "large" (800 px) thumbnails for photos, videos and Office/PDF files.</summary>
    public async Task<Stream?> OpenThumbnailAsync(StorageItem file, int size, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        string rendition = size <= 176 ? "medium" : "large";
        using var response = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}{ItemRef(path)}/thumbnails/0/{rendition}/content"),
            authorize: true, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null; // 404: no thumbnail for this file
        var memory = new MemoryStream();
        await response.Content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        memory.Position = 0;
        return memory;
    }

    /// <summary>
    /// Graph hands out a short-lived, pre-authenticated <c>@microsoft.graph.downloadUrl</c> (no token needed, Range
    /// supported); a fresh one is fetched when it expires mid-download.
    /// </summary>
    public async Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        using var json = await SendAsync(HttpMethod.Get, $"{GraphBase}{ItemRef(path)}?$select=id,@microsoft.graph.downloadUrl", null, cancellationToken, path)
            .ConfigureAwait(false);
        return json is not null && json.RootElement.TryGetProperty("@microsoft.graph.downloadUrl", out var url) && url.GetString() is { Length: > 0 } link
            ? new DirectDownload(link, new Dictionary<string, string> { ["User-Agent"] = "AkioExplorer" })
            : null; // OneNote packages etc. have no file stream
    }

    public async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        // /content answers with a redirect to a pre-authenticated download URL; HttpClient follows it and drops the
        // Authorization header on the way (it must not be sent to the download host).
        var response = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}{ItemRef(path)}/content"),
            authorize: true, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (response) throw MapError(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), path);
        }
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    public async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        string behavior = overwrite ? "replace" : "fail";

        if (length >= 0 && length <= SimpleUploadLimit)
        {
            var data = new byte[length];
            int read = await content.ReadFullAsync(data, cancellationToken).ConfigureAwait(false);
            using var response = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Put,
                    $"{GraphBase}{ItemRef(path)}/content?@microsoft.graph.conflictBehavior={behavior}")
                { Content = new ByteArrayContent(data, 0, read) }, authorize: true, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, text, path);
            using var json = JsonDocument.Parse(text);
            return ToItem(json.RootElement, path);
        }

        // Upload session: chunks go straight to the pre-authenticated upload URL, each retried on its own.
        var sessionBody = new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = behavior } };
        string uploadUrl;
        using (var session = await SendAsync(HttpMethod.Post, $"{GraphBase}{ItemRef(path)}/createUploadSession", sessionBody, cancellationToken, path)
                   .ConfigureAwait(false))
        {
            uploadUrl = session!.RootElement.GetProperty("uploadUrl").GetString()!;
        }

        try
        {
            var buffer = new byte[Core.Transfers.UploadChunking.OneDrive(length)];
            long offset = 0;
            while (true)
            {
                int read = await content.ReadFullAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new StorageException(StorageErrorKind.Unknown, $"The source of \"{name}\" ended early.");
                using var done = await PutChunkAsync(uploadUrl, buffer, read, offset, length, path, cancellationToken).ConfigureAwait(false);
                offset += read;
                if (done is not null) return ToItem(done.RootElement, path);
            }
        }
        catch
        {
            // Abandon the session so OneDrive doesn't keep the partial upload around.
            try
            {
                using var cancel = new HttpRequestMessage(HttpMethod.Delete, uploadUrl);
                using var _ = await _http.SendAsync(cancel, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }

    private async Task<JsonDocument?> PutChunkAsync(string uploadUrl, byte[] buffer, int count, long offset, long total, string path, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, total);
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Accepted) return null; // more chunks expected
                if (response.IsSuccessStatusCode) return JsonDocument.Parse(text);
                bool transient = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
                if (!transient || attempt >= MaxChunkAttempts) throw MapError(response.StatusCode, text, path);
            }
            catch (HttpRequestException) when (attempt < MaxChunkAttempts)
            {
            }
            catch (HttpRequestException ex)
            {
                throw new StorageException(StorageErrorKind.Unavailable, "The upload to OneDrive was interrupted. Check your connection.", ex);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Graph copies asynchronously: start the job, then follow its monitor URL until it finishes.</summary>
    public async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        string from = Normalize(source.Path);
        string target = CombinePath(parentPath, name);
        var body = new JsonObject { ["parentReference"] = new JsonObject { ["path"] = GraphParentPath(parentPath) }, ["name"] = name };

        string monitor;
        using (var response = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post,
                       $"{GraphBase}{ItemRef(from)}/copy?@microsoft.graph.conflictBehavior=fail") { Content = JsonContent.Create(body) },
                   authorize: true, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode != HttpStatusCode.Accepted || response.Headers.Location is null)
                throw MapError(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), from);
            monitor = response.Headers.Location.AbsoluteUri;
        }

        var deadline = DateTimeOffset.UtcNow + CopyTimeout;
        var delay = TimeSpan.FromMilliseconds(400);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
            using var status = await _http.GetAsync(monitor, cancellationToken).ConfigureAwait(false); // monitor URLs need no token
            string text = await status.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!status.IsSuccessStatusCode && status.StatusCode != HttpStatusCode.Accepted) continue;
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = json.RootElement;
            string state = root.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : root.TryGetProperty("id", out _) ? "completed" : string.Empty;
            if (state is "completed") return await GetItemAsync(target, cancellationToken).ConfigureAwait(false);
            if (state is "failed")
            {
                string reason = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                throw new StorageException(StorageErrorKind.Unknown, $"OneDrive couldn't copy \"{source.Name}\". {reason}");
            }
        }

        throw new StorageException(StorageErrorKind.Unavailable, $"OneDrive is still copying \"{source.Name}\". Check the folder later.");
    }

    public async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        string from = Normalize(source.Path);
        var body = new JsonObject { ["parentReference"] = new JsonObject { ["path"] = GraphParentPath(parentPath) }, ["name"] = name };
        using var json = await SendAsync(HttpMethod.Patch, $"{GraphBase}{ItemRef(from)}?@microsoft.graph.conflictBehavior=fail", body, cancellationToken, from)
            .ConfigureAwait(false);
        return ToItem(json!.RootElement, CombinePath(parentPath, name));
    }

    /// <summary>Sends with a bearer token; on 401 refreshes once and retries. Caller owns the response.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> build, bool authorize, HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = build();
            if (authorize)
            {
                string token = await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                request.Dispose();
                throw new StorageException(StorageErrorKind.Unavailable, "Can't reach OneDrive. Check your internet connection.", ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && authorize && attempt == 0)
            {
                response.Dispose();
                request.Dispose();
                continue;
            }
            return response;
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<string?> ResolvePathAsync(JsonElement element, CancellationToken cancellationToken)
    {
        string name = element.GetProperty("name").GetString()!;
        if (element.TryGetProperty("parentReference", out var parent))
        {
            if (parent.TryGetProperty("path", out var p) && p.GetString() is { } parentPath)
                return CombinePath(StripRootPrefix(parentPath), name);

            if (parent.TryGetProperty("id", out var idElement) && idElement.GetString() is { } parentId)
            {
                if (!_pathById.TryGetValue(parentId, out var resolved))
                {
                    using var json = await SendAsync(HttpMethod.Get, $"{GraphBase}/me/drive/items/{Uri.EscapeDataString(parentId)}?$select=name,parentReference",
                        null, cancellationToken, string.Empty).ConfigureAwait(false);
                    var parentElement = json!.RootElement;
                    resolved = parentElement.TryGetProperty("parentReference", out var grand) && grand.TryGetProperty("path", out var gp)
                        ? CombinePath(StripRootPrefix(gp.GetString()!), parentElement.GetProperty("name").GetString()!)
                        : string.Empty; // the drive root has no parent path
                    _pathById[parentId] = resolved;
                }
                return CombinePath(resolved, name);
            }
        }
        return null;
    }

    internal static string StripRootPrefix(string graphPath)
    {
        // "/drive/root:" or "/drives/{id}/root:" followed by "/folder/sub"
        int marker = graphPath.IndexOf("root:", StringComparison.Ordinal);
        string rest = marker < 0 ? graphPath : graphPath[(marker + 5)..];
        return Normalize(Uri.UnescapeDataString(rest));
    }

    internal static StorageItem ToItem(JsonElement e, string path)
    {
        bool isFolder = e.TryGetProperty("folder", out _) ||
                        (e.TryGetProperty("remoteItem", out var remote) && remote.TryGetProperty("folder", out _));
        bool isPackage = e.TryGetProperty("package", out var package);
        return new StorageItem
        {
            Name = e.GetProperty("name").GetString()!,
            Path = Normalize(path),
            Kind = isFolder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = isFolder ? null : e.TryGetProperty("size", out var size) ? size.GetInt64() : null,
            Modified = e.TryGetProperty("lastModifiedDateTime", out var modified) ? modified.GetDateTimeOffset().ToLocalTime() : null,
            Created = e.TryGetProperty("createdDateTime", out var created) ? created.GetDateTimeOffset().ToLocalTime() : null,
            TypeDescription = isPackage && package.TryGetProperty("type", out var t) && t.GetString() == "oneNote" ? "OneNote notebook" : null,
        };
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new StorageException(StorageErrorKind.Unavailable, "Can't reach OneDrive. Check your internet connection.", ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue; // token revoked/expired early
                string text = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
                throw MapError(response.StatusCode, text, path);
            }
        }
    }

    internal StorageException MapError(HttpStatusCode status, string body, string path)
    {
        string code = string.Empty, message = string.Empty;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                message = error.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        string name = path.Length == 0 ? DisplayName : $"\"{GetDisplayName(path)}\"";
        return status switch
        {
            HttpStatusCode.Unauthorized => new StorageException(StorageErrorKind.AuthenticationRequired,
                $"The sign-in for \"{DisplayName}\" has expired. Reconnect the account to continue."),
            HttpStatusCode.NotFound => new StorageException(StorageErrorKind.NotFound, $"Can't find {name} in OneDrive. It may have been moved or deleted."),
            HttpStatusCode.Conflict => new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named {name} here."),
            HttpStatusCode.Forbidden => new StorageException(StorageErrorKind.AccessDenied, $"You don't have permission to change {name}."),
            HttpStatusCode.InsufficientStorage => new StorageException(StorageErrorKind.Unknown, "Your OneDrive is full."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new StorageException(StorageErrorKind.Throttled,
                "OneDrive is busy right now. Wait a moment and try again."),
            HttpStatusCode.BadRequest when code is "invalidRequest" or "notSupported" =>
                new StorageException(StorageErrorKind.NotSupported, $"OneDrive doesn't support this: {message}"),
            _ => new StorageException(StorageErrorKind.Unknown, $"OneDrive error ({(int)status}{(code.Length > 0 ? ", " + code : string.Empty)}): {message}"),
        };
    }

    private static void EnsureValidName(string name)
    {
        if (Core.Logic.FileNameValidator.Validate(name) is { } error)
            throw new StorageException(StorageErrorKind.InvalidName, error);
    }
}
