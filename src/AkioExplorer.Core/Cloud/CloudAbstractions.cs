using System.Text.Json.Serialization;
using AkioExplorer.Core.Abstractions;

namespace AkioExplorer.Core.Cloud;

/// <summary>Every service AkioExplorer can connect to. Stored by name in settings, so the order doesn't matter.</summary>
public enum CloudProviderType
{
    GoogleDrive,
    Dropbox,
    OneDrive,
    OneDriveBusiness,
    SharePoint,
    Box,
    Mega,
    PCloud,
    Yandex,

    // Self-hosted and servers
    WebDav,
    Nextcloud,
    OwnCloud,
    Ftp,
    Sftp,

    // Object storage
    AmazonS3,
    S3Compatible,
    Wasabi,
    Backblaze,
    AzureBlob,
    MegaS4,

    /// <summary>Only for shared links opened without an account (no sign-in connector).</summary>
    MediaFire,
}

/// <summary>How the add-account dialog groups services.</summary>
public enum CloudCategory
{
    Popular,
    Business,
    Servers,
    ObjectStorage,
}

/// <summary>
/// A connected cloud account. Holds no secrets: tokens and passwords live in Windows Credential Manager under
/// <see cref="CredentialKey"/>; <see cref="Settings"/> only holds what isn't secret (server, user name, bucket…).
/// </summary>
public sealed class CloudAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public CloudProviderType Type { get; set; }

    /// <summary>User-editable name shown in the sidebar.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? UserName { get; set; }

    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;

    /// <summary>Non-secret connection settings entered in the add-account form (keys are <see cref="CloudField.Key"/>).</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Compact label for the sidebar and tabs, where the provider icon already says which service it is:
    /// "Google Drive name@example.com" becomes "name". A name the user typed themself is kept as it is.
    /// </summary>
    [JsonIgnore]
    public string ShortName
    {
        get
        {
            string? who = Email ?? UserName;
            if (string.IsNullOrWhiteSpace(who) || !DisplayName.EndsWith(" " + who, StringComparison.OrdinalIgnoreCase))
                return DisplayName;
            int at = who.IndexOf('@');
            return at > 0 ? who[..at] : who;
        }
    }

    [JsonIgnore]
    public string ProviderId => "cloud:" + Id.ToString("N");

    [JsonIgnore]
    public string CredentialKey => "AkioExplorer/" + Id.ToString("N");

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    /// <summary>A copy for trying new settings without touching the stored account.</summary>
    public CloudAccount CloneWith(IReadOnlyDictionary<string, string> settings) => new()
    {
        Id = Id,
        Type = Type,
        DisplayName = DisplayName,
        Email = Email,
        UserName = UserName,
        Added = Added,
        Settings = new Dictionary<string, string>(settings, StringComparer.Ordinal),
    };
}

public sealed record StorageQuota(long? Used, long? Total)
{
    /// <summary>Bandwidth used in the current period, for services that limit transfers (MEGA).</summary>
    public long? TransferUsed { get; init; }

    public long? TransferTotal { get; init; }
}

/// <summary>A storage provider backed by a cloud account.</summary>
public interface ICloudStorageProvider : IStorageProvider
{
    CloudAccount Account { get; }

    CloudProviderType Type { get; }

    Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}

/// <summary>Identity returned by a successful sign-in.</summary>
public sealed record CloudIdentity(string? Email, string? UserName, string? AccountKey);

public enum CloudFieldKind
{
    Text,

    /// <summary>Password, secret key, token: kept in Credential Manager, never in settings, masked in the form.</summary>
    Secret,

    Url,
    Number,

    /// <summary>One of <see cref="CloudField.Choices"/>.</summary>
    Choice,

    /// <summary>"true" / "false".</summary>
    Toggle,

    /// <summary>A local file (e.g. an SSH private key).</summary>
    FilePath,
}

/// <summary>One input of a service's connect form (server, user name, password, bucket…).</summary>
public sealed record CloudField(string Key, string Label, CloudFieldKind Kind = CloudFieldKind.Text)
{
    public string? Default { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>Short help under the field (where to find it, what format).</summary>
    public string? Help { get; init; }

    public bool Optional { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    public bool IsSecret => Kind == CloudFieldKind.Secret;
}

/// <summary>
/// One cloud service: knows how to authenticate and how to create a provider for a stored account.
/// New services are added by registering another connector – nothing else in the app changes.
/// </summary>
public interface ICloudConnector
{
    CloudProviderType Type { get; }

    string Name { get; }

    CloudCategory Category => CloudCategory.Popular;

    /// <summary>One line under the name in the add-account list ("Sign in with your browser", "Server address and password").</summary>
    string Description => UsesBrowserSignIn ? "Sign in with your browser" : "Enter your sign-in details";

    /// <summary>Extra words the add-account search matches (e.g. "NAS Synology" for WebDAV).</summary>
    string Keywords => string.Empty;

    /// <summary>Asked before signing in. Empty for pure browser sign-in.</summary>
    IReadOnlyList<CloudField> Fields => [];

    /// <summary>True when sign-in continues in the user's browser (OAuth).</summary>
    bool UsesBrowserSignIn => true;

    /// <summary>False when the app registration (client id) for this service hasn't been configured.</summary>
    bool IsConfigured { get; }

    /// <summary>What the user (or developer) must do to enable the service when <see cref="IsConfigured"/> is false.</summary>
    string SetupHint { get; }

    /// <summary>
    /// Signs <paramref name="account"/> in with the form <paramref name="values"/> (keys are <see cref="CloudField.Key"/>;
    /// a blank secret on reconnect keeps the stored one), verifies the connection, and stores the credentials securely.
    /// Non-secret values end up in <see cref="CloudAccount.Settings"/> only when sign-in succeeds.
    /// </summary>
    Task<CloudIdentity> AuthenticateAsync(CloudAccount account, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);

    ICloudStorageProvider CreateProvider(CloudAccount account);

    /// <summary>Forgets the account's tokens (and revokes them where the service supports it).</summary>
    Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken);
}

/// <summary>Secret storage protected by Windows (Credential Manager / DPAPI). Never a plaintext file.</summary>
public interface ICredentialStore
{
    void Save(string key, string secret);

    string? Load(string key);

    void Delete(string key);
}

public interface ICloudAccountService
{
    IReadOnlyList<CloudAccount> Accounts { get; }

    IReadOnlyList<ICloudConnector> Connectors { get; }

    /// <summary>Raised when accounts are added, removed or renamed.</summary>
    event EventHandler? AccountsChanged;

    /// <summary>Registers providers for stored accounts (no network access).</summary>
    void Initialize();

    Task<CloudAccount> AddAccountAsync(CloudProviderType type, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);

    Task ReconnectAsync(Guid accountId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);

    void Rename(Guid accountId, string displayName);

    Task RemoveAsync(Guid accountId);

    ICloudStorageProvider? GetProvider(Guid accountId);

    ICloudConnector? GetConnector(CloudProviderType type);
}

public static class CloudAccountServiceExtensions
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    public static Task<CloudAccount> AddAccountAsync(this ICloudAccountService service, CloudProviderType type, CancellationToken cancellationToken) =>
        service.AddAccountAsync(type, NoValues, cancellationToken);

    public static Task ReconnectAsync(this ICloudAccountService service, Guid accountId, CancellationToken cancellationToken) =>
        service.ReconnectAsync(accountId, NoValues, cancellationToken);
}
