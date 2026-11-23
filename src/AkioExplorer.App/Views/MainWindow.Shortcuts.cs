using AkioExplorer.App.Services;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace AkioExplorer.App.Views;

/// <summary>
/// App-wide keyboard shortcuts, built from <see cref="ShortcutCatalog"/> and the user's changes
/// (Settings › Keyboard shortcuts), and rebuilt when those change.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<KeyboardAccelerator> _shortcutAccelerators = [];
    private ShortcutService? _shortcuts;

    private Dictionary<string, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs>> ShortcutHandlers() => new()
    {
        ["Back"] = OnBackAccelerator,
        ["Forward"] = OnForwardAccelerator,
        ["Up"] = OnUpAccelerator,
        ["Refresh"] = OnRefreshAccelerator,
        ["Address"] = OnAddressAccelerator,
        ["FocusSidebar"] = OnFocusSidebarAccelerator,
        ["SwitchPane"] = OnSwitchPaneAccelerator,
        ["Filter"] = OnFilterAccelerator,
        ["AdvancedSearch"] = OnAdvancedSearchAccelerator,
        ["Palette"] = OnPaletteAccelerator,
        ["NewTab"] = OnNewTabAccelerator,
        ["CloseTab"] = OnCloseTabAccelerator,
        ["ReopenTab"] = OnReopenTabAccelerator,
        ["NextTab"] = OnNextTabAccelerator,
        ["PreviousTab"] = OnPreviousTabAccelerator,
        ["Copy"] = OnCopyAccelerator,
        ["Cut"] = OnCutAccelerator,
        ["Paste"] = OnPasteAccelerator,
        ["Undo"] = OnUndoAccelerator,
        ["CopyPath"] = OnCopyPathAccelerator,
        ["NewFolder"] = OnNewFolderAccelerator,
        ["NewFile"] = OnNewFileAccelerator,
        ["BatchRename"] = OnBatchRenameAccelerator,
        ["Properties"] = OnPropertiesAccelerator,
        ["SelectNone"] = OnSelectNoneAccelerator,
        ["InvertSelection"] = OnInvertSelectionAccelerator,
        ["DetailsView"] = OnDetailsAccelerator,
        ["GridView"] = OnGridAccelerator,
        ["DualPane"] = OnDualPaneAccelerator,
        ["Preview"] = OnPreviewAccelerator,
        ["HiddenItems"] = OnHiddenItemsAccelerator,
        ["FolderSizes"] = OnFolderSizesAccelerator,
        ["Terminal"] = OnTerminalAccelerator,
        ["Editor"] = OnEditorAccelerator,
    };

    private void InitShortcuts()
    {
        _shortcuts = App.Services.GetRequiredService<ShortcutService>();
        _shortcuts.Changed += (_, _) => DispatcherQueue.TryEnqueue(ApplyShortcuts);
        ApplyShortcuts();
    }

    private void ApplyShortcuts()
    {
        if (_shortcuts is null) return;
        foreach (var accelerator in _shortcutAccelerators) RootGrid.KeyboardAccelerators.Remove(accelerator);
        _shortcutAccelerators.Clear();

        foreach (var (id, handler) in ShortcutHandlers())
        {
            foreach (var gesture in _shortcuts.Gestures(id))
            {
                var accelerator = new KeyboardAccelerator { Key = (VirtualKey)gesture.Key, Modifiers = ToVirtualKeyModifiers(gesture.Modifiers) };
                accelerator.Invoked += handler;
                RootGrid.KeyboardAccelerators.Add(accelerator);
                _shortcutAccelerators.Add(accelerator);
            }
        }
    }

    private static VirtualKeyModifiers ToVirtualKeyModifiers(HotkeyModifiers modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if ((modifiers & HotkeyModifiers.Ctrl) != 0) result |= VirtualKeyModifiers.Control;
        if ((modifiers & HotkeyModifiers.Alt) != 0) result |= VirtualKeyModifiers.Menu;
        if ((modifiers & HotkeyModifiers.Shift) != 0) result |= VirtualKeyModifiers.Shift;
        if ((modifiers & HotkeyModifiers.Win) != 0) result |= VirtualKeyModifiers.Windows;
        return result;
    }

    /// <summary>The key shown next to a command (palette, tooltips): today's key for a shortcut written with its default.</summary>
    private string? CurrentKeys(string? defaultText) => _shortcuts?.Current(defaultText) ?? defaultText;
}
