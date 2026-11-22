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

namespace AkioExplorer.CloudProviders.S3;

public sealed record S3Options
{
    /// <summary>"https://s3.us-east-1.amazonaws.com" style endpoint (no bucket).</summary>
    public required string Endpoint { get; init; }

    public required string Region { get; init; }

    /// <summary>Only this bucket is shown (its contents become the root). Null lists all buckets.</summary>
    public string? Bucket { get; init; }

    /// <summary>endpoint/bucket/key instead of bucket.endpoint/key (MinIO, most self-hosted servers).</summary>
    public bool PathStyle { get; init; }

    /// <summary>AWS: follow buckets to their own region (x-amz-bucket-region).</summary>
    public bool IsAws { get; init; }

    public bool SupportsMultiDelete { get; init; } = true;
}

/// <summary>
/// Amazon S3 and S3-compatible object storage. Paths are "/bucket/folder/file" (or "/folder/file" when the account
/// is pinned to one bucket); folders are key prefixes, empty ones are kept with a "folder/" marker object. Big
/// uploads use multipart with several parts in flight; downloads use pre-signed URLs so the segmented downloader
/// can open several ranges at once.
/// </summary>
public sealed class S3Provider : RemotePathProvider, IDirectDownloadProvider, IDisposable
{
    private const int SinglePutLimit = 16 * 1024 * 1024;
    private const int ParallelParts = 4;
    private const long MaxCopySize = 5L * 1024 * 1024 * 1024;
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly S3Options _options;
    private readonly S3Signer _signer;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly ConcurrentDictionary<string, string> _bucketRegions = new(StringComparer.Ordinal);

    public S3Provider(CloudAccount account, CloudProviderType type, string serviceName, string accessKey, string secretKey, S3Options options)
        : base(account, type, serviceName)
    {
        _options = options;
        _signer = new S3Signer(accessKey, secretKey);
        _endpoint = new Uri(options.Endpoint.TrimEnd('/') + "/");
        _http = new HttpClient(CloudHttp.CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AkioExplorer");
    }

    public override StorageCapabilities Capabilities =>
        base.Capabilities | StorageCapabilities.ServerSideCopy | (_options.Bucket is null ? StorageCapabilities.VirtualRoot : 0);

    // ------------------------------------------------------------------ addressing

    /// <summary>Splits a provider path into bucket and key ("" = bucket root, null bucket = account root).</summary>
    internal (string? Bucket, string Key) Split(string path)
    {
        path = Normalize(path).TrimStart('/');
        if (_options.Bucket is { } fixedBucket) return (fixedBucket, path);
        if (path.Length == 0) return (null, string.Empty);
        int slash = path.IndexOf('/');
        return slash < 0 ? (path, string.Empty) : (path[..slash], path[(slash + 1)..]);
    }

    private string PathOf(string bucket, string key)
    {
        string trimmed = key.TrimEnd('/');
        return _options.Bucket is not null ? Normalize("/" + trimmed) : Normalize($"/{bucket}/{trimmed}");
    }

    private bool VirtualHost(string bucket) =>
        !_options.PathStyle && !bucket.Contains('.') && Uri.CheckHostName(bucket + "." + _endpoint.Host) == UriHostNameType.Dns;

    internal Uri ObjectUrl(string? bucket, string key, string? query = null)
    {
        string encoded = S3Signer.EncodeKey(key);
        string url;
        if (bucket is null) url = _endpoint.AbsoluteUri;
        else if (VirtualHost(bucket))
            url = $"{_endpoint.Scheme}://{bucket}.{_endpoint.Authority}{_endpoint.AbsolutePath}{encoded}";
        else
            url = $"{_endpoint.AbsoluteUri}{S3Signer.Encode(bucket)}/{encoded}";
        return new Uri(query is null ? url : url + "?" + query);
    }

    private string RegionOf(string? bucket) => bucket is not null && _bucketRegions.TryGetValue(bucket, out var region) ? region : _options.Region;

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path);
        if (bucket is null)
        {
            yield return await ListBucketsAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }

        string prefix = key.Length == 0 ? string.Empty : key.TrimEnd('/') + "/";
        string? token = null;
        bool any = false;
        do
        {
            string query = "list-type=2&delimiter=%2F&max-keys=1000&prefix=" + S3Signer.Encode(prefix) +
                           (token is null ? string.Empty : "&continuation-token=" + S3Signer.Encode(token));
            var xml = await SendXmlAsync(HttpMethod.Get, bucket, string.Empty, query, null, path, cancellationToken).ConfigureAwait(false);
            var root = xml.Root!;
            var page = new List<StorageItem>();
            foreach (var common in Elements(root, "CommonPrefixes"))
            {
                string folderKey = Value(common, "Prefix");
                if (folderKey.Length <= prefix.Length) continue;
                page.Add(Folder(bucket, folderKey));
            }
            foreach (var content in Elements(root, "Contents"))
            {
                string objectKey = Value(content, "Key");
                if (objectKey == prefix || objectKey.Length <= prefix.Length) continue; // the folder's own marker
                if (objectKey.EndsWith('/')) { page.Add(Folder(bucket, objectKey)); continue; }
                page.Add(File(bucket, objectKey, content));
            }

            // A folder with no objects at all and no marker doesn't exist.
            if (!any && page.Count == 0 && prefix.Length > 0 && Value(root, "KeyCount") == "0")
            {
                var marker = await HeadAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
                if (marker is null) throw NotFound(path);
            }

            token = Value(root, "IsTruncated") == "true" ? Value(root, "NextContinuationToken") : null;
            if (token is { Length: 0 }) token = null;
            any = true;
            yield return page;
        }
        while (token is not null);
    }

    private async Task<IReadOnlyList<StorageItem>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        var xml = await SendXmlAsync(HttpMethod.Get, null, string.Empty, null, null, string.Empty, cancellationToken).ConfigureAwait(false);
        return xml.Descendants().Where(e => e.Name.LocalName == "Bucket").Select(b => new StorageItem
        {
            Name = Value(b, "Name"),
            Path = Normalize("/" + Value(b, "Name")),
            Kind = StorageItemKind.Drive,
            Created = ParseDate(Value(b, "CreationDate")),
            TypeDescription = "Bucket",
        }).ToList();
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path);
        if (bucket is null) return null;
        if (key.Length == 0)
            return _options.Bucket is null ? new StorageItem { Name = bucket, Path = Normalize("/" + bucket), Kind = StorageItemKind.Drive, TypeDescription = "Bucket" } : null;

        if (await HeadAsync(bucket, key, cancellationToken).ConfigureAwait(false) is { } head) return head;
        // A "folder": a marker object or any object below the prefix.
        string prefix = key.TrimEnd('/') + "/";
        var xml = await SendXmlAsync(HttpMethod.Get, bucket, string.Empty, "list-type=2&max-keys=1&prefix=" + S3Signer.Encode(prefix), null, path,
            cancellationToken).ConfigureAwait(false);
        return Elements(xml.Root!, "Contents").Any() ? Folder(bucket, prefix) : null;
    }

    private async Task<StorageItem?> HeadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Head, bucket, key, null, null, null, PathOf(bucket, key), cancellationToken, allowNotFound: true)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        string name = key.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return new StorageItem
        {
            Name = name,
            Path = PathOf(bucket, key),
            Kind = key.EndsWith('/') ? StorageItemKind.Folder : StorageItemKind.File,
            Size = key.EndsWith('/') ? null : response.Content.Headers.ContentLength,
            Modified = response.Content.Headers.LastModified?.ToLocalTime(),
        };
    }

    private StorageItem Folder(string bucket, string key)
    {
        string trimmed = key.TrimEnd('/');
        return new StorageItem { Name = trimmed[(trimmed.LastIndexOf('/') + 1)..], Path = PathOf(bucket, trimmed), Kind = StorageItemKind.Folder };
    }

    private StorageItem File(string bucket, string key, XElement content) => new()
    {
        Name = key[(key.LastIndexOf('/') + 1)..],
        Path = PathOf(bucket, key),
        Kind = StorageItemKind.File,
        Size = long.TryParse(Value(content, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) ? size : null,
        Modified = ParseDate(Value(content, "LastModified")),
    };

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var (bucket, key) = Split(path);
        if (bucket is null) throw RootChange("changed");
        if (key.Length == 0)
        {
            // New bucket at the account root.
            string body = _options.Region == "us-east-1" || !_options.IsAws
                ? string.Empty
                : $"<CreateBucketConfiguration xmlns=\"{Ns.NamespaceName}\"><LocationConstraint>{_options.Region}</LocationConstraint></CreateBucketConfiguration>";
            using (await SendAsync(HttpMethod.Put, bucket, string.Empty, null, Encoding.UTF8.GetBytes(body), null, path, cancellationToken).ConfigureAwait(false))
            {
            }
            return new StorageItem { Name = name, Path = Normalize("/" + bucket), Kind = StorageItemKind.Drive, TypeDescription = "Bucket" };
        }
        if (await GetItemAsync(path, cancellationToken).ConfigureAwait(false) is not null) throw Exists(path);
        using (await SendAsync(HttpMethod.Put, bucket, key.TrimEnd('/') + "/", null, [], null, path, cancellationToken).ConfigureAwait(false))
        {
        }
        return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.Folder, Modified = DateTimeOffset.Now };
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            var (bucket, key) = Split(raw);
            if (bucket is null || (key.Length == 0 && _options.Bucket is not null)) throw RootChange("deleted");
            var item = await GetItemAsync(raw, cancellationToken).ConfigureAwait(false);
            if (item is null) continue;

            if (item.Kind == StorageItemKind.File)
            {
                using (await SendAsync(HttpMethod.Delete, bucket, key, null, null, null, raw, cancellationToken).ConfigureAwait(false))
                {
                }
                continue;
            }

            string prefix = key.Length == 0 ? string.Empty : key.TrimEnd('/') + "/";
            var keys = new List<string>();
            await foreach (var objectKey in AllKeysAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
            {
                keys.Add(objectKey);
                if (keys.Count == 1000)
                {
                    await DeleteKeysAsync(bucket, keys, raw, cancellationToken).ConfigureAwait(false);
                    keys.Clear();
                }
            }
            if (prefix.Length > 0 && !keys.Contains(prefix)) keys.Add(prefix); // marker, if any
            await DeleteKeysAsync(bucket, keys, raw, cancellationToken).ConfigureAwait(false);
            if (key.Length == 0)
            {
                using (await SendAsync(HttpMethod.Delete, bucket, string.Empty, null, null, null, raw, cancellationToken).ConfigureAwait(false))
                {
                }
            }
        }
    }

    private async IAsyncEnumerable<string> AllKeysAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? token = null;
        do
        {
            string query = "list-type=2&max-keys=1000&prefix=" + S3Signer.Encode(prefix) + (token is null ? string.Empty : "&continuation-token=" + S3Signer.Encode(token));
            var xml = await SendXmlAsync(HttpMethod.Get, bucket, string.Empty, query, null, PathOf(bucket, prefix), cancellationToken).ConfigureAwait(false);
            foreach (var content in Elements(xml.Root!, "Contents")) yield return Value(content, "Key");
            token = Value(xml.Root!, "IsTruncated") == "true" ? Value(xml.Root!, "NextContinuationToken") : null;
            if (token is { Length: 0 }) token = null;
        }
        while (token is not null);
    }

    private async Task DeleteKeysAsync(string bucket, List<string> keys, string path, CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return;
        if (_options.SupportsMultiDelete)
        {
            var body = new XElement("Delete", new XElement("Quiet", "true"), keys.Select(k => new XElement("Object", new XElement("Key", k))));
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(SaveOptions.DisableFormatting));
            try
            {
                using var _ = await SendAsync(HttpMethod.Post, bucket, string.Empty, "delete=", bytes, null, path, cancellationToken, md5: true).ConfigureAwait(false);
                return;
            }
            catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotSupported)
            {
            }
        }
        foreach (var key in keys)
        {
            using var _ = await SendAsync(HttpMethod.Delete, bucket, key, null, null, null, path, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        var (fromBucket, fromKey) = Split(source.Path);
        var (toBucket, toKey) = Split(CombinePath(parentPath, name));
        if (fromBucket is null || toBucket is null || fromKey.Length == 0 || toKey.Length == 0) return null;
        if (await GetItemAsync(CombinePath(parentPath, name), cancellationToken).ConfigureAwait(false) is not null) throw Exists(CombinePath(parentPath, name));

        if (source.Kind == StorageItemKind.File)
        {
            if (source.Size > MaxCopySize) return null; // the transfer engine streams it instead
            await CopyObjectAsync(fromBucket, fromKey, toBucket, toKey, cancellationToken).ConfigureAwait(false);
            return source with { Name = name, Path = PathOf(toBucket, toKey) };
        }

        string fromPrefix = fromKey.TrimEnd('/') + "/", toPrefix = toKey.TrimEnd('/') + "/";
        bool copiedAny = false;
        await foreach (var key in AllKeysAsync(fromBucket, fromPrefix, cancellationToken).ConfigureAwait(false))
        {
            await CopyObjectAsync(fromBucket, key, toBucket, toPrefix + key[fromPrefix.Length..], cancellationToken).ConfigureAwait(false);
            copiedAny = true;
        }
        if (!copiedAny)
        {
            using var _ = await SendAsync(HttpMethod.Put, toBucket, toPrefix, null, [], null, CombinePath(parentPath, name), cancellationToken).ConfigureAwait(false);
        }
        return new StorageItem { Name = name, Path = PathOf(toBucket, toKey), Kind = StorageItemKind.Folder };
    }

    /// <summary>S3 has no move: copy, then delete the original.</summary>
    public override async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        var copied = await CopyWithinAsync(source, parentPath, name, cancellationToken).ConfigureAwait(false);
        if (copied is null) return null;
        await DeleteAsync([source.Path], permanent: true, cancellationToken).ConfigureAwait(false);
        return copied;
    }

    private async Task CopyObjectAsync(string fromBucket, string fromKey, string toBucket, string toKey, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["x-amz-copy-source"] = "/" + S3Signer.Encode(fromBucket) + "/" + S3Signer.EncodeKey(fromKey) };
        using var response = await SendAsync(HttpMethod.Put, toBucket, toKey, null, [], headers, PathOf(toBucket, toKey), cancellationToken).ConfigureAwait(false);
        // CopyObject can answer 200 with an <Error> body.
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (text.Contains("<Error>", StringComparison.Ordinal)) throw MapError(HttpStatusCode.InternalServerError, text, PathOf(toBucket, toKey));
    }

    // ------------------------------------------------------------------ content

    public async Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(file.Path);
        if (bucket is null || key.Length == 0) return null;
        await Task.CompletedTask.ConfigureAwait(false);
        var url = _signer.Presign(ObjectUrl(bucket, key), RegionOf(bucket), TimeSpan.FromHours(6), DateTimeOffset.UtcNow);
        return new DirectDownload(url.AbsoluteUri, new Dictionary<string, string> { ["User-Agent"] = "AkioExplorer" }) { MaxConnections = 8 };
    }

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(file.Path);
        if (bucket is null) throw NotFound(file.Path);
        var response = await SendAsync(HttpMethod.Get, bucket, key, null, null, null, file.Path, cancellationToken, completion: HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var (bucket, key) = Split(path);
        if (bucket is null || key.Length == 0) throw new StorageException(StorageErrorKind.NotSupported, "Files go inside a bucket. Open a bucket first.");
        if (!overwrite && await HeadAsync(bucket, key, cancellationToken).ConfigureAwait(false) is not null) throw Exists(path);

        if (length >= 0 && length <= SinglePutLimit)
        {
            var data = new byte[length];
            int read = await content.ReadFullAsync(data, cancellationToken).ConfigureAwait(false);
            using var _ = await SendAsync(HttpMethod.Put, bucket, key, null, data.AsMemory(0, read).ToArray(), null, path, cancellationToken).ConfigureAwait(false);
            return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = read, Modified = DateTimeOffset.Now };
        }

        long total = await MultipartUploadAsync(bucket, key, content, length, path, cancellationToken).ConfigureAwait(false);
        return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = total, Modified = DateTimeOffset.Now };
    }

    internal static int PartSize(long length)
    {
        const int mb = 1024 * 1024;
        int size = Core.Transfers.UploadChunking.ChunkSize(length, mb, 8 * mb, 64 * mb);
        if (length > 0 && length / size >= 10_000) size = (int)Math.Min(int.MaxValue / 2, (length / 9_999 + mb - 1) / mb * mb);
        return size;
    }

    private async Task<long> MultipartUploadAsync(string bucket, string key, Stream content, long length, string path, CancellationToken cancellationToken)
    {
        var created = await SendXmlAsync(HttpMethod.Post, bucket, key, "uploads=", [], path, cancellationToken).ConfigureAwait(false);
        string uploadId = Value(created.Root!, "UploadId");
        var etags = new ConcurrentDictionary<int, string>();
        long total = 0;
        try
        {
            int partSize = PartSize(length);
            using var slots = new SemaphoreSlim(ParallelParts);
            var running = new List<Task>();
            using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
            for (int number = 1; ; number++)
            {
                await slots.WaitAsync(failed.Token).ConfigureAwait(false);
                var buffer = new byte[partSize];
                int read = await content.ReadFullAsync(buffer, failed.Token).ConfigureAwait(false);
                if (read == 0 && number > 1)
                {
                    slots.Release();
                    break;
                }
                total += read;
                int part = number;
                byte[] data = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var response = await SendAsync(HttpMethod.Put, bucket, key,
                            $"partNumber={part}&uploadId={S3Signer.Encode(uploadId)}", data, null, path, failed.Token).ConfigureAwait(false);
                        etags[part] = response.Headers.ETag?.Tag ?? (response.Headers.TryGetValues("ETag", out var e) ? e.First() : string.Empty);
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
                // A part failed and cancelled the others: report that failure, not the cancellation.
                await Task.WhenAll(running).ConfigureAwait(false);
                throw;
            }
            await Task.WhenAll(running).ConfigureAwait(false);

            var body = new XElement("CompleteMultipartUpload",
                etags.OrderBy(e => e.Key).Select(e => new XElement("Part", new XElement("PartNumber", e.Key), new XElement("ETag", e.Value))));
            using var done = await SendAsync(HttpMethod.Post, bucket, key, "uploadId=" + S3Signer.Encode(uploadId),
                Encoding.UTF8.GetBytes(body.ToString(SaveOptions.DisableFormatting)), null, path, cancellationToken).ConfigureAwait(false);
            string text = await done.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Contains("<Error>", StringComparison.Ordinal)) throw MapError(HttpStatusCode.InternalServerError, text, path);
            return total;
        }
        catch
        {
            try
            {
                using var _ = await SendAsync(HttpMethod.Delete, bucket, key, "uploadId=" + S3Signer.Encode(uploadId), null, null, path, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<XDocument> SendXmlAsync(HttpMethod method, string? bucket, string key, string? query, byte[]? body, string path,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, bucket, key, query, body, null, path, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string? bucket, string key, string? query, byte[]? body,
        IReadOnlyDictionary<string, string>? headers, string path, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool allowNotFound = false, bool md5 = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, ObjectUrl(bucket, key, query));
            string hash = S3Signer.EmptyHash;
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                hash = S3Signer.Sha256Hex(body);
                if (md5) request.Content.Headers.ContentMD5 = MD5.HashData(body);
            }
            if (headers is not null)
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            _signer.Sign(request, RegionOf(bucket), hash, DateTimeOffset.UtcNow);

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

            string text = method == HttpMethod.Head ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // AWS: the bucket lives in another region. Learn it and sign again.
            string? region = response.Headers.TryGetValues("x-amz-bucket-region", out var values) ? values.FirstOrDefault() : null;
            region ??= TryXml(text, "Region");
            response.Dispose();
            if (bucket is not null && region is { Length: > 0 } && region != RegionOf(bucket) && attempt == 0 &&
                (int)response.StatusCode is 301 or 307 or 400 or 403)
            {
                _bucketRegions[bucket] = region;
                continue;
            }
            throw MapError(response.StatusCode, text, path);
        }
    }

    internal StorageException MapError(HttpStatusCode status, string body, string path)
    {
        string code = TryXml(body, "Code") ?? string.Empty;
        string message = TryXml(body, "Message") ?? string.Empty;
        return code switch
        {
            "NoSuchKey" or "NoSuchBucket" or "NoSuchUpload" => NotFound(path),
            "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "ExpiredToken" => SignInFailed(),
            "AccessDenied" or "AllAccessDisabled" => Denied(path),
            "BucketAlreadyExists" or "BucketAlreadyOwnedByYou" => Exists(path),
            "InvalidBucketName" => new StorageException(StorageErrorKind.InvalidName,
                "Bucket names use 3–63 lowercase letters, numbers, dots and hyphens, and must be unique across the service."),
            "SlowDown" or "ServiceUnavailable" or "RequestTimeout" => Busy(),
            "NotImplemented" => new StorageException(StorageErrorKind.NotSupported, $"{ServiceName} doesn't support this: {message}"),
            "QuotaExceeded" or "StorageQuotaExceeded" => Full(),
            _ => (int)status switch
            {
                401 => SignInFailed(),
                403 => Denied(path),
                404 => NotFound(path),
                501 => new StorageException(StorageErrorKind.NotSupported, $"{ServiceName} doesn't support this request."),
                503 or 429 => Busy(),
                _ => new StorageException(StorageErrorKind.Unknown, $"{ServiceName} error ({(int)status}{(code.Length > 0 ? ", " + code : "")}): {message}"),
            },
        };
    }

    private static string? TryXml(string body, string element)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('<')) return null;
        try
        {
            return XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == element)?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static IEnumerable<XElement> Elements(XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);

    private static string Value(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? string.Empty;

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToLocalTime() : null;

    public void Dispose() => _http.Dispose();
}
