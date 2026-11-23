using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.WebDav;

/// <summary>
/// The WebDAV-based services. Each preset fills in the address it knows; the "WebDAV address" field stays editable
/// where services hand out per-account servers, so a changed endpoint never blocks the user.
/// </summary>
public static class WebDavServices
{
    internal static CloudField User(string label = "User name", string? help = null) => new("user", label) { Help = help };

    internal static CloudField Password(string label = "Password", string? help = null) => new("password", label, CloudFieldKind.Secret) { Help = help };

    internal static CloudField Address(string? fallback, string? placeholder = null, string? help = null) =>
        new(WebDavConnector.UrlKey, "WebDAV address", CloudFieldKind.Url) { Default = fallback, Placeholder = placeholder ?? fallback, Help = help };

    private static string Url(IReadOnlyDictionary<string, string> s) => s.GetValueOrDefault(WebDavConnector.UrlKey, string.Empty);

    private static string Server(IReadOnlyDictionary<string, string> s) =>
        Forms.CloudForm.NormalizeUrl(s.GetValueOrDefault("server", string.Empty));

    internal static string Required(IReadOnlyDictionary<string, string> s, string key, string label) =>
        s.TryGetValue(key, out var value) && value.Length > 0 ? value.Trim().Trim('/')
            : throw new StorageException(StorageErrorKind.InvalidName, $"Enter the {label}.");

    public static IReadOnlyList<WebDavService> All { get; } =
    [
        new(CloudProviderType.WebDav, "WebDAV", CloudCategory.Servers,
            [Address(null, "https://server/path", "NAS boxes (Synology, QNAP, TrueNAS), Seafile, Apache, IIS and other WebDAV servers."), User(), Password()],
            Url, "Any WebDAV server or NAS", "nas synology qnap truenas seafile server"),

        new(CloudProviderType.Nextcloud, "Nextcloud", CloudCategory.Servers,
            [new CloudField("server", "Server address", CloudFieldKind.Url) { Placeholder = "https://cloud.example.com" }, User(),
             Password("App password", "Nextcloud → Settings → Security → Devices & sessions → Create new app password.")],
            s => $"{Server(s)}/remote.php/dav/files/{Uri.EscapeDataString(Required(s, "user", "user name"))}",
            "Your Nextcloud server", "self-hosted", new WebDavOptions { ServerTrash = true }),

        new(CloudProviderType.OwnCloud, "ownCloud", CloudCategory.Servers,
            [new CloudField("server", "Server address", CloudFieldKind.Url) { Placeholder = "https://owncloud.example.com" }, User(), Password()],
            s => $"{Server(s)}/remote.php/webdav", "Your ownCloud server", "self-hosted", new WebDavOptions { ServerTrash = true }),

        new(CloudProviderType.PCloud, "pCloud", CloudCategory.Popular,
            [new CloudField("region", "Data region", CloudFieldKind.Choice) { Choices = ["United States", "Europe"], Default = "United States",
                Help = "Where your pCloud account was created (shown in pCloud settings)." },
             User("Email address"), Password()],
            s => s.GetValueOrDefault("region") == "Europe" ? "https://ewebdav.pcloud.com" : "https://webdav.pcloud.com",
            "Email and password", "swiss"),

        new(CloudProviderType.Yandex, "Yandex Disk", CloudCategory.Popular,
            [User("Yandex login"),
             Password("App password", "Not your Yandex password: create one at Yandex ID → Security → App passwords → Files (WebDAV). It is 16 lowercase letters.")],
            _ => "https://webdav.yandex.com",
            "Login and app password", "yandex disk"),
    ];
}
