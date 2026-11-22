using AkioExplorer.CloudProviders.Forms;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.S3;

/// <summary>One S3-compatible service: its form and how the form becomes endpoint + region.</summary>
public sealed record S3Service(
    CloudProviderType Type,
    string Name,
    IReadOnlyList<CloudField> Fields,
    Func<IReadOnlyDictionary<string, string>, (string Endpoint, string Region)> Resolve,
    string Description,
    string Keywords = "",
    bool PathStyle = false,
    bool IsAws = false,
    string AccessKeyLabel = "Access key ID",
    string SecretKeyLabel = "Secret access key");

public sealed class S3Connector(S3Service service, ICredentialStore store) : FormConnectorBase(store)
{
    public override CloudProviderType Type => service.Type;

    public override string Name => service.Name;

    public override CloudCategory Category => CloudCategory.ObjectStorage;

    public override IReadOnlyList<CloudField> Fields { get; } =
    [
        .. service.Fields,
        new CloudField("user", service.AccessKeyLabel),
        new CloudField("secret", service.SecretKeyLabel, CloudFieldKind.Secret),
        new CloudField("bucket", "Bucket") { Optional = true, Help = "Leave empty to see all buckets. Needed when the key only has access to one bucket." },
    ];

    public override string Description => service.Description;

    public override string Keywords => "s3 bucket object storage " + service.Keywords;

    protected override Dictionary<string, string> Normalize(Dictionary<string, string> settings)
    {
        var (endpoint, region) = service.Resolve(settings);
        settings["endpoint"] = CloudForm.NormalizeUrl(endpoint);
        settings["region"] = region.Length > 0 ? region : "us-east-1";
        if (settings.TryGetValue("bucket", out var bucket)) settings["bucket"] = bucket.Trim('/');
        return settings;
    }

    protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets)
    {
        var options = new S3Options
        {
            Endpoint = account.Setting("endpoint"),
            Region = account.Setting("region", "us-east-1"),
            Bucket = account.Setting("bucket") is { Length: > 0 } bucket ? bucket : null,
            PathStyle = service.PathStyle || account.IsTrue("pathStyle"),
            IsAws = service.IsAws,
        };
        if (options.Endpoint.Length == 0)
            throw new StorageException(StorageErrorKind.AuthenticationRequired, $"\"{account.DisplayName}\" has no endpoint. Reconnect the account to continue.");
        return new S3Provider(account, service.Type, service.Name, account.Setting("user"), secrets.Get("secret") ?? string.Empty, options);
    }

    protected override CloudIdentity Identity(CloudAccount account)
    {
        string bucket = account.Setting("bucket");
        string host = Uri.TryCreate(account.Setting("endpoint"), UriKind.Absolute, out var uri) ? uri.Host : account.Setting("endpoint");
        string who = bucket.Length > 0 ? $"{bucket} ({host})" : $"{account.Setting("user")}@{host}";
        return new CloudIdentity(null, who, $"{account.Setting("user")}@{host}/{bucket}");
    }
}

public static class S3Services
{
    private static CloudField Region(string fallback, IReadOnlyList<string>? choices = null, string? help = null) =>
        choices is null
            ? new CloudField("region", "Region") { Default = fallback, Placeholder = fallback, Help = help }
            : new CloudField("region", "Region", CloudFieldKind.Choice) { Default = fallback, Choices = choices, Help = help };

    private static string Get(IReadOnlyDictionary<string, string> s, string key, string fallback = "") =>
        s.TryGetValue(key, out var value) && value.Length > 0 ? value.Trim() : fallback;

    private static readonly string[] AwsRegions =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "sa-east-1",
        "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1", "eu-central-2", "eu-north-1", "eu-south-1", "eu-south-2",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3", "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4",
        "ap-south-1", "ap-south-2", "ap-east-1", "me-south-1", "me-central-1", "af-south-1", "il-central-1",
    ];

    private static readonly string[] WasabiRegions =
    [
        "us-east-1", "us-east-2", "us-central-1", "us-west-1", "ca-central-1", "eu-central-1", "eu-central-2", "eu-west-1", "eu-west-2",
        "eu-west-3", "eu-south-1", "ap-northeast-1", "ap-northeast-2", "ap-southeast-1", "ap-southeast-2",
    ];

    public static IReadOnlyList<S3Service> All { get; } =
    [
        new(CloudProviderType.AmazonS3, "Amazon S3",
            [Region("us-east-1", AwsRegions, "Buckets in other regions are found automatically.")],
            s => ($"https://s3.{Get(s, "region", "us-east-1")}.amazonaws.com", Get(s, "region", "us-east-1")),
            "AWS object storage", "aws amazon", IsAws: true),

        new(CloudProviderType.S3Compatible, "S3-compatible storage",
            [new CloudField("endpoint", "Endpoint", CloudFieldKind.Url) { Placeholder = "https://s3.example.com",
                 Help = "MinIO, Cloudflare R2, DigitalOcean Spaces, Scaleway, Storj, Ceph, Synology C2, Linode…" },
             Region("us-east-1", help: "Use the region your provider shows; most self-hosted servers accept us-east-1."),
             new CloudField("pathStyle", "Use path-style addresses (endpoint/bucket)", CloudFieldKind.Toggle) { Default = "true", Optional = true }],
            s => (Get(s, "endpoint"), Get(s, "region", "us-east-1")),
            "Any server with an S3 API", "minio cloudflare r2 digitalocean spaces scaleway storj ceph linode synology c2 garage"),

        new(CloudProviderType.Wasabi, "Wasabi",
            [Region("us-east-1", WasabiRegions)],
            s => ($"https://s3.{Get(s, "region", "us-east-1")}.wasabisys.com", Get(s, "region", "us-east-1")),
            "Wasabi hot cloud storage"),

        new(CloudProviderType.Backblaze, "Backblaze B2",
            [Region("us-west-004", help: "From the bucket's Endpoint in Backblaze: s3.<region>.backblazeb2.com (e.g. us-west-004, eu-central-003).")],
            s => ($"https://s3.{Get(s, "region", "us-west-004")}.backblazeb2.com", Get(s, "region", "us-west-004")),
            "Backblaze B2 cloud storage", "b2", AccessKeyLabel: "Key ID", SecretKeyLabel: "Application key"),

    ];
}
