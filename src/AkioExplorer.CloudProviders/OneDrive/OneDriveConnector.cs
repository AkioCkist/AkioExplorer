using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Cloud;

namespace AkioExplorer.CloudProviders.OneDrive;

/// <summary>OneDrive (personal + work/school) via Microsoft identity platform v2 with PKCE.</summary>
public sealed class OneDriveConnector(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    : OAuthConnectorBase(config, store, http)
{
    private static readonly OAuthEndpoints Endpoints = new(
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token");

    public override CloudProviderType Type => CloudProviderType.OneDrive;

    public override string Name => OneDriveProvider.ServiceName;

    protected override CloudAppRegistration Registration => Config.OneDrive;

    protected override OAuthClient CreateOAuthClient() => new(Endpoints, new OAuthApp(
        Registration.ClientId!,
        ClientSecret: null,
        Scopes: ["Files.ReadWrite.All", "offline_access", "User.Read"],
        ExtraAuthorizeParameters: new Dictionary<string, string> { ["prompt"] = "select_account" }), Http);

    protected override Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken) =>
        OneDriveProvider.GetIdentityAsync(Http, accessToken, cancellationToken);

    public override ICloudStorageProvider CreateProvider(CloudAccount account) => new OneDriveProvider(account, Http, TokenSource(account));

    public override Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        // Microsoft has no token revocation endpoint for public clients; forgetting the refresh token is the sign-out.
        return Task.CompletedTask;
    }
}
