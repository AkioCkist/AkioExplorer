using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace AkioExplorer.Tests;

public class SearchQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 15, 0, 0, TimeSpan.FromHours(7));

    private static StorageItem File(string name, long size = 10, DateTimeOffset? modified = null, string? type = null) => new()
    {
        Name = name, Path = "/" + name, Kind = StorageItemKind.File, Size = size, Modified = modified ?? Now, TypeDescription = type,
    };

    private static StorageItem Folder(string name) => new() { Name = name, Path = "/" + name, Kind = StorageItemKind.Folder, Modified = Now };

    [Fact]
    public void Empty_query_has_no_criteria_and_matches_everything()
    {
        Assert.False(SearchQuery.Empty.HasCriteria);
        Assert.True(SearchQuery.Empty.CreateMatcher(Now).MatchesEverything);
    }

    [Fact]
    public void Scope_alone_is_a_filter_but_not_a_criterion()
    {
        var q = new SearchQuery { Scope = SearchScope.Everywhere };
        Assert.True(q.HasFilters);
        Assert.False(q.HasCriteria);
    }

    [Theory]
    [InlineData("report", "Q3 Report.docx", true)]
    [InlineData("report", "summary.docx", false)]
    [InlineData("*.psd", "poster.PSD", true)]
    [InlineData("lab ?", "lab 4", true)]
    public void Name_matches_substring_or_wildcard(string pattern, string name, bool expected)
    {
        var matcher = new SearchQuery { Name = pattern }.CreateMatcher(Now);
        Assert.Equal(expected, matcher.Matches(File(name)));
    }

    [Fact]
    public void Type_filter_uses_extension_folder_kind_and_google_types()
    {
        var videos = new SearchQuery { Category = FileCategory.Video }.CreateMatcher(Now);
        Assert.True(videos.Matches(File("clip.MKV")));
        Assert.False(videos.Matches(File("song.mp3")));
        Assert.False(videos.Matches(Folder("videos")));

        var folders = new SearchQuery { Category = FileCategory.Folder }.CreateMatcher(Now);
        Assert.True(folders.Matches(Folder("hci")));
        Assert.False(folders.Matches(File("hci.txt")));

        var docs = new SearchQuery { Category = FileCategory.Document }.CreateMatcher(Now);
        Assert.True(docs.Matches(File("Untitled document", type: "Google Docs")));
    }

    [Fact]
    public void Date_presets_resolve_against_local_today()
    {
        var last7 = new SearchQuery { Modified = DateFilter.Last7Days }.CreateMatcher(Now);
        Assert.True(last7.Matches(File("a", modified: Now.AddDays(-6).Date)));
        Assert.False(last7.Matches(File("a", modified: Now.AddDays(-8))));

        var lastYear = new SearchQuery { Modified = DateFilter.LastYear }.CreateMatcher(Now);
        Assert.True(lastYear.Matches(File("a", modified: new DateTimeOffset(2025, 6, 1, 0, 0, 0, Now.Offset))));
        Assert.False(lastYear.Matches(File("a", modified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, Now.Offset))));
    }

    [Fact]
    public void Custom_date_range_includes_both_end_days()
    {
        var q = new SearchQuery { Modified = DateFilter.Custom, ModifiedFrom = new DateTime(2026, 11, 1), ModifiedTo = new DateTime(2026, 11, 10) };
        var matcher = q.CreateMatcher(Now);
        Assert.True(matcher.Matches(File("a", modified: new DateTimeOffset(2026, 11, 10, 23, 59, 0, Now.Offset))));
        Assert.False(matcher.Matches(File("a", modified: new DateTimeOffset(2026, 11, 11, 0, 0, 0, Now.Offset))));
        Assert.False(matcher.Matches(File("a", modified: new DateTimeOffset(2026, 10, 31, 23, 0, 0, Now.Offset))));
    }

    [Fact]
    public void Size_filter_matches_files_only()
    {
        var big = new SearchQuery { Size = SizeFilter.Over1GB }.CreateMatcher(Now);
        Assert.True(big.Matches(File("movie.mkv", size: 2L << 30)));
        Assert.False(big.Matches(File("small.txt", size: 100)));
        Assert.False(big.Matches(Folder("huge folder")));

        var custom = new SearchQuery { Size = SizeFilter.Custom, MinSize = 100, MaxSize = 200 }.CreateMatcher(Now);
        Assert.True(custom.Matches(File("a", size: 200)));
        Assert.False(custom.Matches(File("a", size: 201)));
    }

    [Fact]
    public void Server_filters_count_as_criteria_and_are_dropped_where_unsupported()
    {
        var q = new SearchQuery { Owner = OwnerFilter.Me, Starred = true, InTrash = true, SharedWith = "a@b.c", Scope = SearchScope.SharedWithMe };
        Assert.True(q.HasCriteria);

        var local = q.RestrictTo(StorageCapabilities.Search);
        Assert.False(local.HasCriteria);
        Assert.Equal(SearchScope.CurrentFolder, local.Scope);

        Assert.False(new SearchQuery { Owner = OwnerFilter.Person }.HasCriteria); // a person without an email is "anyone"
    }

    [Fact]
    public void Google_query_includes_owner_sharing_star_and_trash()
    {
        var q = new SearchQuery
        {
            Name = "lab", Category = FileCategory.Pdf, Owner = OwnerFilter.Person, OwnerEmail = "x@y.z",
            SharedWith = "o'neil@y.z", Starred = true, InTrash = true, Scope = SearchScope.SharedWithMe,
        };
        string text = AkioExplorer.CloudProviders.GoogleDrive.GoogleDriveProvider.BuildQuery(q, q.CreateMatcher(Now));
        Assert.Contains("trashed = true", text);
        Assert.Contains("starred = true", text);
        Assert.Contains("sharedWithMe = true", text);
        Assert.Contains("'x@y.z' in owners", text);
        Assert.Contains(@"('o\'neil@y.z' in readers or 'o\'neil@y.z' in writers)", text);
        Assert.Contains("mimeType = 'application/pdf'", text);
        Assert.Contains("name contains 'lab'", text);
    }

    [Fact]
    public async Task Walker_finds_nested_matches_breadth_first()
    {
        var provider = new FakeTreeProvider(new()
        {
            [""] = [Folder("docs"), File("top.mp4")],
            ["/docs"] = [File("deep.mp4"), File("notes.txt")],
        });
        var matcher = new SearchQuery { Category = FileCategory.Video }.CreateMatcher(Now);
        var found = new List<string>();
        await foreach (var item in SearchWalker.WalkAsync(provider, "", matcher, CancellationToken.None)) found.Add(item.Name);
        Assert.Equal(["top.mp4", "deep.mp4"], found);
    }

    [Fact]
    public async Task Local_search_applies_type_filter_without_a_name()
    {
        string root = Path.Combine(Path.GetTempPath(), "akio-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        System.IO.File.WriteAllText(Path.Combine(root, "a.png"), "x");
        System.IO.File.WriteAllText(Path.Combine(root, "sub", "b.jpg"), "x");
        System.IO.File.WriteAllText(Path.Combine(root, "sub", "c.txt"), "x");
        try
        {
            var provider = new LocalStorageProvider(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);
            var names = new List<string>();
            await foreach (var item in provider.SearchAsync(root, new SearchQuery { Category = FileCategory.Image }, CancellationToken.None))
                names.Add(item.Name);
            names.Sort();
            Assert.Equal(["a.png", "b.jpg"], names);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeTreeProvider(Dictionary<string, StorageItem[]> tree) : AkioExplorer.Core.Abstractions.IStorageProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public StorageCapabilities Capabilities => StorageCapabilities.Search;
        public string RootPath => "";

        public async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return tree.GetValueOrDefault(path, []);
        }

        public Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<StorageItem> SearchAsync(string path, SearchQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path) => [];
        public string? GetParentPath(string path) => null;
        public string GetDisplayName(string path) => path;
        public IDisposable? Watch(string path, Action onChanged) => null;
        public string CombinePath(string parentPath, string name) => parentPath + "/" + name;
        public bool IsSameOrDescendant(string path, string ancestor) => path.StartsWith(ancestor, StringComparison.Ordinal);
        public string? TryNormalizePath(string input) => input;
    }
}
