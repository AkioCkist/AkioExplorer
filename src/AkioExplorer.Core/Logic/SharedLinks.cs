using System.Text.RegularExpressions;

namespace AkioExplorer.Core.Logic;

/// <summary>A server address typed or pasted as a URL: ftp://user:pass@host:21/path, sftp://host/path.</summary>
public sealed record ServerLink(string Scheme, string Host, int? Port, string? User, string? Password, string Path);

/// <summary>
/// Recognises links pasted into the address bar and pulls out what each service needs (file/folder ids and keys).
/// Pure string work, no network.
/// </summary>
public static partial class SharedLinks
{
    private static readonly string[] Schemes = ["http", "https", "ftp", "ftps", "ftpes", "sftp", "file"];

    /// <summary>Hosts accepted without "https://" in front ("drive.google.com/drive/folders/…").</summary>
    private static readonly string[] BareHosts =
    [
        "drive.google.com", "docs.google.com", "mega.nz", "mega.io", "mega.co.nz", "www.mediafire.com", "mediafire.com",
        "www.dropbox.com", "dropbox.com", "1drv.ms", "onedrive.live.com", "app.box.com", "www.box.com",
    ];

    /// <summary>
    /// True when <paramref name="text"/> is a link (http, https, ftp, ftps, ftpes, sftp, file) rather than a path or a
    /// command. "www.example.com/x" and the bare hosts of supported services count too; "C:\x" and "cmd" don't.
    /// </summary>
    public static bool TryParseLink(string text, out Uri link)
    {
        link = null!;
        text = text.Trim().Trim('"', '<', '>');
        if (text.Length < 4 || text.Contains(' ') && !text.Contains("://", StringComparison.Ordinal)) return false;

        int colon = text.IndexOf("://", StringComparison.Ordinal);
        if (colon > 0)
        {
            string scheme = text[..colon].ToLowerInvariant();
            if (Array.IndexOf(Schemes, scheme) < 0) return false;
            return Uri.TryCreate(text, UriKind.Absolute, out link!) && (scheme == "file" || link.Host.Length > 0);
        }

        // No scheme: only "www.…" or a known service host, so "notes.txt" or "a/b" stay paths.
        int slash = text.IndexOf('/');
        string host = (slash < 0 ? text : text[..slash]).ToLowerInvariant();
        if (!host.StartsWith("www.", StringComparison.Ordinal) && Array.IndexOf(BareHosts, host) < 0) return false;
        if (host.IndexOf('.') < 0 || host.Contains('\\')) return false;
        return Uri.TryCreate("https://" + text, UriKind.Absolute, out link!);
    }

    /// <summary>ftp://, ftps:// (implicit TLS), ftpes:// (explicit TLS) and sftp:// addresses.</summary>
    public static bool TryParseServer(Uri link, out ServerLink server)
    {
        server = null!;
        string scheme = link.Scheme.ToLowerInvariant();
        if (scheme is not ("ftp" or "ftps" or "ftpes" or "sftp") || link.Host.Length == 0) return false;

        string? user = null, password = null;
        if (link.UserInfo.Length > 0)
        {
            int split = link.UserInfo.IndexOf(':');
            user = Uri.UnescapeDataString(split < 0 ? link.UserInfo : link.UserInfo[..split]);
            if (split >= 0) password = Uri.UnescapeDataString(link.UserInfo[(split + 1)..]);
        }
        int? port = link.IsDefaultPort || link.Port <= 0 ? null : link.Port;
        string path = Uri.UnescapeDataString(link.AbsolutePath);
        if (path.Length > 1) path = path.TrimEnd('/');
        server = new ServerLink(scheme, link.Host, port, string.IsNullOrEmpty(user) ? null : user, password, path.Length == 0 ? "/" : path);
        return true;
    }

    /// <summary>
    /// drive.google.com/drive/folders/ID, …/drive/u/0/folders/ID, …/file/d/ID/view, …/open?id=ID,
    /// docs.google.com/document/d/ID/edit (Docs, Sheets, Slides, Forms). <paramref name="isFolder"/> is what the link
    /// says; null when it doesn't say.
    /// </summary>
    public static bool TryGetGoogleDriveId(Uri link, out string id, out bool? isFolder)
    {
        id = string.Empty;
        isFolder = null;
        string host = link.Host.ToLowerInvariant();
        if (host is not ("drive.google.com" or "docs.google.com")) return false;

        string path = link.AbsolutePath;
        var folder = DriveFolderPath().Match(path);
        if (folder.Success)
        {
            id = folder.Groups[1].Value;
            isFolder = true;
            return true;
        }
        var file = DriveFilePath().Match(path);
        if (file.Success)
        {
            id = file.Groups[1].Value;
            isFolder = false;
            return true;
        }
        if (QueryValue(link, "id") is { } queryId && DriveId().IsMatch(queryId))
        {
            id = queryId;
            return true;
        }
        return false;
    }

    /// <summary>
    /// mediafire.com/folder/KEY[/name], mediafire.com/file/KEY[/name/file], /view/KEY, /download/KEY, /?KEY and
    /// /file_premium/KEY. Folder keys and file "quick keys" are told apart by the link, else by length (folders: 13).
    /// </summary>
    public static bool TryParseMediaFire(Uri link, out string key, out bool isFolder)
    {
        key = string.Empty;
        isFolder = false;
        string host = link.Host.ToLowerInvariant();
        if (host is not ("www.mediafire.com" or "mediafire.com")) return false;

        var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            string kind = segments[0].ToLowerInvariant();
            if (kind is "folder" or "file" or "view" or "download" or "file_premium" && MediaFireKey().IsMatch(segments[1]))
            {
                key = segments[1];
                isFolder = kind == "folder";
                return true;
            }
        }
        // Old style: mediafire.com/?KEY (folder or file) and mediafire.com/#KEY (folder).
        string old = link.Query.TrimStart('?');
        if (old.Length == 0) old = link.Fragment.TrimStart('#');
        int amp = old.IndexOf('&');
        if (amp >= 0) old = old[..amp];
        if (segments.Length == 0 && MediaFireKey().IsMatch(old))
        {
            key = old;
            isFolder = old.Length <= 13;
            return true;
        }
        return false;
    }

    /// <summary>
    /// dropbox.com/scl/fo/… and /sh/… (folders), /scl/fi/… and /s/… (files). The link is used as it is (its rlkey
    /// is part of it).
    /// </summary>
    public static bool TryParseDropbox(Uri link, out bool isFolder)
    {
        isFolder = false;
        string host = link.Host.ToLowerInvariant();
        if (host is not ("www.dropbox.com" or "dropbox.com")) return false;
        var segments = link.AbsolutePath.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && segments[0] == "scl" && segments[1] is "fo" or "fi")
        {
            isFolder = segments[1] == "fo";
            return true;
        }
        if (segments.Length >= 2 && segments[0] is "sh" or "s")
        {
            isFolder = segments[0] == "sh";
            return true;
        }
        return false;
    }

    /// <summary>
    /// OneDrive / SharePoint sharing links: 1drv.ms/…, onedrive.live.com/…?id=…|resid=…|redeem=…, and
    /// *.sharepoint.com/:f:/… (":f:" folder, ":w:", ":x:", ":b:"… files). <paramref name="isFolder"/> is null when the
    /// link doesn't say.
    /// </summary>
    public static bool TryParseOneDrive(Uri link, out bool? isFolder)
    {
        isFolder = null;
        string host = link.Host.ToLowerInvariant();
        if (host == "1drv.ms") return link.AbsolutePath.Length > 3;
        if (host == "onedrive.live.com")
            return link.Query.Length > 1 && (QueryValue(link, "id") ?? QueryValue(link, "resid") ?? QueryValue(link, "redeem")) is not null;
        if (host.EndsWith(".sharepoint.com", StringComparison.Ordinal) && SharePointKind().Match(link.AbsolutePath) is { Success: true } kind)
        {
            isFolder = kind.Groups[1].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        return false;
    }

    /// <summary>Graph's share id for a sharing URL: "u!" + unpadded base64url of the URL.</summary>
    public static string GraphShareId(Uri link) =>
        "u!" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(link.OriginalString)).TrimEnd('=').Replace('/', '_').Replace('+', '-');

    /// <summary>Box shared links: app.box.com/s/…, *.app.box.com/s/…, and custom /v/… vanity links.</summary>
    public static bool TryParseBox(Uri link)
    {
        string host = link.Host.ToLowerInvariant();
        if (host is not ("app.box.com" or "box.com" or "www.box.com") && !host.EndsWith(".app.box.com", StringComparison.Ordinal) &&
            !host.EndsWith(".box.com", StringComparison.Ordinal))
            return false;
        var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0] is "s" or "v";
    }

    /// <summary>
    /// The service whose files the app can browse from <paramref name="link"/> ("Google Drive", "MEGA", "FTP"…), or
    /// null for an ordinary web page. Only says what the link looks like; opening it can still fail.
    /// </summary>
    public static string? BrowsableService(Uri link)
    {
        if (TryParseServer(link, out var server))
            return server.Scheme switch { "sftp" => "SFTP", "ftps" or "ftpes" => "FTPS", _ => "FTP" };
        if (TryGetGoogleDriveId(link, out _, out _)) return "Google Drive";
        if (IsMegaLink(link)) return "MEGA";
        if (TryParseMediaFire(link, out _, out _)) return "MediaFire";
        if (TryParseDropbox(link, out _)) return "Dropbox";
        if (TryParseOneDrive(link, out _)) return "OneDrive";
        if (TryParseBox(link)) return "Box";
        return null;
    }

    /// <summary>mega.nz/file/…, mega.nz/folder/… and the older #!… / #F!… forms (key checked when opened).</summary>
    private static bool IsMegaLink(Uri link)
    {
        string host = link.Host.ToLowerInvariant();
        if (!host.EndsWith("mega.nz", StringComparison.Ordinal) && !host.EndsWith("mega.co.nz", StringComparison.Ordinal) &&
            !host.EndsWith("mega.io", StringComparison.Ordinal))
            return false;
        string path = link.AbsolutePath.Trim('/');
        string fragment = link.Fragment.TrimStart('#');
        return (path.StartsWith("file/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("folder/", StringComparison.OrdinalIgnoreCase)) && fragment.Length > 0
               || fragment.StartsWith('!') || fragment.StartsWith("F!", StringComparison.Ordinal);
    }

    /// <summary>file:///C:/folder → C:\folder (and \\server\share for file://server/share).</summary>
    public static string? TryGetLocalPath(Uri link) => link.IsFile ? link.LocalPath : null;

    private static string? QueryValue(Uri link, string name)
    {
        foreach (var part in link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    [GeneratedRegex(@"/(?:drive/(?:u/\d+/)?(?:mobile/)?folders|embeddedfolderview)/([A-Za-z0-9_-]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex DriveFolderPath();

    [GeneratedRegex(@"/(?:file|document|spreadsheets|presentation|forms|drawings)/(?:u/\d+/)?d/(?:e/)?([A-Za-z0-9_-]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex DriveFilePath();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,}$")]
    private static partial Regex DriveId();

    [GeneratedRegex(@"^[A-Za-z0-9]{8,20}$")]
    private static partial Regex MediaFireKey();

    [GeneratedRegex(@"^/:([a-z]):/", RegexOptions.IgnoreCase)]
    private static partial Regex SharePointKind();
}
