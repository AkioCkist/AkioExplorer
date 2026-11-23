using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace AkioExplorer.CloudProviders.GoogleDrive;

/// <summary>
/// Google Drive through the official Drive v3 SDK. Drive is id-based (names aren't unique), so a "path" here is a
/// file id; "root" is My Drive. Names/parents learned while browsing are cached so breadcrumbs, Up and titles work
/// without extra requests. <see cref="CombinePath"/> returns a virtual "parentId/name" address that
/// <see cref="GetItemAsync"/> resolves by name, which lets the generic copy engine check for conflicts.
/// </summary>
public sealed partial class GoogleDriveProvider : ICloudStorageProvider, IFileContentProvider, IThumbnailProvider, IDirectDownloadProvider, ITrashProvider, IShareLinkProvider, IItemMarksProvider, ISharedLinkSource
{
    public const string ServiceName = "Google Drive";
    public const string Root = "root";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string ShortcutMime = "application/vnd.google-apps.shortcut";
    private const string FileFields = "id,name,mimeType,size,modifiedTime,createdTime,parents,shortcutDetails(targetId,targetMimeType),thumbnailLink,capabilities(canDownload),starred,driveId,folderColorRgb";

    private const string ApiBase = "https://www.googleapis.com/drive/v3";

    private readonly DriveService _drive;
    private readonly IAccessTokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (string Name, string? Parent)> _nodes = new();
    private readonly ConcurrentDictionary<string, string> _mimeById = new();
    private readonly ConcurrentDictionary<string, string> _thumbnailById = new();

    /// <summary>Files whose owner turned off downloading for viewers/commenters (Drive "capabilities.canDownload").</summary>
    private readonly ConcurrentDictionary<string, byte> _noDownload = new();
    private readonly ConcurrentDictionary<string, string> _resolvedFolders = new();
    private string? _rootId;

    public GoogleDriveProvider(CloudAccount account, IAccessTokenProvider tokens, HttpClient http)
        : this(account, tokens, http, null)
    {
    }

    /// <summary>Test seam: <paramref name="httpFactory"/> replaces the SDK's HTTP stack.</summary>
    internal GoogleDriveProvider(CloudAccount account, IAccessTokenProvider tokens, HttpClient http, IHttpClientFactory? httpFactory)
    {
        Account = account;
        _tokens = tokens;
        _http = http;
        _drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientFactory = httpFactory ?? new HttpClientFactory(),
            ApplicationName = "AkioExplorer",
            HttpClientInitializer = new BearerTokenInitializer(tokens),
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.Exception | ExponentialBackOffPolicy.UnsuccessfulResponse503,
            GZipEnabled = true,
        });
    }

    public CloudAccount Account { get; }

    public CloudProviderType Type => CloudProviderType.GoogleDrive;

    public string Id => Account.ProviderId;

    public string DisplayName => Account.DisplayName;

    public StorageCapabilities Capabilities =>
        StorageCapabilities.CreateFolder | StorageCapabilities.Rename | StorageCapabilities.Delete |
        StorageCapabilities.RecycleBin | StorageCapabilities.Search | StorageCapabilities.ServerSideCopy |
        StorageCapabilities.ContentSearch | StorageCapabilities.SearchSharing | StorageCapabilities.SearchTrash |
        StorageCapabilities.SearchStarred | StorageCapabilities.AccountWideSearch;

    public string RootPath => Root;

    // ------------------------------------------------------------------ listing

    public async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureRootIdAsync(cancellationToken).ConfigureAwait(false);
        if (path == SharedDrivesPath)
        {
            yield return await ListSharedDrivesAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }
        if (path == SharedWithMePath)
        {
            await foreach (var page in ListSharedWithMeAsync(cancellationToken).ConfigureAwait(false)) yield return page;
            yield break;
        }
        if (path == Root) yield return await VirtualFoldersAsync(cancellationToken).ConfigureAwait(false);

        string folderId = await ResolveFolderIdAsync(path, cancellationToken).ConfigureAwait(false);
        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            if (_driveOf.TryGetValue(folderId, out var driveId))
            {
                request.Corpora = "drive"; // inside a shared drive
                request.DriveId = driveId;
            }
            request.Q = $"'{Escape(folderId)}' in parents and trashed = false";
            request.Fields = $"nextPageToken, files({FileFields})";
            request.PageSize = 1000;
            request.OrderBy = "folder,name_natural";
            request.Spaces = "drive";
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;
            request.PageToken = pageToken;

            var result = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            pageToken = result.NextPageToken;
            yield return (result.Files ?? []).Select(f => ToItem(f, folderId)).ToList();
        }
        while (pageToken is not null);
    }

    public async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        if (path == Root) return null;
        if (VirtualFolder(path) is { } virtualFolder) return virtualFolder;

        int slash = path.LastIndexOf('/');
        if (slash > 0)
        {
            // Virtual "parentId/name" address: find a child by name.
            string parent = await ResolveFolderIdAsync(path[..slash], cancellationToken).ConfigureAwait(false);
            string name = path[(slash + 1)..];
            var request = _drive.Files.List();
            request.Q = $"'{Escape(parent)}' in parents and name = '{Escape(name)}' and trashed = false";
            request.Fields = $"files({FileFields})";
            request.PageSize = 1;
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;
            var result = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            return result.Files is { Count: > 0 } files ? ToItem(files[0], parent) : null;
        }

        try
        {
            var get = _drive.Files.Get(path);
            get.Fields = FileFields;
            get.SupportsAllDrives = true;
            var file = await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            return file.Trashed == true ? null : ToItem(file, file.Parents?.FirstOrDefault());
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ mutations

    public async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        if (parentPath == SharedDrivesPath) return await CreateSharedDriveAsync(name, cancellationToken).ConfigureAwait(false);
        EnsureWritable(parentPath);
        string parent = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        // Drive allows duplicate names; Explorer semantics don't, so refuse like a file system would.
        if (await GetItemAsync(CombinePath(parent, name), cancellationToken).ConfigureAwait(false) is not null)
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{name}\" here.");

        var create = _drive.Files.Create(new DriveFile { Name = name, MimeType = FolderMime, Parents = [parent] });
        create.Fields = FileFields;
        create.SupportsAllDrives = true;
        var folder = await ExecuteAsync(() => create.ExecuteAsync(cancellationToken), parentPath).ConfigureAwait(false);
        _resolvedFolders[CombinePath(parentPath, name)] = folder.Id;
        return ToItem(folder, parent);
    }

    public async Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        EnsureValidName(newName);
        if (IsVirtual(path)) throw new StorageException(StorageErrorKind.NotSupported, $"\"{GetDisplayName(path)}\" can't be renamed.");
        if (_sharedDrives.ContainsKey(path)) return await RenameSharedDriveAsync(path, newName, cancellationToken).ConfigureAwait(false);
        string id = await ResolveFolderIdAsync(path, cancellationToken).ConfigureAwait(false);
        if (GetParentPath(id) is { } parent && !string.Equals(GetDisplayName(id), newName, StringComparison.OrdinalIgnoreCase) &&
            await GetItemAsync(CombinePath(parent, newName), cancellationToken).ConfigureAwait(false) is not null)
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{newName}\" here.");

        var update = _drive.Files.Update(new DriveFile { Name = newName }, id);
        update.Fields = FileFields;
        update.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
        return ToItem(file, file.Parents?.FirstOrDefault());
    }

    // ------------------------------------------------------------------ links, star

    /// <summary>"Anyone with the link can view", like Drive's Share › Copy link.</summary>
    public async Task<string> CreateShareLinkAsync(StorageItem item, CancellationToken cancellationToken)
    {
        if (item.Path == Root) throw new StorageException(StorageErrorKind.NotSupported, "My Drive itself can't be shared by link.");
        var permission = _drive.Permissions.Create(new Google.Apis.Drive.v3.Data.Permission { Type = "anyone", Role = "reader" }, item.Path);
        permission.SupportsAllDrives = true;
        await ExecuteAsync(() => permission.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        var get = _drive.Files.Get(item.Path);
        get.Fields = "webViewLink";
        get.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        return file.WebViewLink ?? $"https://drive.google.com/open?id={item.Path}";
    }

    public async Task RemoveShareLinkAsync(StorageItem item, CancellationToken cancellationToken)
    {
        try
        {
            var delete = _drive.Permissions.Delete(item.Path, "anyoneWithLink");
            delete.SupportsAllDrives = true;
            await ExecuteAsync(() => delete.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            // No link to remove.
        }
    }

    /// <summary>Folder colours (Drive has none for files).</summary>
    public bool SupportsLabels => true;

    public bool SupportsHiding => false;

    public async Task SetStarredAsync(IReadOnlyList<StorageItem> items, bool starred, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var update = _drive.Files.Update(new DriveFile { Starred = starred }, item.Path);
            update.SupportsAllDrives = true;
            await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), item.Path).ConfigureAwait(false);
        }
    }

    public async Task SetLabelAsync(IReadOnlyList<StorageItem> items, ItemLabel label, CancellationToken cancellationToken)
    {
        var folders = items.Where(i => i.IsContainer && !IsVirtual(i.Path) && !_sharedDrives.ContainsKey(i.Path)).ToList();
        if (folders.Count == 0) throw new StorageException(StorageErrorKind.NotSupported, "Google Drive only colours folders.");
        string color = label == ItemLabel.None ? DefaultFolderColor : $"#{LabelRgb(label).R:x2}{LabelRgb(label).G:x2}{LabelRgb(label).B:x2}";
        foreach (var folder in folders)
        {
            var update = _drive.Files.Update(new DriveFile { FolderColorRgb = color }, folder.Path);
            update.SupportsAllDrives = true;
            await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), folder.Path).ConfigureAwait(false);
        }
    }

    public Task SetHiddenAsync(IReadOnlyList<StorageItem> items, bool hidden, CancellationToken cancellationToken) =>
        throw new StorageException(StorageErrorKind.NotSupported, "Google Drive can't hide items.");

    // ------------------------------------------------------------------ trash

    public string TrashName => "Trash";

    public string RestoreFallbackName => "My Drive";

    public async Task<IReadOnlyList<TrashItem>> ListTrashAsync(CancellationToken cancellationToken)
    {
        var files = new List<DriveFile>();
        string? page = null;
        do
        {
            var list = _drive.Files.List();
            list.Q = "trashed = true";
            list.Fields = "nextPageToken, files(id,name,mimeType,size,trashedTime,modifiedTime,explicitlyTrashed,parents)";
            list.PageSize = 1000;
            list.PageToken = page;
            var result = await ExecuteAsync(() => list.ExecuteAsync(cancellationToken), Root).ConfigureAwait(false);
            // Files inside a trashed folder are "trashed" too; only list what was deleted itself (like Drive's own Trash).
            files.AddRange(result.Files.Where(f => f.ExplicitlyTrashed != false));
            page = result.NextPageToken;
        } while (page is not null);

        await LearnParentsAsync(files.Select(f => f.Parents?.FirstOrDefault()).OfType<string>(), cancellationToken).ConfigureAwait(false);
        return files.Select(f =>
        {
            string? parent = f.Parents?.FirstOrDefault();
            string? from = parent is null ? null
                : parent == _rootId ? RestoreFallbackName
                : _nodes.TryGetValue(parent, out var node) ? node.Name
                : null;
            return new TrashItem(f.Id, f.Name ?? "(unnamed)", f.MimeType == FolderMime, f.Size,
                f.TrashedTimeDateTimeOffset?.ToLocalTime(), f.ModifiedTimeDateTimeOffset?.ToLocalTime(), from);
        }).ToList();
    }

    public async Task RestoreAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var untrash = _drive.Files.Update(new DriveFile { Trashed = false }, item.Id); // Drive puts it back in its folder
            untrash.SupportsAllDrives = true;
            await ExecuteAsync(() => untrash.ExecuteAsync(cancellationToken), item.Name).ConfigureAwait(false);
        }
        _resolvedFolders.Clear();
    }

    public async Task DeleteFromTrashAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var delete = _drive.Files.Delete(item.Id);
            delete.SupportsAllDrives = true;
            await ExecuteAsync(() => delete.ExecuteAsync(cancellationToken), item.Name).ConfigureAwait(false);
            _nodes.TryRemove(item.Id, out _);
        }
    }

    public async Task EmptyTrashAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(() => _drive.Files.EmptyTrash().ExecuteAsync(cancellationToken), Root).ConfigureAwait(false);
        _nodes.Clear(); // trashed folders may have been cached
    }

    public async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path == Root) throw new StorageException(StorageErrorKind.NotSupported, "My Drive itself can't be deleted.");
            if (IsVirtual(path)) throw new StorageException(StorageErrorKind.NotSupported, $"\"{GetDisplayName(path)}\" can't be deleted.");
            if (_sharedDrives.ContainsKey(path))
            {
                await DeleteSharedDriveAsync(path, cancellationToken).ConfigureAwait(false);
                continue;
            }
            string id = await ResolveFolderIdAsync(path, cancellationToken).ConfigureAwait(false);
            if (permanent)
            {
                var delete = _drive.Files.Delete(id);
                delete.SupportsAllDrives = true;
                await ExecuteAsync(() => delete.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            }
            else
            {
                var trash = _drive.Files.Update(new DriveFile { Trashed = true }, id);
                trash.SupportsAllDrives = true;
                await ExecuteAsync(() => trash.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            }
            _nodes.TryRemove(id, out _);
        }
    }

    // ------------------------------------------------------------------ search & quota

    /// <summary>
    /// Drive's query API can't limit a search to a subtree, so results cover the whole drive. Name, words, type and
    /// date go into the server query; size (not queryable) and wildcards are filtered here.
    /// </summary>
    public async IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!query.HasCriteria) yield break;
        var matcher = query.CreateMatcher(DateTimeOffset.Now);
        string q = BuildQuery(query, matcher);

        // The server's MIME match is authoritative (a Google Doc or an extensionless upload has no telling name),
        // so the local check only covers what the query couldn't express.
        if (MimeQuery(query.Category) is not null) matcher = (query with { Category = FileCategory.Any }).CreateMatcher(DateTimeOffset.Now);

        // Drive can't filter by size, but it can sort by it: biggest first, and stop below the minimum
        // ("Storage" page: what takes the space). Saves listing the whole account.
        long? minSize = query.SizeRange().Min;
        bool bySize = minSize is > 0 && query.NameText.Length == 0 && query.ContentText.Length == 0;

        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            request.Q = q;
            request.Fields = $"nextPageToken, files({FileFields})";
            request.PageSize = 200;
            request.Spaces = "drive";
            if (bySize) request.OrderBy = "quotaBytesUsed desc";
            request.PageToken = pageToken;
            var result = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            pageToken = result.NextPageToken;

            var files = result.Files ?? [];
            await LearnParentsAsync(files.SelectMany(f => f.Parents ?? []), cancellationToken).ConfigureAwait(false);
            foreach (var file in files)
            {
                var item = ToItem(file, file.Parents?.FirstOrDefault());
                if (matcher.Matches(item)) yield return item;
            }
            if (bySize && files.Count > 0 && files[^1].Size is { } last && last < minSize) yield break;
        }
        while (pageToken is not null);
    }

    internal static string BuildQuery(SearchQuery query, SearchMatcher matcher)
    {
        var terms = new List<string> { query.InTrash ? "trashed = true" : "trashed = false" };
        if (query.Starred) terms.Add("starred = true");
        if (query.Scope == SearchScope.SharedWithMe) terms.Add("sharedWithMe = true");
        switch (query.EffectiveOwner)
        {
            case OwnerFilter.Me: terms.Add("'me' in owners"); break;
            case OwnerFilter.NotMe: terms.Add("not 'me' in owners"); break;
            case OwnerFilter.Person: terms.Add($"'{Escape(query.OwnerEmailText)}' in owners"); break;
        }
        if (query.SharedWithText.Length > 0)
        {
            string who = Escape(query.SharedWithText);
            terms.Add($"('{who}' in readers or '{who}' in writers)");
        }
        if (query.NameText.Length > 0 && !query.HasWildcards) terms.Add($"name contains '{Escape(query.NameText)}'");
        if (query.ContentText.Length > 0) terms.Add($"fullText contains '{Escape(query.ContentText)}'");
        if (MimeQuery(query.Category) is { } mime) terms.Add(mime);
        if (matcher.ModifiedAfter is { } after) terms.Add($"modifiedTime >= '{after.UtcDateTime:yyyy-MM-ddTHH:mm:ss}'");
        if (matcher.ModifiedBefore is { } before) terms.Add($"modifiedTime < '{before.UtcDateTime:yyyy-MM-ddTHH:mm:ss}'");
        return string.Join(" and ", terms);
    }

    /// <summary>Server-side type filter. Categories with no clean MIME family return null and are filtered by extension.</summary>
    private static string? MimeQuery(FileCategory category) => category switch
    {
        FileCategory.Folder => $"mimeType = '{FolderMime}'",
        FileCategory.Image => "(mimeType contains 'image/' or mimeType = 'application/vnd.google-apps.drawing')",
        FileCategory.Video => "mimeType contains 'video/'",
        FileCategory.Media => "(mimeType contains 'image/' or mimeType contains 'video/')",
        FileCategory.Audio => "mimeType contains 'audio/'",
        FileCategory.Pdf => "mimeType = 'application/pdf'",
        FileCategory.Document => "(mimeType = 'application/vnd.google-apps.document' or mimeType contains 'wordprocessingml' or " +
            "mimeType = 'application/msword' or mimeType = 'application/vnd.oasis.opendocument.text' or mimeType = 'application/rtf' or " +
            "mimeType = 'text/plain' or mimeType = 'text/markdown')",
        FileCategory.Spreadsheet => "(mimeType = 'application/vnd.google-apps.spreadsheet' or mimeType contains 'spreadsheetml' or " +
            "mimeType = 'application/vnd.ms-excel' or mimeType = 'application/vnd.oasis.opendocument.spreadsheet' or mimeType = 'text/csv')",
        FileCategory.Presentation => "(mimeType = 'application/vnd.google-apps.presentation' or mimeType contains 'presentationml' or " +
            "mimeType = 'application/vnd.ms-powerpoint' or mimeType = 'application/vnd.oasis.opendocument.presentation')",
        _ => null,
    };

    public async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        var request = _drive.About.Get();
        request.Fields = "storageQuota(limit,usage)";
        var about = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), Root).ConfigureAwait(false);
        return new StorageQuota(about.StorageQuota?.Usage, about.StorageQuota?.Limit);
    }

    internal static async Task<CloudIdentity> GetIdentityAsync(IAccessTokenProvider tokens, CancellationToken cancellationToken)
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "AkioExplorer",
            HttpClientInitializer = new BearerTokenInitializer(tokens),
        });
        var request = drive.About.Get();
        request.Fields = "user(displayName,emailAddress,permissionId)";
        var about = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new CloudIdentity(about.User?.EmailAddress, about.User?.DisplayName, about.User?.PermissionId ?? about.User?.EmailAddress);
    }

    // ------------------------------------------------------------------ content

    /// <summary>Google-native documents have no bytes of their own; they are exported to the matching Office/PDF format.</summary>
    private static (string Mime, string Extension)? ExportFormat(string mime) => mime switch
    {
        "application/vnd.google-apps.document" => ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        "application/vnd.google-apps.spreadsheet" => ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        "application/vnd.google-apps.presentation" => ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"),
        "application/vnd.google-apps.drawing" => ("image/png", ".png"),
        "application/vnd.google-apps.script" => ("application/vnd.google-apps.script+json", ".json"),
        _ => null,
    };

    private bool IsNative(StorageItem file, out string mime) =>
        _mimeById.TryGetValue(file.Path, out mime!) && mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal);

    private static StorageException DownloadDisabled(StorageItem file) => new(StorageErrorKind.AccessDenied,
        $"The owner of \"{file.Name}\" turned off downloading and copying for people who can only view it. Ask the owner to allow downloads or give you edit access.");

    /// <summary>Drive only forbids "/" in names.</summary>
    public string MakeValidName(string name) => string.IsNullOrWhiteSpace(name) ? "_" : name.Replace('/', '_');

    public string GetTransferName(StorageItem file) =>
        IsNative(file, out var mime) && ExportFormat(mime) is { } format && !file.Name.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase)
            ? file.Name + format.Extension
            : file.Name;

    // ------------------------------------------------------------------ thumbnails

    public bool MayHaveThumbnail(StorageItem file) => _thumbnailById.ContainsKey(file.Path);

    /// <summary>
    /// Drive renders thumbnails for photos, videos, PDFs, Office files and Google Docs/Sheets/Slides. The listing's
    /// short-lived <c>thumbnailLink</c> ends in <c>=s220</c>; the size suffix is swapped for the one needed.
    /// </summary>
    public async Task<Stream?> OpenThumbnailAsync(StorageItem file, int size, CancellationToken cancellationToken)
    {
        if (!_thumbnailById.TryGetValue(file.Path, out var link)) return null;
        int cut = link.LastIndexOf("=s", StringComparison.Ordinal);
        string url = (cut > 0 ? link[..cut] : link) + $"=s{Math.Clamp(size, 32, 1600)}";

        string token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null; // expired link or no rendition: the icon stays
        var memory = new MemoryStream();
        await response.Content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        memory.Position = 0;
        return memory;
    }

    /// <summary>
    /// <c>files/{id}?alt=media</c> honours Range requests, so big downloads run over several connections. Google Docs
    /// are exported on the fly (no Range): they keep the streaming path.
    /// </summary>
    public async Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken)
    {
        if (IsNative(file, out _) || _noDownload.ContainsKey(file.Path)) return null; // streaming path reports why
        string token = await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: refresh).ConfigureAwait(false);
        return new DirectDownload(
            $"{ApiBase}/files/{Uri.EscapeDataString(file.Path)}?alt=media&supportsAllDrives=true",
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["User-Agent"] = "AkioExplorer" });
    }

    public async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        if (_noDownload.ContainsKey(file.Path)) throw DownloadDisabled(file);
        string url;
        if (IsNative(file, out var mime))
        {
            var format = ExportFormat(mime) ?? throw new StorageException(StorageErrorKind.NotSupported,
                $"\"{file.Name}\" ({GoogleTypeName(mime) ?? "Google file"}) can't be downloaded. Open it in your browser instead.");
            url = $"{ApiBase}/files/{Uri.EscapeDataString(file.Path)}/export?mimeType={Uri.EscapeDataString(format.Mime)}";
        }
        else
        {
            url = $"{ApiBase}/files/{Uri.EscapeDataString(file.Path)}?alt=media&supportsAllDrives=true";
        }
        return await OpenUrlAsync(url, file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GET with the account's token (one retry with a fresh token); Drive's refusals become readable errors.</summary>
    private async Task<Stream> OpenUrlAsync(string url, StorageItem file, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0).ConfigureAwait(false);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                request.Dispose();
                throw new StorageException(StorageErrorKind.Unavailable, "Can't reach Google Drive. Check your internet connection.", ex);
            }

            if (response.IsSuccessStatusCode)
                return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);

            var status = response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            request.Dispose();
            if (status == HttpStatusCode.Unauthorized && attempt == 0) continue;
            throw status switch
            {
                HttpStatusCode.NotFound => new StorageException(StorageErrorKind.NotFound, $"\"{file.Name}\" no longer exists in {DisplayName}."),
                HttpStatusCode.Forbidden when body.Contains("exportSizeLimitExceeded") =>
                    new StorageException(StorageErrorKind.NotSupported, $"\"{file.Name}\" is too large for Google to export."),
                HttpStatusCode.Forbidden when body.Contains("cannotDownloadFile") || body.Contains("cannotExportFile") => DownloadDisabled(file),
                HttpStatusCode.Forbidden when body.Contains("cannotDownloadAbusiveFile") =>
                    new StorageException(StorageErrorKind.AccessDenied, $"Google blocked downloading \"{file.Name}\" (flagged as harmful)."),
                HttpStatusCode.Forbidden => new StorageException(StorageErrorKind.AccessDenied, $"You don't have permission to download \"{file.Name}\"."),
                HttpStatusCode.TooManyRequests => new StorageException(StorageErrorKind.Throttled, "Google Drive is limiting requests right now."),
                HttpStatusCode.Unauthorized => new StorageException(StorageErrorKind.AuthenticationRequired,
                    $"The sign-in for \"{DisplayName}\" has expired. Reconnect the account to continue."),
                _ => new StorageException(StorageErrorKind.Unknown, $"Google Drive download failed ({(int)status})."),
            };
        }
    }

    /// <summary>Resumable upload in 8 MiB chunks; the SDK retries failed chunks.</summary>
    public async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureWritable(parentPath);
        EnsureValidName(name);
        string parent = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var existing = await GetItemAsync(CombinePath(parent, name), cancellationToken).ConfigureAwait(false);
        if (existing is not null && !overwrite)
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{name}\" here.");

        string contentType = StreamExtensions.GuessMimeType(name);
        ResumableUpload<DriveFile, DriveFile> upload;
        if (existing is { IsContainer: false })
        {
            var update = _drive.Files.Update(new DriveFile(), existing.Path, content, contentType);
            update.Fields = FileFields;
            update.SupportsAllDrives = true;
            update.ChunkSize = Core.Transfers.UploadChunking.Google(length);
            upload = update;
        }
        else
        {
            var create = _drive.Files.Create(new DriveFile { Name = name, Parents = [parent] }, content, contentType);
            create.Fields = FileFields;
            create.SupportsAllDrives = true;
            create.ChunkSize = Core.Transfers.UploadChunking.Google(length);
            upload = create;
        }

        var result = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status != UploadStatus.Completed)
        {
            throw result.Exception switch
            {
                GoogleApiException api => Map(api, parentPath),
                StorageException storage => storage,
                OperationCanceledException canceled => canceled,
                HttpRequestException http => new StorageException(StorageErrorKind.Unavailable, "Upload to Google Drive was interrupted. Check your connection.", http),
                { } other => new StorageException(StorageErrorKind.Unknown, $"Upload to Google Drive failed: {other.Message}", other),
                null => new StorageException(StorageErrorKind.Unknown, "Upload to Google Drive failed."),
            };
        }

        var body = upload switch
        {
            FilesResource.CreateMediaUpload c => c.ResponseBody,
            FilesResource.UpdateMediaUpload u => u.ResponseBody,
            _ => null,
        };
        return body is null
            ? (await GetItemAsync(CombinePath(parent, name), cancellationToken).ConfigureAwait(false))!
            : ToItem(body, parent);
    }

    public async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        if (source.IsContainer) return null; // Drive copies files only; the engine recreates folders
        string parent = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var copy = _drive.Files.Copy(new DriveFile { Name = name, Parents = [parent] }, source.Path);
        copy.Fields = FileFields;
        copy.SupportsAllDrives = true;
        var file = await ExecuteAsync(() => copy.ExecuteAsync(cancellationToken), source.Path).ConfigureAwait(false);
        return ToItem(file, parent);
    }

    /// <summary>Moving = changing parents (instant, also for whole folders).</summary>
    public async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        string parent = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var get = _drive.Files.Get(source.Path);
        get.Fields = "parents";
        get.SupportsAllDrives = true;
        var current = await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), source.Path).ConfigureAwait(false);

        var update = _drive.Files.Update(new DriveFile { Name = name }, source.Path);
        update.AddParents = parent;
        update.RemoveParents = string.Join(',', current.Parents ?? []);
        update.Fields = FileFields;
        update.SupportsAllDrives = true;
        var moved = await ExecuteAsync(() => update.ExecuteAsync(cancellationToken), source.Path).ConfigureAwait(false);
        return ToItem(moved, parent);
    }

    // ------------------------------------------------------------------ id ↔ name/parent cache

    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path)
    {
        var chain = new List<BreadcrumbSegment>();
        string? current = path;
        int guard = 0;
        while (current is not null && current != Root && current != _rootId && guard++ < 64)
        {
            if (IsVirtual(current))
            {
                chain.Add(new BreadcrumbSegment(GetDisplayName(current), current));
                break;
            }
            if (!_nodes.TryGetValue(current, out var node)) break;
            chain.Add(new BreadcrumbSegment(node.Name, current));
            current = node.Parent;
        }

        chain.Add(new BreadcrumbSegment(DisplayName, Root));
        chain.Reverse();
        return chain;
    }

    public string? GetParentPath(string path)
    {
        if (path == Root || path == _rootId) return null;
        if (IsVirtual(path)) return Root;
        int slash = path.LastIndexOf('/');
        if (slash > 0) return path[..slash];
        if (_nodes.TryGetValue(path, out var node) && node.Parent is { } parent)
            return parent == _rootId ? Root : parent;
        return Root; // unknown ancestry: go to My Drive
    }

    public string GetDisplayName(string path)
    {
        if (path == Root || path == _rootId) return DisplayName;
        if (path == SharedDrivesPath) return SharedDrivesName;
        if (path == SharedWithMePath) return SharedWithMeName;
        int slash = path.LastIndexOf('/');
        if (slash > 0) return path[(slash + 1)..];
        return _nodes.TryGetValue(path, out var node) ? node.Name : "…";
    }

    public IDisposable? Watch(string path, Action onChanged) => null;

    public string CombinePath(string parentPath, string name) => parentPath + "/" + name;

    public bool IsSameOrDescendant(string path, string ancestor)
    {
        if (ancestor == Root || ancestor == _rootId) return true;
        string? current = path;
        for (int guard = 0; current is not null && guard < 64; guard++)
        {
            if (current == ancestor) return true;
            current = GetParentPath(current);
            if (current == Root) return false;
        }
        return false;
    }

    /// <summary>Typed paths aren't meaningful for an id-based store; only the root name is accepted.</summary>
    public string? TryNormalizePath(string input) =>
        input.Trim().Equals(DisplayName, StringComparison.OrdinalIgnoreCase) || input.Trim() is "/" or "" ? Root : null;

    // ------------------------------------------------------------------ links other people shared

    public bool CanOpenSharedLink(Uri link) => SharedLinks.TryGetGoogleDriveId(link, out _, out _);

    /// <summary>
    /// Opens a drive.google.com / docs.google.com link with this account's access: a folder is browsed directly (its
    /// id is a path here); a file opens its folder with it selected, when this account can see that folder.
    /// </summary>
    public async Task<SharedLinkTarget> OpenSharedLinkAsync(Uri link, CancellationToken cancellationToken)
    {
        if (!SharedLinks.TryGetGoogleDriveId(link, out string id, out _))
            throw new StorageException(StorageErrorKind.NotSupported, "That isn't a Google Drive link.");

        var item = await GetItemAsync(id, cancellationToken).ConfigureAwait(false)
                   ?? throw new StorageException(StorageErrorKind.NotFound, $"\"{DisplayName}\" can't open that link: it was removed, or it isn't shared with this account.");
        if (item.IsContainer) return new SharedLinkTarget(this, item.Path);

        if (_nodes.TryGetValue(item.Path, out var node) && node.Parent is { } parent)
            return new SharedLinkTarget(this, parent, item.Path);
        throw new StorageException(StorageErrorKind.NotSupported, "The file was shared on its own, without its folder.");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<string> ResolveFolderIdAsync(string path, CancellationToken cancellationToken)
    {
        if (path == Root) return Root;
        if (path.LastIndexOf('/') > 0)
        {
            if (_resolvedFolders.TryGetValue(path, out var cached)) return cached;
            var item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false)
                       ?? throw new StorageException(StorageErrorKind.NotFound, $"Can't find \"{GetDisplayName(path)}\" in {DisplayName}.");
            if (item.IsContainer) _resolvedFolders[path] = item.Path; // copy jobs resolve the same folders for every file
            return item.Path;
        }
        return path;
    }

    private async Task EnsureRootIdAsync(CancellationToken cancellationToken)
    {
        if (_rootId is not null) return;
        var get = _drive.Files.Get(Root);
        get.Fields = "id";
        _rootId = (await ExecuteAsync(() => get.ExecuteAsync(cancellationToken), Root).ConfigureAwait(false)).Id;
    }

    private async Task LearnParentsAsync(IEnumerable<string> parentIds, CancellationToken cancellationToken)
    {
        await EnsureRootIdAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in parentIds.Distinct().Where(id => id != _rootId && !_nodes.ContainsKey(id)).Take(50))
        {
            try
            {
                var get = _drive.Files.Get(id);
                get.Fields = "id,name,parents";
                get.SupportsAllDrives = true;
                var folder = await get.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                _nodes[folder.Id] = (folder.Name, folder.Parents?.FirstOrDefault());
            }
            catch (GoogleApiException)
            {
                // Parent not visible (shared item): the result simply shows "…" as its folder.
            }
        }
    }

    private StorageItem ToItem(DriveFile file, string? parentPath)
    {
        bool shortcut = file.MimeType == ShortcutMime && file.ShortcutDetails?.TargetId is not null;
        string mime = shortcut ? file.ShortcutDetails!.TargetMimeType ?? string.Empty : file.MimeType ?? string.Empty;
        string id = shortcut ? file.ShortcutDetails!.TargetId! : file.Id;
        bool folder = mime == FolderMime;

        string? parentId = parentPath ?? file.Parents?.FirstOrDefault();
        if (parentId is not null && parentId == _rootId) parentId = Root;
        if (file.DriveId is { } drive) _driveOf[id] = drive;
        _nodes[id] = (file.Name, parentId);
        _mimeById[id] = mime;
        if (!shortcut) // a shortcut's capabilities describe the shortcut, not its target
        {
            if (!folder && file.Capabilities?.CanDownload == false) _noDownload[id] = 0;
            else _noDownload.TryRemove(id, out _);
        }
        if (!folder && !string.IsNullOrEmpty(file.ThumbnailLink)) _thumbnailById[id] = file.ThumbnailLink;
        else _thumbnailById.TryRemove(id, out _);

        return new StorageItem
        {
            Name = file.Name,
            Path = id,
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = folder ? null : file.Size,
            IsStarred = file.Starred == true,
            Label = folder ? LabelOfColor(file.FolderColorRgb) : ItemLabel.None,
            Modified = file.ModifiedTimeDateTimeOffset?.ToLocalTime(),
            Created = file.CreatedTimeDateTimeOffset?.ToLocalTime(),
            TypeDescription = GoogleTypeName(mime) ?? (shortcut && !folder ? "Shortcut" : null),
        };
    }

    private static string? GoogleTypeName(string mime) => mime switch
    {
        "application/vnd.google-apps.document" => "Google Docs",
        "application/vnd.google-apps.spreadsheet" => "Google Sheets",
        "application/vnd.google-apps.presentation" => "Google Slides",
        "application/vnd.google-apps.form" => "Google Forms",
        "application/vnd.google-apps.drawing" => "Google Drawings",
        "application/vnd.google-apps.map" => "Google My Maps",
        "application/vnd.google-apps.site" => "Google Sites",
        _ => null,
    };

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static void EnsureValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new StorageException(StorageErrorKind.InvalidName, "A name can't be empty.");
        if (name.Contains('/')) throw new StorageException(StorageErrorKind.InvalidName, "A name can't contain \"/\" in AkioExplorer.");
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> call, string path)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw Map(ex, path);
        }
        catch (HttpRequestException ex)
        {
            throw new StorageException(StorageErrorKind.Unavailable, "Can't reach Google Drive. Check your internet connection.", ex);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && ex.InnerException is TimeoutException)
        {
            throw new StorageException(StorageErrorKind.Unavailable, "Google Drive didn't respond in time. Try again.", ex);
        }
    }

    private StorageException Map(GoogleApiException ex, string path)
    {
        string reason = ex.Error?.Errors?.FirstOrDefault()?.Reason ?? string.Empty;
        string name = path == Root ? DisplayName : $"\"{GetDisplayName(path)}\"";
        return ex.HttpStatusCode switch
        {
            HttpStatusCode.Unauthorized => new StorageException(StorageErrorKind.AuthenticationRequired,
                $"The sign-in for \"{DisplayName}\" has expired. Reconnect the account to continue.", ex),
            HttpStatusCode.NotFound => new StorageException(StorageErrorKind.NotFound, $"Can't find {name}. It may have been moved or deleted.", ex),
            HttpStatusCode.Forbidden when reason is "userRateLimitExceeded" or "rateLimitExceeded" =>
                new StorageException(StorageErrorKind.Throttled, "Google Drive is limiting requests right now. Wait a moment and try again.", ex),
            HttpStatusCode.Forbidden when reason == "storageQuotaExceeded" =>
                new StorageException(StorageErrorKind.Unknown, "Your Google storage is full.", ex),
            HttpStatusCode.Forbidden => new StorageException(StorageErrorKind.AccessDenied, $"You don't have permission to change {name}.", ex),
            HttpStatusCode.TooManyRequests => new StorageException(StorageErrorKind.Throttled, "Google Drive is limiting requests right now. Wait a moment and try again.", ex),
            _ => new StorageException(StorageErrorKind.Unknown, $"Google Drive error: {ex.Error?.Message ?? ex.Message}", ex),
        };
    }

    /// <summary>Adds the OAuth bearer token to SDK requests and refreshes once on 401.</summary>
    private sealed class BearerTokenInitializer(IAccessTokenProvider tokens)
        : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor, IHttpUnsuccessfulResponseHandler
    {
        public void Initialize(ConfigurableHttpClient httpClient)
        {
            httpClient.MessageHandler.AddExecuteInterceptor(this);
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
        }

        public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (args.Response.StatusCode != HttpStatusCode.Unauthorized || !args.SupportsRetry || args.CurrentFailedTry > 1) return false;
            string token = await tokens.GetAccessTokenAsync(args.CancellationToken, forceRefresh: true).ConfigureAwait(false);
            args.Request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return true;
        }
    }
}
