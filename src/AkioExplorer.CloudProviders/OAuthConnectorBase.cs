using System.Collections.Concurrent;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders;

/// <summary>Common OAuth plumbing for connectors: interactive sign-in, token sources per account, error mapping.</summary>
public abstract class OAuthConnectorBase : ICloudConnector
{
    private readonly ConcurrentDictionary<Guid, OAuthTokenSource> _sources = new();

    protected OAuthConnectorBase(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    {
        Config = config;
        Store = store;
        Http = http;
    }

    protected CloudAppsConfig Config { get; }

    protected ICredentialStore Store { get; }

    protected HttpClient Http { get; }

    public abstract CloudProviderType Type { get; }

    public abstract string Name { get; }

    protected abstract CloudAppRegistration Registration { get; }

    public bool IsConfigured => Registration.IsSet;

    public virtual string SetupHint =>
        $"{Name} needs a client id from its developer console (one-time). Add it in \"Add cloud account\" or see docs/CLOUD-SETUP.md.";

    protected abstract OAuthClient CreateOAuthClient();

    protected abstract Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken);

    public abstract ICloudStorageProvider CreateProvider(CloudAccount account);

    protected OAuthTokenSource TokenSource(CloudAccount account) =>
        _sources.GetOrAdd(account.Id, _ => new OAuthTokenSource(CreateOAuthClient(), Store, account, Name));

    public virtual CloudCategory Category => CloudCategory.Popular;

    public virtual string Description => IsConfigured ? "Sign in with your browser" : "One-time setup, then sign in with your browser";

    public virtual string Keywords => string.Empty;

    /// <summary>Asked before the browser opens (e.g. a SharePoint site address). Stored in the account's settings.</summary>
    public virtual IReadOnlyList<CloudField> Fields => [];

    public bool UsesBrowserSignIn => true;

    /// <summary>Checks and normalises the form values before the browser opens; throw <see cref="StorageException"/> to reject.</summary>
    protected virtual IReadOnlyDictionary<string, string> PrepareSettings(IReadOnlyDictionary<string, string> values) =>
        Forms.CloudForm.NonSecret(Fields, values, existing: null);

    public async Task<CloudIdentity> AuthenticateAsync(CloudAccount account, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new StorageException(StorageErrorKind.NotSupported, SetupHint);
        if (Fields.Count > 0)
        {
            var settings = PrepareSettings(Forms.CloudForm.Merge(Fields, values, account.Settings));
            account.Settings = new Dictionary<string, string>(settings, StringComparer.Ordinal);
        }
        try
        {
            var client = CreateOAuthClient();
            var tokens = await client.AuthorizeInteractiveAsync(cancellationToken).ConfigureAwait(false);
            var source = new OAuthTokenSource(client, Store, account, Name);
            source.Accept(tokens);
            _sources[account.Id] = source;
            return await GetIdentityAsync(account, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthException ex) when (ex.Error == "access_denied")
        {
            throw new OperationCanceledException(ex.Message, ex);
        }
        catch (OAuthException ex)
        {
            throw new StorageException(StorageErrorKind.AuthenticationRequired, $"{Name} sign-in failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new StorageException(StorageErrorKind.Unavailable, $"Can't reach {Name}. Check your internet connection.", ex);
        }
    }

    public virtual async Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        _sources.TryRemove(account.Id, out _);
        if (Store.Load(account.CredentialKey) is { Length: > 0 } refresh && IsConfigured)
            await CreateOAuthClient().RevokeAsync(refresh, cancellationToken).ConfigureAwait(false);
    }
}
