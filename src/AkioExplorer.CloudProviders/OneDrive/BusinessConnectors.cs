using System.Net.Http.Headers;
using System.Text.Json;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.OneDrive;

/// <summary>
/// OneDrive for work or school (Microsoft 365). Same Graph API and app registration as personal OneDrive, but the
/// sign-in page only offers organisational accounts.
/// </summary>
public sealed class OneDriveBusinessConnector(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    : OAuthConnectorBase(config, store, http)
{
    internal static readonly OAuthEndpoints Endpoints = new(
        "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/organizations/oauth2/v2.0/token");

    public const string ServiceName = "OneDrive for Business";

    public override CloudProviderType Type => CloudProviderType.OneDriveBusiness;

    public override string Name => ServiceName;

    public override CloudCategory Category => CloudCategory.Business;

    public override string Keywords => "microsoft 365 office work school";

    protected override CloudAppRegistration Registration => Config.OneDrive;

    protected override OAuthClient CreateOAuthClient() => new(Endpoints, new OAuthApp(
        Registration.ClientId!,
        ClientSecret: null,
        Scopes: ["Files.ReadWrite.All", "offline_access", "User.Read"],
        ExtraAuthorizeParameters: new Dictionary<string, string> { ["prompt"] = "select_account" }), Http);

    protected override Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken) =>
        OneDriveProvider.GetIdentityAsync(Http, accessToken, cancellationToken);

    public override ICloudStorageProvider CreateProvider(CloudAccount account) =>
        new OneDriveProvider(account, Http, TokenSource(account), CloudProviderType.OneDriveBusiness, ServiceName);

    public override Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A SharePoint Online document library. The user names the site (its web address); after sign-in the site and
/// library are resolved once to a Graph drive id, which the account keeps.
/// </summary>
public sealed class SharePointConnector(CloudAppsConfig config, ICredentialStore store, HttpClient http)
    : OAuthConnectorBase(config, store, http)
{
    public const string ServiceName = "SharePoint";
    private const string DriveSetting = "drive";

    public override CloudProviderType Type => CloudProviderType.SharePoint;

    public override string Name => "SharePoint Online";

    public override CloudCategory Category => CloudCategory.Business;

    public override string Description => IsConfigured ? "Site address, then sign in with your browser" : base.Description;

    public override string Keywords => "microsoft 365 office teams site document library";

    public override IReadOnlyList<CloudField> Fields { get; } =
    [
        new("site", "Site address", CloudFieldKind.Url) { Placeholder = "https://contoso.sharepoint.com/sites/Team",
            Help = "Open the site in your browser and copy the address up to the site name." },
        new("library", "Document library") { Optional = true, Placeholder = "Documents", Help = "Leave empty for the site's default library." },
    ];

    protected override CloudAppRegistration Registration => Config.OneDrive;

    protected override IReadOnlyDictionary<string, string> PrepareSettings(IReadOnlyDictionary<string, string> values)
    {
        var settings = new Dictionary<string, string>(values, StringComparer.Ordinal);
        string site = Forms.CloudForm.NormalizeUrl(settings.GetValueOrDefault("site", string.Empty));
        if (!Uri.TryCreate(site, UriKind.Absolute, out var uri) || !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new StorageException(StorageErrorKind.InvalidName, "Enter a SharePoint site address, like https://contoso.sharepoint.com/sites/Team.");
        // Keep only "/sites/Name" (or "/teams/Name"): pages and folders after it aren't part of the site address.
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string sitePath = parts.Length >= 2 && parts[0] is "sites" or "teams" ? $"/{parts[0]}/{parts[1]}" : string.Empty;
        settings["site"] = $"https://{uri.Host}{sitePath}";
        settings.Remove(DriveSetting);
        return settings;
    }

    protected override OAuthClient CreateOAuthClient() => new(OneDriveBusinessConnector.Endpoints, new OAuthApp(
        Registration.ClientId!,
        ClientSecret: null,
        Scopes: ["Files.ReadWrite.All", "Sites.Read.All", "offline_access", "User.Read"],
        ExtraAuthorizeParameters: new Dictionary<string, string> { ["prompt"] = "select_account" }), Http);

    protected override async Task<CloudIdentity> GetIdentityAsync(CloudAccount account, string accessToken, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Setting("site"));
        string siteRef = uri.AbsolutePath.Length > 1 ? $"/sites/{uri.Host}:{uri.AbsolutePath}" : $"/sites/{uri.Host}";
        using var site = await GetAsync($"{OneDriveProvider.GraphBase}{siteRef}?$select=id,displayName", accessToken, "site", cancellationToken).ConfigureAwait(false);
        string siteId = site.RootElement.GetProperty("id").GetString()!;
        string siteName = site.RootElement.TryGetProperty("displayName", out var n) ? n.GetString() ?? uri.Host : uri.Host;

        string drive = $"/sites/{siteId}/drive";
        string library = account.Setting("library");
        if (library.Length > 0)
        {
            using var drives = await GetAsync($"{OneDriveProvider.GraphBase}/sites/{siteId}/drives?$select=id,name", accessToken, "library", cancellationToken)
                .ConfigureAwait(false);
            var match = drives.RootElement.GetProperty("value").EnumerateArray()
                .FirstOrDefault(d => string.Equals(d.GetProperty("name").GetString(), library, StringComparison.OrdinalIgnoreCase));
            if (match.ValueKind != JsonValueKind.Object)
                throw new StorageException(StorageErrorKind.NotFound, $"The site \"{siteName}\" has no document library named \"{library}\".");
            drive = $"/drives/{match.GetProperty("id").GetString()}";
        }
        account.Settings[DriveSetting] = drive;

        var me = await OneDriveProvider.GetIdentityAsync(Http, accessToken, cancellationToken).ConfigureAwait(false);
        string label = library.Length > 0 ? $"{siteName} – {library}" : siteName;
        return new CloudIdentity(null, label, $"{me.Email}|{drive}");
    }

    private async Task<JsonDocument> GetAsync(string url, string accessToken, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new StorageException(StorageErrorKind.NotFound, $"SharePoint couldn't find that {what}. Check the address.");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new StorageException(StorageErrorKind.AccessDenied, $"Your account doesn't have access to that {what}.");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public override ICloudStorageProvider CreateProvider(CloudAccount account) =>
        new OneDriveProvider(account, Http, TokenSource(account), CloudProviderType.SharePoint, ServiceName, drive: account.Setting(DriveSetting, "/me/drive"));

    public override Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken) => Task.CompletedTask;
}
