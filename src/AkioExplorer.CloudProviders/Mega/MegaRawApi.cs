using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CG.Web.MegaApiClient;

namespace AkioExplorer.CloudProviders.Mega;

/// <summary>
/// MEGA's own cryptography for node keys and attributes. A node's key is stored AES-ECB encrypted with the account's
/// master key; a file key is 32 bytes (AES key ⊕ nonce/MAC halves), a folder key 16. Attributes ("n" name, "fav",
/// "lbl", "sen", "rr"…) are JSON prefixed with "MEGA", zero-padded and AES-CBC encrypted (zero IV) with the 16-byte
/// attribute key.
/// </summary>
internal static class MegaCrypto
{
    public static byte[] DecryptKey(byte[] encrypted, byte[] masterKey)
    {
        using var aes = Aes.Create();
        aes.Key = masterKey;
        return aes.DecryptEcb(encrypted, PaddingMode.None);
    }

    public static byte[] EncryptKey(byte[] key, byte[] masterKey)
    {
        using var aes = Aes.Create();
        aes.Key = masterKey;
        return aes.EncryptEcb(key, PaddingMode.None);
    }

    /// <summary>The AES key used for attributes and file attributes (thumbnails): file keys fold their halves.</summary>
    public static byte[] AttributeKey(byte[] nodeKey)
    {
        if (nodeKey.Length < 32) return nodeKey[..16];
        var key = new byte[16];
        for (int i = 0; i < 16; i++) key[i] = (byte)(nodeKey[i] ^ nodeKey[i + 16]);
        return key;
    }

    /// <summary>AES-CTR parameters of a file: key, and the 8-byte nonce that starts the counter block.</summary>
    public static (byte[] Key, byte[] Nonce) ContentKey(byte[] fileKey) => (AttributeKey(fileKey), fileKey[16..24]);

    public static JsonObject? DecryptAttributes(string encrypted, byte[] attributeKey)
    {
        try
        {
            byte[] data = MegaWebClient.Base64UrlDecode(encrypted);
            if (data.Length == 0 || data.Length % 16 != 0) return null;
            using var aes = Aes.Create();
            aes.Key = attributeKey;
            string text = Encoding.UTF8.GetString(aes.DecryptCbc(data, new byte[16], PaddingMode.None)).TrimEnd('\0');
            return text.StartsWith("MEGA{", StringComparison.Ordinal) ? JsonNode.Parse(text[4..]) as JsonObject : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    public static string EncryptAttributes(JsonObject attributes, byte[] attributeKey)
    {
        byte[] text = Encoding.UTF8.GetBytes("MEGA" + attributes.ToJsonString());
        var padded = new byte[(text.Length + 15) / 16 * 16];
        text.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = attributeKey;
        return MegaWebClient.Base64UrlEncode(aes.EncryptCbc(padded, new byte[16], PaddingMode.None));
    }

    /// <summary>AES-CBC (zero IV, zero padding) for file attributes – thumbnails and previews.</summary>
    public static byte[] EncryptFileAttribute(byte[] data, byte[] attributeKey)
    {
        var padded = new byte[(data.Length + 15) / 16 * 16];
        data.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = attributeKey;
        return aes.EncryptCbc(padded, new byte[16], PaddingMode.None);
    }

    public static byte[] DecryptFileAttribute(byte[] data, byte[] attributeKey)
    {
        using var aes = Aes.Create();
        aes.Key = attributeKey;
        return aes.DecryptCbc(data[..(data.Length / 16 * 16)], new byte[16], PaddingMode.None);
    }

    /// <summary>Decrypts AES-CTR content in place, <paramref name="offset"/> being the position of data[0] in the file.</summary>
    public static void DecryptContent(Span<byte> data, long offset, byte[] key, byte[] nonce)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        long block = offset / 16;
        int skip = (int)(offset % 16);
        int i = 0;
        // One ECB call over many counter blocks is far faster than one per block.
        const int Batch = 4096; // blocks
        byte[] counters = new byte[Batch * 16];
        byte[] keystream = new byte[Batch * 16];
        while (i < data.Length)
        {
            int needed = (skip + data.Length - i + 15) / 16;
            int blocks = Math.Min(Batch, needed);
            for (int b = 0; b < blocks; b++)
            {
                nonce.CopyTo(counters, b * 16);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counters.AsSpan(b * 16 + 8), block + b);
            }
            aes.EncryptEcb(counters.AsSpan(0, blocks * 16), keystream.AsSpan(0, blocks * 16), PaddingMode.None);
            int available = blocks * 16 - skip;
            int count = Math.Min(available, data.Length - i);
            var ks = keystream.AsSpan(skip, count);
            var target = data.Slice(i, count);
            for (int k = 0; k < count; k++) target[k] ^= ks[k];
            i += count;
            block += blocks;
            skip = 0;
        }
    }
}

/// <summary>A node as MEGA's "f" command returns it, with key and attributes decrypted where the account owns them.</summary>
internal sealed record MegaRawNode(string Handle, string? Parent, int Type, long Size, long Timestamp, byte[]? Key, JsonObject? Attributes,
    string? EncryptedAttributes, string? FileAttributes, string? Owner, string? EncryptedKey)
{
    public const int File = 0, Folder = 1, Root = 2, Inbox = 3, Trash = 4;

    public string? Name => Attributes?["n"]?.GetValue<string>();

    public bool IsFavourite => Attributes?["fav"] is JsonValue v && v.TryGetValue(out int f) && f == 1;

    public int Label => Attributes?["lbl"] is JsonValue v && v.TryGetValue(out int l) ? l : 0;

    public bool IsSensitive => Attributes?["sen"] is JsonValue v && v.TryGetValue(out int s) && s == 1;

    /// <summary>Folder the item was deleted from (Rubbish bin only).</summary>
    public string? RestoreTo => Attributes?["rr"] is JsonValue v && v.TryGetValue(out string? r) ? r : null;

    public byte[]? AttributeKey => Key is null ? null : MegaCrypto.AttributeKey(Key);

    /// <summary>File attribute handle of a type (0 = 120 px thumbnail, 1 = preview up to 1000 px).</summary>
    public string? FileAttribute(int type)
    {
        if (FileAttributes is null) return null;
        foreach (var part in FileAttributes.Split('/'))
        {
            // "123:0*handle" – user/cluster id, type, handle
            int colon = part.IndexOf(':');
            int star = part.IndexOf('*');
            if (colon < 0 || star < colon) continue;
            if (int.TryParse(part.AsSpan(colon + 1, star - colon - 1), out int t) && t == type) return part[(star + 1)..];
        }
        return null;
    }
}

/// <summary>
/// Direct calls to MEGA's JSON API (https://g.api.mega.co.nz/cs) with the stored session, for what MegaApiClient
/// doesn't do: server-side copy, attributes (favourite, label, hidden, restore location), versions, links,
/// thumbnails, fast downloads and transfer quota.
/// </summary>
internal sealed class MegaRawApi
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    private readonly string _sessionId;
    private int _sequence = Random.Shared.Next(1, int.MaxValue / 2);

    public MegaRawApi(string sessionId, byte[] masterKey)
    {
        _sessionId = sessionId;
        MasterKey = masterKey;
    }

    public byte[] MasterKey { get; }

    public static HttpClient Client => Http;

    /// <summary>Sends one command and returns its result (object, array, string or number ≥ 0).</summary>
    public async Task<JsonNode?> RequestAsync(JsonObject command, CancellationToken cancellationToken) =>
        (await RequestManyAsync([command], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Several commands in one round trip; each result in order. A failed command throws.</summary>
    public async Task<IReadOnlyList<JsonNode?>> RequestManyAsync(IReadOnlyList<JsonObject> commands, CancellationToken cancellationToken)
    {
        var body = new JsonArray(commands.Select(c => (JsonNode)c.DeepClone()).ToArray());
        for (int attempt = 0; ; attempt++)
        {
            int id = Interlocked.Increment(ref _sequence);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://g.api.mega.co.nz/cs?id={id}&sid={Uri.EscapeDataString(_sessionId)}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonNode.Parse(text);
            if (parsed is JsonValue whole && whole.TryGetValue(out long code) && code < 0)
            {
                if (IsTransient(code) && attempt < 6)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new MegaApiCodeException((ApiResultCode)code);
            }
            if (parsed is not JsonArray results) throw new IOException("Unexpected answer from MEGA.");
            foreach (var result in results)
            {
                if (result is JsonValue value && value.TryGetValue(out long each) && each < 0)
                {
                    if (IsTransient(each) && attempt < 6 && commands.Count == 1)
                        goto retry;
                    throw new MegaApiCodeException((ApiResultCode)each);
                }
            }
            return results.ToList();
        retry:
            await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(long code) => code is -3 or -4 or -6 or -18;

    /// <summary>Every node of the account (own tree, Rubbish bin, versions), keys and attributes decrypted.</summary>
    public async Task<IReadOnlyList<MegaRawNode>> FetchNodesAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync(new JsonObject { ["a"] = "f", ["c"] = 1, ["r"] = 1 }, cancellationToken).ConfigureAwait(false);
        var list = result?["f"] as JsonArray ?? [];
        string? me = list.FirstOrDefault(n => n?["t"]?.GetValue<int>() == MegaRawNode.Root)?["u"]?.GetValue<string>();
        var nodes = new List<MegaRawNode>(list.Count);
        foreach (var n in list.OfType<JsonObject>())
        {
            string handle = n["h"]?.GetValue<string>() ?? string.Empty;
            int type = n["t"]?.GetValue<int>() ?? 0;
            string? encryptedKey = OwnKey(n["k"]?.GetValue<string>(), me);
            byte[]? key = null;
            JsonObject? attributes = null;
            string? a = n["a"]?.GetValue<string>();
            if (encryptedKey is not null)
            {
                try
                {
                    key = MegaCrypto.DecryptKey(MegaWebClient.Base64UrlDecode(encryptedKey), MasterKey);
                    if (a is not null) attributes = MegaCrypto.DecryptAttributes(a, MegaCrypto.AttributeKey(key));
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    key = null;
                }
            }
            nodes.Add(new MegaRawNode(handle, n["p"]?.GetValue<string>(), type, n["s"]?.GetValue<long>() ?? 0, n["ts"]?.GetValue<long>() ?? 0,
                key, attributes, a, n["fa"]?.GetValue<string>(), n["u"]?.GetValue<string>(), encryptedKey));
        }
        return nodes;
    }

    /// <summary>"user:key/user2:key2" → the key encrypted for this account.</summary>
    private static string? OwnKey(string? keys, string? me)
    {
        if (string.IsNullOrEmpty(keys)) return null;
        foreach (var part in keys.Split('/'))
        {
            int colon = part.IndexOf(':');
            if (colon < 0) continue;
            if (me is null || part.AsSpan(0, colon).SequenceEqual(me)) return part[(colon + 1)..];
        }
        return null;
    }

    /// <summary>Replaces a node's attributes (MEGA stores them as one encrypted blob).</summary>
    public Task SetAttributesAsync(MegaRawNode node, JsonObject attributes, CancellationToken cancellationToken)
    {
        if (node.AttributeKey is not { } key) throw new IOException("This item's key isn't available.");
        return RequestAsync(new JsonObject { ["a"] = "a", ["n"] = node.Handle, ["at"] = MegaCrypto.EncryptAttributes(attributes, key) }, cancellationToken);
    }

    /// <summary>Download URL (range-capable: append "/start-end") and size of a file.</summary>
    public async Task<(string Url, long Size)> DownloadUrlAsync(string handle, CancellationToken cancellationToken)
    {
        var result = await RequestAsync(new JsonObject { ["a"] = "g", ["g"] = 1, ["ssl"] = 2, ["n"] = handle }, cancellationToken).ConfigureAwait(false);
        if (result?["e"] is JsonValue error && error.TryGetValue(out long code) && code < 0) throw new MegaApiCodeException((ApiResultCode)code);
        string url = result?["g"]?.GetValue<string>() ?? throw new IOException("MEGA didn't give a download address.");
        return (url, result?["s"]?.GetValue<long>() ?? 0);
    }
}

/// <summary>An error code from MEGA's API (MegaApiClient's ApiException can't be created outside the library).</summary>
internal sealed class MegaApiCodeException(ApiResultCode code) : Exception($"MEGA answered {code}.")
{
    public ApiResultCode Code { get; } = code;
}
