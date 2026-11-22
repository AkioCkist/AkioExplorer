using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Tests;

public class BatchRenamerTests
{
    private static StorageItem File(string name) => new()
    {
        Name = name, Path = @"C:\t\" + name, Kind = StorageItemKind.File,
        Modified = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
    };

    private static StorageItem Folder(string name) => new() { Name = name, Path = @"C:\t\" + name, Kind = StorageItemKind.Folder };

    [Fact]
    public void Default_options_keep_names()
    {
        var plan = BatchRenamer.Plan([File("a.txt"), Folder("b.c")], new BatchRenameOptions(), []);
        Assert.All(plan, p => Assert.False(p.IsChange));
        Assert.Equal("b.c", plan[1].NewName);
    }

    [Fact]
    public void Mask_with_counter_and_padding()
    {
        var options = new BatchRenameOptions { NameMask = "Trip [C]", CounterStart = 7, CounterStep = 2, CounterDigits = 3 };
        var plan = BatchRenamer.Plan([File("x.JPG"), File("y.jpg")], options, []);
        Assert.Equal("Trip 007.JPG", plan[0].NewName);
        Assert.Equal("Trip 009.jpg", plan[1].NewName);
    }

    [Fact]
    public void Substring_extension_and_parent_tokens()
    {
        var options = new BatchRenameOptions { NameMask = "[P]_[N2-4]", ExtensionMask = "bak" };
        var entry = BatchRenamer.Plan([File("abcdef.txt")], options, [], parentName: "Docs")[0];
        Assert.Equal("Docs_bcd.bak", entry.NewName);
    }

    [Fact]
    public void Find_replace_plain_is_case_insensitive_by_default()
    {
        var options = new BatchRenameOptions { Find = "img", Replace = "Photo" };
        Assert.Equal("Photo_01.png", BatchRenamer.Plan([File("IMG_01.png")], options, [])[0].NewName);
    }

    [Fact]
    public void Regex_replace_with_groups()
    {
        var options = new BatchRenameOptions { Find = @"(\d+)-(\d+)", Replace = "$2-$1", UseRegex = true };
        Assert.Equal("22-11.txt", BatchRenamer.Plan([File("11-22.txt")], options, [])[0].NewName);
    }

    [Fact]
    public void Invalid_regex_is_reported_not_thrown()
    {
        var options = new BatchRenameOptions { Find = "(", UseRegex = true };
        var entry = BatchRenamer.Plan([File("a.txt")], options, [])[0];
        Assert.NotNull(entry.Error);
        Assert.False(entry.IsChange);
    }

    [Fact]
    public void Case_change_applies_to_whole_name()
    {
        var options = new BatchRenameOptions { Case = RenameCase.Lower };
        Assert.Equal("my file.jpg", BatchRenamer.Plan([File("My FILE.JPG")], options, [])[0].NewName);
    }

    [Fact]
    public void Date_token_uses_modified_time()
    {
        var item = File("a.txt");
        var options = new BatchRenameOptions { NameMask = "[YMD] [N]" };
        string expected = item.Modified!.Value.LocalDateTime.ToString("yyyy-MM-dd") + " a.txt";
        Assert.Equal(expected, BatchRenamer.Plan([item], options, [])[0].NewName);
    }

    [Fact]
    public void Clashes_with_other_items_and_each_other_are_flagged()
    {
        var options = new BatchRenameOptions { NameMask = "same" };
        var plan = BatchRenamer.Plan([File("a.txt"), File("b.txt")], options, ["a.txt", "b.txt"]);
        Assert.All(plan, p => Assert.Equal("Two items would get this name.", p.Error));

        var clash = BatchRenamer.Plan([File("a.txt")], new BatchRenameOptions { NameMask = "keep" }, ["a.txt", "keep.txt"])[0];
        Assert.Equal("An item with this name already exists.", clash.Error);
    }

    [Fact]
    public void Swapping_names_between_renamed_items_is_allowed()
    {
        var options = new BatchRenameOptions { Find = @"^(a|b)", Replace = "x$1", UseRegex = true };
        var plan = BatchRenamer.Plan([File("a.txt"), File("b.txt")], options, ["a.txt", "b.txt"]);
        Assert.All(plan, p => Assert.Null(p.Error));
    }

    [Fact]
    public void Invalid_characters_are_flagged()
    {
        var entry = BatchRenamer.Plan([File("a.txt")], new BatchRenameOptions { NameMask = "a:b" }, [])[0];
        Assert.NotNull(entry.Error);
    }
}
