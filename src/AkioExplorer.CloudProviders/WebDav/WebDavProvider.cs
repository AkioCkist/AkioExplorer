using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.WebDav;

public sealed record WebDavOptions
{
    /// <summary>The server keeps deleted files in its own trash (Nextcloud, ownCloud…).</summary>
    public bool ServerTrash { get; init; }


    /// <summary>Connections per file for segmented downloads (some services limit them per account).</summary>
    public int MaxDownloadConnections { get; init; } = 6;
}

/// <summary>
/// WebDAV (RFC 4918) – Nextcloud, ownCloud, NAS boxes and the many services that speak it. Basic auth is sent up
/// front (one round trip per request); servers that want Digest/NTLM switch to the handler's challenge flow.
/// Listing parses the multistatus response as a stream and hands the UI pages while it is still arriving.
/// </summary>
public sealed class WebDavProvider : RemotePathProvider, IDirectDownloadProvider, IDisposable
{
    private const int PageSize = 400;
    private static readonly XNamespace Dav = "DAV:";

    private static readonly string ListBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:creationdate/>" +
        "</d:prop></d:propfind>";

    private static readonly string QuotaBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:quota-used-bytes/><d:quota-available-bytes/></d:prop></d:propfind>";

    private readonly Uri _base;          // always ends with "/"
    private readonly string _basePath;   // decoded absolute path of _base, without trailing "/"
    private readonly string _user;
    private readonly string _password;
    private readonly WebDavOptions _options;
    private readonly HttpClient _basic;
    private readonly Lazy<HttpClient> _challenge;
    private volatile bool _useChallenge;

    public WebDavProvider(CloudAccount account, CloudProviderType type, string serviceName, string baseUrl, string user, string password,
        WebDavOptions? options = null)
        : base(account, type, serviceName)
    {
        _base = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _basePath = Uri.UnescapeDataString(_base.AbsolutePath).TrimEnd('/');
        _user = user;
        _password = password;
        _options = options ?? new WebDavOptions();
        _basic = new HttpClient(CloudHttp.CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _basic.DefaultRequestHeaders.UserAgent.ParseAdd("AkioExplorer");
        _challenge = new Lazy<HttpClient>(() =>
        {
            var handler = new SocketsHttpHandler
            {
                Credentials = new NetworkCredential(_user, _password),
                PreAuthenticate = true,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = 16,
            };
            var client = new HttpClient(new RetryHandler(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AkioExplorer");
            return client;
        });
    }

    public override StorageCapabilities Capabilities =>
        base.Capabilities | StorageCapabilities.ServerSideCopy | (_options.ServerTrash ? StorageCapabilities.RecycleBin : 0);

    // ------------------------------------------------------------------ addressing

    internal Uri UrlOf(string path, bool folder = false)
    {
        path = Normalize(path);
        var builder = new StringBuilder(_base.AbsoluteUri);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            builder.Append(Uri.EscapeDataString(part)).Append('/');
        if (!folder && path.Length > 0) builder.Length--; // files: no trailing slash
        return new Uri(builder.ToString());
    }

    /// <summary>Maps an href from a multistatus response back to a provider path, or null if it is outside the base.</summary>
    internal string? PathOfHref(string href)
    {
        string absolute = Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath
            : href.Split('?', 2)[0];
        string decoded = Uri.UnescapeDataString(absolute).TrimEnd('/');
        if (!decoded.StartsWith('/')) decoded = "/" + decoded;
        if (decoded.Equals(_basePath, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (!decoded.StartsWith(_basePath + "/", StringComparison.OrdinalIgnoreCase)) return null;
        return Normalize(decoded[_basePath.Length..]);
    }

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        using var response = await SendAsync(() => PropFind(UrlOf(path, folder: true), "1", ListBody), path, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var page = new List<StorageItem>(64);
        bool yielded = false;
        await foreach (var (entryPath, item) in ParseAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (entryPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                if (!item.IsContainer) throw new StorageException(StorageErrorKind.NotFound, $"{Describe(path)} is a file, not a folder.");
                continue;
            }
            if (!IsSameOrDescendant(entryPath, path)) continue;
            page.Add(item);
            if (page.Count >= PageSize)
            {
                yield return page;
                yielded = true;
                page = new List<StorageItem>(PageSize);
            }
        }
        if (page.Count > 0 || !yielded) yield return page;
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        try
        {
            using var response = await SendAsync(() => PropFind(UrlOf(path), "0", ListBody), path, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var (_, item) in ParseAsync(stream, cancellationToken).ConfigureAwait(false))
                return item with { Path = path, Name = ItemName(path) };
            return null;
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    private async IAsyncEnumerable<(string Path, StorageItem Item)> ParseAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "response" || reader.NamespaceURI != "DAV:") continue;
            if (await XNode.ReadFromAsync(reader, cancellationToken).ConfigureAwait(false) is not XElement response) continue;
            if (ToItem(response) is { } entry) yield return entry;
        }
    }

    internal (string Path, StorageItem Item)? ToItem(XElement response)
    {
        string? href = response.Element(Dav + "href")?.Value;
        if (href is null || PathOfHref(href) is not { } path) return null;

        // Several propstat blocks may come back (200 for known props, 404 for unknown ones): merge the 200s.
        var props = response.Elements(Dav + "propstat")
            .Where(ps => ps.Element(Dav + "status")?.Value.Contains(" 200", StringComparison.Ordinal) != false)
            .SelectMany(ps => ps.Element(Dav + "prop")?.Elements() ?? [])
            .ToList();
        XElement? Prop(XName name) => props.FirstOrDefault(p => p.Name == name);

        bool isFolder = Prop(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
        long? size = isFolder ? null
            : long.TryParse(Prop(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length) ? length : 0;

        string name = path.Length == 0 ? DisplayName : ItemName(path);
        return (path, new StorageItem
        {
            Name = name,
            Path = path,
            Kind = isFolder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = size,
            Modified = ParseDate(Prop(Dav + "getlastmodified")?.Value),
            Created = ParseDate(Prop(Dav + "creationdate")?.Value),
            Attributes = name.StartsWith('.') ? StorageItemAttributes.Hidden : StorageItemAttributes.None,
        });
    }

    internal static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // getlastmodified is RFC 1123 ("Tue, 15 Nov 1994 12:45:26 GMT"), creationdate is ISO 8601.
        if (DateTimeOffset.TryParseExact(text.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rfc)) return rfc.ToLocalTime();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any) ? any.ToLocalTime() : null;
    }

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        using (await SendAsync(() => new HttpRequestMessage(new HttpMethod("MKCOL"), UrlOf(path, folder: true)), path, HttpCompletionOption.ResponseContentRead,
                   cancellationToken, mkcol: true).ConfigureAwait(false))
        {
        }
        return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.Folder, Modified = DateTimeOffset.Now };
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Normalize(raw);
            if (path.Length == 0) throw RootChange("deleted");
            var item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false);
            using (await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, UrlOf(path, folder: item?.IsContainer == true)), path,
                       HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
            {
            }
        }
    }

    public override Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken) =>
        TransferWithinAsync("MOVE", source, parentPath, name, cancellationToken);

    public override Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken) =>
        TransferWithinAsync("COPY", source, parentPath, name, cancellationToken);

    private async Task<StorageItem?> TransferWithinAsync(string method, StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string from = Normalize(source.Path), to = CombinePath(parentPath, name);
        bool folder = source.IsContainer;
        using (await SendAsync(() =>
               {
                   var request = new HttpRequestMessage(new HttpMethod(method), UrlOf(from, folder));
                   request.Headers.Add("Destination", UrlOf(to, folder).AbsoluteUri);
                   request.Headers.Add("Overwrite", "F");
                   if (folder) request.Headers.Add("Depth", "infinity");
                   return request;
               }, to, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
        {
        }
        return source with { Name = name, Path = to };
    }

    // ------------------------------------------------------------------ content

    public async Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (_useChallenge) return null; // Digest/NTLM can't be expressed as a fixed header
        return new DirectDownload(UrlOf(file.Path).AbsoluteUri, new Dictionary<string, string>
        {
            ["Authorization"] = BasicHeader().ToString(),
            ["User-Agent"] = "AkioExplorer",
        })
        { MaxConnections = _options.MaxDownloadConnections };
    }

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, UrlOf(path)), path, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var existing = await GetItemAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !overwrite) throw Exists(path);

        try
        {
            // The body is a live stream: it can't be replayed, so the request is sent once (Expect: 100-continue lets
            // the server refuse before the upload starts).
            using var response = await SendAsync(() =>
            {
                var body = new StreamContent(new NonDisposingStream(content), 256 * 1024);
                if (length >= 0) body.Headers.ContentLength = length;
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var request = new HttpRequestMessage(HttpMethod.Put, UrlOf(path)) { Content = body };
                request.Headers.ExpectContinue = true;
                return request;
            }, path, HttpCompletionOption.ResponseContentRead, cancellationToken, replayable: false).ConfigureAwait(false);
        }
        catch when (existing is null)
        {
            // Don't leave half a file behind.
            try
            {
                using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, UrlOf(path)), path, HttpCompletionOption.ResponseContentRead,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }

        return await GetItemAsync(path, cancellationToken).ConfigureAwait(false)
               ?? new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = length, Modified = DateTimeOffset.Now };
    }

    public override async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => PropFind(UrlOf(string.Empty, folder: true), "0", QuotaBody), string.Empty,
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        long? Read(string name) =>
            document.Descendants(Dav + name).Select(e => long.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : (long?)null)
                .FirstOrDefault(v => v is >= 0);
        long? used = Read("quota-used-bytes"), available = Read("quota-available-bytes");
        if (used is null) return null;
        return new StorageQuota(used, available is { } free ? used + free : null);
    }

    // ------------------------------------------------------------------ HTTP

    private static HttpRequestMessage PropFind(Uri url, string depth, string body)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        request.Headers.Add("Depth", depth);
        return request;
    }

    private AuthenticationHeaderValue BasicHeader() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}")));

    /// <summary>Sends, maps failures to <see cref="StorageException"/>s; the caller owns the (successful) response.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, string path, HttpCompletionOption completion,
        CancellationToken cancellationToken, bool replayable = true, bool mkcol = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = build();
            bool challenge = _useChallenge;
            if (!challenge && _user.Length > 0) request.Headers.Authorization = BasicHeader();
            HttpResponseMessage response;
            try
            {
                response = await (challenge ? _challenge.Value : _basic).SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                request.Dispose();
                throw Unreachable(ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                request.Dispose();
                throw Unreachable(ex);
            }

            if (response.IsSuccessStatusCode || (int)response.StatusCode == 207) return response;

            var status = response.StatusCode;
            bool wantsOtherScheme = status == HttpStatusCode.Unauthorized && !challenge &&
                                    response.Headers.WwwAuthenticate.Any(h => !h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase));
            response.Dispose();
            request.Dispose();
            if (wantsOtherScheme && attempt == 0 && replayable)
            {
                _useChallenge = true; // Digest / NTLM: let the handler answer the challenge from now on
                continue;
            }
            throw Map(status, path, mkcol);
        }
    }

    private StorageException Map(HttpStatusCode status, string path, bool mkcol) => (int)status switch
    {
        401 => SignInFailed(),
        // 402 Payment Required: the sign-in worked, but the service won't serve WebDAV on this account's plan.
        402 => new StorageException(StorageErrorKind.NotSupported,
            $"{ServiceName} accepted the sign-in but refused WebDAV access for this account (402 Payment Required). " +
            $"{ServiceName} may only allow WebDAV on a paid plan."),
        403 => Denied(path),
        404 => NotFound(path),
        405 when mkcol => Exists(path),
        409 => new StorageException(StorageErrorKind.NotFound, $"The folder for {Describe(path)} no longer exists in {ServiceName}."),
        412 => Exists(path),
        423 => new StorageException(StorageErrorKind.InUse, $"{Describe(path)} is locked on the server. Try again later."),
        507 => Full(),
        413 => new StorageException(StorageErrorKind.NotSupported, $"{ServiceName} refused {Describe(path)}: the file is larger than the server allows."),
        429 or 503 => Busy(),
        _ => new StorageException(StorageErrorKind.Unknown, $"{ServiceName} error ({(int)status}) for {Describe(path)}."),
    };

    public void Dispose()
    {
        _basic.Dispose();
        if (_challenge.IsValueCreated) _challenge.Value.Dispose();
    }
}

/// <summary>Lets HttpClient dispose a StreamContent without closing the caller's source stream.</summary>
internal sealed class NonDisposingStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
