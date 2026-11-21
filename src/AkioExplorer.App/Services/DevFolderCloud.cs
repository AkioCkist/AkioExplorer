#if DEBUG
using System.Runtime.CompilerServices;
using AkioExplorer.CloudProviders;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.App.Services;

/// <summary>
/// DEBUG BUILDS ONLY. A cloud-like provider backed by a local folder, enabled with the environment variable
/// AKIO_DEV_CLOUD=&lt;folder&gt;. It behaves like a remote account (no shell, path addressing, throttled at ~8 MB/s
/// so progress, pause and cancel can be exercised) and lets developers test upload/download/transfer UI without
/// OAuth credentials. It is compiled out of Release builds.
/// </summary>
internal sealed class DevFolderCloud : PathCloudProvider, IFileContentProvider
{
    private const int BytesPerSecond = 8 * 1024 * 1024;
    private readonly string _root;

    public DevFolderCloud(string root)
        : base(new CloudAccount { Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), DisplayName = "Dev cloud (folder)", Type = CloudProviderType.OneDrive },
            CloudProviderType.OneDrive)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.ServerSideCopy;

    private string Local(string path) => Path.Combine(_root, Normalize(path).TrimStart('/').Replace('/', '\\'));

    private StorageItem ToItem(FileSystemInfo info, string path) => new()
    {
        Name = info.Name,
        Path = Normalize(path),
        Kind = info is DirectoryInfo ? StorageItemKind.Folder : StorageItemKind.File,
        Size = info is FileInfo f ? f.Length : null,
        Modified = info.LastWriteTime,
    };

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(150, ct); // network latency
        var dir = new DirectoryInfo(Local(path));
        if (!dir.Exists) throw new StorageException(StorageErrorKind.NotFound, $"Can't find \"{GetDisplayName(path)}\".");
        yield return dir.EnumerateFileSystemInfos().Select(i => ToItem(i, CombinePath(path, i.Name))).ToList();
    }

    public override Task<StorageItem?> GetItemAsync(string path, CancellationToken ct)
    {
        string local = Local(path);
        FileSystemInfo? info = Directory.Exists(local) ? new DirectoryInfo(local) : File.Exists(local) ? new FileInfo(local) : null;
        return Task.FromResult(Normalize(path).Length == 0 || info is null ? null : ToItem(info, path));
    }

    public override Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken ct)
    {
        string path = CombinePath(parentPath, name);
        if (Directory.Exists(Local(path)) || File.Exists(Local(path))) throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{name}\" here.");
        return Task.FromResult(ToItem(Directory.CreateDirectory(Local(path)), path));
    }

    public override Task<StorageItem> RenameAsync(string path, string newName, CancellationToken ct)
    {
        string target = CombinePath(GetParentPath(path) ?? "", newName);
        if (Directory.Exists(Local(path))) Directory.Move(Local(path), Local(target)); else File.Move(Local(path), Local(target));
        return GetItemAsync(target, ct)!;
    }

    public override Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken ct)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(Local(p))) Directory.Delete(Local(p), true); else File.Delete(Local(p));
        }
        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (!query.HasCriteria) yield break;
        var matcher = query.CreateMatcher(DateTimeOffset.Now);
        foreach (var f in new DirectoryInfo(Local(path)).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var item = ToItem(f, "/" + Path.GetRelativePath(_root, f.FullName).Replace('\\', '/'));
            if (matcher.Matches(item)) yield return item;
        }
    }

    public override Task<StorageQuota?> GetQuotaAsync(CancellationToken ct) => Task.FromResult<StorageQuota?>(new StorageQuota(1L << 30, 15L << 30));

    public Task<Stream> OpenReadAsync(StorageItem file, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(Local(file.Path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true));

    public async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken ct)
    {
        string path = CombinePath(parentPath, name);
        if (!overwrite && File.Exists(Local(path))) throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{name}\" here.");
        string temp = Local(path) + ".devpart";
        try
        {
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    await Task.Delay(TimeSpan.FromSeconds((double)read / BytesPerSecond), ct); // simulated bandwidth
                }
            }
            File.Move(temp, Local(path), overwrite);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return (await GetItemAsync(path, ct))!;
    }

    public async Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken ct)
    {
        if (source.IsContainer) return null;
        string path = CombinePath(parentPath, name);
        File.Copy(Local(source.Path), Local(path));
        return await GetItemAsync(path, ct);
    }

    public async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken ct)
    {
        string path = CombinePath(parentPath, name);
        if (source.IsContainer) Directory.Move(Local(source.Path), Local(path)); else File.Move(Local(source.Path), Local(path));
        return await GetItemAsync(path, ct);
    }
}
#endif
