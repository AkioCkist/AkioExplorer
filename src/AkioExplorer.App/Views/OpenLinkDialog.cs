using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AkioExplorer.App.Views;

/// <summary>
/// "Open a link…": paste a link someone shared (mega.nz/file/…, mega.nz/folder/…), see its name and size, then save
/// a copy into the current folder of this account. A link already on the clipboard is filled in.
/// </summary>
public static class OpenLinkDialog
{
    /// <summary>Returns the saved item, or null when nothing was saved.</summary>
    public static async Task<StorageItem?> ShowAsync(DialogService dialogs, IPublicLinkProvider provider, string folderPath, string accountName)
    {
        var box = new TextBox { PlaceholderText = "https://mega.nz/file/…#…" };
        AutomationProperties.SetName(box, "Link");
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.8 };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 10, Width = 480 };
        panel.Children.Add(box);
        panel.Children.Add(progress);
        panel.Children.Add(details);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "Open a link",
            Content = panel,
            PrimaryButtonText = "Save to this folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        var cts = new CancellationTokenSource();
        StorageItem? saved = null;
        PublicLinkInfo? info = null;
        CancellationTokenSource? inspect = null;

        async Task InspectAsync()
        {
            inspect?.Cancel();
            inspect = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var token = inspect.Token;
            info = null;
            dialog.IsPrimaryButtonEnabled = false;
            details.Text = string.Empty;
            string link = box.Text.Trim();
            if (link.Length == 0)
            {
                status.Text = $"Paste a link someone shared with you. A copy is saved in {accountName}, in the folder that's open.";
                return;
            }
            if (!provider.CanOpenLink(link))
            {
                status.Text = "That doesn't look like a link this service can open (it needs the part after #, the key).";
                return;
            }
            progress.Visibility = Visibility.Visible;
            status.Text = "Reading the link…";
            try
            {
                var found = await provider.InspectLinkAsync(link, token);
                if (token.IsCancellationRequested) return;
                info = found;
                details.Text = found.IsFolder
                    ? $"Folder: {found.Name} · {found.FileCount:N0} files · {Formatting.FormatSize(found.Size)}"
                    : $"{found.Name} · {Formatting.FormatSize(found.Size)}";
                status.Text = found.IsFolder
                    ? "Folders are copied file by file through this PC (MEGA can't copy a shared folder on its side)."
                    : "The copy is made on MEGA's servers: nothing is downloaded.";
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) status.Text = $"Couldn't open the link: {ex.Message}";
            }
            finally
            {
                if (!token.IsCancellationRequested) progress.Visibility = Visibility.Collapsed;
            }
        }

        box.TextChanged += async (_, _) => await InspectAsync();
        dialog.Opened += async (_, _) =>
        {
            // A link on the clipboard is most likely the one to open.
            try
            {
                var clip = Clipboard.GetContent();
                if (clip.Contains(StandardDataFormats.Text) && (await clip.GetTextAsync()).Trim() is { } text && provider.CanOpenLink(text))
                    box.Text = text;
            }
            catch (Exception)
            {
                // Clipboard busy: the user pastes.
            }
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
            if (box.Text.Length == 0) await InspectAsync();
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (info is null) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            box.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                var report = new Progress<string>(name => status.Text = $"Saving {name}…");
                saved = await provider.ImportLinkAsync(box.Text.Trim(), folderPath, report, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Text = $"Couldn't save it: {ex.Message}";
                args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = true;
                box.IsEnabled = true;
            }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
                deferral.Complete();
            }
        };
        dialog.Closed += (_, _) => cts.Cancel();

        await dialogs.ShowAsync(dialog);
        cts.Dispose();
        return saved;
    }
}
