using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace AkioExplorer.Tests;

public sealed class SegmentedDownloadExecutorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "akio-segexec-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStorageProvider _local = new(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);

    public SegmentedDownloadExecutorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Downloads_in_parts_and_reports_them()
    {
        var data = TestFileServer.RandomData(24 * 1024 * 1024, seed: 3);
        using var server = new TestFileServer(data) { Faults = _ => new Fault(DelayPerChunkMs: 1) };
        var cloud = new MemoryCloud("cloud") { DirectLinks = _ => new DirectDownload(server.Url, new Dictionary<string, string>()) };
        var file = new StorageItem { Name = "big.bin", Path = "big.bin", Kind = StorageItemKind.File, Size = data.Length };
        var progress = new RecordingProgress();
        var executor = new SegmentedDownloadExecutor(() => 6);

        Assert.True(executor.CanTransfer(cloud, _local));
        string target = Path.Combine(_dir, "big.bin");
        await executor.CopyFileAsync(cloud, file, _local, target, overwrite: false, progress, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.Equal(data.Length, progress.Bytes);
        Assert.True(progress.MostParts > 1, $"expected several parts, saw {progress.MostParts}");
        Assert.True(server.MaxConcurrent > 1);
        Assert.Null(progress.LastParts); // cleared when the file is done
        Assert.False(File.Exists(target + ".akiodl"));
    }

    [Fact]
    public async Task Small_files_and_files_without_a_link_stream()
    {
        var cloud = new MemoryCloud("cloud") { DirectLinks = _ => null };
        cloud.AddFile("a.txt", "hello");
        var file = (await cloud.GetItemAsync("a.txt", CancellationToken.None))!;
        string target = Path.Combine(_dir, "a.txt");

        await new SegmentedDownloadExecutor().CopyFileAsync(cloud, file, _local, target, overwrite: false, new RecordingProgress(), CancellationToken.None);

        Assert.Equal("hello", await File.ReadAllTextAsync(target));
    }

    private sealed class RecordingProgress : ITransferProgress
    {
        private long _bytes;
        public long Bytes => Interlocked.Read(ref _bytes);
        public int MostParts { get; private set; }
        public PartedFileProgress? LastParts { get; private set; }

        public void Report(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public ValueTask WaitIfPausedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void WaitIfPaused(CancellationToken cancellationToken)
        {
        }

        public void ReportParts(PartedFileProgress? parts)
        {
            LastParts = parts;
            if (parts is not null) MostParts = Math.Max(MostParts, parts.Segments.Count);
        }
    }
}
