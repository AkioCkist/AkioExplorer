using System.Diagnostics;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AkioExplorer.Tests;

public sealed class LocalSearchTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "akio-search-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStorageProvider _provider = new(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);

    public LocalSearchTests(ITestOutputHelper output)
    {
        _output = output;
        for (int i = 0; i < 20; i++)
        {
            string dir = Path.Combine(_root, "d" + i, "inner");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"report-{i}.txt"), "x");
            File.WriteAllText(Path.Combine(dir, $"photo-{i}.png"), "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<List<StorageItem>> Search(string path, SearchQuery query)
    {
        var results = new List<StorageItem>();
        await foreach (var item in _provider.SearchAsync(path, query, CancellationToken.None)) results.Add(item);
        return results;
    }

    [Fact]
    public async Task Parallel_walk_finds_every_match()
    {
        _provider.UseEverythingIndex = false;
        var results = await Search(_root, new SearchQuery { Name = "report" });
        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.StartsWith("report-", r.Name));

        var images = await Search(_root, new SearchQuery { Category = FileCategory.Image });
        Assert.Equal(20, images.Count);

        var wildcard = await Search(_root, new SearchQuery { Name = "photo-1?.png" });
        Assert.Equal(10, wildcard.Count);
    }

    [Fact]
    public async Task Stopping_early_does_not_hang()
    {
        _provider.UseEverythingIndex = false;
        int seen = 0;
        await foreach (var _ in _provider.SearchAsync(_root, new SearchQuery { Name = "-" }, CancellationToken.None))
            if (++seen == 3) break;
        Assert.Equal(3, seen);
    }

    [Fact]
    public void Builds_everything_search_text()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(7));
        string text = LocalSearch.BuildSearch(
            new SearchQuery { Name = "my \"report\"", Category = FileCategory.Pdf, Modified = DateFilter.Last7Days },
            @"C:\Users\me\", now);
        Assert.Equal(@"""C:\Users\me\"" ""my report"" file: ext:pdf dm:>=2026-11-16", text);

        string sized = LocalSearch.BuildSearch(new SearchQuery { Size = SizeFilter.Over1GB }, null, now);
        Assert.Equal("file: size:>=1073741824", sized);
    }

    [Fact]
    public async Task Everything_index_matches_walk_when_running()
    {
        if (!EverythingSession.IsAvailable())
        {
            _output.WriteLine("Everything isn't running: skipped.");
            return;
        }

        // Everything picks new files up from the USN journal within a moment.
        List<StorageItem> fromIndex = [];
        for (int attempt = 0; attempt < 20 && fromIndex.Count < 20; attempt++)
        {
            await Task.Delay(attempt == 0 ? 0 : 250);
            fromIndex = await Search(_root, new SearchQuery { Name = "report" });
        }

        Assert.Equal(20, fromIndex.Count);
        var sample = fromIndex.OrderBy(i => i.Name, StringComparer.Ordinal).First();
        Assert.Equal(Path.Combine(_root, "d0", "inner", "report-0.txt"), sample.Path, ignoreCase: true);
        Assert.Equal(1, sample.Size);
        Assert.NotNull(sample.Modified);

        var clock = Stopwatch.StartNew();
        var everywhere = new List<StorageItem>();
        await foreach (var item in _provider.SearchAsync(string.Empty, new SearchQuery { Name = ".dll" }, CancellationToken.None))
            if (everywhere.Count < 5000) everywhere.Add(item); else break;
        _output.WriteLine($"Whole PC '.dll': {everywhere.Count} in {clock.ElapsedMilliseconds} ms");
        Assert.NotEmpty(everywhere);
    }
}
