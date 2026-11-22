using AkioExplorer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AkioExplorer.App.Views;

/// <summary>One entry of the command palette. <see cref="CanRun"/> is checked when the palette opens.</summary>
public sealed record PaletteCommand(string Title, string Category, string? Shortcut, string Glyph, Func<bool> CanRun, Action Run)
{
    /// <summary>Extra words that should find this command ("zip" → Compress).</summary>
    public string Keywords { get; init; } = string.Empty;
}

/// <summary>
/// Ctrl+Shift+P: every command, searchable by name, with its shortcut shown (recognition over recall, and it teaches
/// the shortcuts). Arrow keys move, Enter runs, Esc closes.
/// </summary>
public static class CommandPalette
{
    public static async Task ShowAsync(DialogService dialogs, IReadOnlyList<PaletteCommand> commands)
    {
        var available = commands.Where(c => SafeCanRun(c)).ToList();

        var search = new TextBox { PlaceholderText = "Type a command, e.g. \"rename\", \"zip\", \"tab\", \"hidden\"" };
        AutomationProperties.SetName(search, "Search commands");
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Height = 380,
            Margin = new Thickness(-12, 8, -12, 0),
        };
        AutomationProperties.SetName(list, "Commands");

        var panel = new StackPanel { Width = 560 };
        panel.Children.Add(search);
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;

        PaletteCommand? chosen = null;

        void Fill()
        {
            var matches = Filter(available, search.Text);
            list.Items.Clear();
            foreach (var c in matches) list.Items.Add(Row(c));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        void Choose(object? row)
        {
            if (row is FrameworkElement { Tag: PaletteCommand c })
            {
                chosen = c;
                dialog.Hide();
            }
        }

        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    if (list.Items.Count > 0) list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case VirtualKey.Up:
                    if (list.Items.Count > 0) list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0);
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case VirtualKey.Enter:
                    Choose(list.SelectedItem);
                    e.Handled = true;
                    break;
            }
        };
        list.ItemClick += (_, e) => Choose(e.ClickedItem);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Choose(list.SelectedItem);
                e.Handled = true;
            }
        };
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);

        Fill();
        await dialogs.ShowAsync(dialog);

        // Run after the dialog is gone: the command may open its own dialog.
        chosen?.Run();
    }

    private static bool SafeCanRun(PaletteCommand c)
    {
        try
        {
            return c.CanRun();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every word must appear in the title, category or keywords; title-prefix matches rank first.</summary>
    internal static IEnumerable<PaletteCommand> Filter(IReadOnlyList<PaletteCommand> commands, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return commands;
        return commands
            .Select((c, index) => (c, index, hay: $"{c.Title} {c.Category} {c.Keywords} {c.Shortcut}"))
            .Where(x => words.All(w => x.hay.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(x => x.c.Title.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(x => x.c.Title.Contains(words[0], StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(x => x.index)
            .Select(x => x.c);
    }

    private static FrameworkElement Row(PaletteCommand c)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = c };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = c.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = c.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = c.Category,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);

        if (!string.IsNullOrEmpty(c.Shortcut))
        {
            var keys = new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = c.Shortcut, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
            };
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);
        }
        AutomationProperties.SetName(grid, string.IsNullOrEmpty(c.Shortcut) ? c.Title : $"{c.Title}, {c.Shortcut}");
        return grid;
    }
}
