using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AkioExplorer.CloudProviders.S3;

/// <summary>
/// AWS Signature Version 4 for S3 (header and pre-signed URL forms). S3 paths are encoded once (no double
/// encoding), query parameters are sorted by encoded name.
/// </summary>
public sealed class S3Signer
{
    public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    private const string Algorithm = "AWS4-HMAC-SHA256";

    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _service;

    public S3Signer(string accessKey, string secretKey, string service = "s3")
    {
        _accessKey = accessKey;
        _secretKey = secretKey;
        _service = service;
    }

    /// <summary>Adds x-amz-date, x-amz-content-sha256 and Authorization to <paramref name="request"/>.</summary>
    public void Sign(HttpRequestMessage request, string region, string payloadHash, DateTimeOffset now)
    {
        var uri = request.RequestUri!;
        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = amzDate[..8];

        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = HostHeader(uri),
        };
        foreach (var header in request.Headers)
        {
            string name = header.Key.ToLowerInvariant();
            if (name.StartsWith("x-amz-", StringComparison.Ordinal) || name is "content-md5" or "range")
                headers[name] = string.Join(',', header.Value.Select(v => v.Trim()));
        }
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                string name = header.Key.ToLowerInvariant();
                if (name is "content-type" or "content-md5" || name.StartsWith("x-amz-", StringComparison.Ordinal))
                    headers[name] = string.Join(',', header.Value.Select(v => v.Trim()));
            }
        }

        string signedHeaders = string.Join(';', headers.Keys);
        string canonical = string.Join('\n',
            request.Method.Method,
            CanonicalPath(uri),
            CanonicalQuery(uri.Query),
            string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n")),
            signedHeaders,
            payloadHash);

        string scope = $"{date}/{region}/{_service}/aws4_request";
        string toSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        string signature = Hex(HMACSHA256.HashData(SigningKey(date, region), Encoding.UTF8.GetBytes(toSign)));

        request.Headers.TryAddWithoutValidation("Authorization",
            $"{Algorithm} Credential={_accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>A GET URL that works without headers for <paramref name="expires"/> (max 7 days); Range requests allowed.</summary>
    public Uri Presign(Uri url, string region, TimeSpan expires, DateTimeOffset now)
    {
        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = amzDate[..8];
        string scope = $"{date}/{region}/{_service}/aws4_request";

        string extra = string.Join('&',
            $"X-Amz-Algorithm={Algorithm}",
            $"X-Amz-Credential={Encode(_accessKey + "/" + scope)}",
            $"X-Amz-Date={amzDate}",
            $"X-Amz-Expires={(int)expires.TotalSeconds}",
            "X-Amz-SignedHeaders=host");
        string query = url.Query.Length > 1 ? url.Query[1..] + "&" + extra : extra;
        var unsigned = new UriBuilder(url) { Query = query }.Uri;

        string canonical = string.Join('\n',
            "GET",
            CanonicalPath(unsigned),
            CanonicalQuery(unsigned.Query),
            $"host:{HostHeader(unsigned)}\n",
            "host",
            UnsignedPayload);
        string toSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        string signature = Hex(HMACSHA256.HashData(SigningKey(date, region), Encoding.UTF8.GetBytes(toSign)));
        return new UriBuilder(unsigned) { Query = query + "&X-Amz-Signature=" + signature }.Uri;
    }

    private byte[] SigningKey(string date, string region)
    {
        byte[] key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + _secretKey), Encoding.UTF8.GetBytes(date));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(region));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(_service));
        return HMACSHA256.HashData(key, "aws4_request"u8.ToArray());
    }

    private static string HostHeader(Uri uri) => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    /// <summary>The path as sent (already percent-encoded by <see cref="EncodeKey"/>); "/" when empty.</summary>
    internal static string CanonicalPath(Uri uri)
    {
        string path = uri.AbsolutePath;
        return path.Length == 0 ? "/" : path;
    }

    internal static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;
        return string.Join('&', query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                int eq = part.IndexOf('=');
                string key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
                string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
                return (Key: Encode(key), Value: Encode(value));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>RFC 3986 unreserved characters stay, everything else is %XX (upper case), as SigV4 requires.</summary>
    public static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Object key for a URL path: each segment encoded, "/" kept.</summary>
    public static string EncodeKey(string key) => string.Join('/', key.Split('/').Select(Encode));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Hex(SHA256.HashData(data));
}
