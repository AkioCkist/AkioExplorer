using System.Xml.Linq;
using AkioExplorer.CloudProviders;
using AkioExplorer.CloudProviders.Forms;
using AkioExplorer.CloudProviders.Ftp;
using AkioExplorer.CloudProviders.Mega;
using AkioExplorer.CloudProviders.S3;
using AkioExplorer.CloudProviders.WebDav;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Tests;

public class S3SignerTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset Date = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Signs_get_object_like_the_aws_documentation_example()
    {
        // "Example: GET Object" from the AWS SigV4 header-signing documentation.
        var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/test.txt");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        new S3Signer(AccessKey, SecretKey).Sign(request, "us-east-1", S3Signer.EmptyHash, Date);

        string auth = string.Join(' ', request.Headers.GetValues("Authorization"));
        Assert.Contains("Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request", auth);
        Assert.Contains("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", auth);
        Assert.EndsWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", auth);
    }

    [Fact]
    public void Presigns_like_the_aws_documentation_example()
    {
        var url = new S3Signer(AccessKey, SecretKey).Presign(new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), "us-east-1",
            TimeSpan.FromSeconds(86400), Date);
        Assert.EndsWith("X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", url.Query);
    }

    [Theory]
    [InlineData("a b/c+d.txt", "a%20b/c%2Bd.txt")]
    [InlineData("ünï/~x_y-z.png", "%C3%BCn%C3%AF/~x_y-z.png")]
    public void Encodes_keys_per_segment(string key, string expected) => Assert.Equal(expected, S3Signer.EncodeKey(key));

    [Fact]
    public void Canonical_query_is_sorted_and_reencoded() =>
        Assert.Equal("delimiter=%2F&list-type=2&prefix=a%20b%2F", S3Signer.CanonicalQuery("?prefix=a%20b%2F&list-type=2&delimiter=%2F"));
}

public class S3ProviderTests
{
    private static S3Provider Create(string? bucket = null, bool pathStyle = false) =>
        new(new CloudAccount { DisplayName = "S3" }, CloudProviderType.AmazonS3, "Amazon S3", "k", "s",
            new S3Options { Endpoint = "https://s3.eu-west-1.amazonaws.com", Region = "eu-west-1", Bucket = bucket, PathStyle = pathStyle });

    [Fact]
    public void Splits_paths_into_bucket_and_key()
    {
        var all = Create();
        Assert.Equal((null, ""), all.Split(""));
        Assert.Equal(("photos", ""), all.Split("/photos"));
        Assert.Equal(("photos", "2024/a.jpg"), all.Split("/photos/2024/a.jpg"));

        var pinned = Create(bucket: "photos");
        Assert.Equal(("photos", "2024/a.jpg"), pinned.Split("/2024/a.jpg"));
        Assert.Equal(("photos", ""), pinned.Split(""));
    }

    [Fact]
    public void Uses_virtual_hosts_unless_the_bucket_name_has_dots()
    {
        var provider = Create();
        Assert.Equal("https://photos.s3.eu-west-1.amazonaws.com/a%20b.jpg", provider.ObjectUrl("photos", "a b.jpg").AbsoluteUri);
        Assert.Equal("https://s3.eu-west-1.amazonaws.com/my.site/a.jpg", provider.ObjectUrl("my.site", "a.jpg").AbsoluteUri);
        Assert.Equal("https://s3.eu-west-1.amazonaws.com/photos/a.jpg", Create(pathStyle: true).ObjectUrl("photos", "a.jpg").AbsoluteUri);
    }

    [Fact]
    public void Part_size_keeps_uploads_under_ten_thousand_parts()
    {
        long fiveTerabytes = 5L * 1024 * 1024 * 1024 * 1024;
        Assert.True(fiveTerabytes / S3Provider.PartSize(fiveTerabytes) < 10_000);
        Assert.Equal(8 * 1024 * 1024, S3Provider.PartSize(20 * 1024 * 1024));
    }
}

public class WebDavTests
{
    private static WebDavProvider Create(string url = "https://cloud.example.com/remote.php/dav/files/ann%20b/") =>
        new(new CloudAccount { DisplayName = "Nextcloud" }, CloudProviderType.Nextcloud, "Nextcloud", url, "ann b", "pw");

    [Theory]
    [InlineData("/remote.php/dav/files/ann%20b/", "")]
    [InlineData("/remote.php/dav/files/ann%20b/Photos/", "/Photos")]
    [InlineData("/remote.php/dav/files/ann%20b/Photos/a%20%23b.jpg", "/Photos/a #b.jpg")]
    [InlineData("https://cloud.example.com/remote.php/dav/files/ann%20b/Docs", "/Docs")]
    [InlineData("/remote.php/dav/files/other/Docs", null)]
    public void Maps_hrefs_back_to_paths(string href, string? expected) => Assert.Equal(expected, Create().PathOfHref(href));

    [Fact]
    public void Builds_escaped_urls()
    {
        var provider = Create();
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/ann%20b/Photos/a%20%23b.jpg", provider.UrlOf("/Photos/a #b.jpg").AbsoluteUri);
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/ann%20b/Photos/", provider.UrlOf("/Photos", folder: true).AbsoluteUri);
    }

    [Fact]
    public void Parses_multistatus_entries()
    {
        var response = XElement.Parse("""
            <d:response xmlns:d="DAV:">
              <d:href>/remote.php/dav/files/ann%20b/Photos/a.jpg</d:href>
              <d:propstat><d:prop>
                <d:resourcetype/><d:getcontentlength>1234</d:getcontentlength>
                <d:getlastmodified>Tue, 15 Nov 1994 12:45:26 GMT</d:getlastmodified>
              </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              <d:propstat><d:prop><d:creationdate/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
            </d:response>
            """);
        var (path, item) = Create().ToItem(response)!.Value;
        Assert.Equal("/Photos/a.jpg", path);
        Assert.Equal(StorageItemKind.File, item.Kind);
        Assert.Equal(1234, item.Size);
        Assert.Equal(new DateTimeOffset(1994, 11, 15, 12, 45, 26, TimeSpan.Zero), item.Modified);

        var folder = XElement.Parse("""
            <d:response xmlns:d="DAV:"><d:href>/remote.php/dav/files/ann%20b/Photos/</d:href>
              <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
            </d:response>
            """);
        Assert.Equal(StorageItemKind.Folder, Create().ToItem(folder)!.Value.Item.Kind);
    }
}

public class CloudFormTests
{
    private sealed class MemoryStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public void Save(string key, string secret) => Values[key] = secret;
        public string? Load(string key) => Values.GetValueOrDefault(key);
        public void Delete(string key) => Values.Remove(key);
    }

    /// <summary>Connector whose "server" accepts only the password "right".</summary>
    private sealed class TestConnector(ICredentialStore store) : FormConnectorBase(store)
    {
        public CloudSecrets? LastSecrets { get; private set; }
        public override CloudProviderType Type => CloudProviderType.WebDav;
        public override string Name => "Test";
        public override CloudCategory Category => CloudCategory.Servers;

        public override IReadOnlyList<CloudField> Fields { get; } =
        [
            new("url", "Server", CloudFieldKind.Url),
            new("user", "User name"),
            new("password", "Password", CloudFieldKind.Secret),
            new("port", "Port") { Optional = true, Default = "443" },
        ];

        protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets)
        {
            LastSecrets = secrets;
            return new WebDavProvider(account, Type, Name, "https://example.invalid/", "u", "p");
        }

        protected override Task<CloudIdentity> VerifyAsync(ICloudStorageProvider provider, CloudAccount account, CloudSecrets secrets, CancellationToken cancellationToken) =>
            secrets.Get("password") == "right"
                ? Task.FromResult(Identity(account))
                : throw new StorageException(StorageErrorKind.AuthenticationRequired, "nope");
    }

    [Fact]
    public async Task Saves_settings_and_secrets_separately_after_a_successful_test()
    {
        var store = new MemoryStore();
        var connector = new TestConnector(store);
        var account = new CloudAccount();

        var identity = await connector.AuthenticateAsync(account,
            new Dictionary<string, string> { ["url"] = " https://dav.example.com/x ", ["user"] = "ann", ["password"] = "right" }, CancellationToken.None);

        Assert.Equal("https://dav.example.com/x", account.Settings["url"]);
        Assert.Equal("443", account.Settings["port"]);
        Assert.False(account.Settings.ContainsKey("password"));
        Assert.Contains("right", store.Values[account.CredentialKey]);
        Assert.Equal("ann@dav.example.com", identity.UserName);
    }

    [Fact]
    public async Task Failed_test_changes_nothing()
    {
        var store = new MemoryStore();
        var account = new CloudAccount { Settings = { ["url"] = "https://old.example.com", ["user"] = "ann" } };
        await Assert.ThrowsAsync<StorageException>(() => new TestConnector(store).AuthenticateAsync(account,
            new Dictionary<string, string> { ["url"] = "https://new.example.com", ["user"] = "ann", ["password"] = "wrong" }, CancellationToken.None));
        Assert.Equal("https://old.example.com", account.Settings["url"]);
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task Reconnect_with_a_blank_password_keeps_the_saved_one()
    {
        var store = new MemoryStore();
        var connector = new TestConnector(store);
        var account = new CloudAccount();
        await connector.AuthenticateAsync(account, new Dictionary<string, string> { ["url"] = "https://a.example.com", ["user"] = "ann", ["password"] = "right" },
            CancellationToken.None);

        await connector.AuthenticateAsync(account, new Dictionary<string, string> { ["url"] = "https://b.example.com", ["user"] = "ann", ["password"] = "" },
            CancellationToken.None);

        Assert.Equal("right", connector.LastSecrets!.Get("password"));
        Assert.Equal("https://b.example.com", account.Settings["url"]);
    }

    [Fact]
    public async Task Names_the_missing_field()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => new TestConnector(new MemoryStore()).AuthenticateAsync(new CloudAccount(),
            new Dictionary<string, string> { ["url"] = "https://a.example.com", ["password"] = "right" }, CancellationToken.None));
        Assert.Equal("Enter the user name.", ex.Message);
    }

    [Theory]
    [InlineData("ftp.example.com", "ftp.example.com", null)]
    [InlineData("ftp.example.com:2121", "ftp.example.com", 2121)]
    [InlineData("ftp://nas.local:2121/", "nas.local", 2121)]
    [InlineData("sftp://10.0.0.5", "10.0.0.5", null)]
    public void Parses_server_names(string input, string host, int? port) => Assert.Equal((host, port), ServerFields.ParseHost(input));

    [Fact]
    public void Every_service_preset_has_a_unique_type()
    {
        var types = WebDavServices.All.Select(s => s.Type).Concat(S3Services.All.Select(s => s.Type)).ToList();
        Assert.Equal(types.Count, types.Distinct().Count());
    }
}

public class MegaHashcashTests
{
    [Fact]
    public async Task Known_length_stream_reports_size_of_a_network_style_source()
    {
        var source = new NoLengthStream(new byte[1000]);
        Assert.Throws<NotSupportedException>(() => source.Length);
        var wrapped = new KnownLengthStream(source, 1000);
        Assert.Equal(1000, wrapped.Length);
        var copy = new MemoryStream();
        await wrapped.CopyToAsync(copy);
        Assert.Equal(1000, copy.Length);
        Assert.Equal(1000, wrapped.Position);
    }

    private sealed class NoLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    [Fact]
    public void Solves_megas_proof_of_work()
    {
        byte[] token = Enumerable.Range(0, 48).Select(i => (byte)(i * 7 + 3)).ToArray();
        string tokenText = MegaWebClient.Base64UrlEncode(token);

        string? answer = MegaWebClient.Solve($"1:192:1795351198:{tokenText}");

        Assert.NotNull(answer);
        var parts = answer!.Split(':');
        Assert.Equal("1", parts[0]);
        Assert.Equal(tokenText, parts[1]);
        byte[] prefix = MegaWebClient.Base64UrlDecode(parts[2]);
        var buffer = new byte[4 + 48 * 262_144];
        prefix.CopyTo(buffer, 0);
        for (int i = 0; i < 262_144; i++) token.CopyTo(buffer, 4 + i * 48);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(buffer);
        uint first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
        Assert.True(first <= 1u << 24, $"hash prefix {first:X8} above the easiness-192 threshold");
    }

    [Theory]
    [InlineData("2:192:1:abc")]
    [InlineData("1:192:1:not-48-bytes")]
    [InlineData("garbage")]
    public void Ignores_unknown_challenges(string challenge) => Assert.Null(MegaWebClient.Solve(challenge));
}
