using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CG.Web.MegaApiClient;

namespace AkioExplorer.CloudProviders.Mega;

/// <summary>
/// HTTP for MegaApiClient. MEGA answers sign-in requests with a proof-of-work challenge ("hashcash", header
/// <c>X-Hashcash: 1:easiness:timestamp:token</c>). MegaApiClient's own solver is single-threaded and slow (minutes
/// per sign-in in practice); this client solves it on all cores with hardware SHA-256 and resends the request, so
/// MegaApiClient never sees the challenge. Upload parts go through <see cref="MegaUploadPipeline"/> (several at once);
/// everything else is passed to the library's own client.
/// </summary>
internal sealed class MegaWebClient : IWebClient
{
    private const int TokenRepeats = 262_144; // MEGA's buffer: 4-byte prefix + token (48 bytes) × 262144 ≈ 12 MB
    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly CG.Web.MegaApiClient.WebClient _inner = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    // Upload parts: several connections per file, no overall timeout (a slow line may need minutes for a part);
    // MegaUploadPipeline cancels a request that stops making progress instead.
    private static readonly HttpClient UploadHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public int BufferSize
    {
        get => _inner.BufferSize;
        set => _inner.BufferSize = value;
    }

    public string PostRequestJson(Uri url, string jsonData) => PostRequestJson(url, jsonData, null!);

    public string PostRequestJson(Uri url, string jsonData, string hashcash)
    {
        string? answer = string.IsNullOrEmpty(hashcash) ? null : hashcash;
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(jsonData, Encoding.UTF8) };
            request.Content.Headers.ContentType = Json;
            request.Headers.UserAgent.ParseAdd("AkioExplorer");
            if (answer is not null) request.Headers.TryAddWithoutValidation("X-Hashcash", answer);

            using var response = _http.Send(request);
            string body = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
            if (attempt < 3 && response.Headers.TryGetValues("X-Hashcash", out var values) && values.FirstOrDefault() is { } challenge &&
                Solve(challenge) is { } solved)
            {
                answer = solved;
                continue;
            }
            return body;
        }
    }

    /// <summary>Upload parts: handed to the running upload's pipeline (parallel), else sent one by one.</summary>
    public string PostRequestRaw(Uri url, Stream dataStream) =>
        MegaUploadPipeline.Current.Value is { } pipeline ? pipeline.Post(UploadHttp, url, dataStream) : _inner.PostRequestRaw(url, dataStream);

    public Stream PostRequestRawAsStream(Uri url, Stream dataStream) => _inner.PostRequestRawAsStream(url, dataStream);

    public Stream GetRequestRaw(Uri url) => _inner.GetRequestRaw(url);

    /// <summary>"1:easiness:timestamp:token" → "1:token:prefix", or null when the challenge isn't understood.</summary>
    internal static string? Solve(string challenge)
    {
        var parts = challenge.Split(':');
        if (parts.Length != 4 || parts[0] != "1" || !int.TryParse(parts[1], out int easiness) || easiness is < 0 or > 255) return null;
        byte[] token;
        try
        {
            token = Base64UrlDecode(parts[3]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (token.Length != 48) return null;
        byte[] prefix = SolvePrefix(token, easiness);
        return $"1:{parts[3]}:{Base64UrlEncode(prefix)}";
    }

    /// <summary>
    /// Finds 4 bytes P such that the first 32 bits (big-endian) of SHA-256(P ‖ token×262144) are at most
    /// ((easiness &amp; 63) × 2 + 1) &lt;&lt; ((easiness &gt;&gt; 6) × 7 + 3). Each core searches its own share of prefixes.
    /// </summary>
    internal static byte[] SolvePrefix(byte[] token, int easiness)
    {
        uint threshold = (uint)((((easiness & 63) << 1) + 1) << (((easiness >> 6) * 7) + 3));
        int workers = Math.Max(1, Environment.ProcessorCount);
        int found = -1;
        long winner = 0;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            byte[] buffer = new byte[4 + token.Length * TokenRepeats];
            for (int i = 0; i < TokenRepeats; i++) Buffer.BlockCopy(token, 0, buffer, 4 + i * token.Length, token.Length);
            Span<byte> hash = stackalloc byte[32];
            for (uint candidate = (uint)worker; Volatile.Read(ref found) < 0; candidate += (uint)workers)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, candidate);
                SHA256.HashData(buffer, hash);
                if (BinaryPrimitives.ReadUInt32BigEndian(hash) <= threshold && Interlocked.CompareExchange(ref found, worker, -1) < 0)
                {
                    Interlocked.Exchange(ref winner, candidate);
                    break;
                }
            }
        });
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)Interlocked.Read(ref winner));
        return prefix;
    }

    internal static byte[] Base64UrlDecode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    internal static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
