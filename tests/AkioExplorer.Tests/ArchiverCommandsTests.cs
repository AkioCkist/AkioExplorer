using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;

namespace AkioExplorer.Tests;

public class ArchiverCommandsTests
{
    private static ArchiverApp App(ArchiverKind kind) => new(kind, kind.ToString(), $@"C:\Apps\{kind}.exe", null);

    private static readonly string[] Items = [@"D:\Work\Photos", @"D:\Work\notes.txt"];

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("b.RAR", true)]
    [InlineData("c.tar.gz", true)]
    [InlineData("d.7z", true)]
    [InlineData("e.txt", false)]
    [InlineData("folder", false)]
    public void Recognises_archives(string name, bool expected) => Assert.Equal(expected, ArchiverCommands.IsArchive(name));

    [Theory]
    [InlineData(@"D:\x\photos.zip", "photos")]
    [InlineData(@"D:\x\photos.tar.gz", "photos")]
    [InlineData(@"D:\x\movie.part1.rar", "movie")]
    [InlineData(@"D:\x\my.party.rar", "my.party")]
    [InlineData(@"D:\x\.zip", ".zip")]
    public void Extract_folder_is_named_after_the_archive(string archive, string expected) =>
        Assert.Equal(expected, ArchiverCommands.ExtractFolderName(archive));

    [Fact]
    public void WinRar_compresses_names_relative_to_their_folder()
    {
        var run = ArchiverCommands.Compress(App(ArchiverKind.WinRar), Items, @"D:\Work\Work.zip");
        Assert.Equal(@"C:\Apps\WinRar.exe", run.FileName);
        Assert.Equal(@"D:\Work", run.WorkingDirectory);
        Assert.Equal(["a", "-afzip", "--", @"D:\Work\Work.zip", "Photos", "notes.txt"], run.Arguments);
        Assert.Contains("-afrar", ArchiverCommands.Compress(App(ArchiverKind.WinRar), Items, @"D:\Work\Work.rar").Arguments);
    }

    [Fact]
    public void WinRar_extracts_into_a_folder_given_with_a_trailing_backslash()
    {
        Assert.Equal(["x", "--", @"D:\d\a.rar", @"D:\d\"], ArchiverCommands.Extract(App(ArchiverKind.WinRar), @"D:\d\a.rar", toFolder: false).Arguments);
        Assert.Equal(["x", "--", @"D:\d\a.rar", @"D:\d\a\"], ArchiverCommands.Extract(App(ArchiverKind.WinRar), @"D:\d\a.rar", toFolder: true).Arguments);
    }

    [Fact]
    public void SevenZip_uses_type_switch_or_its_own_dialog()
    {
        var quick = ArchiverCommands.Compress(App(ArchiverKind.SevenZip), Items, @"D:\Work\Work.7z");
        Assert.Equal(["a", "-t7z", "--", @"D:\Work\Work.7z", "Photos", "notes.txt"], quick.Arguments);

        var dialog = ArchiverCommands.Compress(App(ArchiverKind.SevenZip), Items, @"D:\Work\Work.7z", showDialog: true);
        Assert.Equal(["a", "-ad", "-saa", "--", @"D:\Work\Work", "Photos", "notes.txt"], dialog.Arguments);

        Assert.Equal(["x", @"-oD:\d\a", "--", @"D:\d\a.7z"], ArchiverCommands.Extract(App(ArchiverKind.SevenZip), @"D:\d\a.7z", toFolder: true).Arguments);
    }

    [Fact]
    public void Bandizip_and_PeaZip_commands()
    {
        Assert.Equal(["c", @"D:\Work\Work.zip", "Photos", "notes.txt"], ArchiverCommands.Compress(App(ArchiverKind.Bandizip), Items, @"D:\Work\Work.zip").Arguments);
        Assert.Equal(["x", @"-o:D:\d\a", @"D:\d\a.zip"], ArchiverCommands.Extract(App(ArchiverKind.Bandizip), @"D:\d\a.zip", toFolder: true).Arguments);
        Assert.Equal(["-add2archive", .. Items], ArchiverCommands.Compress(App(ArchiverKind.PeaZip), Items, @"D:\Work\Work.7z", showDialog: true).Arguments);
        Assert.Equal(["-ext2here", @"D:\d\a.zip"], ArchiverCommands.Extract(App(ArchiverKind.PeaZip), @"D:\d\a.zip", toFolder: false).Arguments);
    }
}
