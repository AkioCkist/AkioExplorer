using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.CloudProviders;
using AkioExplorer.CloudProviders.Dropbox;
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

        // OAuth 2.0 + PKCE: the sign-in page opens in the default Windows browser; the dialog waits for the redirect.
        async Task SignInAsync(CloudServiceItem service)
        {
            message.IsOpen = false;
            setup.Visibility = Visibility.Collapsed;
            choices.Visibility = Visibility.Collapsed;
            waiting.Visibility = Visibility.Visible;
            waitingText.Text = $"Sign in to {service.Name} in the browser window that just opened.\nThis window continues automatically.";
            signIn = new CancellationTokenSource();
            try
            {
                result = await accounts.AddAsync(service.Type, signIn.Token);
                dialog.Hide();
                return;
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
        }

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
                accounts.ReloadConfiguration(); // picks up cloud-apps.json / environment changes
                if (service.IsConfigured)
                    await SignInAsync(service);
                else
                    ShowSetup(setup, choices, message, service, accounts, SignInAsync);
            };
            choices.Children.Add(WithClearButton(button, service, accounts, message));
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

    /// <summary>
    /// Puts a "Clear app ID" button beside a service row when the user saved an id for it, so a wrong or blocked
    /// registration can be replaced. A flyout confirms (a second ContentDialog can't open over this one).
    /// </summary>
    private static Grid WithClearButton(Button serviceButton, CloudServiceItem service, CloudAccountsViewModel accounts, InfoBar message)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(serviceButton);

        var clear = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 16 },
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(12, 0, 12, 0),
            Visibility = accounts.HasSavedRegistration(service.Type) ? Visibility.Visible : Visibility.Collapsed,
        };
        string label = $"Clear {service.Name} app ID";
        ToolTipService.SetToolTip(clear, label);
        AutomationName(clear, label);
        Grid.SetColumn(clear, 1);
        row.Children.Add(clear);

        var confirm = new Button { Content = "Clear app ID", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyoutBody = new StackPanel { Spacing = 12, MaxWidth = 320 };
        flyoutBody.Children.Add(new TextBlock { Text = $"Clear the saved {service.Name} app ID?", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        flyoutBody.Children.Add(new TextBlock
        {
            Text = accounts.HasAccountsOf(service.Type)
                ? $"The next sign-in asks for a new ID. Connected {service.Name} accounts were signed in with this ID and will need to be reconnected."
                : "The next sign-in asks for a new ID.",
            TextWrapping = TextWrapping.Wrap,
        });
        flyoutBody.Children.Add(confirm);
        var flyout = new Flyout { Content = flyoutBody };
        clear.Flyout = flyout;

        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            try
            {
                accounts.ClearRegistration(service.Type);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Show(message, $"Couldn't save {CloudAppsConfig.FilePath}: {ex.Message}", InfoBarSeverity.Error);
                return;
            }

            clear.Visibility = Visibility.Collapsed;
            serviceButton.Content = ServiceRow(service);
            if (service.IsConfigured)
                Show(message, $"Saved {service.Name} app ID cleared. This build or an environment variable still provides one, so sign-in keeps using it.", InfoBarSeverity.Warning);
            else
                Show(message, $"{service.Name} app ID cleared. Pick {service.Name} to enter a new one.", InfoBarSeverity.Success);
        };
        return row;
    }

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
            Text = service.IsConfigured ? "Sign in with your browser" : "One-time setup, then sign in with your browser",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>What the user needs from each service's developer console (only shown when the build has no built-in id).</summary>
    private sealed record SetupInfo(string ConsoleUrl, string Steps, string IdLabel, bool NeedsSecret);

    private static SetupInfo SetupFor(CloudProviderType type) => type switch
    {
        CloudProviderType.GoogleDrive => new(
            "https://console.cloud.google.com/apis/credentials",
            "Enable the Google Drive API, then create an OAuth client ID of type \"Desktop app\". Add your Google account as a test user on the OAuth consent screen.",
            "Client ID", NeedsSecret: true),
        CloudProviderType.Dropbox => new(
            "https://www.dropbox.com/developers/apps",
            $"Create a \"Scoped access\" app with Full Dropbox, enable the files.* and account_info.read permissions, and add the redirect URI http://localhost:{DropboxConnector.RedirectPort}/",
            "App key", NeedsSecret: false),
        _ => new(
            "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
            "New registration → personal and work accounts → redirect URI \"Public client/native\" http://localhost. Under Authentication, allow public client flows.",
            "Application (client) ID", NeedsSecret: false),
    };

    private static void ShowSetup(StackPanel setup, StackPanel choices, InfoBar message, CloudServiceItem service,
        CloudAccountsViewModel accounts, Func<CloudServiceItem, Task> signIn)
    {
        var info = SetupFor(service.Type);
        setup.Children.Clear();
        setup.Children.Add(new TextBlock { Text = $"Connect AkioExplorer to {service.Name}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        setup.Children.Add(new TextBlock
        {
            Text = $"One-time step: {service.Name} only lets registered apps sign in. {info.Steps} After this, signing in is just your browser.",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        var console = new HyperlinkButton { Content = $"Open {service.Name} developer console", NavigateUri = new Uri(info.ConsoleUrl), Padding = new Thickness(0) };
        setup.Children.Add(console);

        var id = new TextBox { Header = info.IdLabel, MinWidth = 380 };
        setup.Children.Add(id);
        TextBox? secret = null;
        if (info.NeedsSecret)
        {
            secret = new TextBox { Header = "Client secret (Google requires it for desktop apps; it isn't confidential)" };
            setup.Children.Add(secret);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button
        {
            Content = "Save and sign in",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = false,
        };
        void Validate() => save.IsEnabled = id.Text.Trim().Length > 0 && (secret is null || secret.Text.Trim().Length > 0);
        id.TextChanged += (_, _) => Validate();
        if (secret is not null) secret.TextChanged += (_, _) => Validate();

        save.Click += async (_, _) =>
        {
            try
            {
                accounts.SaveRegistration(service.Type, id.Text, secret?.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Show(message, $"Couldn't save {CloudAppsConfig.FilePath}: {ex.Message}", InfoBarSeverity.Error);
                return;
            }
            await signIn(service);
        };
        var back = new Button { Content = "Back" };
        back.Click += (_, _) =>
        {
            setup.Visibility = Visibility.Collapsed;
            choices.Visibility = Visibility.Visible;
        };
        buttons.Children.Add(save);
        buttons.Children.Add(back);
        setup.Children.Add(buttons);

        choices.Visibility = Visibility.Collapsed;
        setup.Visibility = Visibility.Visible;
        id.Loaded += (_, _) => id.Focus(FocusState.Programmatic);
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
