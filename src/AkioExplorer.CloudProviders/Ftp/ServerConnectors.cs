using AkioExplorer.CloudProviders.Forms;
using AkioExplorer.CloudProviders.Sftp;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Ftp;

internal static class ServerFields
{
    /// <summary>Accepts "host", "host:port" or "ftp://host:port/" and splits it.</summary>
    public static (string Host, int? Port) ParseHost(string text)
    {
        text = text.Trim();
        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (uri.Host, uri.IsDefaultPort ? null : uri.Port);
        text = text.TrimEnd('/');
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port)) return (text[..colon], port);
        return (text.Trim('[', ']'), null);
    }

    public static Dictionary<string, string> Normalize(Dictionary<string, string> settings, int defaultPort)
    {
        var (host, port) = ParseHost(settings.GetValueOrDefault("host", string.Empty));
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new StorageException(StorageErrorKind.InvalidName, $"\"{settings.GetValueOrDefault("host")}\" isn't a valid server name.");
        settings["host"] = host;
        if (port is { } p && !settings.ContainsKey("port")) settings["port"] = p.ToString();
        if (!int.TryParse(settings.GetValueOrDefault("port", defaultPort.ToString()), out int value) || value is < 1 or > 65535)
            throw new StorageException(StorageErrorKind.InvalidName, "The port is a number from 1 to 65535.");
        settings["port"] = value.ToString();
        return settings;
    }
}

public sealed class FtpConnector(ICredentialStore store) : FormConnectorBase(store)
{
    public const string Explicit = "FTPS – explicit TLS (recommended)";
    public const string Implicit = "FTPS – implicit TLS (port 990)";
    public const string Plain = "FTP – no encryption";

    public override CloudProviderType Type => CloudProviderType.Ftp;

    public override string Name => "FTP / FTPS";

    public override CloudCategory Category => CloudCategory.Servers;

    public override string Description => "Your FTP server or NAS";

    public override string Keywords => "ftp ftps ftpes tls server nas synology qnap filezilla hosting";

    public override IReadOnlyList<CloudField> Fields { get; } =
    [
        new("host", "Server") { Placeholder = "ftp.example.com" },
        new("security", "Encryption", CloudFieldKind.Choice) { Choices = [Explicit, Implicit, Plain], Default = Explicit,
            Help = "Without encryption, your password and files cross the network readable by anyone in between." },
        new("port", "Port", CloudFieldKind.Number) { Optional = true, Placeholder = "21" },
        new("user", "User name") { Optional = true, Placeholder = "anonymous" },
        new("password", "Password", CloudFieldKind.Secret) { Optional = true },
        new("trustAny", "Accept self-signed certificates", CloudFieldKind.Toggle) { Optional = true, Default = "false",
            Help = "Only for your own server: any certificate is accepted, so a man-in-the-middle wouldn't be noticed." },
        new("active", "Active mode (only if passive mode fails)", CloudFieldKind.Toggle) { Optional = true, Default = "false" },
    ];

    protected override Dictionary<string, string> Normalize(Dictionary<string, string> settings) =>
        ServerFields.Normalize(settings, settings.GetValueOrDefault("security") == Implicit ? 990 : 21);

    protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets) => new FtpProvider(account, new FtpOptions(
        account.Setting("host"),
        account.Int("port", 21),
        account.Setting("user"),
        secrets.Get("password") ?? string.Empty,
        account.Setting("security") switch { Plain => FtpSecurity.None, Implicit => FtpSecurity.Implicit, _ => FtpSecurity.Explicit },
        account.IsTrue("trustAny"),
        account.IsTrue("active")));
}

public sealed class SftpConnector(ICredentialStore store) : FormConnectorBase(store)
{
    public const string HostKeySetting = "hostKey";

    public override CloudProviderType Type => CloudProviderType.Sftp;

    public override string Name => "SFTP";

    public override CloudCategory Category => CloudCategory.Servers;

    public override string Description => "SSH file transfer to your server or NAS";

    public override string Keywords => "ssh sftp server linux nas synology qnap raspberry vps";

    public override IReadOnlyList<CloudField> Fields { get; } =
    [
        new("host", "Server") { Placeholder = "server.example.com" },
        new("port", "Port", CloudFieldKind.Number) { Optional = true, Placeholder = "22" },
        new("user", "User name"),
        new("password", "Password", CloudFieldKind.Secret) { Optional = true, Help = "Leave empty when you sign in with a key." },
        new("keyFile", "Private key file", CloudFieldKind.FilePath) { Optional = true,
            Help = "OpenSSH or PuTTY key (e.g. C:\\Users\\you\\.ssh\\id_ed25519). The file stays where it is." },
        new("passphrase", "Key passphrase", CloudFieldKind.Secret) { Optional = true },
    ];

    protected override Dictionary<string, string> Normalize(Dictionary<string, string> settings)
    {
        settings = ServerFields.Normalize(settings, 22);
        if (settings.TryGetValue("keyFile", out var key))
        {
            key = Environment.ExpandEnvironmentVariables(key.Trim('"'));
            if (!File.Exists(key)) throw new StorageException(StorageErrorKind.NotFound, $"Can't find the key file \"{key}\".");
            settings["keyFile"] = key;
        }
        return settings;
    }

    protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets) => new SftpProvider(account, new SftpOptions(
        account.Setting("host"),
        account.Int("port", 22),
        account.Setting("user"),
        secrets.Get("password"),
        account.Setting("keyFile") is { Length: > 0 } key ? key : null,
        secrets.Get("passphrase"),
        account.Setting(HostKeySetting) is { Length: > 0 } pinned ? pinned : null));

    protected override async Task<CloudIdentity> VerifyAsync(ICloudStorageProvider provider, CloudAccount account, CloudSecrets secrets,
        CancellationToken cancellationToken)
    {
        if (secrets.Get("password") is null && account.Setting("keyFile").Length == 0)
            throw new StorageException(StorageErrorKind.InvalidName, "Enter a password or choose a private key file.");
        var identity = await base.VerifyAsync(provider, account, secrets, cancellationToken).ConfigureAwait(false);
        if (provider is SftpProvider { SeenFingerprint: { } fingerprint }) account.Settings[HostKeySetting] = fingerprint; // pin it
        return identity;
    }
}
