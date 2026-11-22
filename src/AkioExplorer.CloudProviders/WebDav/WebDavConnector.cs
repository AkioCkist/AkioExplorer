using AkioExplorer.CloudProviders.Forms;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.WebDav;

/// <summary>
/// One WebDAV-based service: its form and how the form becomes the WebDAV address. Every preset shares
/// <see cref="WebDavProvider"/>.
/// </summary>
public sealed record WebDavService(
    CloudProviderType Type,
    string Name,
    CloudCategory Category,
    IReadOnlyList<CloudField> Fields,
    Func<IReadOnlyDictionary<string, string>, string> BuildUrl,
    string Description,
    string Keywords = "",
    WebDavOptions? Options = null);

public sealed class WebDavConnector(WebDavService service, ICredentialStore store) : FormConnectorBase(store)
{
    public const string UrlKey = "url";

    public WebDavService Service => service;

    public override CloudProviderType Type => service.Type;

    public override string Name => service.Name;

    public override CloudCategory Category => service.Category;

    public override IReadOnlyList<CloudField> Fields => service.Fields;

    public override string Description => service.Description;

    public override string Keywords => "webdav " + service.Keywords;

    protected override Dictionary<string, string> Normalize(Dictionary<string, string> settings)
    {
        settings[UrlKey] = CloudForm.NormalizeUrl(service.BuildUrl(settings));
        return settings;
    }

    protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets)
    {
        string url = account.Setting(UrlKey);
        if (url.Length == 0)
            throw new StorageException(StorageErrorKind.AuthenticationRequired, $"\"{account.DisplayName}\" has no server address. Reconnect the account to continue.");
        return new WebDavProvider(account, service.Type, service.Name, url, account.Setting("user"), secrets.Get("password") ?? string.Empty, service.Options);
    }
}
