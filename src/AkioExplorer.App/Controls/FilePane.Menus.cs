using System.Runtime.InteropServices.WindowsRuntime;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.Core;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Right-click menus, OneCommander style: the app's own commands plus the real Windows shell items
/// (7-Zip, Git, VLC, "Send to"…), an "Open with" list with app icons and the installed code editors.
/// </summary>
public sealed partial class FilePane
{
    /// <summary>How long the menu waits for shell items before opening without them (they are added when they arrive).</summary>
    private static readonly TimeSpan ShellMenuWait = TimeSpan.FromMilliseconds(180);

    /// <summary>Runtime items and the list they were put in, removed before the next menu opens.</summary>
    private readonly List<(IList<MenuFlyoutItemBase> Host, MenuFlyoutItemBase Item)> _shellItems = new();
    private readonly Dictionary<IconBitmap, ImageSource> _menuIcons = new(ReferenceEqualityComparer.Instance);
    private int _menuVersion;

    private async Task ShowContextMenuAsync(ListViewBase list, FrameworkElement anchor, FlyoutShowOptions? options, bool background)
    {
        int version = ++_menuVersion;
        RemoveShellItems();

        MenuFlyout menu;
        if (background)
        {
            ConfigureBackgroundMenu();
            menu = BackgroundMenu;
        }
        else
        {
            ConfigureItemMenu();
            menu = ItemMenu;
        }

        bool extended = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        var shellTask = ViewModel.GetShellMenuAsync(background, extended);
        var editorTask = ViewModel.GetPreferredEditorAsync();
        Task openWithTask = background ? Task.CompletedTask : FillOpenWithAsync(version);
        var archiverTask = background ? Task.FromResult<IReadOnlyList<ArchiverApp>>([]) : ViewModel.GetArchiversAsync();

        // Short wait so the menu usually opens complete (no jump); slow shell extensions are added once ready.
        await Task.WhenAny(Task.WhenAll(shellTask, editorTask, openWithTask, archiverTask), Task.Delay(ShellMenuWait));
        if (version != _menuVersion) return;

        if (editorTask.IsCompletedSuccessfully) ApplyEditor(editorTask.Result);
        if (archiverTask.IsCompletedSuccessfully) AddArchiverItems(archiverTask.Result);
        if (shellTask.IsCompletedSuccessfully) AddShellItems(shellTask.Result, background, version);

        if (options is not null) menu.ShowAt(list, options);
        else menu.ShowAt(anchor);

        if (!editorTask.IsCompleted) ApplyEditor(await editorTask);
        if (!archiverTask.IsCompleted)
        {
            IReadOnlyList<ArchiverApp> apps = [];
            try { apps = await archiverTask; } catch { }
            if (version == _menuVersion) AddArchiverItems(apps);
        }
        if (!shellTask.IsCompleted)
        {
            IShellMenu? shell = null;
            try { shell = await shellTask; } catch { }
            if (version == _menuVersion) AddShellItems(shell, background, version);
            else shell?.Dispose();
        }
    }

    private void ApplyEditor(CodeEditor? editor)
    {
        string text = editor is null ? "Open in code editor" : $"Open in {editor.Name}";
        MenuOpenEditor.Text = text;
        MenuBgEditor.Text = text;
        if (editor?.Icon is { } icon)
        {
            MenuOpenEditor.Icon = ToIcon(icon);
            MenuBgEditor.Icon = ToIcon(icon);
        }
        if (editor is null)
        {
            // Nothing installed: hide rather than offer a command that can only fail.
            MenuOpenEditor.Visibility = Visibility.Collapsed;
            MenuBgEditor.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ installed archivers

    /// <summary>
    /// Archive ▸ WinRAR ▸ / 7-Zip ▸ …: compress to the app's formats and extract any archive type it opens. One submenu
    /// per installed app, with its icon, so "Extract here" never leaves doubt about which program runs.
    /// </summary>
    private void AddArchiverItems(IReadOnlyList<ArchiverApp> apps)
    {
        if (apps.Count == 0) return;
        bool compress = ViewModel.CanCompressWithArchiver;
        var archives = ViewModel.SelectedArchives;
        if (!compress && archives.Count == 0) return;

        var host = MenuArchive.Items;
        if (host.Any(i => i.Visibility == Visibility.Visible)) Track(host, new MenuFlyoutSeparator());
        foreach (var app in apps)
        {
            var sub = new MenuFlyoutSubItem { Text = app.Name, Icon = ToIcon(app.Icon) ?? new FontIcon { Glyph = "" } };
            if (compress)
            {
                foreach (var format in ArchiverCommands.CompressFormats(app.Kind))
                {
                    var item = new MenuFlyoutItem { Text = $"Add to \"{ViewModel.ArchiveNameFor(format)}\"", Icon = new FontIcon { Glyph = "" } };
                    string chosen = format;
                    item.Click += (_, _) => ViewModel.CompressWith(app, chosen);
                    sub.Items.Add(item);
                }
                if (ArchiverCommands.HasAddDialog(app.Kind))
                {
                    var dialog = new MenuFlyoutItem { Text = "Add to archive…", Icon = new FontIcon { Glyph = "" } };
                    dialog.Click += (_, _) => ViewModel.CompressWith(app, format: null);
                    sub.Items.Add(dialog);
                }
            }
            if (archives.Count > 0)
            {
                if (sub.Items.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
                var here = new MenuFlyoutItem { Text = "Extract here", Icon = new FontIcon { Glyph = "" } };
                here.Click += (_, _) => ViewModel.ExtractWith(app, toFolder: false);
                var folder = new MenuFlyoutItem
                {
                    Text = archives.Count == 1 ? $"Extract to \"{ArchiverCommands.ExtractFolderName(archives[0].Path)}\\\"" : "Extract each to its own folder",
                    Icon = new FontIcon { Glyph = "" },
                };
                folder.Click += (_, _) => ViewModel.ExtractWith(app, toFolder: true);
                sub.Items.Add(here);
                sub.Items.Add(folder);
            }
            Track(host, sub);
        }
        MenuArchive.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ open with

    private async Task FillOpenWithAsync(int version)
    {
        var items = MenuOpenWith.Items;
        items.Clear();
        if (MenuOpenWith.Visibility != Visibility.Visible) return;

        var selected = ViewModel.SelectedItems;
        var files = selected.Where(s => !s.IsContainer).ToList();
        bool folders = selected.Any(s => s.IsContainer);
        bool oneType = files.Count > 0 && !folders && files.Select(f => System.IO.Path.GetExtension(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;

        var appsTask = oneType ? ViewModel.ShellMenus.GetOpenWithAppsAsync(files[0].Path) : Task.FromResult<IReadOnlyList<OpenWithApp>>([]);
        var editorsTask = ViewModel.ShellMenus.GetCodeEditorsAsync();
        IReadOnlyList<OpenWithApp> apps;
        IReadOnlyList<CodeEditor> editors;
        try
        {
            apps = await appsTask;
            editors = await editorsTask;
        }
        catch
        {
            apps = [];
            editors = [];
        }
        if (version != _menuVersion) return;

        foreach (var app in apps)
        {
            var item = new MenuFlyoutItem { Text = app.Name, Icon = ToIcon(app.Icon, app.IconImagePath) };
            var captured = app;
            item.Click += async (_, _) => await ViewModel.OpenWithAppAsync(captured);
            items.Add(item);
        }

        if (editors.Count > 0)
        {
            if (items.Count > 0) items.Add(new MenuFlyoutSeparator());
            foreach (var editor in editors)
            {
                var item = new MenuFlyoutItem { Text = editor.Name, Icon = ToIcon(editor.Icon) };
                var captured = editor;
                item.Click += (_, _) => ViewModel.OpenInEditor(captured);
                items.Add(item);
            }
        }

        if (folders && selected.Count == 1)
        {
            if (items.Count > 0) items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = "Terminal", Icon = new FontIcon { Glyph = "" }, Command = ViewModel.OpenTerminalCommand });
            items.Add(new MenuFlyoutItem { Text = "File Explorer", Icon = new FontIcon { Glyph = "" }, Command = ViewModel.RevealInExplorerCommand });
        }

        if (files.Count == 1 && !folders)
        {
            if (items.Count > 0) items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = "Choose another app…", Command = ViewModel.OpenWithCommand });
        }

        if (items.Count == 0) MenuOpenWith.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ shell items

    private void AddShellItems(IShellMenu? shell, bool background, int version)
    {
        if (version != _menuVersion)
        {
            shell?.Dispose();
            return;
        }
        RemoveShellItems();
        if (shell is null || shell.Entries.Count == 0) return;

        var moreMenu = background ? MenuBgShellMore : MenuShellMore;
        var more = moreMenu.Items;
        foreach (var entry in shell.Entries)
        {
            if (ViewModel.IsShellEntryHidden(entry) || DuplicatesOwnCommand(entry)) continue;

            if (entry.Children is { } children)
            {
                // The shell's "New" (Word document, shortcut…) merges into the app's own New menu.
                if (background && IsCaption(entry, "New"))
                {
                    MergeNewItems(shell, children);
                    continue;
                }
                // Windows "Send to" targets join the app's Send to (other pane first).
                if (!background && IsCaption(entry, "Send to"))
                {
                    AddSendToItems(shell, children);
                    continue;
                }
            }

            if (entry.IsSeparator && (more.Count == 0 || more[^1] is MenuFlyoutSeparator)) continue;
            Track(more, ToMenuItem(shell, entry));
        }
        while (more.Count > 0 && more[^1] is MenuFlyoutSeparator separator)
        {
            more.RemoveAt(more.Count - 1);
            _shellItems.RemoveAll(t => t.Item == separator);
        }
        moreMenu.Visibility = Show(more.Count > 0);
    }

    private static bool IsCaption(ShellMenuEntry entry, string caption) => entry.Text.Equals(caption, StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows items the app already shows with its own (faster) command.</summary>
    private bool DuplicatesOwnCommand(ShellMenuEntry entry) =>
        IsCaption(entry, "Open in Terminal")
        || IsCaption(entry, "Open with Code") && MenuOpenEditor.Text.Contains("VS Code", StringComparison.Ordinal);

    private void Track(IList<MenuFlyoutItemBase> host, MenuFlyoutItemBase item, int? index = null)
    {
        if (index is { } at) host.Insert(at, item);
        else host.Add(item);
        _shellItems.Add((host, item));
    }

    private void AddSendToItems(IShellMenu shell, IReadOnlyList<ShellMenuEntry> children)
    {
        var items = MenuSendTo.Items;
        bool any = false;
        foreach (var child in children)
        {
            if (child.IsSeparator || ViewModel.IsShellEntryHidden(child)) continue;
            Track(items, ToMenuItem(shell, child));
            any = true;
        }
        if (!any) return;
        MenuSendToSeparator.Visibility = Show(MenuCopyOther.Visibility == Visibility.Visible);
        MenuSendTo.Visibility = Visibility.Visible;
    }

    private void MergeNewItems(IShellMenu shell, IReadOnlyList<ShellMenuEntry> children)
    {
        var items = MenuNew.Items;
        int index = items.IndexOf(MenuNewTabSeparator);
        // The app already has Folder and Text document (with inline rename).
        var extra = children.Where(c => !c.IsSeparator && c.Children is null
                                        && !string.Equals(c.Verb, "NewFolder", StringComparison.OrdinalIgnoreCase)
                                        && !string.Equals(c.Verb, ".txt", StringComparison.OrdinalIgnoreCase)).ToList();
        if (extra.Count == 0) return;

        Track(items, new MenuFlyoutSeparator(), index++);
        foreach (var entry in extra) Track(items, ToMenuItem(shell, entry), index++);
    }

    private void RemoveShellItems()
    {
        foreach (var (host, item) in _shellItems) host.Remove(item);
        _shellItems.Clear();
        MenuShellMore.Visibility = Visibility.Collapsed;
        MenuBgShellMore.Visibility = Visibility.Collapsed;
        // The menu object itself is released by the service when the next one is built (an item may still be running).
    }

    private MenuFlyoutItemBase ToMenuItem(IShellMenu shell, ShellMenuEntry entry)
    {
        if (entry.IsSeparator) return new MenuFlyoutSeparator();

        if (entry.Children is { } children)
        {
            var sub = new MenuFlyoutSubItem { Text = entry.Text, Icon = ToIcon(entry.Icon), IsEnabled = entry.IsEnabled };
            foreach (var child in children)
            {
                if (!ViewModel.IsShellEntryHidden(child)) sub.Items.Add(ToMenuItem(shell, child));
            }
            return sub;
        }

        var item = new MenuFlyoutItem { Text = entry.Text, Icon = ToIcon(entry.Icon), IsEnabled = entry.IsEnabled };
        item.Click += async (_, _) =>
        {
            try
            {
                await shell.InvokeAsync(entry);
            }
            catch (Exception ex)
            {
                ViewModel.ShowError($"\"{entry.Text}\" failed: {ex.Message}");
            }
        };
        return item;
    }

    private IconElement? ToIcon(IconBitmap? bitmap, string? imagePath = null)
    {
        if (bitmap is not null)
        {
            if (!_menuIcons.TryGetValue(bitmap, out var source))
            {
                var writeable = new WriteableBitmap(bitmap.Width, bitmap.Height);
                using (var stream = writeable.PixelBuffer.AsStream())
                {
                    stream.Write(bitmap.Pixels, 0, bitmap.Pixels.Length);
                }
                writeable.Invalidate();
                source = writeable;
                _menuIcons[bitmap] = source;
            }
            return new ImageIcon { Source = source, Width = 16, Height = 16 };
        }
        if (imagePath is not null)
            return new ImageIcon { Source = new BitmapImage(new Uri(imagePath)) { DecodePixelWidth = 32 }, Width = 16, Height = 16 };
        return null;
    }
}
