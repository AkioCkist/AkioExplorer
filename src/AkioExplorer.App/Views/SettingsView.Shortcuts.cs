using AkioExplorer.App.Controls;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace AkioExplorer.App.Views;

/// <summary>
/// Settings › Keyboard shortcuts: one row per command with its keys. Click the keys to record new ones (replace or
/// add), the arrow button puts the defaults back. A key taken from another command is moved, never shared.
/// </summary>
public sealed partial class SettingsView
{
    private readonly Dictionary<string, (Button Keys, Button Reset)> _shortcutRows = new(StringComparer.OrdinalIgnoreCase);
    private ShortcutService Shortcuts => App.Services.GetRequiredService<ShortcutService>();

    private void BuildShortcutRows()
    {
        foreach (var command in ShortcutCatalog.All)
        {
            var keys = new Button { MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center, Tag = command };
            keys.Click += OnEditShortcut;
            var reset = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Tag = command,
                Padding = new Thickness(8, 6, 8, 6),
            };
            ToolTipService.SetToolTip(reset, "Back to the default keys");
            AutomationProperties.SetName(reset, $"Reset {command.Title} to its default keys");
            reset.Click += (_, _) => Shortcuts.Reset(command.Id);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(reset);
            actions.Children.Add(keys);

            SectionShortcuts.Children.Add(new SettingRow
            {
                Header = command.Title,
                Description = $"{command.Category} · default {string.Join(", ", command.Defaults)}",
                Action = actions,
            });
            _shortcutRows[command.Id] = (keys, reset);
        }
        RefreshShortcutRows();
        Shortcuts.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshShortcutRows);
    }

    private void RefreshShortcutRows()
    {
        foreach (var command in ShortcutCatalog.All)
        {
            if (!_shortcutRows.TryGetValue(command.Id, out var row)) continue;
            var gestures = Shortcuts.Gestures(command.Id);
            string text = gestures.Count == 0 ? "None" : string.Join("  ·  ", gestures);
            row.Keys.Content = text;
            AutomationProperties.SetName(row.Keys, $"{command.Title}: {text}. Change");
            row.Reset.Visibility = Shortcuts.IsCustomized(command.Id) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnResetAllShortcuts(object sender, RoutedEventArgs e) => Shortcuts.ResetAll();

    private async void OnEditShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutCommand command }) return;
        var current = Shortcuts.Gestures(command.Id);
        HotkeyGesture? recorded = null;

        var recorder = new TextBox
        {
            IsReadOnly = true,
            PlaceholderText = "Press the keys…",
            FontSize = 18,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 8),
        };
        AutomationProperties.SetName(recorder, "Press the new keys");
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var remove = new HyperlinkButton { Content = "Remove all keys from this command", Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) };

        var panel = new StackPanel { Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = current.Count == 0 ? "No keys yet." : "Now: " + string.Join(", ", current),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(recorder);
        panel.Children.Add(note);
        panel.Children.Add(remove);

        var dialog = new ContentDialog
        {
            Title = command.Title,
            Content = panel,
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
        };

        bool removeAll = false;
        remove.Click += (_, _) =>
        {
            removeAll = true;
            dialog.Hide();
        };

        recorder.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) return; // closes the dialog
            args.Handled = true; // Tab, Alt+…, F-keys: record them instead of moving focus or running commands
            int key = (int)args.Key;
            if (HotkeyGesture.IsModifierKey(key)) return;

            var gesture = new HotkeyGesture(CurrentModifiers(), key);
            recorder.Text = gesture.ToString();
            bool valid = gesture.IsValidShortcut;
            recorded = valid ? gesture : null;
            dialog.IsPrimaryButtonEnabled = valid;
            dialog.IsSecondaryButtonEnabled = valid && !current.Contains(gesture);
            note.Text = !valid
                ? "Letters, digits and other keys need Ctrl, Alt or Win (F1–F12 work alone), so typing never runs a command."
                : Shortcuts.UsedBy(gesture, command.Id) is { } other
                    ? $"{gesture} is used by {other.Title}. Saving moves it here."
                    : current.Contains(gesture) ? $"{gesture} already runs this command." : string.Empty;
        };
        dialog.Opened += (_, _) => recorder.Focus(FocusState.Programmatic);

        var result = await App.Services.GetRequiredService<DialogService>().ShowAsync(dialog);
        if (removeAll) Shortcuts.Assign(command.Id, []);
        else if (recorded is { } chosen && result == ContentDialogResult.Primary) Shortcuts.Assign(command.Id, [chosen]);
        else if (recorded is { } extra && result == ContentDialogResult.Secondary) Shortcuts.Assign(command.Id, [.. current, extra]);
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

        var modifiers = HotkeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (Down(VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }
}
