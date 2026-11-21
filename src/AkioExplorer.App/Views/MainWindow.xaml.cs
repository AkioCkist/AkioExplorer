using System.Collections.Specialized;
using System.ComponentModel;
using AkioExplorer.App.Controls;
using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using AkioExplorer.Core.Transfers;
using Windows.Storage.Pickers;
using Windows.Graphics;

namespace AkioExplorer.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly FavoritesService _favorites;
    private readonly ILogger<MainWindow> _logger;
    private readonly CloudAccountsViewModel _cloud;
    private const string AddAccountTag = "add-cloud-account";
    private bool _initialized;
    private bool _syncingNav;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, ThemeService theme, DialogService dialogs, FavoritesService favorites, ILogger<MainWindow> logger, CloudAccountsViewModel cloud)
    {
        ViewModel = viewModel;
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _favorites = favorites;
        _logger = logger;
        _cloud = cloud;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 420;
        }

        _theme.Attach(RootGrid, AppWindow);
        RestorePlacement();

        RootGrid.Loaded += OnRootLoaded;
        RootGrid.SizeChanged += (_, _) => UpdateContentWidth();
        Nav.DisplayModeChanged += (_, _) => UpdateContentWidth();
        Nav.PaneOpened += (_, _) => UpdateContentWidth();
        Nav.PaneClosed += (_, _) => UpdateContentWidth();
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FocusPaneRequested += (_, pane) => FocusPane(pane);
        ViewModel.LeftPane.PropertyChanged += OnPanePropertyChanged;
        ViewModel.RightPane.PropertyChanged += OnPanePropertyChanged;
        ViewModel.LeftPane.OpenInOtherPaneRequested += (_, location) => ViewModel.OpenInOtherPane(location);
        ViewModel.RightPane.OpenInOtherPaneRequested += (_, location) => ViewModel.OpenInOtherPane(location);
        ViewModel.Favorites.CollectionChanged += (_, _) => BuildSidebar();
        ViewModel.Drives.CollectionChanged += (_, _) => BuildSidebar();
        _cloud.Changed += (_, _) => BuildSidebar();
        foreach (var pane in new[] { ViewModel.LeftPane, ViewModel.RightPane })
        {
            pane.AuthenticationExpired += (_, providerId) => _cloud.MarkNeedsReconnect(providerId);
            pane.ReconnectRequested += async (sender, providerId) => await ReconnectAsync(providerId, (FilePaneViewModel)sender!);
        }
        BuildSidebar();
    }

    public MainViewModel ViewModel { get; }

    // ------------------------------------------------------------------ lifecycle

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _dialogs.XamlRoot = RootGrid.XamlRoot;
        if (_initialized) return;
        _initialized = true;

        await ViewModel.InitializeAsync();
        LeftPaneView.FocusList();
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        TitleText.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.5 : 1.0;
        if (!_initialized || args.WindowActivationState == WindowActivationState.Deactivated) return;

        // Keyboard-first: if nothing has focus after activation, shortcuts would go nowhere.
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        _logger.LogDebug("Activated, focused element: {Focused}", focused?.GetType().Name ?? "<none>");
        if (focused is null && ExplorerVisible)
            ViewFor(ViewModel.ActivePane).FocusList();

        await ViewModel.RefreshDrivesAsync(); // pick up USB drives plugged in while away (throttled)
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SavePlacement();
        ViewModel.SaveState();
        _settings.Flush();
    }

    private void RestorePlacement()
    {
        var p = _settings.Current.Window;
        var size = new SizeInt32(Math.Max(p.Width, 640), Math.Max(p.Height, 420));
        if (p.X != int.MinValue && p.Y != int.MinValue)
        {
            var rect = new RectInt32(p.X, p.Y, size.Width, size.Height);
            if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
            {
                AppWindow.MoveAndResize(rect);
                if (p.IsMaximized && AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
                return;
            }
        }

        // First run: centre on the primary work area.
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int scale = (int)Math.Round(GetDpiScale() * 100);
        int width = Math.Min(size.Width * scale / 100, area.Width);
        int height = Math.Min(size.Height * scale / 100, area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SavePlacement()
    {
        var p = _settings.Current.Window;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            p.IsMaximized = true;
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        p.IsMaximized = false;
        p.X = AppWindow.Position.X;
        p.Y = AppWindow.Position.Y;
        p.Width = AppWindow.Size.Width;
        p.Height = AppWindow.Size.Height;
    }

    // ------------------------------------------------------------------ panes

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDualPane):
                ApplyDualPaneLayout();
                break;
            case nameof(MainViewModel.ActivePane):
                SyncSidebarSelection();
                UpdateWindowTitle();
                break;
        }
    }

    private void ApplyDualPaneLayout()
    {
        bool dual = ViewModel.IsDualPane;
        RightColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneDivider.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        RightPaneView.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        if (!dual) LeftPaneView.FocusList();
    }

    private FilePane ViewFor(FilePaneViewModel pane) => pane == ViewModel.LeftPane ? LeftPaneView : RightPaneView;

    /// <summary>Created on first use so startup doesn't pay for the settings page.</summary>
    private SettingsView? SettingsHost { get; set; }

    private void FocusPane(FilePaneViewModel pane)
    {
        ShowExplorer();
        // Right pane may have just been created by x:Load: focus after layout.
        DispatcherQueue.TryEnqueue(() => ViewFor(pane).FocusList());
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePaneViewModel.Location) && sender == ViewModel.ActivePane)
            SyncSidebarSelection();
        if (e.PropertyName is nameof(FilePaneViewModel.Title) or nameof(FilePaneViewModel.Location) && sender == ViewModel.ActivePane)
            UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        string folder = ViewModel.ActivePane.Title;
        Title = string.IsNullOrEmpty(folder) ? "AkioExplorer" : $"{folder} - AkioExplorer";
        TitleText.Text = Title;
    }

    // ------------------------------------------------------------------ sidebar

    private void BuildSidebar()
    {
        Nav.MenuItems.Clear();

        Nav.MenuItems.Add(CreateNavItem("Home", "", MainViewModel.Home, "This PC: your folders and drives"));

        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Favorites" });
        if (ViewModel.Favorites.Count == 0)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = "Right-click a folder › Pin",
                IsEnabled = false,
                SelectsOnInvoked = false,
                Icon = new FontIcon { Glyph = "" },
            });
        }
        foreach (var favorite in ViewModel.Favorites)
        {
            var item = CreateNavItem(favorite.Name, "", new StorageLocation(favorite.ProviderId, favorite.Path), favorite.Path);
            var unpin = new MenuFlyoutItem { Text = "Unpin from Favorites", Icon = new FontIcon { Glyph = "" } };
            var entry = favorite;
            unpin.Click += (_, _) => _favorites.Remove(entry);
            item.ContextFlyout = new MenuFlyout { Items = { unpin } };
            Nav.MenuItems.Add(item);
        }

        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Cloud accounts" });
        foreach (var account in _cloud.Accounts)
        {
            var item = new NavigationViewItem
            {
                Content = account.Name,
                Icon = AccountDialogs.ProviderIcon(account.IconUri),
                Tag = _cloud.RootOf(account.Account),
            };
            if (account.NeedsReconnect)
                item.InfoBadge = new InfoBadge { Style = (Style)Application.Current.Resources["AttentionIconInfoBadgeStyle"] };
            ToolTipService.SetToolTip(item, account.NeedsReconnect ? $"{account.Subtitle} · sign-in expired" : account.Subtitle);
            item.ContextFlyout = AccountMenu(account);
            Nav.MenuItems.Add(item);
        }
        var add = new NavigationViewItem
        {
            Content = "Add cloud account",
            Icon = new FontIcon { Glyph = "\uE710" },
            Tag = AddAccountTag,
            SelectsOnInvoked = false,
        };
        ToolTipService.SetToolTip(add, "Connect Google Drive, Dropbox or OneDrive");
        Nav.MenuItems.Add(add);

        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Local Computer" });
        foreach (var folder in ViewModel.KnownFolders)
            Nav.MenuItems.Add(CreateNavItem(folder.Name, folder.Glyph, new StorageLocation("local", folder.Path), folder.Path));
        foreach (var drive in ViewModel.Drives)
        {
            string glyph = drive.TypeDescription switch
            {
                "USB Drive" => "",
                "Network Drive" => "",
                "CD Drive" => "",
                _ => "",
            };
            string tip = drive.TotalSize is { } total
                ? $"{drive.Name} · {Core.Logic.Formatting.FormatSize(drive.FreeSpace ?? 0)} free of {Core.Logic.Formatting.FormatSize(total)}"
                : drive.Name;
            Nav.MenuItems.Add(CreateNavItem(drive.Name, glyph, new StorageLocation("local", drive.Path), tip));
        }

        SyncSidebarSelection();
    }

    private static NavigationViewItem CreateNavItem(string text, string glyph, StorageLocation location, string tooltip)
    {
        var item = new NavigationViewItem
        {
            Content = text,
            Icon = new FontIcon { Glyph = glyph },
            Tag = location,
        };
        ToolTipService.SetToolTip(item, tooltip);
        return item;
    }

    private void SyncSidebarSelection()
    {
        if (SettingsHost is { Visibility: Visibility.Visible }) return;
        var location = ViewModel.ActivePane.Location;
        var match = location is null ? null : Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag is StorageLocation l && l.Equals(location));

        _syncingNav = true;
        Nav.SelectedItem = match;
        _syncingNav = false;
    }

    private async void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_syncingNav) return;
        if (args.IsSettingsInvoked)
        {
            ShowSettings();
            return;
        }

        if (args.InvokedItemContainer?.Tag is AddAccountTag)
        {
            await AddCloudAccountAsync();
            return;
        }

        if (args.InvokedItemContainer?.Tag is StorageLocation location)
        {
            ShowExplorer();
            await ViewModel.NavigateActiveAsync(location);
            SyncSidebarSelection(); // if navigation failed, the highlight returns to where the pane really is
            ViewFor(ViewModel.ActivePane).FocusList();
        }
    }

    // ------------------------------------------------------------------ cloud accounts

    private MenuFlyout AccountMenu(CloudAccountItem account)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open in other pane", Icon = new FontIcon { Glyph = "\uE8A4" } };
        open.Click += (_, _) => ViewModel.OpenInOtherPane(_cloud.RootOf(account.Account));
        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await AccountDialogs.RenameAsync(_dialogs, _cloud, account);
        var reconnect = new MenuFlyoutItem { Text = "Reconnect…", Icon = new FontIcon { Glyph = "\uE72C" } };
        reconnect.Click += async (_, _) => await ReconnectAsync(account.Account.ProviderId, ViewModel.ActivePane);
        var disconnect = new MenuFlyoutItem { Text = "Disconnect…", Icon = new FontIcon { Glyph = "\uE8D8" } };
        disconnect.Click += async (_, _) => await DisconnectAsync(account);
        menu.Items.Add(open);
        menu.Items.Add(rename);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(reconnect);
        menu.Items.Add(disconnect);
        return menu;
    }

    private async Task AddCloudAccountAsync()
    {
        var account = await AccountDialogs.AddAccountAsync(_dialogs, _cloud);
        if (account is null) return;
        ShowExplorer();
        await ViewModel.NavigateActiveAsync(_cloud.RootOf(account));
        ViewFor(ViewModel.ActivePane).FocusList();
    }

    private async Task ReconnectAsync(string providerId, FilePaneViewModel pane)
    {
        if (_cloud.FindByProvider(providerId) is not { } account) return;
        if (await AccountDialogs.ReconnectAsync(_dialogs, _cloud, account))
        {
            pane.IsErrorOpen = false;
            BuildSidebar();
            if (pane.Location?.ProviderId == providerId) pane.RefreshCommand.Execute(null);
        }
    }

    private async Task DisconnectAsync(CloudAccountItem account)
    {
        if (!await AccountDialogs.DisconnectAsync(_dialogs, _cloud, account)) return;
        foreach (var pane in new[] { ViewModel.LeftPane, ViewModel.RightPane })
        {
            if (pane.Location?.ProviderId == account.Account.ProviderId) await pane.NavigateAsync(MainViewModel.Home);
        }
    }

    /// <summary>Used by the Settings page.</summary>
    public Task AddCloudAccountFromSettingsAsync() => AddCloudAccountAsync();

    public Task DisconnectFromSettingsAsync(CloudAccountItem account) => DisconnectAsync(account);

    private void ShowSettings()
    {
        if (SettingsHost is null)
        {
            SettingsHost = new SettingsView();
            ContentRoot.Children.Add(SettingsHost);
        }
        UpdateContentWidth();
        SettingsHost.Visibility = Visibility.Visible;
        ExplorerHost.Visibility = Visibility.Collapsed;
        Nav.SelectedItem = Nav.SettingsItem;
    }

    /// <summary>
    /// NavigationView measures its content with unbounded width; pin the content to the real width so text wraps,
    /// the toolbar overflows correctly and nothing renders past the window edge.
    /// </summary>
    private void UpdateContentWidth()
    {
        double pane = Nav.DisplayMode switch
        {
            NavigationViewDisplayMode.Expanded => Nav.IsPaneOpen ? Nav.OpenPaneLength : Nav.CompactPaneLength,
            NavigationViewDisplayMode.Compact => Nav.CompactPaneLength,
            _ => 0,
        };
        double width = RootGrid.ActualWidth - pane - 1;
        if (width <= 0) return;
        ContentRoot.Width = width;
        SettingsHost?.SetAvailableWidth(width);
    }

    private void ShowExplorer()
    {
        if (ExplorerHost.Visibility == Visibility.Visible) return;
        ExplorerHost.Visibility = Visibility.Visible;
        if (SettingsHost is not null) SettingsHost.Visibility = Visibility.Collapsed;
        SyncSidebarSelection();
    }

    // ------------------------------------------------------------------ toolbar menus

    private void OnSortMenuOpening(object sender, object e)
    {
        var pane = ViewModel.ActivePane;
        SortName.IsChecked = pane.SortField == SortField.Name;
        SortDate.IsChecked = pane.SortField == SortField.Modified;
        SortType.IsChecked = pane.SortField == SortField.Type;
        SortSize.IsChecked = pane.SortField == SortField.Size;
        SortAsc.IsChecked = !pane.SortDescending;
        SortDesc.IsChecked = pane.SortDescending;
    }

    private void OnSortFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortField>(tag, out var field))
            ViewModel.ActivePane.SortField = field;
    }

    private void OnSortDirectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) ViewModel.ActivePane.SortDescending = tag == "Desc";
    }

    private void OnViewMenuOpening(object sender, object e)
    {
        ViewDetails.IsChecked = ViewModel.ActivePane.IsDetailsView;
        ViewGrid.IsChecked = ViewModel.ActivePane.IsGridView;
    }

    private void OnViewDetailsClick(object sender, RoutedEventArgs e) => ViewModel.ActivePane.ViewMode = FileViewMode.Details;

    private void OnViewGridClick(object sender, RoutedEventArgs e) => ViewModel.ActivePane.ViewMode = FileViewMode.Grid;

    // ------------------------------------------------------------------ copy / move to

    private void OnCopyToOtherPane(object sender, RoutedEventArgs e) => ViewModel.TransferToOtherPane(ViewModel.ActivePane, OperationKind.Copy);

    private void OnMoveToOtherPane(object sender, RoutedEventArgs e) => ViewModel.TransferToOtherPane(ViewModel.ActivePane, OperationKind.Move);

    private async void OnCopyToFolder(object sender, RoutedEventArgs e) => await TransferToPickedFolderAsync(OperationKind.Copy);

    private async void OnMoveToFolder(object sender, RoutedEventArgs e) => await TransferToPickedFolderAsync(OperationKind.Move);

    private async Task TransferToPickedFolderAsync(OperationKind kind)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !string.IsNullOrEmpty(folder.Path))
            ViewModel.TransferSelectionTo(kind, new StorageLocation("local", folder.Path));
    }

    // ------------------------------------------------------------------ accelerators

    private bool ExplorerVisible => ExplorerHost.Visibility == Visibility.Visible;

    private void Run(KeyboardAcceleratorInvokedEventArgs args, Action<FilePaneViewModel> action)
    {
        if (!ExplorerVisible) return;
        args.Handled = true;
        action(ViewModel.ActivePane);
    }

    private void OnBackAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.GoBackCommand.Execute(null));

    private void OnForwardAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.GoForwardCommand.Execute(null));

    private void OnUpAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.GoUpCommand.Execute(null));

    private void OnRefreshAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.RefreshCommand.Execute(null));

    private void OnAddressAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.RequestAddressEdit());

    private void OnFilterAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.RequestFilterFocus());

    private void OnNewFolderAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.NewFolderCommand.Execute(null));

    private void OnCopyPathAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.CopyPathCommand.Execute(null));

    private void OnDetailsAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.ViewMode = FileViewMode.Details);

    private void OnGridAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.ViewMode = FileViewMode.Grid);

    private void OnPropertiesAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.ShowPropertiesCommand.Execute(null));

    private void OnSwitchPaneAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        if (ViewModel.IsDualPane) ViewModel.SwitchPaneCommand.Execute(null);
        else FocusPane(ViewModel.ActivePane);
    }

    private void OnDualPaneAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        ShowExplorer();
        ViewModel.ToggleDualPaneCommand.Execute(null);
    }

    private void OnUndoAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        // Text boxes keep their own Ctrl+Z (undo typing).
        if (TextBoxHasFocus) return;
        a.Handled = true;
        ViewModel.UndoCommand.Execute(null);
    }

    private bool TextBoxHasFocus => RootGrid.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox;

    // Clipboard shortcuts act on the active pane (even an empty one), but text boxes keep their own copy/paste.
    private void OnCopyAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus) Run(a, p => p.CopyCommand.Execute(null));
    }

    private void OnCutAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus) Run(a, p => p.CutCommand.Execute(null));
    }

    private void OnPasteAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus) Run(a, p => p.PasteCommand.Execute(null));
    }

    private void OnFocusSidebarAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        if (Nav.SelectedItem is NavigationViewItem selected) selected.Focus(FocusState.Keyboard);
        else if (Nav.MenuItems.FirstOrDefault() is NavigationViewItem first) first.Focus(FocusState.Keyboard);
    }
}
