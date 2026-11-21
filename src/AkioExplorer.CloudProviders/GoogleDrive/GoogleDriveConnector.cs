using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Cloud;

namespace AkioExplorer.CloudProviders.GoogleDrive;

/// <summary>Google Drive via Google OAuth 2.0 for installed apps (loopback + PKCE).</summary>
public sealed class GoogleDriveConnector(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    : OAuthConnectorBase(config, store, http)
{
    private static readonly OAuthEndpoints Endpoints = new(
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "https://oauth2.googleapis.com/revoke");

    public override CloudProviderType Type => CloudProviderType.GoogleDrive;

    public override string Name => GoogleDriveProvider.ServiceName;

    protected override CloudAppRegistration Registration => Config.GoogleDrive;

    protected override OAuthClient CreateOAuthClient() => new(Endpoints, new OAuthApp(
        Registration.ClientId!,
        Registration.ClientSecret,
        Scopes: ["https://www.googleapis.com/auth/drive", "openid", "email"],
        RedirectHost: "127.0.0.1",
        // offline + consent guarantees a refresh token even if the user granted access before.
        ExtraAuthorizeParameters: new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent select_account" }), Http);

    protected override Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken) =>
        GoogleDriveProvider.GetIdentityAsync(TokenSource(account), cancellationToken);

    public override ICloudStorageProvider CreateProvider(CloudAccount account) => new GoogleDriveProvider(account, TokenSource(account), Http);
}
