using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
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
public sealed class GoogleDriveProvider : ICloudStorageProvider, IFileContentProvider, IThumbnailProvider
{
    public const string ServiceName = "Google Drive";
    public const string Root = "root";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string ShortcutMime = "application/vnd.google-apps.shortcut";
    private const string FileFields = "id,name,mimeType,size,modifiedTime,createdTime,parents,shortcutDetails(targetId,targetMimeType),thumbnailLink";

    private const string ApiBase = "https://www.googleapis.com/drive/v3";
    private const int UploadChunkSize = 8 * 1024 * 1024; // multiple of 256 KiB as the resumable protocol requires

    private readonly DriveService _drive;
    private readonly IAccessTokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (string Name, string? Parent)> _nodes = new();
    private readonly ConcurrentDictionary<string, string> _mimeById = new();
    private readonly ConcurrentDictionary<string, string> _thumbnailById = new();
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
        StorageCapabilities.RecycleBin | StorageCapabilities.Search | StorageCapabilities.ServerSideCopy;

    public string RootPath => Root;

    // ------------------------------------------------------------------ listing

    public async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureRootIdAsync(cancellationToken).ConfigureAwait(false);
        string folderId = await ResolveFolderIdAsync(path, cancellationToken).ConfigureAwait(false);
        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
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

    public async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path == Root) throw new StorageException(StorageErrorKind.NotSupported, "My Drive itself can't be deleted.");
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

    /// <summary>Drive's query API can't limit a search to a subtree, so results cover the whole drive.</summary>
    public async IAsyncEnumerable<StorageItem> SearchAsync(string path, string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0) yield break;

        string? pageToken = null;
        do
        {
            var request = _drive.Files.List();
            request.Q = $"name contains '{Escape(query)}' and trashed = false";
            request.Fields = $"nextPageToken, files({FileFields})";
            request.PageSize = 200;
            request.Spaces = "drive";
            request.PageToken = pageToken;
            var result = await ExecuteAsync(() => request.ExecuteAsync(cancellationToken), path).ConfigureAwait(false);
            pageToken = result.NextPageToken;

            var files = result.Files ?? [];
            await LearnParentsAsync(files.SelectMany(f => f.Parents ?? []), cancellationToken).ConfigureAwait(false);
            foreach (var file in files) yield return ToItem(file, file.Parents?.FirstOrDefault());
        }
        while (pageToken is not null);
    }

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

    public async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
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
            update.ChunkSize = UploadChunkSize;
            upload = update;
        }
        else
        {
            var create = _drive.Files.Create(new DriveFile { Name = name, Parents = [parent] }, content, contentType);
            create.Fields = FileFields;
            create.SupportsAllDrives = true;
            create.ChunkSize = UploadChunkSize;
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
        int slash = path.LastIndexOf('/');
        if (slash > 0) return path[..slash];
        if (_nodes.TryGetValue(path, out var node) && node.Parent is { } parent)
            return parent == _rootId ? Root : parent;
        return Root; // unknown ancestry: go to My Drive
    }

    public string GetDisplayName(string path)
    {
        if (path == Root || path == _rootId) return DisplayName;
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
        _nodes[id] = (file.Name, parentId);
        _mimeById[id] = mime;
        if (!folder && !string.IsNullOrEmpty(file.ThumbnailLink)) _thumbnailById[id] = file.ThumbnailLink;
        else _thumbnailById.TryRemove(id, out _);

        return new StorageItem
        {
            Name = file.Name,
            Path = id,
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = folder ? null : file.Size,
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
