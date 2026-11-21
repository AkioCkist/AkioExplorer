using System.Globalization;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Tests;

public class NaturalStringComparerTests
{
    private readonly NaturalStringComparer _comparer = new(CultureInfo.InvariantCulture);

    [Fact]
    public void Orders_numbers_numerically()
    {
        var input = new[] { "File 10", "file 2", "File 1", "File 02", "a", "File" };
        var sorted = input.OrderBy(x => x, _comparer).ToArray();
        Assert.Equal(new[] { "a", "File", "File 1", "file 2", "File 02", "File 10" }, sorted);
    }

    [Fact]
    public void Is_case_insensitive_and_symmetric()
    {
        Assert.True(_comparer.Compare("abc", "ABD") < 0);
        Assert.True(_comparer.Compare("ABD", "abc") > 0);
        Assert.True(_comparer.Compare("img9.png", "img10.png") < 0);
        Assert.True(_comparer.Compare("x99999999999999999999", "x100000000000000000000") < 0);
    }
}

public class StorageItemComparerTests
{
    private static StorageItem File(string name, long size = 0, int day = 1) => new()
    {
        Name = name, Path = @"C:\t\" + name, Kind = StorageItemKind.File, Size = size, Modified = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
    };

    private static StorageItem Folder(string name) => new() { Name = name, Path = @"C:\t\" + name, Kind = StorageItemKind.Folder };

    [Fact]
    public void Folders_first_even_when_descending()
    {
        var items = new[] { File("b.txt", 5), Folder("z"), File("a.txt", 10), Folder("a") };
        var sorted = items.Order(new StorageItemComparer(new SortOptions(SortField.Size, Descending: true))).Select(i => i.Name).ToArray();
        Assert.Equal(new[] { "z", "a", "a.txt", "b.txt" }, sorted);
    }

    [Fact]
    public void Sorts_by_date()
    {
        var items = new[] { File("new", day: 20), File("old", day: 2) };
        var sorted = items.Order(new StorageItemComparer(new SortOptions(SortField.Modified))).Select(i => i.Name).ToArray();
        Assert.Equal(new[] { "old", "new" }, sorted);
    }

    [Fact]
    public void Without_folders_first_folders_and_files_mix_by_the_field()
    {
        var folder = Folder("Projects") with { Modified = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero) };
        var items = new[] { File("new.txt", day: 20), folder, File("old.txt", day: 2) };

        var byDate = items.Order(new StorageItemComparer(new SortOptions(SortField.Modified, FoldersFirst: false))).Select(i => i.Name);
        Assert.Equal(new[] { "old.txt", "Projects", "new.txt" }, byDate);

        var byName = items.Order(new StorageItemComparer(new SortOptions(SortField.Name, FoldersFirst: false))).Select(i => i.Name);
        Assert.Equal(new[] { "new.txt", "old.txt", "Projects" }, byName);
    }
}

public class FileNameValidatorTests
{
    [Theory]
    [InlineData("ok.txt", true)]
    [InlineData("Tài liệu 2026", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("what?", false)]
    [InlineData("CON", false)]
    [InlineData("con.txt", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    public void Validates(string name, bool valid)
    {
        Assert.Equal(valid, FileNameValidator.Validate(name) is null);
    }

    [Fact]
    public void Unique_name_appends_counter()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New folder", "new folder (2)" };
        Assert.Equal("New folder (3)", FileNameValidator.GetUniqueName("New folder", existing));
        Assert.Equal("Other", FileNameValidator.GetUniqueName("Other", existing));
    }
}

public class ListDiffTests
{
    private sealed class Row(string key)
    {
        public string Key { get; } = key;
        public int Version { get; set; }
    }

    [Theory]
    [InlineData("a,b,c", "a,b,c")]
    [InlineData("a,b,c", "c,b,a")]
    [InlineData("a,b,c", "a,x,c,y")]
    [InlineData("", "a,b")]
    [InlineData("a,b", "")]
    [InlineData("a,b,c,d,e", "e,a,c")]
    public void Produces_source_order_and_reuses_rows(string before, string after)
    {
        var target = Split(before).Select(k => new Row(k)).ToList();
        var originals = target.ToDictionary(r => r.Key);
        var source = Split(after);

        ListDiff.Apply(target, source, r => r.Key, s => s, s => new Row(s), (r, _) => r.Version++);

        Assert.Equal(source, target.Select(r => r.Key).ToArray());
        foreach (var row in target)
        {
            if (originals.TryGetValue(row.Key, out var original)) Assert.Same(original, row);
        }
    }

    private static string[] Split(string s) => s.Length == 0 ? [] : s.Split(',');
}

public class ListingCacheTests
{
    [Fact]
    public void Evicts_least_recently_used()
    {
        var cache = new ListingCache(capacity: 2);
        var a = new StorageLocation("local", @"C:\a");
        var b = new StorageLocation("local", @"C:\b");
        var c = new StorageLocation("local", @"C:\c");

        cache.Set(a, []);
        cache.Set(b, []);
        Assert.True(cache.TryGet(a, out _)); // a is now most recent
        cache.Set(c, []);

        Assert.True(cache.TryGet(a, out _));
        Assert.False(cache.TryGet(b, out _));
        Assert.True(cache.TryGet(new StorageLocation("local", @"c:\C"), out _)); // case-insensitive paths
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(512L * 1024 * 1024 * 1024, "512 GB")]
    public void Formats_sizes(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.FormatSize(bytes, CultureInfo.InvariantCulture));
    }
}
