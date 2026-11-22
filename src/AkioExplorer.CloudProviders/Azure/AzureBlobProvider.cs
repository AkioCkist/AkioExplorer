using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Azure;

/// <summary>
/// Azure Blob Storage with a storage-account key (Shared Key auth). Paths are "/container/folder/file" (or
/// "/folder/file" when pinned to one container). Uploads are parallel blocks; downloads use short-lived SAS URLs
/// for the segmented downloader.
/// </summary>
public sealed class AzureBlobProvider : RemotePathProvider, IDirectDownloadProvider, IDisposable
{
    internal const string ApiVersion = "2023-11-03";
    private const int SinglePutLimit = 16 * 1024 * 1024;
    private const int ParallelBlocks = 4;

    private readonly string _accountName;
    private readonly byte[] _key;
    private readonly string? _container;
    private readonly Uri _endpoint;
    private readonly HttpClient _http;

    public AzureBlobProvider(CloudAccount account, string accountName, string accountKey, string endpointSuffix, string? container)
        : base(account, CloudProviderType.AzureBlob, "Azure Blob Storage")
    {
        _accountName = accountName;
        try
        {
            _key = Convert.FromBase64String(accountKey);
        }
        catch (FormatException)
        {
            _key = [];
        }
        _container = container;
        _endpoint = new Uri($"https://{accountName}.blob.{endpointSuffix}/");
        _http = new HttpClient(CloudHttp.CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AkioExplorer");
    }

    public override StorageCapabilities Capabilities =>
        base.Capabilities | StorageCapabilities.ServerSideCopy | (_container is null ? StorageCapabilities.VirtualRoot : 0);

    // ------------------------------------------------------------------ addressing

    internal (string? Container, string Blob) Split(string path)
    {
        path = Normalize(path).TrimStart('/');
        if (_container is { } fixedContainer) return (fixedContainer, path);
        if (path.Length == 0) return (null, string.Empty);
        int slash = path.IndexOf('/');
        return slash < 0 ? (path, string.Empty) : (path[..slash], path[(slash + 1)..]);
    }

    private string PathOf(string container, string blob)
    {
        string trimmed = blob.TrimEnd('/');
        return _container is not null ? Normalize("/" + trimmed) : Normalize($"/{container}/{trimmed}");
    }

    private Uri Url(string? container, string blob, string? query)
    {
        string path = container is null ? string.Empty : Uri.EscapeDataString(container) + (blob.Length > 0 ? "/" + EncodeBlob(blob) : string.Empty);
        return new Uri(_endpoint.AbsoluteUri + path + (query is null ? string.Empty : "?" + query));
    }

    private static string EncodeBlob(string blob) => string.Join('/', blob.Split('/').Select(Uri.EscapeDataString));

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (container, blob) = Split(path);
        string? marker = null;
        if (container is null)
        {
            do
            {
                var xml = await SendXmlAsync(HttpMethod.Get, null, string.Empty, "comp=list&maxresults=5000" + Marker(marker), path, cancellationToken).ConfigureAwait(false);
                yield return xml.Descendants("Container").Select(c => new StorageItem
                {
                    Name = c.Element("Name")!.Value,
                    Path = Normalize("/" + c.Element("Name")!.Value),
                    Kind = StorageItemKind.Drive,
                    Modified = ParseDate(c.Element("Properties")?.Element("Last-Modified")?.Value),
                    TypeDescription = "Container",
                }).ToList();
                marker = NextMarker(xml);
            }
            while (marker is not null);
            yield break;
        }

        string prefix = blob.Length == 0 ? string.Empty : blob.TrimEnd('/') + "/";
        bool first = true;
        do
        {
            var xml = await SendXmlAsync(HttpMethod.Get, container, string.Empty,
                "restype=container&comp=list&delimiter=%2F&maxresults=5000&prefix=" + Uri.EscapeDataString(prefix) + Marker(marker), path, cancellationToken).ConfigureAwait(false);
            var page = new List<StorageItem>();
            foreach (var p in xml.Descendants("BlobPrefix"))
            {
                string name = p.Element("Name")!.Value;
                if (name.Length > prefix.Length) page.Add(Folder(container, name));
            }
            foreach (var b in xml.Descendants("Blob"))
            {
                string name = b.Element("Name")!.Value;
                if (name == prefix || name.Length <= prefix.Length) continue;
                page.Add(name.EndsWith('/') ? Folder(container, name) : FileItem(container, name, b.Element("Properties")));
            }
            if (first && page.Count == 0 && prefix.Length > 0 && NextMarker(xml) is null &&
                await HeadAsync(container, prefix, cancellationToken).ConfigureAwait(false) is null)
                throw NotFound(path);
            first = false;
            marker = NextMarker(xml);
            yield return page;
        }
        while (marker is not null);
    }

    private static string Marker(string? marker) => marker is null ? string.Empty : "&marker=" + Uri.EscapeDataString(marker);

    private static string? NextMarker(XDocument xml) => xml.Root?.Element("NextMarker")?.Value is { Length: > 0 } next ? next : null;

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        var (container, blob) = Split(path);
        if (container is null) return null;
        if (blob.Length == 0)
            return _container is null ? new StorageItem { Name = container, Path = Normalize("/" + container), Kind = StorageItemKind.Drive, TypeDescription = "Container" } : null;
        if (await HeadAsync(container, blob, cancellationToken).ConfigureAwait(false) is { } file) return file;
        string prefix = blob.TrimEnd('/') + "/";
        var xml = await SendXmlAsync(HttpMethod.Get, container, string.Empty,
            "restype=container&comp=list&maxresults=1&prefix=" + Uri.EscapeDataString(prefix), path, cancellationToken).ConfigureAwait(false);
        return xml.Descendants("Blob").Any() ? Folder(container, prefix) : null;
    }

    private async Task<StorageItem?> HeadAsync(string container, string blob, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Head, container, blob, null, null, null, PathOf(container, blob), cancellationToken, allowNotFound: true)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        bool folder = blob.EndsWith('/') ||
                      (response.Headers.TryGetValues("x-ms-meta-hdi_isfolder", out var hdi) && hdi.FirstOrDefault() == "true");
        string name = blob.TrimEnd('/');
        return new StorageItem
        {
            Name = name[(name.LastIndexOf('/') + 1)..],
            Path = PathOf(container, blob),
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = folder ? null : response.Content.Headers.ContentLength,
            Modified = response.Content.Headers.LastModified?.ToLocalTime(),
        };
    }

    private StorageItem Folder(string container, string name)
    {
        string trimmed = name.TrimEnd('/');
        return new StorageItem { Name = trimmed[(trimmed.LastIndexOf('/') + 1)..], Path = PathOf(container, trimmed), Kind = StorageItemKind.Folder };
    }

    private StorageItem FileItem(string container, string name, XElement? properties) => new()
    {
        Name = name[(name.LastIndexOf('/') + 1)..],
        Path = PathOf(container, name),
        Kind = StorageItemKind.File,
        Size = long.TryParse(properties?.Element("Content-Length")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) ? size : null,
        Modified = ParseDate(properties?.Element("Last-Modified")?.Value),
        Created = ParseDate(properties?.Element("Creation-Time")?.Value),
    };

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var (container, blob) = Split(path);
        if (container is null) throw RootChange("changed");
        if (blob.Length == 0)
        {
            using var _ = await SendAsync(HttpMethod.Put, container, string.Empty, "restype=container", [], null, path, cancellationToken).ConfigureAwait(false);
            return new StorageItem { Name = name, Path = Normalize("/" + container), Kind = StorageItemKind.Drive, TypeDescription = "Container" };
        }
        if (await GetItemAsync(path, cancellationToken).ConfigureAwait(false) is not null) throw Exists(path);
        using (await SendAsync(HttpMethod.Put, container, blob.TrimEnd('/') + "/", null, [], BlockBlob, path, cancellationToken).ConfigureAwait(false))
        {
        }
        return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.Folder, Modified = DateTimeOffset.Now };
    }

    private static readonly Dictionary<string, string> BlockBlob = new() { ["x-ms-blob-type"] = "BlockBlob" };

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            var (container, blob) = Split(raw);
            if (container is null || (blob.Length == 0 && _container is not null)) throw RootChange("deleted");
            if (blob.Length == 0)
            {
                using var _ = await SendAsync(HttpMethod.Delete, container, string.Empty, "restype=container", null, null, raw, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var item = await GetItemAsync(raw, cancellationToken).ConfigureAwait(false);
            if (item is null) continue;
            if (item.Kind == StorageItemKind.File)
            {
                await DeleteBlobAsync(container, blob, raw, cancellationToken).ConfigureAwait(false);
                continue;
            }
            string prefix = blob.TrimEnd('/') + "/";
            var names = new List<string>();
            await foreach (var name in AllBlobsAsync(container, prefix, cancellationToken).ConfigureAwait(false)) names.Add(name);
            await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                async (name, token) => await DeleteBlobAsync(container, name, raw, token).ConfigureAwait(false)).ConfigureAwait(false);
            if (await HeadAsync(container, prefix, cancellationToken).ConfigureAwait(false) is not null)
                await DeleteBlobAsync(container, prefix, raw, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteBlobAsync(string container, string blob, string path, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Delete, container, blob, null, null, new Dictionary<string, string> { ["x-ms-delete-snapshots"] = "include" },
            path, cancellationToken, allowNotFound: true).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<string> AllBlobsAsync(string container, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? marker = null;
        do
        {
            var xml = await SendXmlAsync(HttpMethod.Get, container, string.Empty,
                "restype=container&comp=list&maxresults=5000&prefix=" + Uri.EscapeDataString(prefix) + Marker(marker), PathOf(container, prefix), cancellationToken)
                .ConfigureAwait(false);
            foreach (var b in xml.Descendants("Blob")) yield return b.Element("Name")!.Value;
            marker = NextMarker(xml);
        }
        while (marker is not null);
    }

    public override async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string target = CombinePath(parentPath, name);
        var (fromContainer, fromBlob) = Split(source.Path);
        var (toContainer, toBlob) = Split(target);
        if (fromContainer is null || toContainer is null || fromBlob.Length == 0 || toBlob.Length == 0) return null;
        if (await GetItemAsync(target, cancellationToken).ConfigureAwait(false) is not null) throw Exists(target);

        if (source.Kind == StorageItemKind.File)
        {
            await CopyBlobAsync(fromContainer, fromBlob, toContainer, toBlob, target, cancellationToken).ConfigureAwait(false);
            return source with { Name = name, Path = PathOf(toContainer, toBlob) };
        }

        string fromPrefix = fromBlob.TrimEnd('/') + "/", toPrefix = toBlob.TrimEnd('/') + "/";
        var names = new List<string>();
        await foreach (var blob in AllBlobsAsync(fromContainer, fromPrefix, cancellationToken).ConfigureAwait(false)) names.Add(blob);
        if (names.Count == 0) names.Add(fromPrefix);
        await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (blob, token) => await CopyBlobAsync(fromContainer, blob, toContainer, toPrefix + blob[fromPrefix.Length..], target, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return new StorageItem { Name = name, Path = PathOf(toContainer, toBlob), Kind = StorageItemKind.Folder };
    }

    public override async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        var copied = await CopyWithinAsync(source, parentPath, name, cancellationToken).ConfigureAwait(false);
        if (copied is null) return null;
        await DeleteAsync([source.Path], permanent: true, cancellationToken).ConfigureAwait(false);
        return copied;
    }

    /// <summary>Copy Blob is asynchronous for large blobs: poll x-ms-copy-status until it settles.</summary>
    private async Task CopyBlobAsync(string fromContainer, string fromBlob, string toContainer, string toBlob, string path, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["x-ms-copy-source"] = Url(fromContainer, fromBlob, null).AbsoluteUri };
        string status;
        using (var response = await SendAsync(HttpMethod.Put, toContainer, toBlob, null, [], headers, path, cancellationToken).ConfigureAwait(false))
            status = response.Headers.TryGetValues("x-ms-copy-status", out var s) ? s.First() : "success";

        var delay = TimeSpan.FromMilliseconds(300);
        while (status == "pending")
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 5000));
            using var head = await SendAsync(HttpMethod.Head, toContainer, toBlob, null, null, null, path, cancellationToken).ConfigureAwait(false);
            status = head.Headers.TryGetValues("x-ms-copy-status", out var s) ? s.First() : "success";
        }
        if (status != "success") throw new StorageException(StorageErrorKind.Unknown, $"Azure couldn't copy {Describe(path)} ({status}).");
    }

    // ------------------------------------------------------------------ content

    public Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        var (container, blob) = Split(file.Path);
        if (container is null || blob.Length == 0) return Task.FromResult<DirectDownload?>(null);
        var url = Url(container, blob, Sas(container, blob, DateTimeOffset.UtcNow.AddHours(6)));
        return Task.FromResult<DirectDownload?>(new DirectDownload(url.AbsoluteUri, new Dictionary<string, string> { ["User-Agent"] = "AkioExplorer" })
        {
            MaxConnections = 8,
        });
    }

    /// <summary>Read-only service SAS for one blob (signed with the account key).</summary>
    internal string Sas(string container, string blob, DateTimeOffset expiry)
    {
        string se = expiry.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string toSign = string.Join('\n',
            "r", "", se, $"/blob/{_accountName}/{container}/{blob}", "", "", "https", ApiVersion, "b", "", "", "", "", "", "", "");
        string sig = Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(toSign)));
        return $"sv={ApiVersion}&sr=b&sp=r&se={Uri.EscapeDataString(se)}&spr=https&sig={Uri.EscapeDataString(sig)}";
    }

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        var (container, blob) = Split(file.Path);
        if (container is null) throw NotFound(file.Path);
        var response = await SendAsync(HttpMethod.Get, container, blob, null, null, null, file.Path, cancellationToken,
            completion: HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var (container, blob) = Split(path);
        if (container is null || blob.Length == 0) throw new StorageException(StorageErrorKind.NotSupported, "Files go inside a container. Open a container first.");
        if (!overwrite && await HeadAsync(container, blob, cancellationToken).ConfigureAwait(false) is not null) throw Exists(path);

        if (length >= 0 && length <= SinglePutLimit)
        {
            var data = new byte[length];
            int read = await content.ReadFullAsync(data, cancellationToken).ConfigureAwait(false);
            using var _ = await SendAsync(HttpMethod.Put, container, blob, null, data.AsSpan(0, read).ToArray(), BlockBlob, path, cancellationToken).ConfigureAwait(false);
            return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = read, Modified = DateTimeOffset.Now };
        }

        // Blocks (uncommitted until Put Block List, so a failed upload leaves nothing visible behind).
        int blockSize = Core.Transfers.UploadChunking.ChunkSize(length, 1024 * 1024, 8 << 20, 64 << 20);
        var ids = new List<string>();
        long total = 0;
        using var slots = new SemaphoreSlim(ParallelBlocks);
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task>();
        try
        {
            for (int index = 0; ; index++)
            {
                await slots.WaitAsync(failed.Token).ConfigureAwait(false);
                var buffer = new byte[blockSize];
                int read = await content.ReadFullAsync(buffer, failed.Token).ConfigureAwait(false);
                if (read == 0 && index > 0)
                {
                    slots.Release();
                    break;
                }
                total += read;
                string id = Convert.ToBase64String(Encoding.ASCII.GetBytes(index.ToString("D6", CultureInfo.InvariantCulture)));
                ids.Add(id);
                byte[] data = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var _ = await SendAsync(HttpMethod.Put, container, blob, "comp=block&blockid=" + Uri.EscapeDataString(id), data, null, path, failed.Token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        failed.Cancel();
                        throw;
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
                running.RemoveAll(t => t.IsCompletedSuccessfully);
                if (read < buffer.Length) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Task.WhenAll(running).ConfigureAwait(false);
            throw;
        }
        await Task.WhenAll(running).ConfigureAwait(false);

        var list = new XElement("BlockList", ids.Select(id => new XElement("Latest", id)));
        using (await SendAsync(HttpMethod.Put, container, blob, "comp=blocklist", Encoding.UTF8.GetBytes(list.ToString(SaveOptions.DisableFormatting)), null, path,
                   cancellationToken).ConfigureAwait(false))
        {
        }
        return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = total, Modified = DateTimeOffset.Now };
    }

    // ------------------------------------------------------------------ HTTP + Shared Key

    private async Task<XDocument> SendXmlAsync(HttpMethod method, string? container, string blob, string? query, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, container, blob, query, null, null, path, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string? container, string blob, string? query, byte[]? body,
        IReadOnlyDictionary<string, string>? headers, string path, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, Url(container, blob, query));
        if (body is not null) request.Content = new ByteArrayContent(body);
        if (headers is not null)
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
        Sign(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;

        string code = response.Headers.TryGetValues("x-ms-error-code", out var c) ? c.First() : string.Empty;
        var status = response.StatusCode;
        response.Dispose();
        throw code switch
        {
            "BlobNotFound" or "ContainerNotFound" or "ResourceNotFound" => NotFound(path),
            "AuthenticationFailed" or "InvalidAuthenticationInfo" => SignInFailed(),
            "AuthorizationFailure" or "AuthorizationPermissionMismatch" => Denied(path),
            "BlobAlreadyExists" or "ContainerAlreadyExists" => Exists(path),
            "ContainerBeingDeleted" => new StorageException(StorageErrorKind.InUse, $"{Describe(path)} is still being deleted. Try again in a minute."),
            "InvalidResourceName" or "OutOfRangeInput" => new StorageException(StorageErrorKind.InvalidName,
                "Container names use 3–63 lowercase letters, numbers and hyphens."),
            "ServerBusy" or "OperationTimedOut" => Busy(),
            _ => (int)status switch
            {
                403 => SignInFailed(),
                404 => NotFound(path),
                409 => Exists(path),
                503 or 500 => Busy(),
                _ => new StorageException(StorageErrorKind.Unknown, $"Azure error ({(int)status}{(code.Length > 0 ? ", " + code : "")}) for {Describe(path)}."),
            },
        };
    }

    internal void Sign(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        string Header(string name) =>
            request.Content?.Headers.TryGetValues(name, out var v) == true ? string.Join(',', v)
            : request.Headers.TryGetValues(name, out var h) ? string.Join(',', h) : string.Empty;

        long length = request.Content?.Headers.ContentLength ?? 0;
        var canonicalHeaders = request.Headers
            .Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: string.Join(',', h.Value).Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .Select(h => $"{h.Name}:{h.Value}\n");

        var resource = new StringBuilder("/").Append(_accountName).Append(uri.AbsolutePath);
        if (uri.Query.Length > 1)
        {
            foreach (var group in uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Split('=', 2))
                         .GroupBy(p => Uri.UnescapeDataString(p[0]).ToLowerInvariant())
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                resource.Append('\n').Append(group.Key).Append(':')
                    .Append(string.Join(',', group.Select(p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty).Order(StringComparer.Ordinal)));
            }
        }

        string toSign = string.Join('\n',
            request.Method.Method,
            Header("Content-Encoding"),
            Header("Content-Language"),
            length > 0 ? length.ToString(CultureInfo.InvariantCulture) : string.Empty,
            Header("Content-MD5"),
            Header("Content-Type"),
            string.Empty, // Date: x-ms-date is used
            Header("If-Modified-Since"),
            Header("If-Match"),
            Header("If-None-Match"),
            Header("If-Unmodified-Since"),
            Header("Range")) + "\n" + string.Concat(canonicalHeaders) + resource;

        string signature = Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(toSign)));
        request.Headers.Authorization = new AuthenticationHeaderValue("SharedKey", $"{_accountName}:{signature}");
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToLocalTime() : null;

    public void Dispose() => _http.Dispose();
}
