using AkioExplorer.App.Services;
using AkioExplorer.Infrastructure.Local;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AkioExplorer.App.Views;

/// <summary>Small dialogs for pane tools: select by pattern, checksums.</summary>
public static class ToolDialogs
{
    private static string _lastPattern = "*.jpg; *.png";

    /// <summary>"Select by pattern" / "Deselect by pattern". Returns the pattern text, or null when cancelled.</summary>
    public static async Task<string?> PatternAsync(DialogService dialogs, bool select)
    {
        var box = new TextBox { Text = _lastPattern, PlaceholderText = "*.jpg; *.png; report*" };
        AutomationProperties.SetName(box, "Pattern");
        var panel = new StackPanel { Spacing = 8, Width = 400 };
        panel.Children.Add(box);
        panel.Children.Add(Caption("Separate patterns with ; – * matches any text, ? one character. Text without * matches anywhere in the name."));

        var dialog = new ContentDialog
        {
            Title = select ? "Select by pattern" : "Deselect by pattern",
            Content = panel,
            PrimaryButtonText = select ? "Select" : "Deselect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        // Enter in the box presses the default (Select) button.
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return null;
        _lastPattern = box.Text;
        return box.Text;
    }

    /// <summary>Hashes of one file, each with a Copy button, plus a box to paste an expected hash and compare.</summary>
    public static async Task ChecksumAsync(DialogService dialogs, string path)
    {
        var cts = new CancellationTokenSource();
        var progress = new ProgressBar { Maximum = 1, Value = 0 };
        var rows = new StackPanel { Spacing = 8 };
        var compare = new TextBox { PlaceholderText = "Paste a hash to compare", IsEnabled = false };
        AutomationProperties.SetName(compare, "Expected hash");
        var verdict = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel { Spacing = 12, Width = 560 };
        panel.Children.Add(progress);
        panel.Children.Add(rows);
        panel.Children.Add(compare);
        panel.Children.Add(verdict);

        var dialog = new ContentDialog
        {
            Title = $"Checksums – {Path.GetFileName(path)}",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 680.0;

        FileHashes? hashes = null;
        compare.TextChanged += (_, _) =>
        {
            string wanted = compare.Text.Trim().Replace(" ", string.Empty).ToLowerInvariant();
            if (hashes is null || wanted.Length == 0)
            {
                verdict.Text = string.Empty;
                return;
            }
            string? match = wanted == hashes.Md5 ? "MD5" : wanted == hashes.Sha1 ? "SHA-1" : wanted == hashes.Sha256 ? "SHA-256" : wanted == hashes.Sha512 ? "SHA-512" : null;
            verdict.Text = match is not null ? $"✓ Matches the {match} of this file." : "✗ Doesn't match any hash of this file.";
            verdict.Foreground = (Brush)Application.Current.Resources[match is not null ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        };

        dialog.Opened += async (_, _) =>
        {
            try
            {
                hashes = await LocalFileTools.ComputeHashesAsync(path, new Progress<double>(p => progress.Value = p), cts.Token);
                progress.Visibility = Visibility.Collapsed;
                rows.Children.Add(HashRow("MD5", hashes.Md5));
                rows.Children.Add(HashRow("SHA-1", hashes.Sha1));
                rows.Children.Add(HashRow("SHA-256", hashes.Sha256));
                rows.Children.Add(HashRow("SHA-512", hashes.Sha512));
                compare.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Core.Models.StorageException ex)
            {
                progress.ShowError = true;
                verdict.Text = ex.Message;
            }
        };
        dialog.Closed += (_, _) => cts.Cancel();
        await dialogs.ShowAsync(dialog);
    }

    private static FrameworkElement HashRow(string name, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var text = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(copy, $"Copy {name}");
        AutomationProperties.SetName(copy, $"Copy {name}");
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(value);
            Clipboard.SetContent(package);
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(label);
        grid.Children.Add(text);
        grid.Children.Add(copy);
        return grid;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
