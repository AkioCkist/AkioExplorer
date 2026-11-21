using System.Text.Json.Serialization;
using AkioExplorer.Core.Abstractions;

namespace AkioExplorer.Core.Cloud;

public enum CloudProviderType
{
    GoogleDrive,
    Dropbox,
    OneDrive,
}

/// <summary>
/// A connected cloud account. Holds no secrets: tokens live in Windows Credential Manager under <see cref="CredentialKey"/>.
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

    [JsonIgnore]
    public string ProviderId => "cloud:" + Id.ToString("N");

    [JsonIgnore]
    public string CredentialKey => "AkioExplorer/" + Id.ToString("N");
}

public sealed record StorageQuota(long? Used, long? Total);

/// <summary>A storage provider backed by a cloud account.</summary>
public interface ICloudStorageProvider : IStorageProvider
{
    CloudAccount Account { get; }

    CloudProviderType Type { get; }

    Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}

/// <summary>Identity returned by a successful sign-in.</summary>
public sealed record CloudIdentity(string? Email, string? UserName, string? AccountKey);

/// <summary>
/// One cloud service: knows how to authenticate (OAuth) and how to create a provider for a stored account.
/// New services are added by registering another connector – nothing else in the app changes.
/// </summary>
public interface ICloudConnector
{
    CloudProviderType Type { get; }

    string Name { get; }

    /// <summary>False when the app registration (client id) for this service hasn't been configured.</summary>
    bool IsConfigured { get; }

    /// <summary>What the user (or developer) must do to enable the service when <see cref="IsConfigured"/> is false.</summary>
    string SetupHint { get; }

    /// <summary>Runs the interactive sign-in for <paramref name="account"/> and stores its credentials securely.</summary>
    Task<CloudIdentity> AuthenticateAsync(CloudAccount account, CancellationToken cancellationToken);

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

    Task<CloudAccount> AddAccountAsync(CloudProviderType type, CancellationToken cancellationToken);

    Task ReconnectAsync(Guid accountId, CancellationToken cancellationToken);

    void Rename(Guid accountId, string displayName);

    Task RemoveAsync(Guid accountId);

    ICloudStorageProvider? GetProvider(Guid accountId);
}
