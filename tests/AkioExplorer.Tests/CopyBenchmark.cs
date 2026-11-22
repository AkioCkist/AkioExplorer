using System.Diagnostics;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AkioExplorer.Tests;

/// <summary>
/// Local copy / move / delete timings. Off by default (slow, disk-bound); run with
/// <c>$env:AKIO_BENCH=1; dotnet test --filter CopyBenchmark --logger "console;verbosity=detailed"</c>.
/// </summary>
public sealed class CopyBenchmark(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "akio-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Local_copy_move_delete()
    {
        if (Environment.GetEnvironmentVariable("AKIO_BENCH") != "1") return;

        var local = new LocalStorageProvider(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);
        var settings = new FileOperationServiceTests.FakeSettings();
        var service = new FileOperationService(new StorageProviderRegistry([local]), [new LocalTransferExecutor()],
            new FileOperationServiceTests.FakeResolver(), settings, NullLogger<FileOperationService>.Instance);

        // 3000 small files in 30 folders + 8 files of 32 MB.
        string source = Path.Combine(_root, "src", "Data");
        var small = new byte[4096];
        Random.Shared.NextBytes(small);
        for (int d = 0; d < 30; d++)
        {
            string dir = Path.Combine(source, $"dir{d}");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 100; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f}.bin"), small);
        }
        var big = new byte[32 << 20];
        Random.Shared.NextBytes(big);
        for (int i = 0; i < 8; i++) File.WriteAllBytes(Path.Combine(source, $"big{i}.bin"), big);

        async Task<TimeSpan> Run(OperationKind kind, string from, string to)
        {
            Directory.CreateDirectory(to);
            var item = (await local.GetItemAsync(from, CancellationToken.None))!;
            var clock = Stopwatch.StartNew();
            var op = service.Enqueue(kind, "local", [item], new StorageLocation("local", to));
            var done = new TaskCompletionSource();
            op.Finished += (_, _) => done.TrySetResult();
            if (op.IsFinished) done.TrySetResult();
            await done.Task.WaitAsync(TimeSpan.FromMinutes(5));
            Assert.Equal(OperationStatus.Completed, op.Status);
            return clock.Elapsed;
        }

        var copy = await Run(OperationKind.Copy, source, Path.Combine(_root, "copy1"));
        var copyAgain = await Run(OperationKind.Copy, source, Path.Combine(_root, "copy2"));
        string copied = Path.Combine(_root, "copy1", "Data");
        var deleteClock = Stopwatch.StartNew();
        await local.DeleteAsync([copied], permanent: true, CancellationToken.None);
        var delete = deleteClock.Elapsed;
        var recycleClock = Stopwatch.StartNew();
        await local.DeleteAsync([Path.Combine(_root, "copy2", "Data")], permanent: false, CancellationToken.None);
        var recycle = recycleClock.Elapsed;

        output.WriteLine($"copy 3000 small + 8x32MB: {copy.TotalMilliseconds:N0} ms (second run {copyAgain.TotalMilliseconds:N0} ms)");
        output.WriteLine($"delete permanently: {delete.TotalMilliseconds:N0} ms, to Recycle Bin: {recycle.TotalMilliseconds:N0} ms");
    }
}
