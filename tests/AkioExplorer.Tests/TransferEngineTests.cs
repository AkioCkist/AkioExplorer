using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using AkioExplorer.CloudProviders.OneDrive;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Settings;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace AkioExplorer.Tests;

/// <summary>In-memory cloud account ("/a/b" paths) implementing the same contracts as the real providers.</summary>
internal sealed class MemoryCloud(string id, bool serverCopy = false, bool unknownSizes = false) : IStorageProvider, IFileContentProvider, IDirectDownloadProvider
{
    /// <summary>Direct (range) download URL per file; null = no direct link (streaming path).</summary>
    public Func<StorageItem, DirectDownload?>? DirectLinks { get; set; }

    public Task<DirectDownload?> GetDirectDownloadAsync(StorageItem file, bool refresh, CancellationToken cancellationToken) =>
        Task.FromResult(DirectLinks?.Invoke(file));

    public readonly ConcurrentDictionary<string, byte[]?> Entries = new(StringComparer.OrdinalIgnoreCase) { [""] = null };
    public readonly ConcurrentBag<string> Trashed = new();
    public int ServerCopies;
    public int Reads;

    public string Id => id;
    public string DisplayName => id;
    public string RootPath => "";
    public StorageCapabilities Capabilities => StorageCapabilities.CreateFolder | StorageCapabilities.Rename | StorageCapabilities.Delete |
                                               StorageCapabilities.RecycleBin | (serverCopy ? StorageCapabilities.ServerSideCopy : 0);

    public void AddFile(string path, string content) => Entries[path] = System.Text.Encoding.UTF8.GetBytes(content);

    public string Text(string path) => System.Text.Encoding.UTF8.GetString(Entries[path]!);

    private StorageItem Item(string path) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        Path = path,
        Kind = Entries[path] is null ? StorageItemKind.Folder : StorageItemKind.File,
        Size = Entries[path] is { } b && !unknownSizes ? b.Length : null,
    };

    public async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return Entries.Keys.Where(k => k.Length > 0 && GetParentPath(k) == path).Select(Item).ToList();
    }

    public Task<StorageItem?> GetItemAsync(string path, CancellationToken ct) =>
        Task.FromResult(path.Length > 0 && Entries.ContainsKey(path) ? Item(path) : null);

    public Task<StorageItem> CreateFolderAsync(string parent, string name, CancellationToken ct)
    {
        string p = CombinePath(parent, name);
        if (Entries.ContainsKey(p)) throw new StorageException(StorageErrorKind.AlreadyExists, "exists");
        Entries[p] = null;
        return Task.FromResult(Item(p));
    }

    public Task<StorageItem> RenameAsync(string path, string newName, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken ct)
    {
        foreach (var p in paths)
        {
            foreach (var key in Entries.Keys.Where(k => k.Equals(p, StringComparison.OrdinalIgnoreCase) || k.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                Entries.TryRemove(key, out _);
            if (!permanent) Trashed.Add(p);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield break;
    }

    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path) => [new(id, "")];
    public string? GetParentPath(string path) => path.Length == 0 ? null : path.LastIndexOf('/') <= 0 ? "" : path[..path.LastIndexOf('/')];
    public string GetDisplayName(string path) => path.Length == 0 ? id : path[(path.LastIndexOf('/') + 1)..];
    public IDisposable? Watch(string path, Action onChanged) => null;
    public string CombinePath(string parentPath, string name) => parentPath + "/" + name;
    public bool IsSameOrDescendant(string path, string ancestor) => ancestor.Length == 0 || path == ancestor || path.StartsWith(ancestor + "/");
    public string? TryNormalizePath(string input) => input;

    public Task<Stream> OpenReadAsync(StorageItem file, CancellationToken ct)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult<Stream>(new MemoryStream(Entries[file.Path]!, writable: false));
    }

    public async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken ct)
    {
        string p = CombinePath(parentPath, name);
        if (!overwrite && Entries.ContainsKey(p)) throw new StorageException(StorageErrorKind.AlreadyExists, "exists");
        Assert.True(length >= 0, "destination must always get a known length");
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, 4096, ct);
        Assert.Equal(length, buffer.Length);
        Entries[p] = buffer.ToArray();
        return Item(p);
    }

    public Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken ct)
    {
        Interlocked.Increment(ref ServerCopies);
        string p = CombinePath(parentPath, name);
        Entries[p] = Entries[source.Path];
        return Task.FromResult<StorageItem?>(Item(p));
    }

    public Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken ct)
    {
        string p = CombinePath(parentPath, name);
        foreach (var key in Entries.Keys.Where(k => k == source.Path || k.StartsWith(source.Path + "/")).ToList())
        {
            Entries[p + key[source.Path.Length..]] = Entries[key];
            Entries.TryRemove(key, out _);
        }
        return Task.FromResult<StorageItem?>(Item(p));
    }
}

public sealed class TransferEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "akio-xfer-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStorageProvider _local = new(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);
    private readonly MemoryCloud _cloudA = new("cloud:a", serverCopy: true);
    private readonly MemoryCloud _cloudB = new("cloud:b");
    private readonly MemoryCloud _docs = new("cloud:docs", unknownSizes: true);
    private readonly FileOperationService _service;

    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new() { ConflictBehavior = ConflictBehavior.Skip };
        public event EventHandler? Changed { add { } remove { } }
        public void Save() { }
        public void Flush() { }
        public void NotifyChanged() { }
    }

    private sealed class NoResolver : IConflictResolver
    {
        public Task<ConflictDecision> ResolveAsync(FileOperation operation, ConflictInfo conflict, CancellationToken ct) =>
            Task.FromResult(new ConflictDecision(ConflictResolution.Skip, false));
    }

    public TransferEngineTests()
    {
        Directory.CreateDirectory(_root);
        _service = new FileOperationService(
            new StorageProviderRegistry([_local, _cloudA, _cloudB, _docs]),
            [new LocalTransferExecutor(), new ServerSideTransferExecutor(), new StreamingTransferExecutor()],
            new NoResolver(), new Settings(), NullLogger<FileOperationService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static async Task<FileOperation> Wait(FileOperation op)
    {
        var done = new TaskCompletionSource();
        op.Finished += (_, _) => done.TrySetResult();
        if (op.IsFinished) done.TrySetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return op;
    }

    private async Task<StorageItem> Item(IStorageProvider p, string path) => (await p.GetItemAsync(path, CancellationToken.None))!;

    [Fact]
    public async Task Uploads_local_folder_tree_to_cloud()
    {
        string folder = Path.Combine(_root, "Photos");
        Directory.CreateDirectory(Path.Combine(folder, "2026"));
        File.WriteAllText(Path.Combine(folder, "a.jpg"), "AAAA");
        File.WriteAllText(Path.Combine(folder, "2026", "b.jpg"), "BB");

        var op = await Wait(_service.Enqueue(OperationKind.Copy, "local", [await Item(_local, folder)], new StorageLocation("cloud:b", "")));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("AAAA", _cloudB.Text("/Photos/a.jpg"));
        Assert.Equal("BB", _cloudB.Text("/Photos/2026/b.jpg"));
        Assert.Equal(6, op.TransferredBytes);
    }

    [Fact]
    public async Task Downloads_cloud_file_to_local_without_partial_files()
    {
        _cloudB.AddFile("/report.pdf", "PDFDATA");
        var op = await Wait(_service.Enqueue(OperationKind.Copy, "cloud:b", [await Item(_cloudB, "/report.pdf")], new StorageLocation("local", _root)));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("PDFDATA", File.ReadAllText(Path.Combine(_root, "report.pdf")));
        Assert.Empty(Directory.GetFiles(_root, "*.akiopart*"));
    }

    [Fact]
    public async Task Copies_between_two_cloud_accounts_by_streaming()
    {
        _cloudA.AddFile("/x.bin", "0123456789");
        var op = await Wait(_service.Enqueue(OperationKind.Copy, "cloud:a", [await Item(_cloudA, "/x.bin")], new StorageLocation("cloud:b", "")));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("0123456789", _cloudB.Text("/x.bin"));
        Assert.Equal(0, _cloudA.ServerCopies);
    }

    [Fact]
    public async Task Same_account_copy_runs_on_the_server()
    {
        _cloudA.AddFile("/big.iso", "lots of bytes");
        _cloudA.Entries["/Backup"] = null;
        var op = await Wait(_service.Enqueue(OperationKind.Copy, "cloud:a", [await Item(_cloudA, "/big.iso")], new StorageLocation("cloud:a", "/Backup")));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal(1, _cloudA.ServerCopies);
        Assert.Equal(0, _cloudA.Reads); // no bytes through this PC
        Assert.Equal("lots of bytes", _cloudA.Text("/Backup/big.iso"));
    }

    [Fact]
    public async Task Same_account_move_of_folder_is_instant()
    {
        _cloudA.Entries["/Old"] = null;
        _cloudA.AddFile("/Old/f.txt", "f");
        _cloudA.Entries["/New"] = null;
        var op = await Wait(_service.Enqueue(OperationKind.Move, "cloud:a", [await Item(_cloudA, "/Old")], new StorageLocation("cloud:a", "/New")));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("f", _cloudA.Text("/New/Old/f.txt"));
        Assert.False(_cloudA.Entries.ContainsKey("/Old"));
        Assert.Equal(0, _cloudA.Reads);
    }

    [Fact]
    public async Task Moving_out_of_cloud_puts_original_in_its_trash()
    {
        _cloudB.AddFile("/m.txt", "move me");
        var op = await Wait(_service.Enqueue(OperationKind.Move, "cloud:b", [await Item(_cloudB, "/m.txt")], new StorageLocation("local", _root)));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("move me", File.ReadAllText(Path.Combine(_root, "m.txt")));
        Assert.Contains("/m.txt", _cloudB.Trashed); // recoverable, not permanently deleted
    }

    [Fact]
    public async Task Unknown_size_sources_are_spooled_so_destination_gets_a_length()
    {
        _docs.AddFile("/Plan", "exported document bytes");
        var op = await Wait(_service.Enqueue(OperationKind.Copy, "cloud:docs", [await Item(_docs, "/Plan")], new StorageLocation("cloud:b", "")));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("exported document bytes", _cloudB.Text("/Plan"));
    }

    [Fact]
    public async Task Pause_holds_the_transfer_and_resume_finishes_it()
    {
        string file = Path.Combine(_root, "p.bin");
        File.WriteAllText(file, "pause me");
        var op = _service.Enqueue(OperationKind.Copy, "local", [await Item(_local, file)], new StorageLocation("cloud:b", ""));
        op.Pause();
        await Task.Delay(400);

        Assert.True(op.IsPaused);
        Assert.False(op.IsFinished);
        Assert.False(_cloudB.Entries.ContainsKey("/p.bin"));

        op.Resume();
        await Wait(op);
        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal("pause me", _cloudB.Text("/p.bin"));
    }

    [Fact]
    public async Task Retry_skips_files_that_already_arrived()
    {
        string folder = Path.Combine(_root, "R");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "done.txt"), "12345");
        File.WriteAllText(Path.Combine(folder, "missing.txt"), "abc");
        _cloudB.Entries["/R"] = null;
        _cloudB.AddFile("/R/done.txt", "12345"); // arrived during the first attempt

        var op = await Wait(_service.Retry(OperationKind.Copy, "local", [await Item(_local, folder)], new StorageLocation("cloud:b", "")));

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal(1, op.SkippedFiles);
        Assert.Equal("abc", _cloudB.Text("/R/missing.txt"));
    }

    [Fact]
    public void Transfer_stream_reports_and_rewinds()
    {
        var progress = new CountingProgress();
        using var stream = new TransferStream(new MemoryStream(new byte[100]), progress, CancellationToken.None);
        var buffer = new byte[60];
        stream.ReadExactly(buffer);
        stream.Position = 20; // SDK resending a failed chunk
        Assert.Equal(20, progress.Total);
    }

    private sealed class CountingProgress : ITransferProgress
    {
        public long Total;
        public void Report(long bytes) => Total += bytes;
        public ValueTask WaitIfPausedAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public void WaitIfPaused(CancellationToken ct) { }
    }

    [Fact]
    public async Task History_persists_and_reloads()
    {
        string file = Path.Combine(_root, "history.json");
        var history = new JsonTransferHistory(file, _service, NullLogger<JsonTransferHistory>.Instance);
        File.WriteAllText(Path.Combine(_root, "h.txt"), "h");
        await Wait(_service.Enqueue(OperationKind.Copy, "local", [await Item(_local, Path.Combine(_root, "h.txt"))], new StorageLocation("cloud:b", "")));
        await Task.Delay(100);
        history.Flush();

        var reloaded = new JsonTransferHistory(file, _service, NullLogger<JsonTransferHistory>.Instance);
        var record = Assert.Single(reloaded.Records);
        Assert.Equal(OperationStatus.Completed, record.Status);
        Assert.Equal("h.txt", record.Items[0].Name);
        Assert.Equal("cloud:b", record.Destination.ProviderId);
    }
}

public class OneDriveUploadTests
{
    [Fact]
    public async Task Large_upload_uses_session_with_content_ranges()
    {
        var ranges = new List<string>();
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsoluteUri.Contains("/createUploadSession"), """{"uploadUrl":"https://upload.test/session1"}""")
            .On(r => r.RequestUri!.AbsoluteUri == "https://upload.test/session1" && r.Method == HttpMethod.Put, r =>
            {
                var range = r.Content!.Headers.ContentRange!;
                ranges.Add(range.ToString());
                Assert.Null(r.Headers.Authorization); // upload URLs are pre-authenticated; never leak the token
                return range.To + 1 == range.Length
                    ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"name":"big.bin","size":21000000,"file":{}}""") }
                    : new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"nextExpectedRanges":["x-"]}""") };
            });
        var account = new CloudAccount { Type = CloudProviderType.OneDrive, DisplayName = "OneDrive" };
        var provider = new OneDriveProvider(account, new HttpClient(fake), new StaticTokens());

        long size = 21_000_000;
        var item = await provider.WriteFileAsync("/Backups", "big.bin", new MemoryStream(new byte[size]), size, overwrite: false, CancellationToken.None);

        Assert.Equal("/Backups/big.bin", item.Path);
        Assert.Equal(["bytes 0-10485759/21000000", "bytes 10485760-20971519/21000000", "bytes 20971520-20999999/21000000"], ranges);
        Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"fail\"", fake.Requests[0].Body);
    }

    [Fact]
    public async Task Small_upload_is_a_single_put()
    {
        var fake = new FakeHttp().OnJson(r => r.Method == HttpMethod.Put, """{"name":"n.txt","size":3,"file":{}}""", HttpStatusCode.Created);
        var provider = new OneDriveProvider(new CloudAccount { DisplayName = "OneDrive" }, new HttpClient(fake), new StaticTokens());

        await provider.WriteFileAsync("", "n.txt", new MemoryStream("abc"u8.ToArray()), 3, overwrite: true, CancellationToken.None);

        var request = Assert.Single(fake.Requests);
        Assert.EndsWith("/root:/n.txt:/content?@microsoft.graph.conflictBehavior=replace", request.Request.RequestUri!.AbsoluteUri);
    }
}
