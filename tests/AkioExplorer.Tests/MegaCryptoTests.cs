using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AkioExplorer.CloudProviders.Mega;
using CG.Web.MegaApiClient;

namespace AkioExplorer.Tests;

/// <summary>Our MEGA crypto checked against MegaApiClient's own (internal) implementation, which talks to MEGA daily.</summary>
public class MegaCryptoTests
{
    private static readonly Assembly Library = typeof(MegaApiClient).Assembly;

    [Fact]
    public void Content_decryption_matches_the_library_at_any_offset()
    {
        var plain = new byte[300_017];
        Random.Shared.NextBytes(plain);
        var crypterType = Library.GetType("CG.Web.MegaApiClient.MegaAesCtrStreamCrypter", throwOnError: true)!;
        using var crypter = (Stream)Activator.CreateInstance(crypterType, new MemoryStream(plain))!;
        var cipher = new byte[plain.Length];
        int read = 0;
        while (read < cipher.Length) read += crypter.Read(cipher, read, cipher.Length - read);
        byte[] fileKey = (byte[])crypterType.GetProperty("FileKey")!.GetValue(crypter)!;
        byte[] iv = (byte[])crypterType.GetProperty("Iv")!.GetValue(crypter)!;

        // The 32-byte node key MEGA stores: halves XOR to the AES key, bytes 16..24 are the nonce.
        var nodeKey = new byte[32];
        for (int i = 0; i < 8; i++) { nodeKey[i] = (byte)(fileKey[i] ^ iv[i]); nodeKey[i + 16] = iv[i]; }
        for (int i = 8; i < 16; i++) { nodeKey[i] = fileKey[i]; nodeKey[i + 16] = 0; }
        var (key, nonce) = MegaCrypto.ContentKey(nodeKey);
        Assert.Equal(fileKey, key);

        var whole = (byte[])cipher.Clone();
        MegaCrypto.DecryptContent(whole, 0, key, nonce);
        Assert.Equal(plain, whole);

        // Ranges that start mid-block, as parallel downloads do.
        foreach (int start in new[] { 37, 65_536, 131_075, 299_990 })
        {
            var part = cipher[start..];
            MegaCrypto.DecryptContent(part, start, key, nonce);
            Assert.Equal(plain[start..], part);
        }
    }

    [Fact]
    public void Attributes_round_trip_and_the_library_reads_ours()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        var attributes = new JsonObject { ["n"] = "Ảnh hè 2026.jpg", ["fav"] = 1, ["lbl"] = 3 };
        string encrypted = MegaCrypto.EncryptAttributes(attributes, key);

        var back = MegaCrypto.DecryptAttributes(encrypted, key)!;
        Assert.Equal("Ảnh hè 2026.jpg", back["n"]!.GetValue<string>());
        Assert.Equal(3, back["lbl"]!.GetValue<int>());

        var crypto = Library.GetType("CG.Web.MegaApiClient.Cryptography.Crypto", throwOnError: true)!;
        object theirs = crypto.GetMethod("DecryptAttributes")!.Invoke(null, [MegaWebClient.Base64UrlDecode(encrypted), key])!;
        Assert.Equal("Ảnh hè 2026.jpg", theirs.GetType().GetProperty("Name")!.GetValue(theirs));
    }

    [Fact]
    public void Node_keys_match_the_library()
    {
        byte[] master = RandomNumberGenerator.GetBytes(16);
        byte[] nodeKey = RandomNumberGenerator.GetBytes(32);
        var crypto = Library.GetType("CG.Web.MegaApiClient.Cryptography.Crypto", throwOnError: true)!;
        byte[] theirs = (byte[])crypto.GetMethod("EncryptKey")!.Invoke(null, [nodeKey, master])!;
        Assert.Equal(theirs, MegaCrypto.EncryptKey(nodeKey, master));
        Assert.Equal(nodeKey, MegaCrypto.DecryptKey(theirs, master));
    }

    [Fact]
    public void File_attribute_handles_are_found_by_type()
    {
        var node = new MegaRawNode("h", null, 0, 1, 0, null, null, null, "924:0*thumbHandle/924:1*previewHandle", null, null);
        Assert.Equal("thumbHandle", node.FileAttribute(0));
        Assert.Equal("previewHandle", node.FileAttribute(1));
        Assert.Null(node.FileAttribute(8));
    }
}

public class MegaDownloadTests
{
    private static readonly System.Reflection.Assembly Library = typeof(MegaApiClient).Assembly;

    /// <summary>Encrypts like MEGA (through MegaApiClient's own crypter) and returns ciphertext + the 32-byte node key.</summary>
    private static (byte[] Cipher, byte[] NodeKey) Encrypt(byte[] plain)
    {
        var type = Library.GetType("CG.Web.MegaApiClient.MegaAesCtrStreamCrypter", throwOnError: true)!;
        using var crypter = (Stream)Activator.CreateInstance(type, new MemoryStream(plain))!;
        var cipher = new byte[plain.Length];
        int read = 0;
        while (read < cipher.Length) read += crypter.Read(cipher, read, cipher.Length - read);
        byte[] fileKey = (byte[])type.GetProperty("FileKey")!.GetValue(crypter)!;
        byte[] iv = (byte[])type.GetProperty("Iv")!.GetValue(crypter)!;
        byte[] metaMac = (byte[])type.GetProperty("MetaMac")!.GetValue(crypter)!;
        var key = new byte[32];
        for (int i = 0; i < 8; i++) { key[i] = (byte)(fileKey[i] ^ iv[i]); key[i + 16] = iv[i]; }
        for (int i = 8; i < 16; i++) { key[i] = (byte)(fileKey[i] ^ metaMac[i - 8]); key[i + 16] = metaMac[i - 8]; }
        return (cipher, key);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(131_072)]
    [InlineData(5_000_017)]
    public void Mac_matches_the_library(int size)
    {
        var plain = new byte[size];
        Random.Shared.NextBytes(plain);
        var (_, key) = Encrypt(plain);
        var (aesKey, nonce) = MegaCrypto.ContentKey(key);
        using var mac = new MegaMac(aesKey, nonce);
        foreach (var piece in plain.Chunk(77_777)) mac.Append(piece); // odd piece sizes
        Assert.Equal(key[24..32], mac.Finish());
    }

    [Fact]
    public async Task Parallel_download_decrypts_in_order_and_checks_the_mac()
    {
        var plain = new byte[MegaDownloadStream.PartSize * 2 + 123_457];
        Random.Shared.NextBytes(plain);
        var (cipher, key) = Encrypt(plain);
        var server = new RangeServer(cipher);
        using var http = new HttpClient(server);
        await using var stream = new MegaDownloadStream(http, "https://dl.example/abc", cipher.Length, key, _ => Task.FromResult("https://dl.example/abc"), CancellationToken.None);

        var output = new MemoryStream();
        await stream.CopyToAsync(output, 100_003);
        Assert.Equal(plain, output.ToArray());
        Assert.True(server.MaxConcurrent >= 2, $"max concurrent {server.MaxConcurrent}");
    }

    [Fact]
    public async Task Damaged_download_fails_the_mac_check()
    {
        var plain = new byte[300_000];
        Random.Shared.NextBytes(plain);
        var (cipher, key) = Encrypt(plain);
        cipher[150_000] ^= 0xFF;
        using var http = new HttpClient(new RangeServer(cipher));
        await using var stream = new MegaDownloadStream(http, "https://dl.example/abc", cipher.Length, key, _ => Task.FromResult("x"), CancellationToken.None);
        await Assert.ThrowsAsync<AkioExplorer.Core.Models.StorageException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Theory]
    [InlineData("https://mega.nz/file/AbCdEfGh#" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("https://mega.nz/folder/AbCdEfGh#AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("https://mega.nz/#!AbCdEfGh!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("https://mega.nz/#F!AbCdEfGh!AAAAAAAAAAAAAAAAAAAAAA", true)]
    public void Parses_public_links(string text, bool folder)
    {
        Assert.True(MegaPublicLink.TryParse(text, out var link));
        Assert.Equal("AbCdEfGh", link.Handle);
        Assert.Equal(folder, link.IsFolder);
        Assert.Equal(folder ? 16 : 32, link.Key.Length);
    }

    [Theory]
    [InlineData("https://example.com/file/AbCd#AAAA")]
    [InlineData("https://mega.nz/file/AbCdEfGh")]
    [InlineData("not a link")]
    public void Rejects_other_links(string text) => Assert.False(MegaPublicLink.TryParse(text, out _));

    private sealed class RangeServer(byte[] data) : HttpMessageHandler
    {
        private int _concurrent;
        public int MaxConcurrent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _concurrent);
            int seen;
            while ((seen = Volatile.Read(ref MaxConcurrent)) < now && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen) { }
            try
            {
                await Task.Delay(50, cancellationToken);
                var range = request.RequestUri!.AbsolutePath.Split('/')[^1].Split('-');
                int start = int.Parse(range[0]), end = int.Parse(range[1]);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data[start..(end + 1)]) };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
