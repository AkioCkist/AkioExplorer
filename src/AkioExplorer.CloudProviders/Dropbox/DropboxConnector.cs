using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using Dropbox.Api;

namespace AkioExplorer.CloudProviders.Dropbox;

/// <summary>
/// Dropbox via OAuth 2.0 code flow with PKCE (no app secret). Dropbox requires the exact redirect URI to be
/// registered, so a fixed loopback port is used: http://localhost:53682/.
/// </summary>
public sealed class DropboxConnector(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    : OAuthConnectorBase(config, store, http)
{
    public const int RedirectPort = 53682;

    private static readonly OAuthEndpoints Endpoints = new(
        "https://www.dropbox.com/oauth2/authorize",
        "https://api.dropboxapi.com/oauth2/token");

    public override CloudProviderType Type => CloudProviderType.Dropbox;

    public override string Name => DropboxProvider.ServiceName;

    protected override CloudAppRegistration Registration => Config.Dropbox;

    protected override OAuthClient CreateOAuthClient() => new(Endpoints, new OAuthApp(
        Registration.ClientId!,
        ClientSecret: null,
        Scopes: ["account_info.read", "files.metadata.read", "files.metadata.write", "files.content.read", "files.content.write", "sharing.read"],
        FixedRedirectPort: RedirectPort,
        ExtraAuthorizeParameters: new Dictionary<string, string> { ["token_access_type"] = "offline", ["force_reapprove"] = "true" }), Http);

    protected override async Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken)
    {
        using var client = new DropboxClient(accessToken, new DropboxClientConfig("AkioExplorer") { HttpClient = Http });
        var me = await client.Users.GetCurrentAccountAsync().ConfigureAwait(false);
        return new CloudIdentity(me.Email, me.Name?.DisplayName, me.AccountId);
    }

    public override ICloudStorageProvider CreateProvider(CloudAccount account) =>
        new DropboxProvider(account, () => CreateClient(account));

    private DropboxClient CreateClient(CloudAccount account)
    {
        string refresh = Store.Load(account.CredentialKey)
            ?? throw new StorageException(StorageErrorKind.AuthenticationRequired,
                $"The sign-in for \"{account.DisplayName}\" is missing. Reconnect the account to continue.");
        if (!IsConfigured) throw new StorageException(StorageErrorKind.NotSupported, SetupHint);
        return new DropboxClient(refresh, Registration.ClientId!, new DropboxClientConfig("AkioExplorer") { HttpClient = Http });
    }

    public override async Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        if (!IsConfigured || Store.Load(account.CredentialKey) is null) return;
        using var client = CreateClient(account);
        await client.Auth.TokenRevokeAsync().ConfigureAwait(false);
    }
}
