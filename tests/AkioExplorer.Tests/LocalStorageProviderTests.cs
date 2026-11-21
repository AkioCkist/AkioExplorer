using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace AkioExplorer.Tests;

public sealed class LocalStorageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "akio-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStorageProvider _provider = new(new KnownFoldersService(), NullLogger<LocalStorageProvider>.Instance);

    public LocalStorageProviderTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "Sub", "deep-match.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<List<StorageItem>> ListAll(string path)
    {
        var all = new List<StorageItem>();
        await foreach (var page in _provider.ListAsync(path, CancellationToken.None)) all.AddRange(page);
        return all;
    }

    [Fact]
    public async Task Lists_files_and_folders_with_metadata()
    {
        var items = await ListAll(_root);
        Assert.Equal(2, items.Count);
        var file = items.Single(i => i.Name == "a.txt");
        Assert.Equal(StorageItemKind.File, file.Kind);
        Assert.Equal(5, file.Size);
        Assert.NotNull(file.Modified);
        Assert.Equal(StorageItemKind.Folder, items.Single(i => i.Name == "Sub").Kind);
    }

    [Fact]
    public async Task Root_lists_drives()
    {
        var items = await ListAll(string.Empty);
        Assert.Contains(items, i => i.Kind == StorageItemKind.Drive && i.Path.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_folder_throws_friendly_error()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => ListAll(Path.Combine(_root, "nope")));
        Assert.Equal(StorageErrorKind.NotFound, ex.Kind);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Create_rename_delete_roundtrip()
    {
        var folder = await _provider.CreateFolderAsync(_root, "New folder", CancellationToken.None);
        Assert.True(Directory.Exists(folder.Path));

        await Assert.ThrowsAsync<StorageException>(() => _provider.CreateFolderAsync(_root, "New folder", CancellationToken.None));
        await Assert.ThrowsAsync<StorageException>(() => _provider.CreateFolderAsync(_root, "bad|name", CancellationToken.None));

        var renamed = await _provider.RenameAsync(folder.Path, "Renamed", CancellationToken.None);
        Assert.Equal("Renamed", renamed.Name);
        Assert.True(Directory.Exists(Path.Combine(_root, "Renamed")));

        var caseOnly = await _provider.RenameAsync(renamed.Path, "RENAMED", CancellationToken.None);
        Assert.Equal("RENAMED", caseOnly.Name);

        await _provider.DeleteAsync([caseOnly.Path], permanent: true, CancellationToken.None);
        Assert.False(Directory.Exists(caseOnly.Path));
    }

    [Fact]
    public async Task Recycle_bin_delete_removes_file()
    {
        string file = Path.Combine(_root, "recycle-me.txt");
        File.WriteAllText(file, "bye");
        await _provider.DeleteAsync([file], permanent: false, CancellationToken.None);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Search_is_recursive()
    {
        var results = new List<StorageItem>();
        await foreach (var item in _provider.SearchAsync(_root, "match", CancellationToken.None)) results.Add(item);
        Assert.Single(results);
        Assert.Equal("deep-match.txt", results[0].Name);
    }

    [Fact]
    public void Breadcrumb_and_parent()
    {
        var crumbs = _provider.GetBreadcrumb(@"C:\Users\Public");
        Assert.Equal(new[] { "", @"C:\", @"C:\Users", @"C:\Users\Public" }, crumbs.Select(c => c.Path).ToArray());
        Assert.Equal(@"C:\Users", _provider.GetParentPath(@"C:\Users\Public"));
        Assert.Equal(string.Empty, _provider.GetParentPath(@"C:\"));
        Assert.Null(_provider.GetParentPath(string.Empty));
    }

    [Theory]
    [InlineData("c:", @"c:\")]
    [InlineData(@"C:\Windows\", @"C:\Windows")]
    [InlineData("\"C:\\Windows\"", @"C:\Windows")]
    [InlineData("This PC", "")]
    [InlineData("relative\\path", null)]
    public void Normalizes_typed_paths(string input, string? expected)
    {
        Assert.Equal(expected, _provider.TryNormalizePath(input));
    }
}
