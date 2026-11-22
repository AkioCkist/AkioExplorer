using AkioExplorer.Core.Abstractions;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AkioExplorer.Tests;

/// <summary>Talks to the real Windows shell: checks the context menu, "Open with" list and editor detection work end to end.</summary>
public sealed class ShellMenuServiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly ShellMenuService _service = new(NullLogger<ShellMenuService>.Instance);
    private readonly string _folder = Directory.CreateTempSubdirectory("akio-shellmenu-").FullName;

    [Fact]
    public async Task ItemMenu_lists_shell_entries_without_the_apps_own_commands()
    {
        string file = Path.Combine(_folder, "note.txt");
        await File.WriteAllTextAsync(file, "hello");

        using var menu = await _service.GetItemMenuAsync([file], extended: false);
        Assert.NotNull(menu);
        Dump(menu!.Entries, "");
        Assert.NotEmpty(menu.Entries);
        Assert.DoesNotContain(menu.Entries, e => e.Verb is "delete" or "cut" or "copy" or "properties" or "rename");
        Assert.False(menu.Entries[0].IsSeparator);
        Assert.False(menu.Entries[^1].IsSeparator);
    }

    [Fact]
    public async Task BackgroundMenu_builds_for_a_folder()
    {
        using var menu = await _service.GetBackgroundMenuAsync(_folder, extended: false);
        Assert.NotNull(menu);
        Dump(menu!.Entries, "");
    }

    [Fact]
    public async Task OpenWith_lists_apps_for_text_files()
    {
        var apps = await _service.GetOpenWithAppsAsync(Path.Combine(_folder, "x.txt"));
        foreach (var app in apps) output.WriteLine($"{app.Index}: {app.Name} icon={(app.Icon is null ? app.IconImagePath ?? "none" : "bitmap")}");
        Assert.NotEmpty(apps);
    }

    [Fact]
    public async Task Editors_are_detected()
    {
        var editors = await _service.GetCodeEditorsAsync();
        foreach (var editor in editors) output.WriteLine($"{editor.Id}: {editor.ExecutablePath} icon={editor.Icon is not null}");
    }

    private void Dump(IReadOnlyList<ShellMenuEntry> entries, string indent)
    {
        foreach (var entry in entries)
        {
            output.WriteLine(entry.IsSeparator ? indent + "----" : $"{indent}{entry.Text} [{entry.Verb}] icon={entry.Icon is not null} enabled={entry.IsEnabled}");
            if (entry.Children is { } children) Dump(children, indent + "    ");
        }
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }
}
