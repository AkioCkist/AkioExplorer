using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace AkioExplorer.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "akio-ops-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStorageProvider _local = new(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);
    private readonly FakeSettings _settings = new();
    private readonly FakeResolver _resolver = new();
    private readonly FileOperationService _service;

    public FileOperationServiceTests()
    {
        Directory.CreateDirectory(_root);
        _service = new FileOperationService(
            new StorageProviderRegistry([_local]),
            [new LocalTransferExecutor()],
            _resolver,
            _settings,
            NullLogger<FileOperationService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string FileAt(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<StorageItem> Item(string path) => (await _local.GetItemAsync(path, CancellationToken.None))!;

    private async Task<FileOperation> Run(OperationKind kind, string destination, params string[] sources)
    {
        var items = new List<StorageItem>();
        foreach (var s in sources) items.Add(await Item(s));
        var op = _service.Enqueue(kind, "local", items, new StorageLocation("local", destination));
        var done = new TaskCompletionSource();
        op.Finished += (_, _) => done.TrySetResult();
        if (op.IsFinished) done.TrySetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return op;
    }

    [Fact]
    public async Task Copies_file_and_reports_progress()
    {
        var src = FileAt(Path.Combine(_root, "a", "report.txt"), new string('x', 100_000));
        var dst = Dir("b");

        var op = await Run(OperationKind.Copy, dst, src);

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal(100_000, new FileInfo(Path.Combine(dst, "report.txt")).Length);
        Assert.True(File.Exists(src));
        Assert.Equal(op.TotalBytes, op.TransferredBytes);
        Assert.Equal([Path.Combine(dst, "report.txt")], op.CreatedPaths);
    }

    [Fact]
    public async Task Copies_folder_tree_including_empty_folders()
    {
        var folder = Dir("tree");
        FileAt(Path.Combine(folder, "one.txt"));
        FileAt(Path.Combine(folder, "sub", "two.txt"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var dst = Dir("out");

        var op = await Run(OperationKind.Copy, dst, folder);

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.True(File.Exists(Path.Combine(dst, "tree", "one.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "tree", "sub", "two.txt")));
        Assert.True(Directory.Exists(Path.Combine(dst, "tree", "empty")));
        Assert.Equal(2, op.TotalFiles);
    }

    [Fact]
    public async Task Copy_into_same_folder_creates_copy_name()
    {
        var folder = Dir("same");
        var src = FileAt(Path.Combine(folder, "doc.txt"));

        await Run(OperationKind.Copy, folder, src);
        await Run(OperationKind.Copy, folder, src);

        Assert.True(File.Exists(Path.Combine(folder, "doc - Copy.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "doc - Copy (2).txt")));
    }

    [Fact]
    public async Task Move_on_same_volume_is_a_rename()
    {
        var folder = Dir("bigfolder");
        FileAt(Path.Combine(folder, "x.bin"));
        var dst = Dir("target");

        var op = await Run(OperationKind.Move, dst, folder);

        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(dst, "bigfolder", "x.bin")));
    }

    [Theory]
    [InlineData(ConflictResolution.Replace, "new", false)]
    [InlineData(ConflictResolution.Skip, "old", false)]
    [InlineData(ConflictResolution.KeepBoth, "old", true)]
    public async Task Conflicts_follow_user_choice(ConflictResolution choice, string expectedContent, bool expectCopy)
    {
        var src = FileAt(Path.Combine(_root, "s", "f.txt"), "new");
        var dst = Dir("d");
        FileAt(Path.Combine(dst, "f.txt"), "old");
        _resolver.Answer = new ConflictDecision(choice, false);

        var op = await Run(OperationKind.Copy, dst, src);

        Assert.Equal(1, _resolver.Calls);
        Assert.Equal(expectedContent, File.ReadAllText(Path.Combine(dst, "f.txt")));
        Assert.Equal(expectCopy, File.Exists(Path.Combine(dst, "f (2).txt")));
        Assert.Equal(OperationStatus.Completed, op.Status);
    }

    [Fact]
    public async Task Apply_to_all_asks_only_once()
    {
        var s = Dir("s2");
        var d = Dir("d2");
        for (int i = 0; i < 5; i++)
        {
            FileAt(Path.Combine(s, $"{i}.txt"), "new");
            FileAt(Path.Combine(d, $"{i}.txt"), "old");
        }
        _resolver.Answer = new ConflictDecision(ConflictResolution.Replace, ApplyToAll: true);

        var sources = Enumerable.Range(0, 5).Select(i => Path.Combine(s, $"{i}.txt")).ToArray();
        await Run(OperationKind.Copy, d, sources);

        Assert.Equal(1, _resolver.Calls);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal("new", File.ReadAllText(Path.Combine(d, $"{i}.txt"))));
    }

    [Fact]
    public async Task Settings_behavior_skips_dialog()
    {
        var src = FileAt(Path.Combine(_root, "s3", "f.txt"), "new");
        var dst = Dir("d3");
        FileAt(Path.Combine(dst, "f.txt"), "old");
        _settings.Current.ConflictBehavior = ConflictBehavior.KeepBoth;

        await Run(OperationKind.Copy, dst, src);

        Assert.Equal(0, _resolver.Calls);
        Assert.True(File.Exists(Path.Combine(dst, "f (2).txt")));
    }

    [Fact]
    public async Task Move_with_skipped_file_keeps_source_folder()
    {
        var folder = Dir("keep");
        FileAt(Path.Combine(folder, "a.txt"), "new");
        FileAt(Path.Combine(folder, "b.txt"), "new");
        var dst = Dir("into");
        FileAt(Path.Combine(dst, "keep", "a.txt"), "old"); // folder exists → merge path, not fast rename
        _resolver.Answer = new ConflictDecision(ConflictResolution.Skip, false);

        var op = await Run(OperationKind.Move, dst, folder);

        Assert.True(Directory.Exists(folder)); // nothing lost
        Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "keep", "a.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "keep", "b.txt")));
        Assert.Equal(OperationStatus.CompletedWithErrors, op.Status);
    }

    [Fact]
    public async Task Refuses_to_copy_folder_into_itself()
    {
        var folder = Dir("self");
        var inner = Dir(Path.Combine("self", "inner"));

        var op = await Run(OperationKind.Copy, inner, folder);

        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Contains("itself", op.Errors[0]);
    }

    [Fact]
    public async Task Cancel_stops_and_leaves_no_partial_file()
    {
        var src = Path.Combine(_root, "big.bin");
        using (var fs = File.Create(src)) fs.SetLength(300L * 1024 * 1024);
        var dst = Dir("cancel");

        var op = _service.Enqueue(OperationKind.Copy, "local", [await Item(src)], new StorageLocation("local", dst));
        var done = new TaskCompletionSource();
        op.Finished += (_, _) => done.TrySetResult();
        op.Cancel();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Canceled, op.Status);
        Assert.False(File.Exists(Path.Combine(dst, "big.bin")));
    }

    [Fact]
    public void Naming_matches_explorer()
    {
        Assert.Equal(["a (2).txt", "a (3).txt"], ConflictNaming.Candidates("a.txt", false).Take(2));
        Assert.Equal(["a - Copy.txt", "a - Copy (2).txt"], ConflictNaming.Candidates("a.txt", true).Take(2));
        Assert.Equal(".gitignore - Copy", ConflictNaming.Candidates(".gitignore", true).First());
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event EventHandler? Changed;
        public void Save() { }
        public void Flush() { }
        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeResolver : IConflictResolver
    {
        public ConflictDecision Answer { get; set; } = new(ConflictResolution.Skip, false);
        public int Calls { get; private set; }

        public Task<ConflictDecision> ResolveAsync(FileOperation operation, ConflictInfo conflict, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }
}
