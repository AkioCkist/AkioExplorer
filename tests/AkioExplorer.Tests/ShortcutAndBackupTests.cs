using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Settings;

namespace AkioExplorer.Tests;

public sealed class ShortcutCatalogTests
{
    [Theory]
    [InlineData("F5", true)]
    [InlineData("Shift+F3", true)]
    [InlineData("Ctrl+Shift+Tab", true)]
    [InlineData("Alt+Left", true)]
    [InlineData("Ctrl+NumPlus", true)]
    [InlineData("Alt+Enter", true)]
    [InlineData("T", false)]          // would fire while typing a file name
    [InlineData("Shift+T", false)]
    [InlineData("Del", false)]
    [InlineData("Ctrl", false)]
    [InlineData("Ctrl+Q+W", false)]
    public void Shortcut_parsing_allows_bare_function_keys_only(string text, bool valid)
    {
        Assert.Equal(valid, HotkeyGesture.TryParseShortcut(text, out var g));
        if (valid) Assert.Equal(text, g.ToString());
    }

    [Fact]
    public void Global_hotkey_still_needs_a_modifier()
    {
        Assert.False(HotkeyGesture.TryParse("F5", out _));
        Assert.True(HotkeyGesture.TryParse("Alt+Space", out _));
    }

    [Fact]
    public void Every_default_is_valid_and_no_two_commands_share_a_key()
    {
        var all = ShortcutCatalog.All.SelectMany(c => c.DefaultGestures.Select(g => (c.Id, g))).ToList();
        Assert.All(all, x => Assert.True(x.g.IsValidShortcut));
        Assert.Equal(all.Count, all.Select(x => x.g).Distinct().Count());
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Assigning_a_used_key_moves_it_and_defaults_are_forgotten()
    {
        var overrides = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        HotkeyGesture.TryParseShortcut("Ctrl+T", out var ctrlT);

        ShortcutCatalog.Assign(overrides, "NewFolder", [ctrlT]);
        Assert.Equal([ctrlT], ShortcutCatalog.Gestures("NewFolder", overrides));
        Assert.Empty(ShortcutCatalog.Gestures("NewTab", overrides)); // taken away, not shared
        Assert.Equal("", ShortcutCatalog.Display("NewTab", overrides));

        // Giving New tab its own default back forgets its override and takes the key from New folder.
        ShortcutCatalog.Assign(overrides, "NewTab", [ctrlT]);
        Assert.False(ShortcutCatalog.IsCustomized("NewTab", overrides));
        Assert.Empty(ShortcutCatalog.Gestures("NewFolder", overrides));

        ShortcutCatalog.Reset(overrides, "NewFolder");
        Assert.Equal("Ctrl+Shift+N", ShortcutCatalog.Display("NewFolder", overrides));
        Assert.Empty(overrides);
    }

    [Fact]
    public void Default_text_maps_to_the_current_key()
    {
        Assert.Equal("NewTab", ShortcutCatalog.ByDefaultText("Ctrl+T")?.Id);
        Assert.Equal("Refresh", ShortcutCatalog.ByDefaultText("F5")?.Id);
        Assert.Null(ShortcutCatalog.ByDefaultText("F2")); // rename lives in the file list, not in the catalog
        Assert.Null(ShortcutCatalog.ByDefaultText("Ctrl+R")); // second default, not shown in menus
    }
}

public sealed class SettingsBackupTests
{
    private static AppSettings Customized()
    {
        var s = new AppSettings
        {
            Theme = AppTheme.Dark,
            ShowHiddenItems = true,
            Terminal = TerminalApp.CommandPrompt,
            QuickSearchHotkey = "Ctrl+Alt+K",
            Favorites = [new FavoriteEntry { Name = "Work", ProviderId = "local", Path = @"D:\Work" }],
            RecentSearches = ["secret query"],
            Window = new WindowPlacement { X = 10, Y = 20, Width = 999 },
        };
        s.Shortcuts["NewTab"] = ["Ctrl+Shift+Y"];
        s.CloudAccounts.Add(new Core.Cloud.CloudAccount());
        return s;
    }

    [Fact]
    public void Export_then_import_brings_preferences_but_not_machine_state()
    {
        string json = SettingsBackup.Export(Customized());
        Assert.DoesNotContain("secret query", json);
        Assert.DoesNotContain(nameof(AppSettings.CloudAccounts), json);
        Assert.DoesNotContain(nameof(AppSettings.Window), json);

        var target = new AppSettings { RecentSearches = ["mine"], Window = new WindowPlacement { Width = 1500 } };
        SettingsBackup.Apply(SettingsBackup.Parse(json), target);

        Assert.Equal(AppTheme.Dark, target.Theme);
        Assert.True(target.ShowHiddenItems);
        Assert.Equal(TerminalApp.CommandPrompt, target.Terminal);
        Assert.Equal("Ctrl+Alt+K", target.QuickSearchHotkey);
        Assert.Equal(["Ctrl+Shift+Y"], target.Shortcuts["newtab"]); // case-insensitive after import
        Assert.Equal(@"D:\Work", Assert.Single(target.Favorites).Path);
        Assert.Equal(["mine"], target.RecentSearches);
        Assert.Equal(1500, target.Window.Width);
    }

    [Fact]
    public void A_plain_copy_of_settings_json_imports_too()
    {
        string raw = System.Text.Json.JsonSerializer.Serialize(new { Theme = "Light", ShowFileExtensions = false });
        var parsed = SettingsBackup.Parse(raw);
        Assert.Equal(AppTheme.Light, parsed.Theme);
        Assert.False(parsed.ShowFileExtensions);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"name\":\"something else\"}")]
    [InlineData("{\"Theme\":\"Purple\"}")]
    public void Other_files_are_refused_with_a_reason(string json) =>
        Assert.Throws<FormatException>(() => SettingsBackup.Parse(json));

    [Fact]
    public void Reset_restores_defaults_and_keeps_favorites_and_machine_state()
    {
        var s = Customized();
        SettingsBackup.ResetToDefaults(s);

        var defaults = new AppSettings();
        Assert.Equal(defaults.Theme, s.Theme);
        Assert.Equal(defaults.ShowHiddenItems, s.ShowHiddenItems);
        Assert.Equal(defaults.QuickSearchHotkey, s.QuickSearchHotkey);
        Assert.Empty(s.Shortcuts);
        Assert.Single(s.Favorites);
        Assert.Single(s.CloudAccounts);
        Assert.Equal(["secret query"], s.RecentSearches);
        Assert.Equal(999, s.Window.Width);
    }
}
