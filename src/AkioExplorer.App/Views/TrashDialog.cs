using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AkioExplorer.App.Views;

/// <summary>
/// What's in a trash (Recycle Bin, MEGA Rubbish bin, Drive Trash), with Restore, Delete permanently and Empty.
/// The user sees what will be lost before anything is deleted for good; irreversible actions confirm in a flyout
/// (a second ContentDialog can't open over this one).
/// </summary>
public static class TrashDialog
{
    private const int MaxRows = 1000; // rows are real elements (not virtualized); Empty still covers everything

    /// <summary>Returns true when something was restored or deleted (the caller refreshes).</summary>
    public static async Task<bool> ShowAsync(DialogService dialogs, ITrashProvider trash, string accountName)
    {
        bool changed = false;
        bool busy = false;
        IReadOnlyList<TrashItem> items = [];
        var cts = new CancellationTokenSource();

        var restore = ToolButton("", "Restore", "Put the selected items back where they were");
        var delete = ToolButton("", "Delete permanently", "Delete the selected items for good (Del)");
        var empty = ToolButton("", "Delete all permanently", $"Empty the {trash.TrashName}: delete every item in it for good, no selection needed");
        var refresh = ToolButton("", "Refresh", "Load the list again (F5)", labelVisible: false);

        var toolbar = new Grid { ColumnSpacing = 4 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(toolbar, restore, 0);
        Place(toolbar, delete, 1);
        Place(toolbar, empty, 3);
        Place(toolbar, refresh, 4);

        var summary = new TextBlock { Style = Style("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
        var header = Row(new FontIcon { Glyph = "", FontSize = 16 }, "Name", "Original location", "Date deleted", "Size", header: true);
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            Height = 380,
            Margin = new Thickness(-12, 0, -12, 0),
        };
        AutomationProperties.SetName(list, $"Items in the {trash.TrashName}");
        var placeholder = new TextBlock
        {
            Text = "Loading…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Style = Style("BodyTextBlockStyle"), Opacity = 0.7, IsHitTestVisible = false,
        };
        var listHost = new Grid();
        listHost.Children.Add(list);
        listHost.Children.Add(placeholder);

        var panel = new StackPanel { Spacing = 8, Width = 820 };
        panel.Children.Add(toolbar);
        panel.Children.Add(progress);
        panel.Children.Add(error);
        panel.Children.Add(header);
        panel.Children.Add(listHost);
        panel.Children.Add(summary);

        var dialog = new ContentDialog
        {
            Title = $"{trash.TrashName} · {accountName}",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        List<TrashItem> Selected() => list.SelectedItems.OfType<FrameworkElement>().Select(e => (TrashItem)e.Tag).ToList();

        void UpdateButtons()
        {
            int selected = list.SelectedItems.Count;
            restore.IsEnabled = !busy && selected > 0;
            delete.IsEnabled = !busy && selected > 0;
            empty.IsEnabled = !busy && items.Count > 0;
            refresh.IsEnabled = !busy;
        }

        void Show(IReadOnlyList<TrashItem> loaded)
        {
            items = loaded.OrderByDescending(i => i.Deleted ?? i.Modified ?? DateTimeOffset.MinValue).ToList();
            bool hasDeletedDates = items.Any(i => i.Deleted is not null);
            ((TextBlock)header.Children[3]).Text = hasDeletedDates || items.Count == 0 ? "Date deleted" : "Date modified";
            list.Items.Clear();
            foreach (var item in items.Take(MaxRows))
            {
                DateTimeOffset? when = item.Deleted ?? item.Modified;
                var row = Row(new FontIcon { Glyph = item.IsFolder ? "" : "", FontSize = 16 },
                    item.Name,
                    item.OriginalLocation ?? $"Unknown · restores to {trash.RestoreFallbackName}",
                    when is { } w ? Formatting.FormatDate(w) : "—",
                    item.Size is { } size && (size > 0 || !item.IsFolder) ? Formatting.FormatSize(size) : "—");
                row.Tag = item;
                AutomationProperties.SetName(row, item.Name);
                ToolTipService.SetToolTip(row, item.OriginalLocation is { } from ? $"{item.Name}\nFrom {from}" : item.Name);
                list.Items.Add(row);
            }
            placeholder.Text = $"The {trash.TrashName} is empty.";
            placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            long total = items.Sum(i => i.Size ?? 0);
            string count = items.Count == 1 ? "1 item" : $"{items.Count:N0} items";
            summary.Text = items.Count == 0
                ? $"Nothing to restore. Items you delete in {accountName} wait here until the {trash.TrashName} is emptied."
                : $"{count} · {Formatting.FormatSize(total)}" +
                  (items.Count > MaxRows ? $" · showing the newest {MaxRows:N0}" : string.Empty) +
                  ". Select items to restore them or delete them for good; Ctrl+A selects all.";
            UpdateButtons();
        }

        async Task RunAsync(Func<CancellationToken, Task> work, bool reload = true)
        {
            if (busy) return;
            busy = true;
            error.IsOpen = false;
            progress.Visibility = Visibility.Visible;
            UpdateButtons();
            try
            {
                await work(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (StorageException ex)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
            }
            catch (Exception ex)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
            }
            finally
            {
                busy = false;
                progress.Visibility = Visibility.Collapsed;
                UpdateButtons();
            }
            if (reload) await LoadAsync();
        }

        async Task LoadAsync() => await RunAsync(async ct => Show(await trash.ListTrashAsync(ct)), reload: false);

        async Task RestoreAsync()
        {
            var chosen = Selected();
            if (chosen.Count == 0) return;
            changed = true;
            await RunAsync(ct => trash.RestoreAsync(chosen, ct));
        }

        // Irreversible actions: the flyout names what goes and needs a second click.
        Flyout Confirm(string message, string action, Func<Task> run)
        {
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
            var go = new Button { Content = action, Style = Style("AccentButtonStyle"), HorizontalAlignment = HorizontalAlignment.Right };
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(text);
            body.Children.Add(go);
            var flyout = new Flyout { Content = body };
            go.Click += async (_, _) =>
            {
                flyout.Hide();
                await run();
            };
            flyout.Opened += (_, _) => go.Focus(FocusState.Programmatic);
            return flyout;
        }

        void AskDelete()
        {
            var chosen = Selected();
            if (chosen.Count == 0 || busy) return;
            string what = chosen.Count == 1 ? $"\"{chosen[0].Name}\"" : $"{chosen.Count:N0} items";
            Confirm($"Delete {what} for good? This can't be undone.", "Delete", async () =>
            {
                changed = true;
                await RunAsync(ct => trash.DeleteFromTrashAsync(chosen, ct));
            }).ShowAt(delete);
        }

        void AskEmpty()
        {
            if (items.Count == 0 || busy) return;
            string what = items.Count == 1 ? "1 item" : $"{items.Count:N0} items";
            Confirm($"Delete all {what} ({Formatting.FormatSize(items.Sum(i => i.Size ?? 0))}) in the {trash.TrashName} for good? This can't be undone.",
                "Delete all", async () =>
                {
                    changed = true;
                    await RunAsync(ct => trash.EmptyTrashAsync(ct));
                }).ShowAt(empty);
        }

        restore.Click += async (_, _) => await RestoreAsync();
        delete.Click += (_, _) => AskDelete();
        empty.Click += (_, _) => AskEmpty();
        refresh.Click += async (_, _) => await LoadAsync();
        list.SelectionChanged += (_, _) => UpdateButtons();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Delete)
            {
                AskDelete();
                e.Handled = true;
            }
        };
        panel.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.F5)
            {
                e.Handled = true;
                await LoadAsync();
            }
        };
        dialog.Opened += async (_, _) =>
        {
            UpdateButtons();
            await LoadAsync();
            list.Focus(FocusState.Programmatic);
        };
        dialog.Closed += (_, _) => cts.Cancel();

        await dialogs.ShowAsync(dialog);
        cts.Dispose();
        return changed;
    }

    private static Grid Row(FontIcon icon, string name, string location, string date, string size, bool header = false)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = header ? new Thickness(12, 0, 12, 0) : new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        icon.Opacity = header ? 0 : 0.85;
        Place(grid, icon, 0);
        string style = header ? "CaptionTextBlockStyle" : "BodyTextBlockStyle";
        var cells = new[] { name, location, date, size };
        for (int i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock
            {
                Text = cells[i],
                Style = Style(style),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = header || i > 0 ? 0.75 : 1,
                HorizontalAlignment = i == 3 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            };
            if (header) text.FontWeight = FontWeights.SemiBold;
            Place(grid, text, i + 1);
        }
        return grid;
    }

    private static Button ToolButton(string glyph, string label, string tip, bool labelVisible = true)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        if (labelVisible) content.Children.Add(new TextBlock { Text = label });
        var button = new Button { Content = content, IsEnabled = false };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
