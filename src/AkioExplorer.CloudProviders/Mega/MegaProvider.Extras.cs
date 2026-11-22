using System.Text.Json.Nodes;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using CG.Web.MegaApiClient;

namespace AkioExplorer.CloudProviders.Mega;

/// <summary>
/// What MEGA's web app does beyond plain files, through <see cref="MegaRawApi"/>: Rubbish bin with "restore to",
/// thumbnails (shown and uploaded), public links, favourites / labels / hidden, versions, server-side copy, links
/// from other people, transfer quota.
/// </summary>
public sealed partial class MegaProvider : IThumbnailProvider, IShareLinkProvider, IItemMarksProvider, IVersionsProvider, IPublicLinkProvider
{
    private const long MaxThumbnailSource = 50L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".jfif" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".wmv", ".webm", ".3gp", ".mpg", ".mpeg" };

    private MegaRawApi RawApi()
    {
        var session = _session ?? Session ?? throw SignInFailed();
        return _raw ??= new MegaRawApi(session.SessionId, session.MasterKey);
    }

    /// <summary>Runs raw API work (not serialised behind MegaApiClient's gate) with MEGA errors mapped.</summary>
    private async Task<T> RawAsync<T>(string path, Func<MegaRawApi, Task<T>> action, CancellationToken cancellationToken)
    {
        await EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(RawApi()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageException)
        {
            throw Map(ex, path);
        }
    }

    private async Task<MegaRawNode> RawNodeAsync(StorageItem item, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        if (tree.Raw.TryGetValue(node.Id, out var raw) && raw.Key is not null) return raw;
        tree = await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        return tree.Raw.TryGetValue(node.Id, out raw) && raw.Key is not null
            ? raw
            : throw new StorageException(StorageErrorKind.Unknown, $"MEGA didn't send the key of {Describe(item.Path)}; try again.");
    }

    /// <summary>The node's key as MegaApiClient decrypted it (its Node class keeps it in a public property).</summary>
    private static byte[]? KeyOf(INode node) => node.GetType().GetProperty("FullKey")?.GetValue(node) as byte[];

    // ------------------------------------------------------------------ rubbish bin

    public string TrashName => "Rubbish bin";

    public string RestoreFallbackName => "Cloud drive";

    /// <summary>Notes the folder an item is deleted from, as MEGA's own apps do ("rr" attribute), so Restore works anywhere.</summary>
    private async Task RememberOriginAsync(INode node, CancellationToken cancellationToken)
    {
        try
        {
            var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
            if (node.ParentId is null || !tree.Raw.TryGetValue(node.Id, out var raw) || raw.Attributes is null) return;
            var attributes = (JsonObject)raw.Attributes.DeepClone();
            attributes["rr"] = node.ParentId;
            await RawAsync(string.Empty, async api => { await api.SetAttributesAsync(raw, attributes, cancellationToken).ConfigureAwait(false); return 0; },
                cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            // Without the note the item still goes to the Rubbish bin; Restore then uses the Cloud drive root.
        }
    }

    public async Task<IReadOnlyList<TrashItem>> ListTrashAsync(CancellationToken cancellationToken)
    {
        var tree = await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        var bin = tree.Raw.Values.FirstOrDefault(n => n.Type == MegaRawNode.Trash);
        if (bin is null) return [];
        var children = tree.Raw.Values.Where(n => n.Parent is not null).GroupBy(n => n.Parent!).ToDictionary(g => g.Key, g => g.ToList());

        long SizeOf(MegaRawNode node) => node.Type == MegaRawNode.File
            ? node.Size
            : children.TryGetValue(node.Handle, out var kids) ? kids.Sum(SizeOf) : 0;

        return (children.GetValueOrDefault(bin.Handle) ?? [])
            .Select(n => new TrashItem(n.Handle, n.Name ?? "(unnamed)", n.Type == MegaRawNode.Folder, SizeOf(n), null,
                n.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(n.Timestamp).ToLocalTime() : null,
                n.RestoreTo is { } rr && tree.PathOf.TryGetValue(rr, out var from) ? DisplayPath(from) : null))
            .ToList();
    }

    public async Task RestoreAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken)
    {
        var tree = await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tree.Raw.TryGetValue(item.Id, out var node)) continue; // already gone
            // Back to the folder it came from while that still exists outside the Rubbish bin, else the Cloud drive root.
            string target = node.RestoreTo is { } rr && tree.PathOf.ContainsKey(rr) ? rr : tree.Root.Id;
            await RawAsync(item.Name, async api =>
            {
                await api.RequestAsync(new JsonObject { ["a"] = "m", ["n"] = node.Handle, ["t"] = target }, cancellationToken).ConfigureAwait(false);
                if (node.Attributes?.ContainsKey("rr") == true)
                {
                    var attributes = (JsonObject)node.Attributes.DeepClone();
                    attributes.Remove("rr");
                    await api.SetAttributesAsync(node, attributes, cancellationToken).ConfigureAwait(false);
                }
                return 0;
            }, cancellationToken).ConfigureAwait(false);
        }
        Invalidate();
    }

    public async Task DeleteFromTrashAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken)
    {
        // One round trip for many items.
        foreach (var batch in items.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RawAsync(batch[0].Name, async api =>
                await api.RequestManyAsync(batch.Select(i => new JsonObject { ["a"] = "d", ["n"] = i.Id }).ToList(), cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        Invalidate();
    }

    public async Task EmptyTrashAsync(CancellationToken cancellationToken) =>
        await DeleteFromTrashAsync(await ListTrashAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private string DisplayPath(string path) => path.Length == 0 ? RestoreFallbackName : RestoreFallbackName + path;

    // ------------------------------------------------------------------ thumbnails

    public bool MayHaveThumbnail(StorageItem file)
    {
        if (file.IsContainer || _tree is not { } tree) return false;
        return tree.ByPath.TryGetValue(Normalize(file.Path), out var node) && tree.Raw.TryGetValue(node.Id, out var raw) &&
               (raw.FileAttribute(0) is not null || raw.FileAttribute(1) is not null);
    }

    public async Task<Stream?> OpenThumbnailAsync(StorageItem file, int size, CancellationToken cancellationToken)
    {
        var raw = await RawNodeAsync(file, cancellationToken).ConfigureAwait(false);
        // 0 = 120 px square thumbnail, 1 = preview up to 1000 px.
        string? handle = size <= 160 ? raw.FileAttribute(0) ?? raw.FileAttribute(1) : raw.FileAttribute(1) ?? raw.FileAttribute(0);
        if (handle is null || raw.AttributeKey is not { } key) return null;
        return await RawAsync(file.Path, async api =>
        {
            var answer = await api.RequestAsync(new JsonObject { ["a"] = "ufa", ["fah"] = handle, ["ssl"] = 2, ["r"] = 1 }, cancellationToken).ConfigureAwait(false);
            string url = answer?["p"]?.GetValue<string>() ?? throw new IOException("MEGA didn't give a thumbnail address.");
            using var content = new ByteArrayContent(MegaWebClient.Base64UrlDecode(handle));
            using var response = await MegaRawApi.Client.PostAsync(url + "/0", content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            // [8-byte handle][4-byte length][encrypted image]
            if (body.Length < 12) return (Stream?)null;
            int length = BitConverter.ToInt32(body, 8);
            if (length <= 0 || 12 + length > body.Length) return null;
            return new MemoryStream(MegaCrypto.DecryptFileAttribute(body[12..(12 + length)], key));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool ThumbnailWorthy(string name) =>
        ImageExtensions.Contains(Path.GetExtension(name)) || VideoExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// MEGA's apps make the thumbnail and preview on the uploading device and store them (encrypted) with the file;
    /// without them the file shows a plain icon everywhere. Best effort: a failure here never fails the upload.
    /// </summary>
    private async Task AddThumbnailsAsync(INode uploaded, string name, Stream content, byte[]? bytes, CancellationToken cancellationToken)
    {
        if (_thumbnails is null || !ThumbnailWorthy(name) || KeyOf(uploaded) is not { } nodeKey) return;
        try
        {
            string? local = (content as TransferStream)?.LocalPath ?? (content as FileStream)?.Name;
            if (local is null && (bytes is null || !ImageExtensions.Contains(Path.GetExtension(name)))) return;
            var made = await _thumbnails.CreateAsync(local, local is null ? bytes : null, cancellationToken).ConfigureAwait(false);
            if (made is null) return;
            byte[] key = MegaCrypto.AttributeKey(nodeKey);
            await RawAsync(string.Empty, async api =>
            {
                string thumb = await PutFileAttributeAsync(api, made.Thumbnail, key, cancellationToken).ConfigureAwait(false);
                string preview = await PutFileAttributeAsync(api, made.Preview, key, cancellationToken).ConfigureAwait(false);
                await api.RequestAsync(new JsonObject { ["a"] = "pfa", ["n"] = uploaded.Id, ["fa"] = $"0*{thumb}/1*{preview}" }, cancellationToken).ConfigureAwait(false);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No thumbnail; the file itself is uploaded.
        }
    }

    private static async Task<string> PutFileAttributeAsync(MegaRawApi api, byte[] image, byte[] key, CancellationToken cancellationToken)
    {
        byte[] encrypted = MegaCrypto.EncryptFileAttribute(image, key);
        var answer = await api.RequestAsync(new JsonObject { ["a"] = "ufa", ["s"] = encrypted.Length, ["ssl"] = 2 }, cancellationToken).ConfigureAwait(false);
        string url = answer?["p"]?.GetValue<string>() ?? throw new IOException("MEGA didn't give an upload address for the thumbnail.");
        using var body = new ByteArrayContent(encrypted);
        using var response = await MegaRawApi.Client.PostAsync(url, body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] handle = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (handle.Length != 8) throw new IOException("MEGA refused the thumbnail.");
        return MegaWebClient.Base64UrlEncode(handle);
    }

    // ------------------------------------------------------------------ public links

    public async Task<string> CreateShareLinkAsync(StorageItem item, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(item.Path, cancellationToken).ConfigureAwait(false);
        // MegaApiClient also sets up the share key a folder link needs.
        var link = await RunAsync(item.Path, () => _client.GetDownloadLinkAsync(node), cancellationToken).ConfigureAwait(false);
        return link.ToString();
    }

    public async Task RemoveShareLinkAsync(StorageItem item, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(item.Path, cancellationToken).ConfigureAwait(false);
        await RawAsync(item.Path, api => api.RequestAsync(new JsonObject { ["a"] = "l", ["n"] = node.Id, ["d"] = 1 }, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ favourite, label, hidden

    public bool SupportsLabels => true;

    public bool SupportsHiding => true;

    public Task SetStarredAsync(IReadOnlyList<StorageItem> items, bool starred, CancellationToken cancellationToken) =>
        EditAttributesAsync(items, a => { if (starred) a["fav"] = 1; else a.Remove("fav"); }, cancellationToken);

    public Task SetLabelAsync(IReadOnlyList<StorageItem> items, ItemLabel label, CancellationToken cancellationToken) =>
        EditAttributesAsync(items, a => { if (label == ItemLabel.None) a.Remove("lbl"); else a["lbl"] = (int)label; }, cancellationToken);

    public Task SetHiddenAsync(IReadOnlyList<StorageItem> items, bool hidden, CancellationToken cancellationToken) =>
        EditAttributesAsync(items, a => { if (hidden) a["sen"] = 1; else a.Remove("sen"); }, cancellationToken);

    private async Task EditAttributesAsync(IReadOnlyList<StorageItem> items, Action<JsonObject> edit, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var raw = await RawNodeAsync(item, cancellationToken).ConfigureAwait(false);
            var attributes = (JsonObject?)raw.Attributes?.DeepClone() ?? new JsonObject { ["n"] = item.Name };
            edit(attributes);
            await RawAsync(item.Path, async api => { await api.SetAttributesAsync(raw, attributes, cancellationToken).ConfigureAwait(false); return 0; },
                cancellationToken).ConfigureAwait(false);
        }
        Invalidate();
    }

    // ------------------------------------------------------------------ versions

    /// <summary>MEGA keeps older versions as a chain under the file: the current file's child is the previous version, and so on.</summary>
    public async Task<IReadOnlyList<FileVersion>> ListVersionsAsync(StorageItem file, CancellationToken cancellationToken)
    {
        await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        var current = await RawNodeAsync(file, cancellationToken).ConfigureAwait(false);
        var tree = _tree!;
        var byParent = tree.Raw.Values.Where(n => n.Type == MegaRawNode.File && n.Parent is not null)
            .GroupBy(n => n.Parent!).ToDictionary(g => g.Key, g => g.First());
        var versions = new List<FileVersion> { ToVersion(current, isCurrent: true) };
        for (var node = current; byParent.TryGetValue(node.Handle, out var older) && versions.Count < 1000; node = older)
            versions.Add(ToVersion(older, isCurrent: false));
        return versions;
    }

    private static FileVersion ToVersion(MegaRawNode node, bool isCurrent) =>
        new(node.Handle, node.Name ?? "(unnamed)", node.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(node.Timestamp).ToLocalTime() : null,
            node.Size, isCurrent);

    public async Task RestoreVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken)
    {
        if (version.IsCurrent) return;
        var current = await RawNodeAsync(file, cancellationToken).ConfigureAwait(false);
        var old = _tree!.Raw.GetValueOrDefault(version.Id) ?? throw new StorageException(StorageErrorKind.NotFound, "That version no longer exists.");
        // A copy of the old version replaces the current file ("ov"); the current one becomes the newest version.
        await RawAsync(file.Path, api => api.RequestAsync(new JsonObject
        {
            ["a"] = "p",
            ["t"] = current.Parent,
            ["n"] = new JsonArray(new JsonObject
            {
                ["h"] = old.Handle, ["t"] = 0, ["a"] = old.EncryptedAttributes, ["k"] = old.EncryptedKey, ["ov"] = current.Handle,
            }),
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
        Invalidate();
    }

    public async Task<Stream> OpenVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken)
    {
        await RawNodeAsync(file, cancellationToken).ConfigureAwait(false);
        if (_tree!.Raw.GetValueOrDefault(version.Id) is not { Key: { Length: 32 } key })
            throw new StorageException(StorageErrorKind.NotFound, "That version no longer exists.");
        var api = RawApi();
        var (url, size) = await RawAsync(file.Path, a => a.DownloadUrlAsync(version.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new MegaDownloadStream(MegaRawApi.Client, url, size, key,
            async ct => (await api.DownloadUrlAsync(version.Id, ct).ConfigureAwait(false)).Url, cancellationToken);
    }

    public async Task DeleteVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken)
    {
        if (version.IsCurrent) throw new StorageException(StorageErrorKind.NotSupported, "Delete the file itself to remove the current version.");
        await RawAsync(file.Path, api => api.RequestAsync(new JsonObject { ["a"] = "d", ["n"] = version.Id }, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        Invalidate();
    }

    // ------------------------------------------------------------------ server-side copy

    /// <summary>
    /// Copies on MEGA's servers (no download/upload): the new nodes reuse the existing encrypted content and keys,
    /// one request for a whole folder tree. Versions aren't copied (as in MEGA's apps).
    /// </summary>
    public override async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string target = CombinePath(parentPath, name);
        if (await GetItemAsync(target, cancellationToken).ConfigureAwait(false) is not null) throw Exists(target);
        var tree = await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        if (!tree.ByPath.TryGetValue(Normalize(source.Path), out var node) || !tree.ByPath.TryGetValue(Normalize(parentPath), out var parent))
            throw NotFound(source.Path);
        if (!tree.Raw.TryGetValue(node.Id, out var root) || root.Attributes is null || root.EncryptedKey is null) return null; // stream instead

        var byParent = tree.Raw.Values.Where(n => n.Parent is not null).GroupBy(n => n.Parent!).ToDictionary(g => g.Key, g => g.ToList());
        var attributes = (JsonObject)root.Attributes.DeepClone();
        attributes["n"] = name;
        attributes.Remove("rr");
        var nodes = new JsonArray(new JsonObject
        {
            ["h"] = root.Handle, ["t"] = root.Type, ["a"] = MegaCrypto.EncryptAttributes(attributes, root.AttributeKey!), ["k"] = root.EncryptedKey,
        });
        if (root.Type == MegaRawNode.Folder)
        {
            var pending = new Queue<MegaRawNode>([root]);
            while (pending.Count > 0)
            {
                var folder = pending.Dequeue();
                foreach (var child in byParent.GetValueOrDefault(folder.Handle) ?? [])
                {
                    if (child.EncryptedKey is null || child.EncryptedAttributes is null) return null;
                    nodes.Add(new JsonObject
                    {
                        ["h"] = child.Handle, ["t"] = child.Type, ["a"] = child.EncryptedAttributes, ["k"] = child.EncryptedKey, ["p"] = folder.Handle,
                    });
                    if (child.Type == MegaRawNode.Folder) pending.Enqueue(child);
                }
            }
        }

        await RawAsync(target, api => api.RequestAsync(new JsonObject { ["a"] = "p", ["t"] = parent.Id, ["n"] = nodes }, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        Invalidate();
        return await GetItemAsync(target, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ links from other people

    public bool CanOpenLink(string link) => MegaPublicLink.TryParse(link, out _);

    public async Task<PublicLinkInfo> InspectLinkAsync(string link, CancellationToken cancellationToken)
    {
        if (!MegaPublicLink.TryParse(link, out var parsed)) throw new StorageException(StorageErrorKind.NotSupported, "That isn't a MEGA link.");
        if (parsed.IsFolder)
        {
            var nodes = await RunAsync(string.Empty, () => _client.GetNodesFromLinkAsync(new Uri(parsed.Url)), cancellationToken).ConfigureAwait(false);
            var list = nodes.ToList();
            var root = list.FirstOrDefault(n => n.Type == NodeType.Directory && list.All(o => o.Id != n.ParentId));
            return new PublicLinkInfo(root?.Name ?? "Shared folder", IsFolder: true, list.Where(n => n.Type == NodeType.File).Sum(n => n.Size),
                list.Count(n => n.Type == NodeType.File));
        }
        var info = await RawAsync(string.Empty, api => api.RequestAsync(new JsonObject { ["a"] = "g", ["p"] = parsed.Handle }, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        string? at = info?["at"]?.GetValue<string>();
        string name = at is null ? "File" : MegaCrypto.DecryptAttributes(at, MegaCrypto.AttributeKey(parsed.Key))?["n"]?.GetValue<string>() ?? "File";
        return new PublicLinkInfo(name, IsFolder: false, info?["s"]?.GetValue<long>() ?? 0, 1);
    }

    public async Task<StorageItem?> ImportLinkAsync(string link, string targetFolderPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!MegaPublicLink.TryParse(link, out var parsed)) throw new StorageException(StorageErrorKind.NotSupported, "That isn't a MEGA link.");
        var target = await NodeAsync(targetFolderPath, cancellationToken).ConfigureAwait(false);
        var api = await RawAsync(string.Empty, a => Task.FromResult(a), cancellationToken).ConfigureAwait(false);

        if (!parsed.IsFolder)
        {
            // Server-side: the file's content is already on MEGA; only a new node with our copy of the key is made.
            var info = await RawAsync(string.Empty, a => a.RequestAsync(new JsonObject { ["a"] = "g", ["p"] = parsed.Handle }, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            string at = info?["at"]?.GetValue<string>() ?? throw new StorageException(StorageErrorKind.NotFound, "The link no longer works.");
            string name = MegaCrypto.DecryptAttributes(at, MegaCrypto.AttributeKey(parsed.Key))?["n"]?.GetValue<string>() ?? "File";
            progress?.Report(name);
            await RawAsync(targetFolderPath, a => a.RequestAsync(new JsonObject
            {
                ["a"] = "p",
                ["t"] = target.Id,
                ["n"] = new JsonArray(new JsonObject
                {
                    ["ph"] = parsed.Handle, ["t"] = 0, ["a"] = at,
                    ["k"] = MegaWebClient.Base64UrlEncode(MegaCrypto.EncryptKey(parsed.Key, api.MasterKey)),
                }),
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            Invalidate();
            return await GetItemAsync(CombinePath(targetFolderPath, name), cancellationToken).ConfigureAwait(false);
        }

        // Folder link: MEGA has no server-side import for those through this API, so each file streams through this PC.
        var nodes = (await RunAsync(string.Empty, () => _client.GetNodesFromLinkAsync(new Uri(parsed.Url)), cancellationToken).ConfigureAwait(false)).ToList();
        var byParent = nodes.Where(n => n.ParentId is not null).GroupBy(n => n.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        var top = nodes.First(n => n.Type == NodeType.Directory && nodes.All(o => o.Id != n.ParentId));
        string folderPath = CombinePath(targetFolderPath, top.Name);
        if (await GetItemAsync(folderPath, cancellationToken).ConfigureAwait(false) is null)
            await CreateFolderAsync(targetFolderPath, top.Name, cancellationToken).ConfigureAwait(false);
        var pending = new Queue<(INode Node, string Path)>([(top, folderPath)]);
        while (pending.Count > 0)
        {
            var (folder, path) = pending.Dequeue();
            foreach (var child in byParent.GetValueOrDefault(folder.Id) ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                string childPath = CombinePath(path, child.Name);
                if (child.Type == NodeType.Directory)
                {
                    if (await GetItemAsync(childPath, cancellationToken).ConfigureAwait(false) is null)
                        await CreateFolderAsync(path, child.Name, cancellationToken).ConfigureAwait(false);
                    pending.Enqueue((child, childPath));
                }
                else if (child.Type == NodeType.File && await GetItemAsync(childPath, cancellationToken).ConfigureAwait(false) is null)
                {
                    progress?.Report(child.Name);
                    await using var stream = await RunAsync(childPath, () => _client.DownloadAsync(child, null, cancellationToken), cancellationToken).ConfigureAwait(false);
                    await WriteFileAsync(path, child.Name, stream, child.Size, overwrite: false, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        Invalidate();
        return await GetItemAsync(folderPath, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ transfer quota

    /// <summary>Adds the transfer (bandwidth) quota when MEGA reports one (paid plans; free accounts have a moving limit).</summary>
    private async Task<StorageQuota> WithTransferQuotaAsync(StorageQuota quota, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await RawAsync(string.Empty, api => api.RequestAsync(new JsonObject { ["a"] = "uq", ["xfer"] = 1 }, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            long max = answer?["mxfer"]?.GetValue<long>() ?? 0;
            if (max <= 0) return quota;
            long used = (answer?["caxfer"]?.GetValue<long>() ?? 0) + (answer?["csxfer"]?.GetValue<long>() ?? 0);
            return quota with { TransferUsed = used, TransferTotal = max };
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or FormatException)
        {
            return quota;
        }
    }
}

/// <summary>mega.nz/file/HANDLE#KEY, mega.nz/folder/HANDLE#KEY and the older #!HANDLE!KEY / #F!HANDLE!KEY forms.</summary>
internal sealed record MegaPublicLink(string Url, string Handle, byte[] Key, bool IsFolder)
{
    public static bool TryParse(string text, out MegaPublicLink link)
    {
        link = null!;
        text = text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("mega.nz", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith("mega.co.nz", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith("mega.io", StringComparison.OrdinalIgnoreCase))
            return false;
        string path = uri.AbsolutePath.Trim('/');
        string fragment = uri.Fragment.TrimStart('#');
        string? handle = null, key = null;
        bool folder = false;
        if (path.StartsWith("file/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("folder/", StringComparison.OrdinalIgnoreCase))
        {
            folder = path.StartsWith("folder/", StringComparison.OrdinalIgnoreCase);
            handle = path[(path.IndexOf('/') + 1)..].Split('/')[0];
            key = fragment.Split('/')[0];
        }
        else if (fragment.StartsWith("!") || fragment.StartsWith("F!"))
        {
            folder = fragment.StartsWith("F!");
            var parts = fragment.TrimStart('F').Split('!', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) (handle, key) = (parts[0], parts[1]);
        }
        if (string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(key)) return false;
        byte[] keyBytes;
        try
        {
            keyBytes = MegaWebClient.Base64UrlDecode(key);
        }
        catch (FormatException)
        {
            return false;
        }
        if (keyBytes.Length != (folder ? 16 : 32)) return false;
        // MegaApiClient wants the new URL form for folder links.
        string url = folder ? $"https://mega.nz/folder/{handle}#{key}" : $"https://mega.nz/file/{handle}#{key}";
        link = new MegaPublicLink(url, handle, keyBytes, folder);
        return true;
    }
}
