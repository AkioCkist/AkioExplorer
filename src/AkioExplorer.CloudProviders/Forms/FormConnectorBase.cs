using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Forms;

/// <summary>
/// Connectors whose sign-in is a form (server, user name, password, access key…) instead of a browser. The
/// connection is tested with the typed values before anything is saved; secrets go to Credential Manager as one
/// JSON entry, the rest to the account's settings.
/// </summary>
public abstract class FormConnectorBase : ICloudConnector
{
    protected FormConnectorBase(ICredentialStore store)
    {
        Store = store;
    }

    protected ICredentialStore Store { get; }

    public abstract CloudProviderType Type { get; }

    public abstract string Name { get; }

    public abstract CloudCategory Category { get; }

    public abstract IReadOnlyList<CloudField> Fields { get; }

    public virtual string Description => "Enter your sign-in details";

    public virtual string Keywords => string.Empty;

    public bool UsesBrowserSignIn => false;

    public virtual bool IsConfigured => true;

    public virtual string SetupHint => string.Empty;

    /// <summary>Checks and completes the settings (adds https://, works out endpoints…). Throw to reject the form.</summary>
    protected virtual Dictionary<string, string> Normalize(Dictionary<string, string> settings) => settings;

    protected abstract ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets);

    /// <summary>Proves the details work (lists the root by default) and says who signed in.</summary>
    protected virtual async Task<CloudIdentity> VerifyAsync(ICloudStorageProvider provider, CloudAccount account, CloudSecrets secrets,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in provider.ListAsync(provider.RootPath, cancellationToken).ConfigureAwait(false)) break;
        return Identity(account);
    }

    /// <summary>
    /// What goes to Credential Manager after a successful sign-in. Default: the typed secrets. Services that hand out
    /// a session (MEGA) store that instead of the password.
    /// </summary>
    protected virtual CloudSecrets SecretsToStore(CloudSecrets typed, ICloudStorageProvider provider) => typed;

    /// <summary>"user@server": tells several accounts of one service apart and detects the same one added twice.</summary>
    protected virtual CloudIdentity Identity(CloudAccount account)
    {
        string user = account.Setting("user");
        string server = account.Setting("url", account.Setting("host", account.Setting("endpoint")));
        if (Uri.TryCreate(server, UriKind.Absolute, out var uri)) server = uri.Host;
        string who = user.Length > 0 && server.Length > 0 ? $"{user}@{server}" : user.Length > 0 ? user : server;
        return new CloudIdentity(null, who.Length > 0 ? who : null, who.Length > 0 ? who : null);
    }

    public async Task<CloudIdentity> AuthenticateAsync(CloudAccount account, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var stored = CloudSecrets.Load(Store, account);
        var merged = CloudForm.Merge(Fields, values, account.Settings);
        var secretValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (field.IsSecret)
            {
                // Blank on reconnect = keep the saved secret.
                string? secret = merged.GetValueOrDefault(field.Key) ?? stored.Get(field.Key);
                if (secret is not null) secretValues[field.Key] = secret;
            }
            else if (merged.TryGetValue(field.Key, out var value))
            {
                settings[field.Key] = value;
            }
        }

        var secrets = new CloudSecrets(secretValues, account.DisplayName);
        CloudForm.RequireFilled(Fields, settings, secrets);
        settings = Normalize(settings);

        var trial = account.CloneWith(settings);
        var provider = Create(trial, secrets);
        CloudIdentity identity;
        CloudSecrets toStore;
        try
        {
            identity = await VerifyAsync(provider, trial, secrets, cancellationToken).ConfigureAwait(false);
            toStore = SecretsToStore(secrets, provider);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.AuthenticationRequired)
        {
            throw new StorageException(StorageErrorKind.AuthenticationRequired, $"{Name} didn't accept the sign-in details. {Hint(ex)}", ex);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Signed in, but the folder or bucket wasn't found. Check the address. {Hint(ex)}", ex);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }

        Store.Save(account.CredentialKey, toStore.Serialize());
        account.Settings = trial.Settings;
        return identity;
    }

    private static string Hint(Exception ex) => ex.Message.EndsWith("Reconnect the account to continue.", StringComparison.Ordinal) ? "Check the user name and password." : ex.Message;

    public ICloudStorageProvider CreateProvider(CloudAccount account) => Create(account, CloudSecrets.Load(Store, account));

    public virtual Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken) => Task.CompletedTask;
}
