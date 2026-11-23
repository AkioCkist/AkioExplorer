using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.CloudProviders;
using AkioExplorer.CloudProviders.Box;
using AkioExplorer.CloudProviders.Dropbox;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;

namespace AkioExplorer.App.Views;

/// <summary>Account management dialogs. Sign-in happens in the user's browser; the dialog just shows progress.</summary>
public static class AccountDialogs
{
    public static IconElement ProviderIcon(string uri, double size = 20) =>
        new ImageIcon { Source = CloudAccountsViewModel.IconSource(uri), Width = size, Height = size };

    /// <summary>
    /// Lets the user pick a service (type to filter, Enter picks the first match), fill in its form if it has one,
    /// and sign in. Returns the new account, or null if cancelled.
    /// </summary>
    public static async Task<CloudAccount?> AddAccountAsync(DialogService dialogs, CloudAccountsViewModel accounts)
    {
        CloudAccount? result = null;
        CancellationTokenSource? signIn = null;
        accounts.ReloadConfiguration();

        var dialog = new ContentDialog { Title = "Add a cloud account", CloseButtonText = "Cancel" };
        var message = new InfoBar { IsClosable = false, IsOpen = false, Margin = new Thickness(0, 0, 0, 8) };

        var search = new TextBox { PlaceholderText = "Search services (e.g. S3, NAS, SharePoint)", Margin = new Thickness(0, 0, 0, 8) };
        AutomationName(search, "Search services");
        var list = new StackPanel { Spacing = 4 };
        var scroller = new ScrollViewer { Content = list, MaxHeight = 460, Padding = new Thickness(0, 0, 12, 0) };
        var choices = new StackPanel();
        choices.Children.Add(search);
        choices.Children.Add(scroller);

        var waiting = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 12) };
        var waitingText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        waiting.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        waiting.Children.Add(waitingText);

        var setup = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var form = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };

        void ShowOnly(UIElement visible)
        {
            foreach (var panel in new UIElement[] { choices, waiting, setup, form }) panel.Visibility = panel == visible ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task SignInAsync(CloudServiceItem service, IReadOnlyDictionary<string, string> values)
        {
            message.IsOpen = false;
            ShowOnly(waiting);
            waitingText.Text = service.UsesBrowserSignIn
                ? $"Sign in to {service.Name} in the browser window that just opened.\nThis window continues automatically."
                : $"Connecting to {service.Name}…";
            signIn = new CancellationTokenSource();
            try
            {
                result = await accounts.AddAsync(service.Type, values, signIn.Token);
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
            // Back to where the user can fix it: the form if there is one, else the list.
            ShowOnly(service.Fields.Count > 0 ? form : choices);
        }

        Task ContinueAsync(CloudServiceItem service)
        {
            if (service.Fields.Count > 0)
            {
                BuildForm(form, service, null, "Connect", values => SignInAsync(service, values), () => ShowOnly(choices));
                ShowOnly(form);
                return Task.CompletedTask;
            }
            return SignInAsync(service, new Dictionary<string, string>());
        }

        async Task PickAsync(CloudServiceItem service)
        {
            message.IsOpen = false;
            accounts.ReloadConfiguration(); // picks up cloud-apps.json / environment changes
            if (service.IsConfigured)
                await ContinueAsync(service);
            else
                ShowSetup(setup, choices, message, service, accounts, ContinueAsync, () => ShowOnly(setup));
        }

        var rows = new List<(CloudServiceItem Service, FrameworkElement Row)>();
        foreach (var service in accounts.Services)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Content = ServiceRow(service),
            };
            AutomationName(button, service.Name);
            button.Click += async (_, _) => await PickAsync(service);
            rows.Add((service, service.UsesBrowserSignIn ? WithClearButton(button, service, accounts, message) : button));
        }

        var iCloud = ICloudRow(message);

        void Render()
        {
            list.Children.Clear();
            string query = search.Text.Trim();
            foreach (var group in rows.Where(r => r.Service.Matches(query)).GroupBy(r => r.Service.Category).OrderBy(g => g.Key))
            {
                list.Children.Add(new TextBlock
                {
                    Text = CloudServiceItem.CategoryName(group.Key),
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 2),
                });
                foreach (var (_, row) in group) list.Children.Add(row);
                if (group.Key == CloudCategory.Popular && ICloudMatches(query)) list.Children.Add(iCloud);
            }
            if (list.Children.Count == 0 && ICloudMatches(query)) list.Children.Add(iCloud);
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "No service matches. Most NAS boxes and self-hosted servers work with WebDAV, SFTP or FTP.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
        }

        search.TextChanged += (_, _) => Render();
        search.KeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            if (rows.FirstOrDefault(r => r.Service.Matches(search.Text.Trim())) is { Service: { } first }) await PickAsync(first);
        };
        search.Loaded += (_, _) => search.Focus(FocusState.Programmatic);
        Render();

        var body = new StackPanel { Width = 460 };
        body.Children.Add(message);
        body.Children.Add(choices);
        body.Children.Add(waiting);
        body.Children.Add(setup);
        body.Children.Add(form);
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
        var service = accounts.ServiceOf(account.Account.Type);
        bool browser = service?.UsesBrowserSignIn != false;

        var error = new InfoBar { IsClosable = false, IsOpen = false, Severity = InfoBarSeverity.Error };
        var ring = new ProgressRing { IsActive = true, Width = 32, Height = 32 };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var progress = new StackPanel { Spacing = 16 };
        progress.Children.Add(ring);
        progress.Children.Add(text);
        var form = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 16, Width = 460 };
        panel.Children.Add(error);
        panel.Children.Add(progress);
        panel.Children.Add(form);

        var dialog = new ContentDialog { Title = $"Reconnect \"{account.Name}\"", Content = panel, CloseButtonText = "Cancel" };
        dialog.CloseButtonClick += (_, _) => signIn.Cancel();

        async Task RunAsync(IReadOnlyDictionary<string, string> values)
        {
            error.IsOpen = false;
            form.Visibility = Visibility.Collapsed;
            progress.Visibility = Visibility.Visible;
            text.Text = browser
                ? $"Finish signing in to {account.ServiceName} as {account.Subtitle} in your browser.\nThis window continues automatically."
                : $"Connecting to {account.ServiceName}…";
            try
            {
                await accounts.ReconnectAsync(account.Account.Id, values, signIn.Token);
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
                progress.Visibility = Visibility.Collapsed;
                if (service is { Fields.Count: > 0 }) form.Visibility = Visibility.Visible;
            }
        }

        if (service is { Fields.Count: > 0 })
        {
            // Settings come back filled in; secrets stay blank and are kept unless the user types new ones.
            progress.Visibility = Visibility.Collapsed;
            form.Visibility = Visibility.Visible;
            BuildForm(form, service, account.Account.Settings, browser ? "Sign in" : "Reconnect", RunAsync, back: null);
        }
        else
        {
            dialog.Opened += async (_, _) => await RunAsync(new Dictionary<string, string>());
        }

        await dialogs.ShowAsync(dialog);
        return ok;
    }

    // ------------------------------------------------------------------ connect form

    /// <summary>
    /// Builds a service's form: one control per field, help under it, Enter submits. With <paramref name="existing"/>
    /// (reconnect) text fields are pre-filled and secret fields may be left blank to keep the saved value.
    /// </summary>
    private static void BuildForm(StackPanel form, CloudServiceItem service, IReadOnlyDictionary<string, string>? existing, string submitText,
        Func<IReadOnlyDictionary<string, string>, Task> submit, Action? back)
    {
        form.Children.Clear();
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = ProviderIcon(service.IconUri, 28);
        header.Children.Add(icon);
        var title = new TextBlock { Text = service.Name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        form.Children.Add(header);

        var readers = new List<(CloudField Field, Func<string> Read)>();
        Control? first = null;
        var submitButton = new Button { Content = submitText, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

        foreach (var field in service.Fields)
        {
            string current = existing?.GetValueOrDefault(field.Key) ?? field.Default ?? string.Empty;
            string label = field.Optional && field.Kind is not CloudFieldKind.Toggle ? $"{field.Label} (optional)" : field.Label;
            Control control;
            Func<string> read;
            switch (field.Kind)
            {
                case CloudFieldKind.Secret:
                    var password = new PasswordBox
                    {
                        Header = label,
                        PlaceholderText = existing is not null ? "Leave empty to keep the saved one" : field.Placeholder ?? string.Empty,
                    };
                    control = password;
                    read = () => password.Password;
                    break;
                case CloudFieldKind.Choice:
                    var combo = new ComboBox { Header = label, ItemsSource = field.Choices, HorizontalAlignment = HorizontalAlignment.Stretch };
                    combo.SelectedItem = field.Choices?.Contains(current) == true ? current : field.Choices?.FirstOrDefault();
                    control = combo;
                    read = () => combo.SelectedItem as string ?? string.Empty;
                    break;
                case CloudFieldKind.Toggle:
                    var check = new CheckBox { Content = field.Label, IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase) };
                    control = check;
                    read = () => check.IsChecked == true ? "true" : "false";
                    break;
                case CloudFieldKind.FilePath:
                    var pathBox = new TextBox { Header = label, Text = current, PlaceholderText = field.Placeholder ?? string.Empty };
                    control = pathBox;
                    read = () => pathBox.Text;
                    break;
                default:
                    var box = new TextBox
                    {
                        Header = label,
                        Text = current,
                        PlaceholderText = field.Placeholder ?? string.Empty,
                        InputScope = new Microsoft.UI.Xaml.Input.InputScope
                        {
                            Names =
                            {
                                new Microsoft.UI.Xaml.Input.InputScopeName(field.Kind switch
                                {
                                    CloudFieldKind.Url => Microsoft.UI.Xaml.Input.InputScopeNameValue.Url,
                                    CloudFieldKind.Number => Microsoft.UI.Xaml.Input.InputScopeNameValue.Number,
                                    _ => Microsoft.UI.Xaml.Input.InputScopeNameValue.Default,
                                }),
                            },
                        },
                    };
                    box.IsSpellCheckEnabled = false;
                    control = box;
                    read = () => box.Text;
                    break;
            }

            AutomationName(control, field.Label);
            control.KeyDown += async (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter || control is ComboBox) return;
                e.Handled = true;
                await submit(Collect());
            };
            first ??= control is CheckBox ? null : control;
            readers.Add((field, read));

            var cell = new StackPanel { Spacing = 4 };
            if (field.Kind == CloudFieldKind.FilePath && control is TextBox target)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(target);
                var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
                browse.Click += async (_, _) =>
                {
                    if (await PickFileAsync() is { } picked) target.Text = picked;
                };
                Grid.SetColumn(browse, 1);
                row.Children.Add(browse);
                cell.Children.Add(row);
            }
            else
            {
                cell.Children.Add(control);
            }
            if (field.Help is { Length: > 0 } help)
            {
                cell.Children.Add(new TextBlock
                {
                    Text = help,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }
            form.Children.Add(cell);
        }

        Dictionary<string, string> Collect() => readers.ToDictionary(r => r.Field.Key, r => r.Read(), StringComparer.Ordinal);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        submitButton.Click += async (_, _) => await submit(Collect());
        buttons.Children.Add(submitButton);
        if (back is not null)
        {
            var backButton = new Button { Content = "Back" };
            backButton.Click += (_, _) => back();
            buttons.Children.Add(backButton);
        }
        form.Children.Add(buttons);

        if (first is not null)
        {
            var focus = first;
            focus.Loaded += (_, _) => focus.Focus(FocusState.Programmatic);
            if (focus.IsLoaded) focus.Focus(FocusState.Programmatic);
        }
    }

    private static async Task<string?> PickFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        var window = App.Services.GetRequiredService<MainWindow>();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ------------------------------------------------------------------ iCloud

    private static bool ICloudMatches(string query) =>
        string.IsNullOrWhiteSpace(query) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => "icloud drive apple iphone".Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Apple offers no file API for iCloud Drive; iCloud for Windows syncs it into a folder, which AkioExplorer shows
    /// in the sidebar. The row says where it is, or how to get it.
    /// </summary>
    private static Button ICloudRow(InfoBar message)
    {
        string folder = Infrastructure.Shell.KnownFoldersService.ICloudDrivePath;
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 24, Width = 28, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "iCloud Drive" });
        text.Children.Add(new TextBlock
        {
            Text = Directory.Exists(folder) ? "Already in the sidebar (synced by iCloud for Windows)" : "Needs iCloud for Windows from the Microsoft Store",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Content = row,
        };
        AutomationName(button, "iCloud Drive");
        button.Click += async (_, _) =>
        {
            if (Directory.Exists(folder))
            {
                Show(message, $"iCloud Drive is in the sidebar under This PC ({folder}). iCloud for Windows keeps it in sync.", InfoBarSeverity.Informational);
                return;
            }
            Show(message, "Apple doesn't let other apps open iCloud Drive directly. Install iCloud for Windows, sign in, turn on iCloud Drive, " +
                          "then restart AkioExplorer: iCloud Drive shows up in the sidebar.", InfoBarSeverity.Informational);
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-windows-store://search/?query=iCloud"));
        };
        return button;
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
            Text = service.Description,
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
        CloudProviderType.Box => new(
            "https://app.box.com/developers/console",
            $"Create a Platform App with User Authentication (OAuth 2.0), tick \"Read and write all files and folders\", and add the redirect URI http://localhost:{BoxConnector.RedirectPort}/",
            "Client ID", NeedsSecret: true),
        CloudProviderType.Dropbox => new(
            "https://www.dropbox.com/developers/apps",
            $"Create a \"Scoped access\" app with Full Dropbox, enable the files.*, sharing.read and account_info.read permissions, and add the redirect URI http://localhost:{DropboxConnector.RedirectPort}/",
            "App key", NeedsSecret: false),
        _ => new(
            "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
            "New registration → personal and work accounts → redirect URI \"Public client/native\" http://localhost. Under Authentication, allow public client flows. " +
            "OneDrive for Business and SharePoint use the same ID (SharePoint also asks for Sites.Read.All).",
            "Application (client) ID", NeedsSecret: false),
    };

    private static void ShowSetup(StackPanel setup, StackPanel choices, InfoBar message, CloudServiceItem service,
        CloudAccountsViewModel accounts, Func<CloudServiceItem, Task> signIn, Action show)
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
            secret = new TextBox { Header = service.Type == CloudProviderType.Box ? "Client secret" : "Client secret (Google requires it for desktop apps; it isn't confidential)" };
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

        show();
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
