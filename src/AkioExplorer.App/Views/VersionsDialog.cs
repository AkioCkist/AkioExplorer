using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AkioExplorer.App.Views;

/// <summary>
/// Earlier versions of a file the service keeps (MEGA versioning): restore one as the current file, or delete old
/// ones to free space. Deleting confirms in a flyout (no second ContentDialog over this one).
/// </summary>
public static class VersionsDialog
{
    /// <summary>Returns true when something changed (the caller refreshes the folder).</summary>
    public static async Task<bool> ShowAsync(DialogService dialogs, IVersionsProvider provider, StorageItem file)
    {
        bool changed = false, busy = false;
        IReadOnlyList<FileVersion> versions = [];
        var cts = new CancellationTokenSource();

        var restore = Button("", "Restore this version", "Make the selected version the current file; the current one is kept as a version");
        var delete = Button("", "Delete version", "Delete the selected old version for good (frees its space)");
        var deleteOld = Button("", "Delete all old versions", "Keep only the current file");
        var download = Button("", "Download", "Save the selected version to the Downloads folder");
        var keep = Button("", "Keep forever", "Google Drive deletes old versions after 30 days unless kept");
        keep.Visibility = provider.SupportsKeepForever ? Visibility.Visible : Visibility.Collapsed;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(download);
        tools.Children.Add(keep);
        tools.Children.Add(restore);
        tools.Children.Add(delete);
        tools.Children.Add(deleteOld);

        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300, Margin = new Thickness(-12, 0, -12, 0) };
        AutomationProperties.SetName(list, "Versions");
        var summary = new TextBlock { Style = Style("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

        var panel = new StackPanel { Spacing = 8, Width = 620 };
        panel.Children.Add(tools);
        panel.Children.Add(progress);
        panel.Children.Add(error);
        panel.Children.Add(list);
        panel.Children.Add(summary);

        var dialog = new ContentDialog { Title = $"Versions of \"{file.Name}\"", Content = panel, CloseButtonText = "Close" };
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;

        FileVersion? Selected() => (list.SelectedItem as FrameworkElement)?.Tag as FileVersion;

        void UpdateButtons()
        {
            var selected = Selected();
            restore.IsEnabled = !busy && selected is { IsCurrent: false };
            download.IsEnabled = !busy && selected is not null;
            keep.IsEnabled = !busy && selected is { KeepForever: not null };
            ((TextBlock)((StackPanel)keep.Content).Children[1]).Text = selected?.KeepForever == true ? "Don't keep forever" : "Keep forever";
            delete.IsEnabled = !busy && selected is { IsCurrent: false };
            deleteOld.IsEnabled = !busy && versions.Count > 1;
        }

        void Show(IReadOnlyList<FileVersion> loaded)
        {
            versions = loaded;
            list.Items.Clear();
            foreach (var version in versions)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = version };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                string label = version.Name + (version.IsCurrent ? "  (current)" : "") + (version.KeepForever == true ? "  · kept forever" : "") +
                               (version.Author is { } author ? $"  · {author}" : "");
                var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
                if (version.IsCurrent) name.FontWeight = FontWeights.SemiBold;
                var date = new TextBlock { Text = version.Modified is { } m ? Formatting.FormatDate(m) : "—", Opacity = 0.75 };
                var size = new TextBlock { Text = version.Size is { } s ? Formatting.FormatSize(s) : "—", Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(date, 1);
                Grid.SetColumn(size, 2);
                row.Children.Add(name);
                row.Children.Add(date);
                row.Children.Add(size);
                AutomationProperties.SetName(row, $"{version.Name}, {date.Text}");
                list.Items.Add(row);
            }
            int old = versions.Count - 1;
            long oldBytes = versions.Where(v => !v.IsCurrent).Sum(v => v.Size ?? 0);
            summary.Text = old == 0
                ? "No earlier versions. When a file with the same name replaces this one, the old one is kept here."
                : $"{old} earlier {(old == 1 ? "version" : "versions")} · {Formatting.FormatSize(oldBytes)}. Old versions count toward your storage.";
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
            if (reload) await RunAsync(async ct => Show(await provider.ListVersionsAsync(file, ct)), reload: false);
        }

        Flyout Confirm(string message, string action, Func<Task> run)
        {
            var go = new Button { Content = action, Style = Style("AccentButtonStyle"), HorizontalAlignment = HorizontalAlignment.Right };
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
            body.Children.Add(go);
            var flyout = new Flyout { Content = body };
            go.Click += async (_, _) => { flyout.Hide(); await run(); };
            flyout.Opened += (_, _) => go.Focus(FocusState.Programmatic);
            return flyout;
        }

        restore.Click += async (_, _) =>
        {
            if (Selected() is not { IsCurrent: false } version) return;
            changed = true;
            await RunAsync(ct => provider.RestoreVersionAsync(file, version, ct));
        };
        delete.Click += (_, _) =>
        {
            if (Selected() is not { IsCurrent: false } version) return;
            Confirm($"Delete the version from {(version.Modified is { } m ? Formatting.FormatDate(m) : "this date")} for good?", "Delete", async () =>
            {
                changed = true;
                await RunAsync(ct => provider.DeleteVersionAsync(file, version, ct));
            }).ShowAt(delete);
        };
        deleteOld.Click += (_, _) =>
        {
            var old = versions.Where(v => !v.IsCurrent).ToList();
            if (old.Count == 0) return;
            Confirm($"Delete all {old.Count} earlier versions for good? The current file stays.", "Delete all", async () =>
            {
                changed = true;
                await RunAsync(async ct => { foreach (var v in old) await provider.DeleteVersionAsync(file, v, ct); });
            }).ShowAt(deleteOld);
        };
        download.Click += async (_, _) =>
        {
            if (Selected() is not { } version) return;
            string stem = Path.GetFileNameWithoutExtension(file.Name);
            string when = version.Modified is { } m ? m.ToString("yyyy-MM-dd HH-mm") : version.Id;
            await RunAsync(async ct =>
            {
                string saved = await ViewModels.FilePaneViewModel.SaveToDownloadsAsync($"{stem} ({when}){Path.GetExtension(file.Name)}",
                    c => provider.OpenVersionAsync(file, version, c));
                summary.Text = $"Saved \"{Path.GetFileName(saved)}\" in Downloads.";
            }, reload: false);
        };
        keep.Click += async (_, _) =>
        {
            if (Selected() is not { KeepForever: { } kept } version) return;
            await RunAsync(ct => provider.SetKeepForeverAsync(file, version, !kept, ct));
        };
        list.SelectionChanged += (_, _) => UpdateButtons();
        dialog.Opened += async (_, _) =>
        {
            UpdateButtons();
            await RunAsync(async ct => Show(await provider.ListVersionsAsync(file, ct)), reload: false);
        };
        dialog.Closed += (_, _) => cts.Cancel();

        await dialogs.ShowAsync(dialog);
        cts.Dispose();
        return changed;
    }

    private static Button Button(string glyph, string label, string tip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = label });
        var button = new Button { Content = content, IsEnabled = false };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
