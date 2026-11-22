using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.Core.Cloud;

/// <summary>
/// Owns the list of connected accounts (metadata in settings, secrets in the credential store) and keeps the
/// provider registry in sync, so every pane can browse any account through <see cref="IStorageProvider"/>.
/// </summary>
public sealed class CloudAccountService : ICloudAccountService
{
    private readonly ISettingsService _settings;
    private readonly IStorageProviderRegistry _registry;
    private readonly ICredentialStore _credentials;
    private readonly Dictionary<CloudProviderType, ICloudConnector> _connectors;
    private readonly ILogger<CloudAccountService> _logger;
    private readonly Dictionary<Guid, ICloudStorageProvider> _providers = new();

    public CloudAccountService(
        ISettingsService settings,
        IStorageProviderRegistry registry,
        ICredentialStore credentials,
        IEnumerable<ICloudConnector> connectors,
        ILogger<CloudAccountService> logger)
    {
        _settings = settings;
        _registry = registry;
        _credentials = credentials;
        _connectors = connectors.ToDictionary(c => c.Type);
        _logger = logger;
    }

    public IReadOnlyList<CloudAccount> Accounts => _settings.Current.CloudAccounts;

    public IReadOnlyList<ICloudConnector> Connectors => _connectors.Values.OrderBy(c => c.Category).ThenBy(c => c.Type).ToList();

    public event EventHandler? AccountsChanged;

    public void Initialize()
    {
        foreach (var account in Accounts)
        {
            if (_connectors.TryGetValue(account.Type, out var connector)) Register(connector, account);
        }
    }

    public async Task<CloudAccount> AddAccountAsync(CloudProviderType type, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var connector = RequireConnector(type);
        var account = new CloudAccount { Type = type, DisplayName = connector.Name };

        CloudIdentity identity;
        try
        {
            identity = await connector.AuthenticateAsync(account, values, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _credentials.Delete(account.CredentialKey);
            throw;
        }

        // Same account signed in twice: refresh the stored one instead of creating a duplicate.
        var existing = Accounts.FirstOrDefault(a => a.Type == type && identity.AccountKey is not null &&
                                                    string.Equals(AccountKey(a), identity.AccountKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var secret = _credentials.Load(account.CredentialKey);
            _credentials.Delete(account.CredentialKey);
            if (secret is not null) _credentials.Save(existing.CredentialKey, secret);
            existing.Settings = account.Settings;
            _settings.Save();
            Register(connector, existing);
            AccountsChanged?.Invoke(this, EventArgs.Empty);
            return existing;
        }

        account.Email = identity.Email;
        account.UserName = identity.UserName;
        account.DisplayName = UniqueDisplayName(connector.Name, identity);
        _settings.Current.CloudAccounts.Add(account);
        _settings.Save();
        Register(connector, account);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Connected {Type} account {Id}", type, account.Id);
        return account;
    }

    public async Task ReconnectAsync(Guid accountId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var account = Find(accountId);
        var connector = RequireConnector(account.Type);
        await connector.AuthenticateAsync(account, values, cancellationToken).ConfigureAwait(false);
        _settings.Save(); // the form may have changed the server, user name…
        Register(connector, account); // fresh provider = fresh token cache
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(Guid accountId, string displayName)
    {
        var account = Find(accountId);
        string name = displayName.Trim();
        if (name.Length == 0 || name == account.DisplayName) return;
        account.DisplayName = name;
        _settings.Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(Guid accountId)
    {
        var account = Find(accountId);
        if (_connectors.TryGetValue(account.Type, out var connector))
        {
            try
            {
                await connector.SignOutAsync(account, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Token revocation failed for {Id}; removing locally anyway", account.Id);
            }
        }

        _credentials.Delete(account.CredentialKey);
        _settings.Current.CloudAccounts.Remove(account);
        _settings.Save();
        if (_providers.Remove(accountId, out var old)) (old as IDisposable)?.Dispose();
        _registry.Unregister(account.ProviderId);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public ICloudStorageProvider? GetProvider(Guid accountId) => _providers.GetValueOrDefault(accountId);

    private void Register(ICloudConnector connector, CloudAccount account)
    {
        var provider = connector.CreateProvider(account);
        _providers[account.Id] = provider;
        _registry.Register(provider);
    }

    public ICloudConnector? GetConnector(CloudProviderType type) => _connectors.GetValueOrDefault(type);

    private ICloudConnector RequireConnector(CloudProviderType type) =>
        _connectors.TryGetValue(type, out var connector)
            ? connector
            : throw new StorageException(StorageErrorKind.NotSupported, $"{type} isn't available in this build.");

    private CloudAccount Find(Guid id) =>
        Accounts.FirstOrDefault(a => a.Id == id) ?? throw new StorageException(StorageErrorKind.NotFound, "That account isn't connected any more.");

    private static string? AccountKey(CloudAccount account) => account.Email ?? account.UserName;

    private string UniqueDisplayName(string serviceName, CloudIdentity identity)
    {
        // "Google Drive name@example.com": service + account, so several accounts are told apart at a glance.
        string? who = identity.Email ?? identity.UserName;
        string name = string.IsNullOrWhiteSpace(who) ? serviceName : $"{serviceName} {who}";
        var taken = Accounts.Select(a => a.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
        {
            string candidate = $"{name} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
