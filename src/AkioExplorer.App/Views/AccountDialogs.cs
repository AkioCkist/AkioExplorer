using System.Diagnostics;
using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.CloudProviders;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AkioExplorer.App.Views;

/// <summary>Account management dialogs. Sign-in happens in the user's browser; the dialog just shows progress.</summary>
public static class AccountDialogs
{
    public static IconElement ProviderIcon(string uri, double size = 20) =>
        new ImageIcon { Source = new SvgImageSource(new Uri(uri)), Width = size, Height = size };

    /// <summary>Lets the user pick a service and sign in. Returns the new account, or null if cancelled.</summary>
    public static async Task<CloudAccount?> AddAccountAsync(DialogService dialogs, CloudAccountsViewModel accounts)
    {
        CloudAccount? result = null;
        CancellationTokenSource? signIn = null;
        accounts.ReloadConfiguration();

        var dialog = new ContentDialog { Title = "Add a cloud account", CloseButtonText = "Cancel" };
        var message = new InfoBar { IsClosable = false, IsOpen = false, Margin = new Thickness(0, 0, 0, 8) };
        var choices = new StackPanel { Spacing = 6 };
        var waiting = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 12) };
        var waitingText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        waiting.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        waiting.Children.Add(waitingText);

        var setup = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };

        foreach (var service in accounts.Services)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10),
                Content = ServiceRow(service),
            };
            AutomationName(button, service.Name);
            button.Click += async (_, _) =>
            {
                message.IsOpen = false;
                accounts.ReloadConfiguration(); // the user may have just saved cloud-apps.json
                if (!service.IsConfigured)
                {
                    ShowSetup(setup, choices, service);
                    return;
                }

                choices.Visibility = Visibility.Collapsed;
                waiting.Visibility = Visibility.Visible;
                waitingText.Text = $"Finish signing in to {service.Name} in your browser.\nThis window continues automatically.";
                signIn = new CancellationTokenSource();
                try
                {
                    result = await accounts.AddAsync(service.Type, signIn.Token);
                    dialog.Hide();
                }
                catch (OperationCanceledException)
                {
                    if (signIn.IsCancellationRequested) return; // dialog closed
                    Show(message, "Sign-in was cancelled.", InfoBarSeverity.Informational);
                }
                catch (StorageException ex)
                {
                    Show(message, ex.Message, InfoBarSeverity.Error);
                }
                choices.Visibility = Visibility.Visible;
                waiting.Visibility = Visibility.Collapsed;
            };
            choices.Children.Add(button);
        }

        var body = new StackPanel { MinWidth = 380 };
        body.Children.Add(message);
        body.Children.Add(choices);
        body.Children.Add(waiting);
        body.Children.Add(setup);
        dialog.Content = body;
        dialog.CloseButtonClick += (_, _) => signIn?.Cancel();

        await dialogs.ShowAsync(dialog);
        signIn?.Cancel();
        return result;
    }

    public static async Task<bool> ReconnectAsync(DialogService dialogs, CloudAccountsViewModel accounts, CloudAccountItem account)
    {
        bool ok = false;
        using var signIn = new CancellationTokenSource();
        var text = new TextBlock
        {
            Text = $"Finish signing in to {account.ServiceName} as {account.Subtitle} in your browser.\nThis window continues automatically.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        var error = new InfoBar { IsClosable = false, IsOpen = false, Severity = InfoBarSeverity.Error };
        var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
        panel.Children.Add(error);
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        panel.Children.Add(text);

        var dialog = new ContentDialog { Title = $"Reconnect \"{account.Name}\"", Content = panel, CloseButtonText = "Cancel" };
        dialog.CloseButtonClick += (_, _) => signIn.Cancel();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                await accounts.ReconnectAsync(account.Account.Id, signIn.Token);
                ok = true;
                dialog.Hide();
            }
            catch (OperationCanceledException) when (signIn.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is StorageException or OperationCanceledException)
            {
                error.Message = ex is OperationCanceledException ? "Sign-in was cancelled." : ex.Message;
                error.IsOpen = true;
                panel.Children[1].Visibility = Visibility.Collapsed;
                text.Text = string.Empty;
            }
        };

        await dialogs.ShowAsync(dialog);
        return ok;
    }

    public static async Task RenameAsync(DialogService dialogs, CloudAccountsViewModel accounts, CloudAccountItem account)
    {
        var box = new TextBox { Text = account.Name, MinWidth = 320 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Keyboard); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            Title = "Rename account",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        if (await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
            accounts.Rename(account.Account.Id, box.Text);
    }

    public static async Task<bool> DisconnectAsync(DialogService dialogs, CloudAccountsViewModel accounts, CloudAccountItem account)
    {
        bool confirmed = await dialogs.ConfirmAsync(
            $"Disconnect \"{account.Name}\"?",
            $"Your files stay in {account.ServiceName}. AkioExplorer removes the account from the sidebar and deletes its saved sign-in from Windows Credential Manager.",
            "Disconnect");
        if (confirmed) await accounts.RemoveAsync(account.Account.Id);
        return confirmed;
    }

    // ------------------------------------------------------------------ helpers

    private static Grid ServiceRow(CloudServiceItem service)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = ProviderIcon(service.IconUri, 28);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = service.Name });
        text.Children.Add(new TextBlock
        {
            Text = service.IsConfigured ? "Sign in with your browser" : "Setup required",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static void ShowSetup(StackPanel setup, StackPanel choices, CloudServiceItem service)
    {
        setup.Children.Clear();
        setup.Children.Add(new TextBlock { Text = $"Set up {service.Name}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        setup.Children.Add(new TextBlock { Text = service.SetupHint, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var open = new Button { Content = "Open cloud-apps.json" };
        open.Click += (_, _) =>
        {
            CloudAppsConfig.EnsureTemplate();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{CloudAppsConfig.FilePath}\"") { UseShellExecute = true })?.Dispose();
        };
        var back = new Button { Content = "Back" };
        back.Click += (_, _) =>
        {
            setup.Visibility = Visibility.Collapsed;
            choices.Visibility = Visibility.Visible;
        };
        buttons.Children.Add(open);
        buttons.Children.Add(back);
        setup.Children.Add(buttons);

        choices.Visibility = Visibility.Collapsed;
        setup.Visibility = Visibility.Visible;
    }

    private static void Show(InfoBar bar, string text, InfoBarSeverity severity)
    {
        bar.Message = text;
        bar.Severity = severity;
        bar.IsOpen = true;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
