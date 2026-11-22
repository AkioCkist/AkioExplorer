using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Cloud;

namespace AkioExplorer.CloudProviders.Box;

/// <summary>
/// Box OAuth 2.0 (authorization code). Box requires the client secret on the token request and an exactly
/// registered redirect URI, so a fixed loopback port is used: http://localhost:53683/. Refresh tokens are single
/// use; the token source stores each rotated one.
/// </summary>
public sealed class BoxConnector(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    : OAuthConnectorBase(config, store, http)
{
    public const int RedirectPort = 53683;

    private static readonly OAuthEndpoints Endpoints = new(
        "https://account.box.com/api/oauth2/authorize",
        "https://api.box.com/oauth2/token");

    public override CloudProviderType Type => CloudProviderType.Box;

    public override string Name => BoxProvider.ServiceName;

    public override CloudCategory Category => CloudCategory.Business;

    protected override CloudAppRegistration Registration => Config.Box;

    protected override OAuthClient CreateOAuthClient() => new(Endpoints, new OAuthApp(
        Registration.ClientId!,
        Registration.ClientSecret,
        Scopes: [],
        FixedRedirectPort: RedirectPort), Http);

    protected override Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken) =>
        BoxProvider.GetIdentityAsync(Http, accessToken, cancellationToken);

    public override ICloudStorageProvider CreateProvider(CloudAccount account) => new BoxProvider(account, Http, TokenSource(account));

    public override async Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        if (!IsConfigured || Store.Load(account.CredentialKey) is not { Length: > 0 } refresh) return;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Registration.ClientId!,
            ["client_secret"] = Registration.ClientSecret ?? string.Empty,
            ["token"] = refresh,
        });
        using var _ = await Http.PostAsync("https://api.box.com/oauth2/revoke", form, cancellationToken).ConfigureAwait(false);
    }
}
