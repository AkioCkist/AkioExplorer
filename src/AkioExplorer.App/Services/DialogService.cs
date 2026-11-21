using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AkioExplorer.App.Services;

/// <summary>Shows ContentDialogs one at a time (WinUI allows only one open dialog per window).</summary>
public sealed class DialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public XamlRoot? XamlRoot { get; set; }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (XamlRoot is null) return ContentDialogResult.None;
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = XamlRoot;
            dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            dialog.RequestedTheme = (XamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Confirmation for destructive, non-undoable actions. Default button is Cancel so a stray Enter can't destroy data.
    /// </summary>
    public async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
