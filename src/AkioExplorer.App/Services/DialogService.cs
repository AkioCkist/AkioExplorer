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

            // Dialogs are laid out for a roomy window; in a small one they shrink instead of being cut off.
            var sized = new List<(FrameworkElement Element, double Width, double MinWidth, double Height)>();
            Collect(dialog.Content, sized, depth: 0);
            var root = XamlRoot;
            void Fit()
            {
                double maxWidth = Math.Max(260, root.Size.Width - 120);
                double maxHeight = Math.Max(140, root.Size.Height - 340);
                foreach (var (element, width, minWidth, height) in sized)
                {
                    if (!double.IsNaN(width)) element.Width = Math.Min(width, maxWidth);
                    if (minWidth > 0) element.MinWidth = Math.Min(minWidth, maxWidth);
                    if (!double.IsNaN(height)) element.Height = Math.Min(height, maxHeight);
                }
            }
            void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Fit();
            Fit();
            root.Changed += OnRootChanged;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                root.Changed -= OnRootChanged;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Elements with a set width, minimum width or tall height (lists), down a few levels of the content.</summary>
    private static void Collect(object? content, List<(FrameworkElement, double, double, double)> sized, int depth)
    {
        if (content is not FrameworkElement element || depth > 6) return;
        bool tall = !double.IsNaN(element.Height) && element.Height >= 200;
        if (!double.IsNaN(element.Width) || element.MinWidth > 0 || tall)
            sized.Add((element, element.Width, element.MinWidth, tall ? element.Height : double.NaN));
        switch (element)
        {
            case Panel panel:
                foreach (var child in panel.Children) Collect(child, sized, depth + 1);
                break;
            case ScrollViewer scroller:
                Collect(scroller.Content, sized, depth + 1);
                break;
            case Border border:
                Collect(border.Child, sized, depth + 1);
                break;
            case ContentControl control when control is not Button:
                Collect(control.Content, sized, depth + 1);
                break;
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
