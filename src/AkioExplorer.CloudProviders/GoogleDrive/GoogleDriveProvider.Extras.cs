using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Google.Apis.Drive.v3.Data;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace AkioExplorer.CloudProviders.GoogleDrive;

/// <summary>
/// Google Drive web features beyond plain files: "Shared with me" and "Shared drives" as folders, Google Docs
/// (open in the browser, create, download as…, convert), sharing with people, comments, activity, versions
/// (revisions), folder colours, shortcuts, descriptions, locking.
/// </summary>
public sealed partial class GoogleDriveProvider : IWebViewProvider, INativeDocumentsProvider, IExportProvider, IDescriptionProvider,
    IShortcutProvider, ILockProvider, ISharingProvider, ICommentsProvider, IActivityProvider, IVersionsProvider
{
    /// <summary>Virtual folders shown at the top of My Drive (not Drive ids: those never start with "~").</summary>
    public const string SharedDrivesPath = "~shared-drives";

    public const string SharedWithMePath = "~shared-with-me";

    private const string SharedDrivesName = "Shared drives";
    private const string SharedWithMeName = "Shared with me";
    private const string DefaultFolderColor = "#8f8f8f";

    /// <summary>Item id → id of the shared drive it lives in (listing inside a shared drive needs it).</summary>
    private readonly ConcurrentDictionary<string, string> _driveOf = new();

    /// <summary>Shared drive id → name.</summary>
    private readonly ConcurrentDictionary<string, string> _sharedDrives = new();

    private bool? _hasSharedDrives;
    private string? _myPermissionId;

    private static bool IsVirtual(string path) => path is SharedDrivesPath or SharedWithMePath;

    private StorageItem? VirtualFolder(string path) => path switch
    {
        SharedDrivesPath => new StorageItem { Name = SharedDrivesName, Path = SharedDrivesPath, Kind = StorageItemKind.Folder, TypeDescription = "Shared drives" },
        SharedWithMePath => new StorageItem { Name = SharedWithMeName, Path = SharedWithMePath, Kind = StorageItemKind.Folder, TypeDescription = "Shared with me" },
        _ => null,
    };

    private static void EnsureWritable(string parentPath)
    {
        if (parentPath == SharedWithMePath)
            throw new StorageException(StorageErrorKind.NotSupported, "Items can't be added to \"Shared with me\"; open one of the shared folders instead.");
        if (parentPath == SharedDrivesPath)
            throw new StorageException(StorageErrorKind.NotSupported, "Open a shared drive first; only new shared drives can be created here.");
    }

    private async Task<IReadOnlyList<StorageItem>> VirtualFoldersAsync(CancellationToken cancellationToken)
    {
        var list = new List<StorageItem> { VirtualFolder(SharedWithMePath)! };
        if (_hasSharedDrives is null)
        {
            try
            {
                var request = _drive.Drives.List();
                request.PageSize = 1;
                request.Fields = "drives(id)";
                _hasSharedDrives = (await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), Root).ConfigureAwait(false)).Drives?.Count > 0;
            }
            catch (StorageException)
            {
                _hasSharedDrives = false;
            }
        }
        if (_hasSharedDrives == true) list.Insert(0, VirtualFolder(SharedDrivesPath)!);
        return list;
    }

    private async Task<IReadOnlyList<StorageItem>> ListSharedDrivesAsync(CancellationToken cancellationToken)
    {
        var items = new List<StorageItem>();
        string? page = null;
        do
        {
            var request = _drive.Drives.List();
            request.PageSize = 100;
            request.Fields = "nextPageToken, drives(id,name,createdTime)";
            request.PageToken = page;
            var result = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), SharedDrivesPath).ConfigureAwait(false);
            foreach (var drive in result.Drives ?? [])
            {
                _sharedDrives[drive.Id] = drive.Name;
                _driveOf[drive.Id] = drive.Id;
                _nodes[drive.Id] = (drive.Name, SharedDrivesPath);
                items.Add(new StorageItem
                {
                    Name = drive.Name, Path = drive.Id, Kind = StorageItemKind.Folder, TypeDescription = "Shared drive",
                    Created = drive.CreatedTimeDateTimeOffset?.ToLocalTime(),
                });
            }
            page = result.NextPageToken;
        } while (page is not null);
        _hasSharedDrives = items.Count > 0;
        return items;
    }

    private async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListSharedWithMeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? page = null;
        do
        {
            var request = _drive.Files.List();
            request.Q = "sharedWithMe = true and trashed = false";
            request.Fields = $"nextPageToken, files({FileFields})";
            request.PageSize = 1000;
            request.OrderBy = "sharedWithMeTime desc";
            request.PageToken = page;
            var result = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), SharedWithMePath).ConfigureAwait(false);
            page = result.NextPageToken;
            yield return (result.Files ?? []).Select(f => ToItem(f, SharedWithMePath)).ToList();
        } while (page is not null);
    }

    private async Task<StorageItem> CreateSharedDriveAsync(string name, CancellationToken cancellationToken)
    {
        var create = _drive.Drives.Create(new Drive { Name = name }, Guid.NewGuid().ToString("N"));
        create.Fields = "id,name";
        var drive = await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), SharedDrivesPath).ConfigureAwait(false);
        _sharedDrives[drive.Id] = drive.Name;
        _driveOf[drive.Id] = drive.Id;
        _nodes[drive.Id] = (drive.Name, SharedDrivesPath);
        _hasSharedDrives = true;
        return new StorageItem { Name = drive.Name, Path = drive.Id, Kind = StorageItemKind.Folder, TypeDescription = "Shared drive" };
    }

    private async Task<StorageItem> RenameSharedDriveAsync(string id, string name, CancellationToken cancellationToken)
    {
        var update = _drive.Drives.Update(new Drive { Name = name }, id);
        await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), id).ConfigureAwait(false);
        _sharedDrives[id] = name;
        _nodes[id] = (name, SharedDrivesPath);
        return new StorageItem { Name = name, Path = id, Kind = StorageItemKind.Folder, TypeDescription = "Shared drive" };
    }

    private async Task DeleteSharedDriveAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(() => _drive.Drives.Delete(id).ExecuteAsync(cancellationToken), id).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Kind is StorageErrorKind.AccessDenied or StorageErrorKind.Unknown)
        {
            throw new StorageException(ex.Kind, $"Couldn't delete the shared drive \"{GetDisplayName(id)}\". Google only deletes empty shared drives, and only for managers. ({ex.Message})", ex);
        }
        _sharedDrives.TryRemove(id, out _);
    }

    // ------------------------------------------------------------------ folder colours (labels)

    public bool LabelsFoldersOnly => true;

    private static ItemLabel LabelOfColor(string? rgb)
    {
        if (string.IsNullOrEmpty(rgb) || rgb.Equals(DefaultFolderColor, StringComparison.OrdinalIgnoreCase) || rgb.Length != 7) return ItemLabel.None;
        int r = Convert.ToInt32(rgb[1..3], 16), g = Convert.ToInt32(rgb[3..5], 16), b = Convert.ToInt32(rgb[5..7], 16);
        return Enum.GetValues<ItemLabel>().Where(l => l != ItemLabel.None)
            .MinBy(l =>
            {
                var (lr, lg, lb) = LabelRgb(l);
                return (lr - r) * (lr - r) + (lg - g) * (lg - g) + (lb - b) * (lb - b);
            });
    }

    /// <summary>Same palette as MEGA's labels; Drive snaps it to the nearest of its own folder colours.</summary>
    private static (int R, int G, int B) LabelRgb(ItemLabel label) => label switch
    {
        ItemLabel.Red => (0xF0, 0x37, 0x3A),
        ItemLabel.Orange => (0xF9, 0x89, 0x1F),
        ItemLabel.Yellow => (0xF5, 0xC3, 0x0F),
        ItemLabel.Green => (0x4C, 0xC8, 0x4A),
        ItemLabel.Blue => (0x3E, 0x8B, 0xF6),
        ItemLabel.Purple => (0xA3, 0x5E, 0xD8),
        _ => (0x9A, 0x9A, 0x9A),
    };

    // ------------------------------------------------------------------ Google Docs: browser, create, export, convert

    public bool OpensInBrowser(StorageItem item) =>
        !item.IsContainer && IsNative(item, out var mime) && mime != ShortcutMime;

    public async Task<string> GetWebUrlAsync(StorageItem item, CancellationToken cancellationToken)
    {
        if (item.Path == Root) return "https://drive.google.com/drive/my-drive";
        if (item.Path == SharedWithMePath) return "https://drive.google.com/drive/shared-with-me";
        if (item.Path == SharedDrivesPath) return "https://drive.google.com/drive/shared-drives";
        var get = _drive.Files.Get(item.Path);
        get.Fields = "webViewLink";
        get.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        return file.WebViewLink ?? $"https://drive.google.com/open?id={item.Path}";
    }

    public IReadOnlyList<NativeDocumentType> DocumentTypes { get; } =
    [
        new("application/vnd.google-apps.document", "Google Docs", "Untitled document", ""),
        new("application/vnd.google-apps.spreadsheet", "Google Sheets", "Untitled spreadsheet", ""),
        new("application/vnd.google-apps.presentation", "Google Slides", "Untitled presentation", ""),
        new("application/vnd.google-apps.form", "Google Forms", "Untitled form", ""),
        new("application/vnd.google-apps.drawing", "Google Drawings", "Untitled drawing", ""),
    ];

    public async Task<StorageItem> CreateDocumentAsync(string parentPath, string name, NativeDocumentType type, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        EnsureWritable(parentPath);
        string parent = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var create = _drive.Files.Create(new DriveFile { Name = name, MimeType = type.Id, Parents = [parent] });
        create.Fields = FileFields;
        create.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), parentPath).ConfigureAwait(false);
        return ToItem(file, parent);
    }

    private static string? ConvertTarget(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".docx" or ".doc" or ".odt" or ".rtf" or ".txt" or ".html" or ".htm" or ".md" => "application/vnd.google-apps.document",
        ".xlsx" or ".xls" or ".ods" or ".csv" or ".tsv" => "application/vnd.google-apps.spreadsheet",
        ".pptx" or ".ppt" or ".odp" => "application/vnd.google-apps.presentation",
        _ => null,
    };

    public bool CanConvert(StorageItem item) => !item.IsContainer && !IsNative(item, out _) && ConvertTarget(item.Name) is not null;

    /// <summary>A Google Docs/Sheets/Slides copy next to the original (the original stays).</summary>
    public async Task<StorageItem> ConvertAsync(StorageItem item, CancellationToken cancellationToken)
    {
        string target = ConvertTarget(item.Name) ?? throw new StorageException(StorageErrorKind.NotSupported, $"\"{item.Name}\" can't be converted.");
        var copy = _drive.Files.Copy(new DriveFile { Name = Path.GetFileNameWithoutExtension(item.Name), MimeType = target }, item.Path);
        copy.Fields = FileFields;
        copy.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => copy.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        return ToItem(file, file.Parents?.FirstOrDefault());
    }

    public IReadOnlyList<ExportOption> ExportOptions(StorageItem item)
    {
        if (item.IsContainer || !IsNative(item, out var mime)) return [];
        return mime switch
        {
            "application/vnd.google-apps.document" =>
            [
                new("PDF document", ".pdf", "application/pdf"),
                new("Microsoft Word", ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
                new("OpenDocument text", ".odt", "application/vnd.oasis.opendocument.text"),
                new("Rich text", ".rtf", "application/rtf"),
                new("Plain text", ".txt", "text/plain"),
                new("Markdown", ".md", "text/markdown"),
                new("Web page (zipped)", ".zip", "application/zip"),
                new("EPUB", ".epub", "application/epub+zip"),
            ],
            "application/vnd.google-apps.spreadsheet" =>
            [
                new("Microsoft Excel", ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
                new("PDF document", ".pdf", "application/pdf"),
                new("OpenDocument spreadsheet", ".ods", "application/vnd.oasis.opendocument.spreadsheet"),
                new("CSV (current sheet)", ".csv", "text/csv"),
                new("TSV (current sheet)", ".tsv", "text/tab-separated-values"),
                new("Web page (zipped)", ".zip", "application/zip"),
            ],
            "application/vnd.google-apps.presentation" =>
            [
                new("Microsoft PowerPoint", ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
                new("PDF document", ".pdf", "application/pdf"),
                new("OpenDocument presentation", ".odp", "application/vnd.oasis.opendocument.presentation"),
                new("Plain text", ".txt", "text/plain"),
                new("JPEG image (first slide)", ".jpg", "image/jpeg"),
                new("PNG image (first slide)", ".png", "image/png"),
                new("SVG image (first slide)", ".svg", "image/svg+xml"),
            ],
            "application/vnd.google-apps.drawing" =>
            [
                new("PNG image", ".png", "image/png"),
                new("JPEG image", ".jpg", "image/jpeg"),
                new("SVG image", ".svg", "image/svg+xml"),
                new("PDF document", ".pdf", "application/pdf"),
            ],
            "application/vnd.google-apps.script" => [new("Apps Script project", ".json", "application/vnd.google-apps.script+json")],
            _ => [],
        };
    }

    public Task<Stream> OpenExportAsync(StorageItem item, ExportOption format, CancellationToken cancellationToken) =>
        OpenUrlAsync($"{ApiBase}/files/{Uri.EscapeDataString(item.Path)}/export?mimeType={Uri.EscapeDataString(format.MimeType)}", item, cancellationToken);

    // ------------------------------------------------------------------ description, shortcut, lock

    public async Task<string?> GetDescriptionAsync(StorageItem item, CancellationToken cancellationToken)
    {
        var get = _drive.Files.Get(item.Path);
        get.Fields = "description";
        get.SupportsAllDrives = true;
        return (await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false)).Description;
    }

    public async Task SetDescriptionAsync(StorageItem item, string description, CancellationToken cancellationToken)
    {
        var update = _drive.Files.Update(new DriveFile { Description = description }, item.Path);
        update.SupportsAllDrives = true;
        await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
    }

    public async Task<StorageItem> CreateShortcutAsync(StorageItem target, string parentPath, CancellationToken cancellationToken)
    {
        EnsureWritable(parentPath);
        string parent = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var create = _drive.Files.Create(new DriveFile
        {
            Name = target.Name, MimeType = ShortcutMime, Parents = [parent], ShortcutDetails = new DriveFile.ShortcutDetailsData { TargetId = target.Path },
        });
        create.Fields = FileFields;
        create.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), parentPath).ConfigureAwait(false);
        return ToItem(file, parent);
    }

    public async Task<bool> IsLockedAsync(StorageItem item, CancellationToken cancellationToken)
    {
        var get = _drive.Files.Get(item.Path);
        get.Fields = "contentRestrictions(readOnly)";
        get.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        return file.ContentRestrictions?.Any(r => r.ReadOnly__ == true) == true;
    }

    public async Task SetLockedAsync(StorageItem item, bool locked, CancellationToken cancellationToken)
    {
        var update = _drive.Files.Update(new DriveFile
        {
            ContentRestrictions = [new ContentRestriction { ReadOnly__ = locked, Reason = locked ? "Locked in AkioExplorer" : null }],
        }, item.Path);
        update.SupportsAllDrives = true;
        await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ sharing

    private static ShareRole RoleOf(string? role) => role switch
    {
        "owner" => ShareRole.Owner,
        "writer" or "fileOrganizer" or "organizer" => ShareRole.Editor,
        "commenter" => ShareRole.Commenter,
        _ => ShareRole.Viewer,
    };

    private static string RoleName(ShareRole role) => role switch
    {
        ShareRole.Owner => "owner",
        ShareRole.Editor => "writer",
        ShareRole.Commenter => "commenter",
        _ => "reader",
    };

    private async Task<string?> MyPermissionIdAsync(CancellationToken cancellationToken)
    {
        if (_myPermissionId is not null) return _myPermissionId;
        var about = _drive.About.Get();
        about.Fields = "user(permissionId)";
        return _myPermissionId = (await ExecuteAsync(() => about.ExecuteAsync(cancellationToken), Root).ConfigureAwait(false)).User?.PermissionId;
    }

    private async Task<IList<Permission>> PermissionsAsync(string id, CancellationToken cancellationToken)
    {
        var list = _drive.Permissions.List(id);
        list.Fields = "permissions(id,type,role,emailAddress,displayName,domain,permissionDetails(inherited))";
        list.SupportsAllDrives = true;
        list.PageSize = 100;
        return (await ExecuteAsync(() => list.ExecuteAsync(cancellationToken), id).ConfigureAwait(false)).Permissions ?? [];
    }

    public async Task<ShareSettings> GetSharingAsync(StorageItem item, CancellationToken cancellationToken)
    {
        string? me = await MyPermissionIdAsync(cancellationToken).ConfigureAwait(false);
        var permissions = await PermissionsAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var anyone = permissions.FirstOrDefault(p => p.Type == "anyone");
        var people = permissions.Where(p => p.Type != "anyone")
            .Select(p => new SharePermission(p.Id,
                p.DisplayName ?? p.EmailAddress ?? p.Domain ?? p.Type,
                p.EmailAddress ?? (p.Type == "domain" ? $"Anyone at {p.Domain}" : null),
                RoleOf(p.Role), p.Id == me, p.PermissionDetails?.Any(d => d.Inherited == true) == true))
            .OrderByDescending(p => p.Role == ShareRole.Owner).ThenByDescending(p => p.IsYou).ThenBy(p => p.DisplayName)
            .ToList();
        string? link = null;
        try
        {
            link = await GetWebUrlAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException)
        {
        }
        return new ShareSettings(people, anyone is null ? LinkAccess.Restricted : LinkAccess.AnyoneWithLink, RoleOf(anyone?.Role), link);
    }

    public async Task AddPersonAsync(StorageItem item, string email, ShareRole role, bool notify, string? message, CancellationToken cancellationToken)
    {
        var create = _drive.Permissions.Create(new Permission { Type = "user", Role = RoleName(role == ShareRole.Owner ? ShareRole.Editor : role), EmailAddress = email.Trim() },
            item.Path);
        create.SupportsAllDrives = true;
        create.SendNotificationEmail = notify;
        if (notify && !string.IsNullOrWhiteSpace(message)) create.EmailMessage = message;
        var added = await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        if (role == ShareRole.Owner) await SetRoleAsync(item, added.Id, ShareRole.Owner, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRoleAsync(StorageItem item, string permissionId, ShareRole role, CancellationToken cancellationToken)
    {
        if (role != ShareRole.Owner)
        {
            var update = _drive.Permissions.Update(new Permission { Role = RoleName(role) }, item.Path, permissionId);
            update.SupportsAllDrives = true;
            await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
            return;
        }
        try
        {
            // Workspace: ownership moves at once.
            var transfer = _drive.Permissions.Update(new Permission { Role = "owner" }, item.Path, permissionId);
            transfer.TransferOwnership = true;
            await ExecuteAsync(() => transfer.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            // Personal accounts: the new owner has to accept a request (they become a pending owner).
            var pending = _drive.Permissions.Update(new Permission { Role = "writer", PendingOwner = true }, item.Path, permissionId);
            await ExecuteAsync(() => pending.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
            throw new StorageException(StorageErrorKind.NotSupported,
                "Ownership request sent. The other person becomes the owner once they accept it in Google Drive.");
        }
    }

    public async Task RemovePersonAsync(StorageItem item, string permissionId, CancellationToken cancellationToken)
    {
        var delete = _drive.Permissions.Delete(item.Path, permissionId);
        delete.SupportsAllDrives = true;
        await ExecuteAsync(() => delete.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
    }

    public async Task SetLinkAccessAsync(StorageItem item, LinkAccess access, ShareRole role, CancellationToken cancellationToken)
    {
        var anyone = (await PermissionsAsync(item.Path, cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.Type == "anyone");
        if (access == LinkAccess.Restricted)
        {
            if (anyone is not null) await RemovePersonAsync(item, anyone.Id, cancellationToken).ConfigureAwait(false);
            return;
        }
        string name = RoleName(role == ShareRole.Owner ? ShareRole.Editor : role);
        if (anyone is null)
        {
            var create = _drive.Permissions.Create(new Permission { Type = "anyone", Role = name, AllowFileDiscovery = false }, item.Path);
            create.SupportsAllDrives = true;
            await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        }
        else if (anyone.Role != name)
        {
            var update = _drive.Permissions.Update(new Permission { Role = name }, item.Path, anyone.Id);
            update.SupportsAllDrives = true;
            await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ comments

    public async Task<IReadOnlyList<FileComment>> ListCommentsAsync(StorageItem item, CancellationToken cancellationToken)
    {
        var comments = new List<FileComment>();
        string? page = null;
        do
        {
            var list = _drive.Comments.List(item.Path);
            list.Fields = "nextPageToken, comments(id,author(displayName,me),content,createdTime,resolved,deleted,quotedFileContent(value)," +
                          "replies(author(displayName,me),content,createdTime,action,deleted))";
            list.PageSize = 100;
            list.PageToken = page;
            var result = await ExecuteAsync(() => list.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
            foreach (var c in result.Comments ?? [])
            {
                if (c.Deleted == true) continue;
                var replies = (c.Replies ?? []).Where(r => r.Deleted != true)
                    .Select(r => new CommentReply(Who(r.Author), ReplyText(r), r.CreatedTimeDateTimeOffset?.ToLocalTime()))
                    .ToList();
                comments.Add(new FileComment(c.Id, Who(c.Author), c.Content ?? string.Empty, c.CreatedTimeDateTimeOffset?.ToLocalTime(),
                    c.Resolved == true, c.QuotedFileContent?.Value, replies));
            }
            page = result.NextPageToken;
        } while (page is not null);
        return comments.OrderByDescending(c => c.When).ToList();

        static string Who(User? user) => user?.Me == true ? "You" : user?.DisplayName ?? "Someone";

        static string ReplyText(Reply reply) => reply.Action switch
        {
            "resolve" => string.IsNullOrEmpty(reply.Content) ? "Marked as resolved" : $"{reply.Content} (resolved)",
            "reopen" => string.IsNullOrEmpty(reply.Content) ? "Re-opened" : $"{reply.Content} (re-opened)",
            _ => reply.Content ?? string.Empty,
        };
    }

    public async Task AddCommentAsync(StorageItem item, string text, CancellationToken cancellationToken)
    {
        var create = _drive.Comments.Create(new Comment { Content = text }, item.Path);
        create.Fields = "id";
        await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
    }

    public async Task ReplyAsync(StorageItem item, string commentId, string text, bool resolve, CancellationToken cancellationToken)
    {
        var create = _drive.Replies.Create(new Reply { Content = text, Action = resolve ? "resolve" : null }, item.Path, commentId);
        create.Fields = "id";
        await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ activity (Drive Activity API v2)

    public async Task<IReadOnlyList<ActivityEntry>> ListActivityAsync(StorageItem item, CancellationToken cancellationToken)
    {
        // Names: the API gives "people/<id>", which for Google accounts is the Drive permission id of that person.
        var names = new Dictionary<string, string>();
        try
        {
            foreach (var p in await PermissionsAsync(item.Path, cancellationToken).ConfigureAwait(false))
                if (p.Id is { } id) names[id] = p.DisplayName ?? p.EmailAddress ?? "Someone";
        }
        catch (StorageException)
        {
        }

        var body = new JsonObject
        {
            [item.IsContainer ? "ancestorName" : "itemName"] = $"items/{item.Path}",
            ["pageSize"] = 100,
            ["consolidationStrategy"] = new JsonObject { ["none"] = new JsonObject() },
        };
        JsonNode? answer = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string token = await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://driveactivity.googleapis.com/v2/activity:query")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new StorageException(StorageErrorKind.AccessDenied,
                    "AkioExplorer doesn't have permission to read Drive activity for this account yet. Right-click the account › Reconnect… and allow it.");
            if (!response.IsSuccessStatusCode) throw new StorageException(StorageErrorKind.Unknown, $"Google Drive activity failed ({(int)response.StatusCode}).");
            answer = JsonNode.Parse(text);
            break;
        }

        var entries = new List<ActivityEntry>();
        foreach (var activity in answer?["activities"] as JsonArray ?? [])
        {
            string when = activity?["timestamp"]?.GetValue<string>() ?? activity?["timeRange"]?["endTime"]?.GetValue<string>() ?? string.Empty;
            var user = (activity?["actors"] as JsonArray)?.FirstOrDefault()?["user"];
            string who = user?["knownUser"] is { } known
                ? known["isCurrentUser"]?.GetValue<bool>() == true ? "You"
                  : known["personName"]?.GetValue<string>() is { } person && names.TryGetValue(person.Replace("people/", string.Empty), out var name) ? name
                  : "Someone"
                : user?["deletedUser"] is not null ? "A deleted user" : "Someone";
            string target = (activity?["targets"] as JsonArray)?.FirstOrDefault()?["driveItem"]?["title"]?.GetValue<string>() ?? item.Name;
            entries.Add(new ActivityEntry(DateTimeOffset.TryParse(when, out var at) ? at.ToLocalTime() : null, who,
                Describe(activity?["primaryActionDetail"], target, item.IsContainer)));
        }
        return entries;

        static string Describe(JsonNode? detail, string target, bool folder)
        {
            string where = folder ? $" “{target}”" : string.Empty;
            if (detail is not JsonObject action || action.Count == 0) return "Changed" + where;
            var (kind, value) = (action.First().Key, action.First().Value);
            return kind switch
            {
                "create" => (value?["upload"] is not null ? "Uploaded" : value?["copy"] is not null ? "Made a copy of" : "Created") + where,
                "edit" => "Edited" + where,
                "move" => "Moved" + where,
                "rename" => $"Renamed “{value?["oldTitle"]?.GetValue<string>()}” to “{value?["newTitle"]?.GetValue<string>()}”",
                "delete" => (value?["type"]?.GetValue<string>() == "PERMANENT_DELETE" ? "Deleted for good" : "Moved to trash") + where,
                "restore" => "Restored" + where,
                "permissionChange" => "Changed sharing of" + (folder ? where : " this item"),
                "comment" => "Commented on" + (folder ? where : " this item"),
                "dlpChange" => "Data-protection change" + where,
                "reference" => "Linked" + where,
                "settingsChange" => "Changed settings" + where,
                "appliedLabelChange" => "Changed labels" + where,
                _ => "Changed" + where,
            };
        }
    }

    // ------------------------------------------------------------------ versions (revisions)

    public bool SupportsKeepForever => true;

    public async Task<IReadOnlyList<FileVersion>> ListVersionsAsync(StorageItem file, CancellationToken cancellationToken)
    {
        var list = _drive.Revisions.List(file.Path);
        list.Fields = "revisions(id,modifiedTime,size,keepForever,originalFilename,lastModifyingUser(displayName,me))";
        list.PageSize = 200;
        var result = await ExecuteAsync(() => list.ExecuteAsync(cancellationToken), file.Path).ConfigureAwait(false);
        var revisions = (result.Revisions ?? []).ToList();
        var versions = new List<FileVersion>();
        for (int i = revisions.Count - 1; i >= 0; i--)
        {
            var r = revisions[i];
            versions.Add(new FileVersion(r.Id, r.OriginalFilename ?? file.Name, r.ModifiedTimeDateTimeOffset?.ToLocalTime(), r.Size, IsCurrent: i == revisions.Count - 1)
            {
                KeepForever = IsNative(file, out _) ? null : r.KeepForever,
                Author = r.LastModifyingUser?.Me == true ? "You" : r.LastModifyingUser?.DisplayName,
            });
        }
        return versions;
    }

    private void EnsureBinary(StorageItem file)
    {
        if (IsNative(file, out _))
            throw new StorageException(StorageErrorKind.NotSupported,
                $"Versions of Google Docs files are managed in the editor: open \"{file.Name}\" in the browser › File › Version history.");
    }

    public Task<Stream> OpenVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken)
    {
        EnsureBinary(file);
        return OpenUrlAsync($"{ApiBase}/files/{Uri.EscapeDataString(file.Path)}/revisions/{Uri.EscapeDataString(version.Id)}?alt=media", file, cancellationToken);
    }

    /// <summary>Drive has no "revert": the old version's bytes are uploaded as the newest version (the others stay).</summary>
    public async Task RestoreVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken)
    {
        if (version.IsCurrent) return;
        EnsureBinary(file);
        string temp = Path.Combine(Path.GetTempPath(), "akio-revision-" + Guid.NewGuid().ToString("N"));
        await using var spool = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (var old = await OpenVersionAsync(file, version, cancellationToken).ConfigureAwait(false))
            await old.CopyToAsync(spool, 1 << 20, cancellationToken).ConfigureAwait(false);
        spool.Position = 0;
        var update = _drive.Files.Update(new DriveFile(), file.Path, spool, Http.StreamExtensions.GuessMimeType(file.Name));
        update.SupportsAllDrives = true;
        var progress = await update.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Exception is { } ex)
            throw ex is Google.GoogleApiException api ? Map(api, file.Path) : new StorageException(StorageErrorKind.Unknown, $"Restoring the version failed: {ex.Message}", ex);
    }

    public async Task DeleteVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken)
    {
        if (version.IsCurrent) throw new StorageException(StorageErrorKind.NotSupported, "Delete the file itself to remove the current version.");
        EnsureBinary(file);
        await ExecuteAsync(() => _drive.Revisions.Delete(file.Path, version.Id).ExecuteAsync(cancellationToken), file.Path).ConfigureAwait(false);
    }

    public async Task SetKeepForeverAsync(StorageItem file, FileVersion version, bool keep, CancellationToken cancellationToken)
    {
        EnsureBinary(file);
        var update = _drive.Revisions.Update(new Revision { KeepForever = keep }, file.Path, version.Id);
        await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), file.Path).ConfigureAwait(false);
    }
}
