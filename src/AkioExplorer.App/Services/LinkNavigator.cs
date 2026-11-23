using System.Collections.Concurrent;
using AkioExplorer.CloudProviders.Ftp;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.App.Services;

/// <summary>What pasting a link into the address bar led to.</summary>
public abstract record LinkOutcome
{
    /// <summary>Show <paramref name="Folder"/> (and select <paramref name="Select"/> in it).</summary>
    public sealed record Browse(StorageLocation Folder, string? Select) : LinkOutcome;

    /// <summary>A file:/// link: an ordinary local path.</summary>
    public sealed record LocalPath(string Path) : LinkOutcome;

    /// <summary>Nothing in the app opens it: it went to the default browser.</summary>
    public sealed record OpenedInBrowser : LinkOutcome;

    public sealed record Failed(string Message) : LinkOutcome;
}

/// <summary>
/// Opens links pasted into the address bar inside the app:
/// <list type="bullet">
/// <item>Google Drive, Dropbox, OneDrive / SharePoint and Box links through a signed-in account of that service (the
/// one showing first, then the others), read-only for links to someone else's files;</item>
/// <item>MEGA and MediaFire links without an account, as a temporary read-only location;</item>
/// <item>ftp://, ftps://, ftpes:// and sftp:// through the matching server account, connected on the spot when
/// there's none yet (it then appears under Cloud accounts);</item>
/// <item>file:/// as the local path.</item>
/// </list>
/// Anything else (and a supported link no account can open) goes to the default browser.
/// </summary>
public sealed class LinkNavigator(
    IStorageProviderRegistry registry,
    ICloudAccountService accounts,
    IEnumerable<ISharedLinkSource> standalone,
    IShellActions shell,
    ILogger<LinkNavigator> logger)
{
    private readonly ISharedLinkSource[] _standalone = standalone.ToArray();

    /// <summary>Links opened this session → their temporary provider, so pasting one again reuses it.</summary>
    private readonly ConcurrentDictionary<string, SharedLinkTarget> _opened = new(StringComparer.Ordinal);

    public static bool IsLink(string text) => SharedLinks.TryParseLink(text, out _);

    public async Task<LinkOutcome> OpenAsync(string text, IStorageProvider? current, CancellationToken cancellationToken)
    {
        if (!SharedLinks.TryParseLink(text, out var link)) return new LinkOutcome.Failed($"\"{text.Trim()}\" isn't a link.");

        if (SharedLinks.TryGetLocalPath(link) is { } local) return new LinkOutcome.LocalPath(local);
        if (SharedLinks.TryParseServer(link, out var server)) return await OpenServerAsync(server, cancellationToken);

        string key = link.AbsoluteUri;
        if (_opened.TryGetValue(key, out var known) && registry.Find(known.Provider.Id) is not null) return Browse(known);

        // Signed-in accounts first (the link may be shared with one of them only), the one showing before the others.
        var candidates = registry.Providers.OfType<ISharedLinkSource>().Where(s => s.CanOpenSharedLink(link))
            .OrderByDescending(s => ReferenceEquals(s, current))
            .Concat(_standalone.Where(s => s.CanOpenSharedLink(link)))
            .ToList();

        foreach (var source in candidates)
        {
            try
            {
                var target = await source.OpenSharedLinkAsync(link, cancellationToken);
                if (target.IsTemporary)
                {
                    registry.Register(target.Provider);
                    _opened[key] = target;
                }
                return Browse(target);
            }
            catch (StorageException ex)
            {
                // Not shared with this account, removed, or the service said no: the next one, then the browser.
                logger.LogInformation(ex, "{Source} couldn't open a shared link", (source as IStorageProvider)?.DisplayName ?? source.GetType().Name);
            }
        }

        // Not something the app browses (or no account here can see it): the browser can.
        if (link.Scheme is "http" or "https")
        {
            shell.Open(link.AbsoluteUri);
            return new LinkOutcome.OpenedInBrowser();
        }
        return new LinkOutcome.Failed($"AkioExplorer can't open {link.Scheme}:// links.");
    }

    private static LinkOutcome.Browse Browse(SharedLinkTarget target) =>
        new(new StorageLocation(target.Provider.Id, target.FolderPath), target.SelectPath);

    // ------------------------------------------------------------------ FTP / SFTP

    private async Task<LinkOutcome> OpenServerAsync(ServerLink server, CancellationToken cancellationToken)
    {
        var type = server.Scheme == "sftp" ? CloudProviderType.Sftp : CloudProviderType.Ftp;
        int defaultPort = server.Scheme switch { "sftp" => 22, "ftps" => 990, _ => 21 };
        int port = server.Port ?? defaultPort;

        var account = accounts.Accounts.FirstOrDefault(a => a.Type == type &&
            string.Equals(a.Setting("host"), server.Host, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(a.Setting("port", defaultPort.ToString()), out int p) && p == port &&
            (server.User is null || string.Equals(a.Setting("user"), server.User, StringComparison.Ordinal)));

        try
        {
            account ??= await ConnectAsync(type, server, port, cancellationToken);
        }
        catch (StorageException ex)
        {
            return new LinkOutcome.Failed($"Couldn't connect to {server.Host}: {ex.Message}");
        }

        if (accounts.GetProvider(account.Id) is not { } provider)
            return new LinkOutcome.Failed($"\"{account.DisplayName}\" isn't connected. Reconnect it under Cloud accounts.");

        string path = provider.TryNormalizePath(server.Path) ?? provider.RootPath;
        if (path == provider.RootPath) return new LinkOutcome.Browse(new StorageLocation(provider.Id, path), null);
        try
        {
            var item = await provider.GetItemAsync(path, cancellationToken);
            if (item is null) return new LinkOutcome.Failed($"Can't find \"{server.Path}\" on {server.Host}.");
            if (item.IsContainer) return new LinkOutcome.Browse(new StorageLocation(provider.Id, item.Path), null);
            return new LinkOutcome.Browse(new StorageLocation(provider.Id, provider.GetParentPath(item.Path) ?? provider.RootPath), item.Path);
        }
        catch (StorageException ex)
        {
            return new LinkOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// A server not added yet: connected with what the link says and kept under Cloud accounts. ftp:// tries
    /// encryption first and falls back to plain FTP (like FileZilla); ftps:// is implicit TLS, ftpes:// explicit only.
    /// </summary>
    private async Task<CloudAccount> ConnectAsync(CloudProviderType type, ServerLink server, int port, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = server.Host,
            ["port"] = port.ToString(),
        };
        if (server.User is not null) values["user"] = server.User;
        if (server.Password is not null) values["password"] = server.Password;
        if (type == CloudProviderType.Sftp)
        {
            if (server.User is null) throw new StorageException(StorageErrorKind.InvalidName, "SFTP needs a user name: sftp://user@server/");
            return await accounts.AddAccountAsync(type, values, cancellationToken);
        }

        values["security"] = server.Scheme switch { "ftps" => FtpConnector.Implicit, _ => FtpConnector.Explicit };
        try
        {
            return await accounts.AddAccountAsync(type, values, cancellationToken);
        }
        catch (StorageException ex) when (server.Scheme == "ftp" && ex.Kind != StorageErrorKind.AuthenticationRequired)
        {
            logger.LogInformation(ex, "FTPS failed for {Host}; trying plain FTP as the ftp:// link asks", server.Host);
            values["security"] = FtpConnector.Plain;
            return await accounts.AddAccountAsync(type, values, cancellationToken);
        }
    }
}
