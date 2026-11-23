using System.Collections.Specialized;
using System.ComponentModel;
using AkioExplorer.App.Controls;
using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Local;
using Microsoft.Extensions.DependencyInjection;
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
    private const string TransfersTag = "transfers";
    private const string NewTabTag = "new-tab";
    private NavigationViewItem? _transfersItem;
    private bool _initialized;
    private bool _ready;
    private bool _syncingNav;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private QuickSearchWindow? _quickSearch;
    private string? _appliedHotkey;
    private bool _exitRequested;
    private bool _shutDown;
    private LaunchRequest? _pendingLaunch;
    private string? _pendingLink;

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
        AddZoomAccelerators();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 420;
        }

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _theme.Attach(RootGrid, AppWindow);
        RestorePlacement();
        App.Services.GetRequiredService<AkioExplorer.Infrastructure.Shell.ShellMenuService>().OwnerWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);

        RootGrid.Loaded += OnRootLoaded;
        RootGrid.SizeChanged += (_, _) => UpdateContentWidth();
        Nav.DisplayModeChanged += (_, _) => UpdateContentWidth();
        // Size the content for the pane's target state as soon as the toggle starts (IsPaneOpen is already the new
        // value). Waiting for PaneOpened/PaneClosed left the old width during the animation, then snapped the
        // toolbar and re-flowed its "More" overflow at the end – the visible jerk.
        Nav.PaneOpening += (_, _) => UpdateContentWidth();
        Nav.PaneClosing += (_, _) => UpdateContentWidth();
        // Upload / Download show only on cloud folders: the toolbar's spacing is worked out again when they come or go.
        foreach (var command in Toolbar.PrimaryCommands.OfType<UIElement>())
            command.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => QueueToolbarJustify());
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FocusPaneRequested += (_, pane) => FocusPane(pane);
        ViewModel.LeftPane.PropertyChanged += OnPanePropertyChanged;
        ViewModel.RightPane.PropertyChanged += OnPanePropertyChanged;
        ViewModel.LeftPane.SelectionChanged += OnPaneSelectionChanged;
        ViewModel.RightPane.SelectionChanged += OnPaneSelectionChanged;
        SetUpPreviewPane();
        ViewModel.LeftPane.OpenInOtherPaneRequested += (_, location) => ViewModel.OpenInOtherPane(location);
        ViewModel.RightPane.OpenInOtherPaneRequested += (_, location) => ViewModel.OpenInOtherPane(location);
        ViewModel.Favorites.CollectionChanged += (_, _) => BuildSidebar();
        ViewModel.Drives.CollectionChanged += (_, _) => BuildSidebar();
        _cloud.Changed += (_, _) => BuildSidebar();
        ViewModel.Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationsViewModel.ActiveCount)) UpdateTransfersBadge();
        };
        ViewModel.RemoteOpenFailed += (_, failure) => DispatcherQueue.TryEnqueue(() => failure.Pane.ShowError(failure.Message));
        foreach (var pane in new[] { ViewModel.LeftPane, ViewModel.RightPane })
        {
            pane.AuthenticationExpired += (_, providerId) => _cloud.MarkNeedsReconnect(providerId);
            pane.TrashChanged += (_, _) => _ = _cloud.LoadQuotasAsync(); // the trash counts toward the quota
            pane.ReconnectRequested += async (sender, providerId) => await ReconnectAsync(providerId, (FilePaneViewModel)sender!);
        }
        BuildSidebar();
        _settings.Changed += (_, _) => DispatcherQueue.TryEnqueue(OnIntegrationSettingsChanged);
        if (_settings.Current.QuickSearchEnabled && !_settings.Current.KeepRunningInBackground)
        {
            // The quick-search hotkey only works while the process runs: closing the window must not quit.
            _settings.Current.KeepRunningInBackground = true;
            _settings.Save();
        }
        if (_settings.Current.KeepRunningInBackground) EnsureTray();
        ApplyBackdrop();
        InitQuickSearch();
        InitWinE();
    }

    /// <summary>
    /// Win+E opens AkioExplorer when it is the default file manager, even after it quits: Windows ignores the
    /// registry override for the key itself, so a small separate process (<see cref="WinEAgent"/>) takes it.
    /// </summary>
    private static void InitWinE() => Task.Run(() =>
    {
        try
        {
            if (new AkioExplorer.Infrastructure.Shell.ShellIntegration().EnsureWinEAgentAtSignIn(AkioExplorer.Infrastructure.Shell.ShellIntegration.ExecutablePath))
                WinEAgent.EnsureRunning();
        }
        catch (Exception)
        {
            // Registry locked down: Win+E stays with File Explorer, nothing else depends on it.
        }
    });

    public MainViewModel ViewModel { get; }

    // ------------------------------------------------------------------ lifecycle

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _dialogs.XamlRoot = RootGrid.XamlRoot;
        if (_initialized) return;
        _initialized = true;
        PlayEnterTransition();

        await ViewModel.InitializeAsync();
        LeftPaneView.FocusList();
        _ready = true;
        if (_pendingLaunch is { Path: not null } pending)
        {
            _pendingLaunch = null;
            await OpenExternalAsync(pending.Path!);
        }
        if (_pendingLink is { } link)
        {
            _pendingLink = null;
            await OpenLinkFromQuickSearchAsync(link);
        }
        _ = WarmUpShellMenusAsync();
        // Build the (hidden) quick-search popup now, so the first hotkey press shows it at once.
        if (_settings.Current.QuickSearchEnabled)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => EnsureQuickSearch());
    }

    // ------------------------------------------------------------------ launches from outside, background mode

    /// <summary>Sign-in autostart: no window yet, just the notification-area icon (the window is built on first open).</summary>
    public void StartInBackground() => EnsureTray();

    /// <summary>A launch request from this process or a later one: show the window and open the requested folder.</summary>
    public void HandleLaunch(LaunchRequest request)
    {
        if (request.Background && request.Path is null)
        {
            EnsureTray();
            return;
        }
        if (request.Path is { } path)
        {
            if (_ready) _ = OpenExternalAsync(path);
            else _pendingLaunch = request;
        }
        ShowAndActivate();
    }

    private async Task OpenExternalAsync(string path)
    {
        ShowExplorer();
        bool newTab = _settings.Current.ExternalOpenTarget == ExternalOpenTarget.NewTab;
        await ViewModel.ActivePane.OpenExternalAsync(path, newTab);
        ViewFor(ViewModel.ActivePane).FocusList();
    }

    public void ShowAndActivate()
    {
        bool minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        bool appearing = !AppWindow.IsVisible || minimized; // already on screen (another folder opened): no replay
        AppWindow.Show();
        if (minimized) ((OverlappedPresenter)AppWindow.Presenter).Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (appearing) PlayEnterTransition();
    }

    private void EnsureTray()
    {
        _tray ??= new TrayIcon(ShowAndActivate, ShowTrayMenu);
        _tray.Show();
    }

    private TrayMenuWindow? _trayMenu;

    private void ShowTrayMenu(int x, int y)
    {
        _trayMenu ??= new TrayMenuWindow();
        var theme = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => RootGrid.ActualTheme,
        };
        _trayMenu.Show(BuildTrayMenu(), x, y, theme);
    }

    /// <summary>Right-click menu of the notification-area icon: the things worth one click without opening the window.</summary>
    private List<TrayMenuItem> BuildTrayMenu()
    {
        var items = new List<TrayMenuItem>
        {
            new("Open AkioExplorer", ShowAndActivate, "", IsDefault: true),
        };
        if (_settings.Current.QuickSearchEnabled)
            items.Add(new("Quick search", ToggleQuickSearch, "", _hotkey?.Active?.ToString()));
        items.Add(TrayMenuItem.Separator);

        // Everyday folders one click away; the rest of the known folders and the favorites in submenus.
        var known = App.Services.GetRequiredService<IKnownFoldersService>().GetKnownFolders();
        string[] everyday =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ];
        var direct = known.Where(f => everyday.Contains(f.Path, StringComparer.OrdinalIgnoreCase) ||
                                      f.Name.Equals("Downloads", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var folder in direct)
            items.Add(new(folder.Name, () => OpenFromTray(new StorageLocation(LocalStorageProvider.ProviderId, folder.Path)), folder.Glyph));
        var others = known.Except(direct)
            .Select(f => new TrayMenuItem(f.Name, () => OpenFromTray(new StorageLocation(LocalStorageProvider.ProviderId, f.Path)), f.Glyph))
            .ToList();
        if (others.Count > 0) items.Add(new("More folders", Glyph: "", Children: others));
        if (ViewModel.Favorites.Count > 0)
        {
            var favorites = ViewModel.Favorites
                .Select(f => new TrayMenuItem(f.Name, () => OpenFromTray(new StorageLocation(f.ProviderId, f.Path)), ""))
                .ToList();
            items.Add(new("Favorites", Glyph: "", Children: favorites));
        }
        items.Add(TrayMenuItem.Separator);

        int running = ViewModel.Operations.ActiveCount;
        items.Add(new(running > 0 ? $"Transfer queue ({running} running)" : "Transfer queue", () => { ShowAndActivate(); ShowTransfers(history: false); }, ""));
        items.Add(new("Recent transfers", () => { ShowAndActivate(); ShowTransfers(history: true); }, ""));
        items.Add(new("Settings", () => { ShowAndActivate(); ShowSettings(); }, ""));
        items.Add(TrayMenuItem.Separator);

        if (_hotkey is { } hotkey && _settings.Current.QuickSearchEnabled)
            items.Add(new("Pause hotkey (game mode)", () => hotkey.Paused = !hotkey.Paused, "", Checked: hotkey.Paused));
        var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        items.Add(new("Start with Windows", () => settingsVm.StartWithWindows = !settingsVm.StartWithWindows, "", Checked: settingsVm.StartWithWindows));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("Exit", ExitApp, ""));
        return items;
    }

    /// <summary>A folder picked in the tray menu: local ones like any outside open (new tab), clouds in the active pane.</summary>
    private async void OpenFromTray(StorageLocation location)
    {
        if (location.ProviderId == LocalStorageProvider.ProviderId)
        {
            HandleLaunch(new LaunchRequest(location.Path, false));
            return;
        }
        ShowAndActivate();
        ShowExplorer();
        await ViewModel.NavigateActiveAsync(location);
    }

    /// <summary>
    /// Tray › Exit: really quit, even with "keep running in the background" on. Window.Close() from code does not
    /// raise AppWindow.Closing (only the system close button does), so the shutdown work runs here and the app is
    /// ended explicitly; otherwise the hidden quick-search and tray-menu windows keep the process alive.
    /// </summary>
    private async void ExitApp()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        await PlayExitTransitionAsync();
        Shutdown();
        Application.Current.Exit();
    }

    private void OnIntegrationSettingsChanged()
    {
        if (_settings.Current.KeepRunningInBackground) EnsureTray();
        else _tray?.Hide();
        ApplyBackdrop();
        ApplyQuickSearchHotkey();
    }

    // ------------------------------------------------------------------ quick search (launcher popup)

    private void InitQuickSearch()
    {
        _hotkey = App.Services.GetRequiredService<GlobalHotkey>();
        _hotkey.Pressed += (_, _) => ToggleQuickSearch();
        ApplyQuickSearchHotkey();
    }

    private void ApplyQuickSearchHotkey()
    {
        if (_hotkey is null) return;
        var s = _settings.Current;
        string? wanted = s.QuickSearchEnabled ? s.QuickSearchHotkey : null;
        if (wanted == _appliedHotkey) return;
        _appliedHotkey = wanted;
        bool ok = _hotkey.Set(wanted is not null && HotkeyGesture.TryParse(wanted, out var gesture) ? gesture : null);
        if (!ok) _logger.LogWarning("Quick search hotkey {Hotkey}: {Status}", wanted, _hotkey.Status);
    }

    private QuickSearchWindow EnsureQuickSearch()
    {
        if (_quickSearch is not null) return _quickSearch;
        _quickSearch = App.Services.GetRequiredService<QuickSearchWindow>();
        _quickSearch.ViewModel.ShowInExplorer = path => HandleLaunch(new LaunchRequest(path, false));
        _quickSearch.ViewModel.OpenLinkInExplorer = link =>
        {
            if (_ready) _ = OpenLinkFromQuickSearchAsync(link);
            else _pendingLink = link;
            ShowAndActivate();
        };
        _quickSearch.ViewModel.Prepare(); // app list ready before the first hotkey press
        return _quickSearch;
    }

    /// <summary>A link picked in quick search (Google Drive, MEGA, ftp://…): its own tab, like the sidebar's New tab.</summary>
    private async Task OpenLinkFromQuickSearchAsync(string link)
    {
        ShowExplorer();
        await ViewModel.ActivePane.OpenLinkInNewTabAsync(link);
        ViewFor(ViewModel.ActivePane).FocusList();
    }

    private void ToggleQuickSearch() => EnsureQuickSearch().Toggle();

    private WindowBackdrop _appliedBackdrop = WindowBackdrop.Mica; // MainWindow.xaml starts with Mica

    /// <summary>Settings › Appearance › Window background.</summary>
    private void ApplyBackdrop()
    {
        var wanted = _settings.Current.Backdrop;
        if (wanted == _appliedBackdrop) return;
        _appliedBackdrop = wanted;
        SystemBackdrop = wanted switch
        {
            WindowBackdrop.MicaAlt => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            WindowBackdrop.Acrylic => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
            WindowBackdrop.Solid => null,
            _ => new Microsoft.UI.Xaml.Media.MicaBackdrop(),
        };
        if (wanted == WindowBackdrop.Solid)
            RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        else
            RootGrid.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>
    /// Loads shell extension DLLs once in the background, so the first right-click opens as fast as later ones.
    /// </summary>
    private static async Task WarmUpShellMenusAsync()
    {
        await Task.Delay(1500);
        try
        {
            var shell = App.Services.GetRequiredService<IShellMenuService>();
            _ = shell.GetCodeEditorsAsync();
            _ = App.Services.GetRequiredService<IArchiverService>().GetArchiversAsync();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            (await shell.GetBackgroundMenuAsync(home, extended: false))?.Dispose();
        }
        catch
        {
            // Best effort only.
        }
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

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true; // the content fades out first, then the window hides or the app quits
        if (_exitRequested || !_settings.Current.KeepRunningInBackground)
        {
            ExitApp();
            return;
        }
        // Stay alive in the notification area so the next Win+E / folder opens instantly.
        SaveSession();
        EnsureTray();
        await PlayExitTransitionAsync();
        AppWindow.Hide();
        ResetTransition();
    }

    private void SaveSession()
    {
        SavePlacement();
        ViewModel.SaveState();
        _settings.Flush();
    }

    /// <summary>Everything that must happen before the process ends. Runs once, from the close button or Tray › Exit.</summary>
    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        SaveSession();
        _tray?.Dispose();
        _tray = null;
        _quickSearch?.CloseForExit(); // a hidden popup would otherwise keep the process alive
        _trayMenu?.CloseForExit();
        _trayMenu = null;
        _quickSearch = null;
        _hotkey?.Dispose();
        // Jobs still running are kept as "Interrupted" in Recent transfers so they can be retried next time.
        if (App.Services.GetService(typeof(Infrastructure.Settings.JsonTransferHistory)) is Infrastructure.Settings.JsonTransferHistory history)
            history.RecordInterrupted();
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
                RefreshPreview();
                break;
            case nameof(MainViewModel.IsPreviewOpen):
                ApplyPreviewLayout();
                break;
        }
    }

    // ------------------------------------------------------------------ preview pane

    private const double MinListWidth = 320;

    private void SetUpPreviewPane()
    {
        PreviewSplitter.GetWidth = () => PreviewColumn.Width.Value;
        PreviewSplitter.SetWidth = width => PreviewColumn.Width = new GridLength(width);
        PreviewSplitter.MaxTargetWidth = () => Math.Max(PreviewSplitter.MinTargetWidth, PanesGrid.ActualWidth - MinListWidth - PreviewSplitter.Width);
        PreviewSplitter.Committed += (_, _) => ViewModel.PreviewWidth = Math.Round(PreviewColumn.Width.Value);
        PanesGrid.SizeChanged += (_, _) => ClampPreviewWidth();
        ApplyPreviewLayout();
    }

    private void ApplyPreviewLayout()
    {
        bool open = ViewModel.IsPreviewOpen;
        PreviewColumn.Width = open ? new GridLength(Math.Max(ViewModel.PreviewWidth, PreviewSplitter.MinTargetWidth)) : new GridLength(0);
        PreviewSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PreviewView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            ClampPreviewWidth();
            RefreshPreview(force: true);
        }
        else
        {
            PreviewView.Clear();
        }
    }

    /// <summary>Keeps the file list usable when the window shrinks; the saved width comes back when it grows.</summary>
    private void ClampPreviewWidth()
    {
        if (!ViewModel.IsPreviewOpen || PanesGrid.ActualWidth <= 0) return;
        double max = PreviewSplitter.MaxTargetWidth();
        double wanted = Math.Max(ViewModel.PreviewWidth, PreviewSplitter.MinTargetWidth);
        double width = Math.Min(wanted, max);
        if (Math.Abs(PreviewColumn.Width.Value - width) > 0.5) PreviewColumn.Width = new GridLength(width);
    }

    private void OnPaneSelectionChanged(object? sender, EventArgs e)
    {
        if (sender == ViewModel.ActivePane) RefreshPreview();
    }

    private void RefreshPreview(bool force = false)
    {
        if (ViewModel.IsPreviewOpen) PreviewView.Show(ViewModel.ActivePane, force);
    }

    private void OnPreviewAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        ShowExplorer();
        ViewModel.IsPreviewOpen = !ViewModel.IsPreviewOpen;
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

        // New tab: a plain row like the rest, so the NavigationView lines it up in every pane state (open, compact,
        // minimal). Opens a tab with the address bar ready for a path or a pasted cloud/FTP link.
        Nav.MenuItems.Add(NewTabNavItem());
        Nav.MenuItems.Add(CreateNavItem("Home", "", MainViewModel.Home, "This PC: your folders and drives"));
        // One entry: the page itself has Queue and Recent tabs.
        _transfersItem = new NavigationViewItem { Content = "Transfers", Icon = new FontIcon { Glyph = "" }, Tag = TransfersTag };
        ToolTipService.SetToolTip(_transfersItem, "Running, waiting and recent copies, uploads and downloads");
        Nav.MenuItems.Add(_transfersItem);
        UpdateTransfersBadge();

        // No header until something is pinned (Pin to Favorites in the right-click menu); an empty section reads as a locked feature.
        if (ViewModel.Favorites.Count > 0) Nav.MenuItems.Add(SectionHeader("Favorites"));
        foreach (var favorite in ViewModel.Favorites)
        {
            var item = CreateNavItem(favorite.Name, "", new StorageLocation(favorite.ProviderId, favorite.Path), favorite.Path);
            var unpin = new MenuFlyoutItem { Text = "Unpin from Favorites", Icon = new FontIcon { Glyph = "" } };
            var entry = favorite;
            unpin.Click += (_, _) => _favorites.Remove(entry);
            item.ContextFlyout = new MenuFlyout { Items = { unpin } };
            Nav.MenuItems.Add(item);
        }

        Nav.MenuItems.Add(SectionHeader("Cloud accounts"));
        foreach (var account in _cloud.Accounts)
        {
            var item = new NavigationViewItem
            {
                Content = SidebarName(account),
                Icon = AccountDialogs.ProviderIcon(account.IconUri),
                Tag = _cloud.RootOf(account.Account),
            };
            if (account.NeedsReconnect)
                item.InfoBadge = new InfoBadge { Style = (Style)Application.Current.Resources["AttentionIconInfoBadgeStyle"] };
            string tip = account.Name == account.ServiceName ? account.Name : $"{account.ServiceName} · {account.Subtitle}";
            ToolTipService.SetToolTip(item, account.NeedsReconnect ? $"{tip} · sign-in expired" : tip);
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
        ToolTipService.SetToolTip(add, "Connect a cloud drive, a server (FTP, SFTP, WebDAV) or object storage (S3, Azure)");
        Nav.MenuItems.Add(add);

        Nav.MenuItems.Add(SectionHeader("This PC"));
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

    /// <summary>
    /// Section label that sits close to its own items: 24 above, 8 below. The template's header row is a fixed 40,
    /// so the shift comes from the margin; it never takes clicks, so the overlap with the first row is harmless.
    /// </summary>
    private static NavigationViewItemHeader SectionHeader(string text) => new()
    {
        Content = text,
        Margin = new Thickness(0, 8, 0, -8),
        IsHitTestVisible = false,
    };

    /// <summary>Short account label; falls back to the full address when two accounts of one service share it.</summary>
    private string SidebarName(CloudAccountItem account)
    {
        string name = account.Account.ShortName;
        bool clash = _cloud.Accounts.Any(other => other != account && other.Account.Type == account.Account.Type
                                                  && string.Equals(other.Account.ShortName, name, StringComparison.OrdinalIgnoreCase));
        return clash ? account.Account.Email ?? account.Account.UserName ?? account.Name : name;
    }

    /// <summary>"New tab" with the shortcut as quiet secondary text at the right edge (hidden with the label when collapsed).</summary>
    private static NavigationViewItem NewTabNavItem()
    {
        var keys = new TextBlock
        {
            Text = "Ctrl+T",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };
        Grid.SetColumn(keys, 1);
        var content = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { new TextBlock { Text = "New tab", VerticalAlignment = VerticalAlignment.Center }, keys },
        };
        var item = new NavigationViewItem
        {
            Content = content,
            Icon = new FontIcon { Glyph = "" },
            Tag = NewTabTag,
            SelectsOnInvoked = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, "New tab");
        ToolTipService.SetToolTip(item, "New tab: type a path, or paste a Google Drive, Dropbox, OneDrive, Box, MEGA, MediaFire or FTP link (Ctrl+T)");
        return item;
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

        if (args.InvokedItemContainer?.Tag is NewTabTag)
        {
            ShowExplorer();
            await ViewModel.ActivePane.NewTabForAddressAsync();
            return;
        }

        if (args.InvokedItemContainer?.Tag is TransfersTag)
        {
            ShowTransfers(history: false);
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
        var recent = new MenuFlyoutItem { Text = "Recent files", Icon = new FontIcon { Glyph = "" } };
        recent.Click += async (_, _) => await OnAccountPaneAsync(account, p => p.ShowRecentFilesAsync());
        var media = new MenuFlyoutItem { Text = "Photos & videos", Icon = new FontIcon { Glyph = "" } };
        media.Click += async (_, _) => await OnAccountPaneAsync(account, p => p.ShowMediaAsync());
        menu.Items.Add(new MenuFlyoutSeparator());
        var starred = new MenuFlyoutItem { Text = "Starred", Icon = new FontIcon { Glyph = "\uE734" } };
        starred.Click += async (_, _) => await OnAccountPaneAsync(account, p => p.ShowStarredAsync());
        var largest = new MenuFlyoutItem { Text = "Largest files", Icon = new FontIcon { Glyph = "\uEDA2" } };
        largest.Click += async (_, _) => await OnAccountPaneAsync(account, p => p.ShowLargestFilesAsync());
        menu.Items.Add(recent);
        menu.Items.Add(media);
        if ((_cloud.ProviderOf(account.Account)?.Capabilities & StorageCapabilities.SearchStarred) != 0) menu.Items.Add(starred);
        menu.Items.Add(largest);
        if (_cloud.ProviderOf(account.Account) is IWebViewProvider)
        {
            var web = new MenuFlyoutItem { Text = "Open in browser", Icon = new FontIcon { Glyph = "\uE774" } };
            web.Click += async (_, _) => await OnAccountPaneAsync(account, p => { p.OpenInBrowserCommand.Execute(null); return Task.CompletedTask; });
            menu.Items.Add(web);
        }
        if (_cloud.ProviderOf(account.Account) is IPublicLinkProvider)
        {
            var link = new MenuFlyoutItem { Text = "Open a link…", Icon = new FontIcon { Glyph = "" } };
            link.Click += async (_, _) => await OnAccountPaneAsync(account, p => { p.OpenPublicLinkCommand.Execute(null); return Task.CompletedTask; });
            menu.Items.Add(link);
        }
        if (_cloud.ProviderOf(account.Account) is ITrashProvider trash)
        {
            var empty = new MenuFlyoutItem { Text = $"{trash.TrashName}…", Icon = new FontIcon { Glyph = "" } };
            empty.Click += async (_, _) => await OpenTrashAsync(account);
            menu.Items.Add(empty);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(reconnect);
        menu.Items.Add(disconnect);
        return menu;
    }

    /// <summary>From the account's sidebar menu: shows the account in the active pane, then runs <paramref name="action"/> there.</summary>
    private async Task OnAccountPaneAsync(CloudAccountItem account, Func<FilePaneViewModel, Task> action)
    {
        ShowExplorer();
        var pane = ViewModel.ActivePane;
        if (pane.Location?.ProviderId != account.Account.ProviderId) await ViewModel.NavigateActiveAsync(_cloud.RootOf(account.Account));
        await action(pane);
        ViewFor(pane).FocusList();
    }

    /// <summary>From the account's sidebar menu: shows the account in the active pane, then its trash.</summary>
    private async Task OpenTrashAsync(CloudAccountItem account)
    {
        ShowExplorer();
        var pane = ViewModel.ActivePane;
        if (pane.Location?.ProviderId != account.Account.ProviderId) await ViewModel.NavigateActiveAsync(_cloud.RootOf(account.Account));
        if (pane.OpenTrashCommand.CanExecute(null)) pane.OpenTrashCommand.Execute(null);
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

    private TransfersView? TransfersHost { get; set; }

    private void UpdateTransfersBadge()
    {
        if (_transfersItem is null) return;
        int count = ViewModel.Operations.ActiveCount;
        _transfersItem.InfoBadge = count > 0 ? new InfoBadge { Value = count } : null;
    }

    private void OnViewTransfers(object sender, RoutedEventArgs e) => ShowTransfers(history: false);

    private void ShowTransfers(bool history)
    {
        if (TransfersHost is null)
        {
            TransfersHost = new TransfersView();
            ContentRoot.Children.Add(TransfersHost);
        }
        TransfersHost.Visibility = Visibility.Visible;
        ExplorerHost.Visibility = Visibility.Collapsed;
        if (SettingsHost is not null) SettingsHost.Visibility = Visibility.Collapsed;
        if (history) TransfersHost.ShowHistory(); else TransfersHost.ShowQueue();
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == TransfersTag);
    }

    // ------------------------------------------------------------------ upload / download

    private async void OnUploadFiles(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await UploadAsync(files.Select(f => f.Path));
    }

    private async void OnUploadFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await UploadAsync([folder.Path]);
    }

    private async Task UploadAsync(IEnumerable<string> paths)
    {
        var items = await ((ClipboardService)App.Services.GetService(typeof(ClipboardService))!).ResolveLocalAsync(paths);
        ViewModel.ActivePane.Transfer(OperationKind.Copy, "local", items);
    }

    private void OnDownloadDefault(object sender, RoutedEventArgs e) => ViewModel.DownloadSelection();

    private async void OnDownloadTo(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.DownloadSelection(folder.Path);
    }

    /// <summary>Folder picker for Settings (needs this window's handle).</summary>
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void ShowSettings()
    {
        if (TransfersHost is not null) TransfersHost.Visibility = Visibility.Collapsed;
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
        double width = Math.Floor(RootGrid.ActualWidth - pane - 1);
        if (width <= 0) return;
        if (width != ContentRoot.Width) ContentRoot.Width = width; // unchanged: skip re-laying out toolbar and lists
        // The CommandBar sizes itself to its commands and only overflows when told the room is short: cap it at the
        // glass bar's inner width (minus the 4 + 8 side margins and 2 + 2 padding) so it never runs past the panes' edge.
        Toolbar.MaxWidth = Math.Max(0, width - ToolbarGlass.Margin.Left - ToolbarGlass.Margin.Right
                                       - ToolbarGlass.Padding.Left - ToolbarGlass.Padding.Right - ToolbarGlass.BorderThickness.Left - ToolbarGlass.BorderThickness.Right);
        QueueToolbarJustify();
        SettingsHost?.SetAvailableWidth(width);
    }

    private bool _justifyQueued;

    /// <summary>Re-spreads the toolbar once layout has settled (coalesces a burst of resize / visibility changes).</summary>
    private void QueueToolbarJustify()
    {
        if (_justifyQueued) return;
        _justifyQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, JustifyToolbar);
    }

    /// <summary>
    /// Justifies the toolbar across the glass bar: the spare width is shared out over the group separators, so the
    /// first command sits at the left edge and the "More" button at the right edge, with the same inset on both sides.
    /// Runs again whenever the width changes (window, sidebar) or a command shows or hides (Upload / Download on a
    /// cloud folder), and after the CommandBar has moved commands into "More" on a narrow window.
    /// </summary>
    private void JustifyToolbar()
    {
        _justifyQueued = false;
        // Hidden behind Settings / Transfers: nothing to measure; ShowExplorer queues it again.
        if (ExplorerHost.Visibility != Visibility.Visible || double.IsInfinity(Toolbar.MaxWidth) || Toolbar.MaxWidth <= 0) return;

        var separators = Toolbar.PrimaryCommands.OfType<AppBarSeparator>().ToList();
        foreach (var separator in separators) separator.Margin = default;
        Toolbar.UpdateLayout(); // natural width, and the CommandBar's own overflow decision at that width

        var shown = separators.Where(s => s.Visibility == Visibility.Visible && !s.IsInOverflow).ToList();
        // Whole pixels only, minus one for rounding, so the bar never grows past the cap and pushes a command into "More".
        int spare = (int)Math.Floor(Toolbar.MaxWidth - Toolbar.ActualWidth) - 1;
        if (shown.Count == 0 || spare <= 0) return;

        for (int i = 0; i < shown.Count; i++)
        {
            int gap = spare / shown.Count + (i < spare % shown.Count ? 1 : 0); // remainder pixels go to the first gaps
            int left = gap / 2;
            shown[i].Margin = new Thickness(left, 0, gap - left, 0);
        }
    }

    private void ShowExplorer()
    {
        if (ExplorerHost.Visibility == Visibility.Visible) return;
        ExplorerHost.Visibility = Visibility.Visible;
        QueueToolbarJustify(); // the width may have changed while another page was showing
        if (SettingsHost is not null) SettingsHost.Visibility = Visibility.Collapsed;
        if (TransfersHost is not null) TransfersHost.Visibility = Visibility.Collapsed;
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
        SortFoldersFirst.IsChecked = pane.FoldersFirst;
    }

    private void OnFoldersFirstClick(object sender, RoutedEventArgs e) =>
        ViewModel.ActivePane.FoldersFirst = SortFoldersFirst.IsChecked;

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
        ViewHidden.IsChecked = ViewModel.ShowHiddenItems;
        ViewExtensions.IsChecked = ViewModel.ShowFileExtensions;
        ViewFolderSizes.IsChecked = ViewModel.AutoFolderSizes;
    }

    // ------------------------------------------------------------------ power tools (overflow menu, palette)

    private void OnPaletteClick(object sender, RoutedEventArgs e) => _ = ShowPaletteAsync();

    private void OnSelectByPatternClick(object sender, RoutedEventArgs e) => ViewModel.ActivePane.RequestDialog(PaneDialog.SelectByPattern);

    private void OnComparePanesClick(object sender, RoutedEventArgs e) => ComparePanes();

    private void ComparePanes()
    {
        var summary = ViewModel.ComparePanes();
        if (summary is null) ViewModel.ActivePane.ShowError("Turn on dual pane (Ctrl+Shift+D) and open the two folders to compare.");
        else ViewModel.ActivePane.ShowMessage(summary, isError: false);
    }

    private async Task ShowPaletteAsync()
    {
        ShowExplorer();
        await CommandPalette.ShowAsync(_dialogs, BuildPaletteCommands());
    }

    /// <summary>Every command of the app, for the palette. Commands read the active pane when they run.</summary>
    private List<PaletteCommand> BuildPaletteCommands()
    {
        FilePaneViewModel P() => ViewModel.ActivePane;
        var list = new List<PaletteCommand>();

        void Cmd(string title, string category, string? keys, string glyph, Func<bool> can, Action run, string keywords = "") =>
            list.Add(new PaletteCommand(title, category, keys, glyph, can, run) { Keywords = keywords });
        void Rc(string title, string category, string? keys, string glyph, Func<FilePaneViewModel, System.Windows.Input.ICommand> command, string keywords = "") =>
            Cmd(title, category, keys, glyph, () => command(P()).CanExecute(null), () => command(P()).Execute(null), keywords);
        void Focus() => DispatcherQueue.TryEnqueue(() => ViewFor(P()).FocusList());

        // File
        Rc("New folder", "File", "Ctrl+Shift+N", "", p => p.NewFolderCommand, "create directory");
        Rc("New text document", "File", "Ctrl+Alt+N", "", p => p.NewTextFileCommand, "create file txt");
        Rc("Rename", "File", "F2", "", p => p.RenameCommand);
        Rc("Rename many (batch rename)…", "File", "Ctrl+M", "", p => p.BatchRenameCommand, "multi rename regex counter replace");
        Rc("Duplicate", "File", null, "", p => p.DuplicateCommand, "copy make a copy");
        Rc("Delete to Recycle Bin", "File", "Del", "", p => p.DeleteCommand, "remove trash");
        Rc("Delete permanently", "File", "Shift+Del", "", p => p.DeletePermanentlyCommand, "remove erase");
        Rc("Trash (Recycle Bin / Rubbish bin): restore or empty…", "File", null, "", p => p.OpenTrashCommand, "rubbish bin recycle restore recover undelete empty clear purge");
        // Cloud
        Rc("Copy link (share)", "Cloud", null, "", p => p.CopyLinkCommand, "share public link url mega drive");
        Rc("Turn off link…", "Cloud", null, "", p => p.RemoveLinkCommand, "unshare stop sharing link");
        Rc("Star / remove star", "Cloud", null, "", p => p.ToggleStarCommand, "star starred favourite favorite");
        Rc("Hide / unhide (cloud)", "Cloud", null, "", p => p.ToggleCloudHiddenCommand, "sensitive hide mega");
        Rc("Versions…", "Cloud", null, "", p => p.ShowVersionsCommand, "history previous version restore revisions");
        Rc("Open a link someone shared…", "Cloud", null, "", p => p.OpenPublicLinkCommand, "import mega link save to my drive");
        Rc("Open in browser", "Cloud", null, "\uE774", p => p.OpenInBrowserCommand, "web google docs online drive.google.com");
        Rc("Share…", "Cloud", null, "\uE72D", p => p.ShareCommand, "share people email permissions access owner");
        Rc("Comments…", "Cloud", null, "\uE90A", p => p.ShowCommentsCommand, "comment reply resolve discussion");
        Rc("Activity…", "Cloud", null, "\uE7C4", p => p.ShowActivityCommand, "history who changed edited");
        Rc("Description…", "Cloud", null, "\uE70B", p => p.EditDescriptionCommand, "notes details info");
        Rc("Add shortcut to My Drive", "Cloud", null, "\uE71B", p => p.AddShortcutCommand, "shortcut link alias");
        Rc("Make a Google copy (convert)", "Cloud", null, "\uE8AB", p => p.ConvertToOnlineCommand, "convert docx xlsx pptx google docs sheets slides");
        Rc("Lock / unlock editing", "Cloud", null, "\uE72E", p => p.ToggleLockCommand, "read-only restrict content");
        Cmd("Starred", "Cloud", null, "\uE734", () => P().Provider is not null, () => _ = P().ShowStarredAsync(), "star starred favourites");
        Cmd("Largest files (storage)", "Cloud", null, "\uEDA2", () => P().Provider is not null, () => _ = P().ShowLargestFilesAsync(), "storage space size big quota clean up");
        Cmd("Recent files (last 30 days)", "Cloud", null, "", () => P().Provider is not null, () => _ = P().ShowRecentFilesAsync(), "recents recently changed added");
        Cmd("Photos & videos", "Cloud", null, "", () => P().Provider is not null, () => _ = P().ShowMediaAsync(), "media timeline pictures gallery");
        Rc("Compress to ZIP", "File", null, "", p => p.CompressCommand, "zip archive pack");
        Rc("Extract here", "File", null, "", p => p.ExtractHereCommand, "unzip archive unpack");
        Rc("Extract to folder", "File", null, "", p => p.ExtractToFolderCommand, "unzip archive unpack");
        Rc("Checksums (MD5, SHA-1, SHA-256)…", "File", null, "", p => p.ChecksumCommand, "hash verify");
        Rc("Toggle Hidden attribute", "File", null, "", p => p.ToggleHiddenAttributeCommand, "attributes hide");
        Rc("Toggle Read-only attribute", "File", null, "", p => p.ToggleReadOnlyAttributeCommand, "attributes lock protect");
        Rc("Open with…", "File", null, "", p => p.OpenWithCommand);
        Rc("Properties", "File", "Alt+Enter", "", p => p.ShowPropertiesCommand);

        // Edit
        Rc("Cut", "Edit", "Ctrl+X", "", p => p.CutCommand);
        Rc("Copy", "Edit", "Ctrl+C", "", p => p.CopyCommand);
        Rc("Paste", "Edit", "Ctrl+V", "", p => p.PasteCommand);
        Cmd("Undo", "Edit", "Ctrl+Z", "", () => ViewModel.UndoCommand.CanExecute(null), () => ViewModel.UndoCommand.Execute(null));
        Rc("Copy path", "Edit", "Ctrl+Shift+C", "", p => p.CopyPathCommand);
        Rc("Copy name", "Edit", null, "", p => p.CopyNameCommand);
        Cmd("Copy to other pane", "Edit", null, "", () => ViewModel.IsDualPane && P().HasCopyableSelection, () => ViewModel.TransferToOtherPane(P(), OperationKind.Copy));
        Cmd("Move to other pane", "Edit", null, "", () => ViewModel.IsDualPane && P().HasMovableSelection, () => ViewModel.TransferToOtherPane(P(), OperationKind.Move));

        // Select
        Cmd("Select all", "Select", "Ctrl+A", "", () => true, () => { P().SelectAllCommand.Execute(null); Focus(); });
        Cmd("Select none", "Select", "Ctrl+Shift+A", "", () => true, () => P().SelectNoneCommand.Execute(null));
        Cmd("Invert selection", "Select", "Ctrl+I", "", () => true, () => { P().InvertSelectionCommand.Execute(null); Focus(); });
        Cmd("Select by pattern…", "Select", "Num +", "", () => true, () => P().RequestDialog(PaneDialog.SelectByPattern), "wildcard filter");
        Cmd("Deselect by pattern…", "Select", "Num -", "", () => true, () => P().RequestDialog(PaneDialog.DeselectByPattern), "wildcard");
        Rc("Select same type", "Select", null, "", p => p.SelectSameTypeCommand, "extension");
        Cmd("Compare panes (select differences)", "Select", null, "", () => ViewModel.IsDualPane, ComparePanes, "diff sync newer");

        // Tabs
        Rc("New tab", "Tabs", "Ctrl+T", "", p => p.NewTabCommand);
        Rc("Open selection in new tab", "Tabs", "Middle-click", "", p => p.OpenSelectionInNewTabCommand);
        Cmd("Close tab", "Tabs", "Ctrl+W", "", () => P().HasTabs, () => P().CloseTabCommand.Execute(null));
        Cmd("Close other tabs", "Tabs", null, "", () => P().HasTabs, () => P().CloseOtherTabsCommand.Execute(null));
        Rc("Reopen closed tab", "Tabs", "Ctrl+Shift+T", "", p => p.ReopenClosedTabCommand);
        Cmd("Next tab", "Tabs", "Ctrl+Tab", "", () => P().HasTabs, () => P().NextTabCommand.Execute(null));
        Cmd("Previous tab", "Tabs", "Ctrl+Shift+Tab", "", () => P().HasTabs, () => P().PreviousTabCommand.Execute(null));

        // Navigate
        Rc("Back", "Navigate", "Alt+Left", "", p => p.GoBackCommand);
        Rc("Forward", "Navigate", "Alt+Right", "", p => p.GoForwardCommand);
        Rc("Up to parent folder", "Navigate", "Alt+Up", "", p => p.GoUpCommand);
        Rc("Refresh", "Navigate", "F5", "", p => p.RefreshCommand, "reload");
        Cmd("Type a path", "Navigate", "Ctrl+L", "", () => true, () => P().RequestAddressEdit(), "address bar go to");
        Cmd("Search", "Navigate", "Ctrl+F", "", () => P().CanSearch, () => P().RequestFilterFocus(), "find filter");
        Cmd("Advanced search…", "Navigate", "Ctrl+Shift+F", "", () => P().CanSearch, () => P().RequestAdvancedSearch(), "find filter");
        Rc("Pin / unpin to Favorites", "Navigate", null, "", p => p.ToggleFavoriteCommand, "bookmark star");
        Rc("Open in Terminal", "Navigate", "Ctrl+Alt+T", "", p => p.OpenTerminalCommand, "powershell cmd console shell");
        Rc("Show in File Explorer", "Navigate", null, "", p => p.RevealInExplorerCommand);
        Cmd("Open in other pane", "Navigate", null, "", () => true, () => P().RequestOpenInOtherPane());

        // View
        Cmd("Details view", "View", "Ctrl+Shift+6", "", () => true, () => P().ViewMode = FileViewMode.Details, "list");
        Cmd("Grid view", "View", "Ctrl+Shift+2", "", () => true, () => P().ViewMode = FileViewMode.Grid, "icons thumbnails");
        Cmd(ViewModel.ShowHiddenItems ? "Hide hidden items" : "Show hidden items", "View", "Ctrl+H", "", () => true, () => ViewModel.ShowHiddenItems = !ViewModel.ShowHiddenItems);
        Cmd(ViewModel.ShowFileExtensions ? "Hide file name extensions" : "Show file name extensions", "View", null, "", () => true, () => ViewModel.ShowFileExtensions = !ViewModel.ShowFileExtensions);
        Cmd(ViewModel.AutoFolderSizes ? "Stop calculating folder sizes automatically" : "Calculate folder sizes automatically", "View", null, "", () => true, () => ViewModel.AutoFolderSizes = !ViewModel.AutoFolderSizes);
        Rc("Calculate folder sizes now", "View", "Ctrl+Alt+S", "", p => p.CalculateFolderSizesCommand);
        Cmd("Dual pane on / off", "View", "Ctrl+Shift+D", "", () => true, () => ViewModel.ToggleDualPaneCommand.Execute(null), "split two panes");
        Cmd("Switch pane", "View", "F6", "", () => ViewModel.IsDualPane, () => ViewModel.SwitchPaneCommand.Execute(null));
        Cmd("Preview pane on / off", "View", "Alt+P", "", () => true, () => ViewModel.IsPreviewOpen = !ViewModel.IsPreviewOpen);
        foreach (var (label, field) in new[] { ("name", SortField.Name), ("date modified", SortField.Modified), ("type", SortField.Type), ("size", SortField.Size) })
            Cmd($"Sort by {label}", "View", null, "", () => true, () => P().SortField = field);
        Cmd("Sort ascending / descending", "View", null, "", () => true, () => P().SortDescending = !P().SortDescending, "reverse order");
        Cmd("Folders first on / off", "View", null, "", () => true, () => P().FoldersFirst = !P().FoldersFirst);

        // App
        Cmd("Transfer queue", "App", null, "", () => true, () => ShowTransfers(history: false), "downloads uploads");
        Cmd("Recent transfers", "App", null, "", () => true, () => ShowTransfers(history: true), "history");
        Cmd("Settings", "App", null, "", () => true, ShowSettings, "options preferences");
        Cmd("Add cloud account…", "App", null, "", () => true, () => _ = AddCloudAccountAsync(), "google drive dropbox onedrive");
        Cmd("Focus sidebar", "App", "Ctrl+1", "", () => true, FocusSidebar);

        // Go to: favorites, folders, drives, cloud accounts
        void Go(string name, StorageLocation location, string glyph) =>
            Cmd($"Go to {name}", "Go to", null, glyph, () => true, () => _ = NavigateAndFocusAsync(location), location.Path);
        foreach (var f in ViewModel.Favorites) Go(f.Name, new StorageLocation(f.ProviderId, f.Path), "");
        foreach (var k in ViewModel.KnownFolders) Go(k.Name, new StorageLocation("local", k.Path), k.Glyph);
        foreach (var d in ViewModel.Drives) Go(d.Name, new StorageLocation("local", d.Path), "");
        foreach (var a in _cloud.Accounts) Go(a.Name, _cloud.RootOf(a.Account), "");
        return list;
    }

    private async Task NavigateAndFocusAsync(StorageLocation location)
    {
        ShowExplorer();
        await ViewModel.NavigateActiveAsync(location);
        ViewFor(ViewModel.ActivePane).FocusList();
    }

    private void OnViewHiddenClick(object sender, RoutedEventArgs e) => ViewModel.ShowHiddenItems = ViewHidden.IsChecked;

    private void OnViewExtensionsClick(object sender, RoutedEventArgs e) => ViewModel.ShowFileExtensions = ViewExtensions.IsChecked;

    private void OnViewFolderSizesClick(object sender, RoutedEventArgs e) => ViewModel.AutoFolderSizes = ViewFolderSizes.IsChecked;

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

    private void OnAdvancedSearchAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.RequestAdvancedSearch());

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

    private void OnPaletteAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        _ = ShowPaletteAsync();
    }

    private void OnNewTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => RunAndFocus(a, p => p.NewTabCommand.Execute(null));

    private void OnCloseTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => RunAndFocus(a, p => p.CloseTabCommand.Execute(null));

    private void OnReopenTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => RunAndFocus(a, p => p.ReopenClosedTabCommand.Execute(null));

    private void OnNextTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => RunAndFocus(a, p => p.NextTabCommand.Execute(null));

    private void OnPreviousTabAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => RunAndFocus(a, p => p.PreviousTabCommand.Execute(null));

    private void OnBatchRenameAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus) Run(a, p => p.BatchRenameCommand.Execute(null));
    }

    private void OnSelectNoneAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus) Run(a, p => p.SelectNoneCommand.Execute(null));
    }

    private void OnInvertSelectionAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus) Run(a, p => p.InvertSelectionCommand.Execute(null));
    }

    private void OnNewFileAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.NewTextFileCommand.Execute(null));

    private void OnFolderSizesAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.CalculateFolderSizesCommand.Execute(null));

    private void OnTerminalAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.OpenTerminalCommand.Execute(null));

    private void OnEditorAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Run(a, p => p.OpenInEditorCommand.Execute(null));

    // ------------------------------------------------------------------ zoom (browser keys; Ctrl+wheel / pinch live in FilePane)

    private void AddZoomAccelerators()
    {
        const Windows.System.VirtualKey OemPlus = (Windows.System.VirtualKey)187, OemMinus = (Windows.System.VirtualKey)189; // the "=/+" and "-" keys beside Backspace
        foreach (var (key, step) in new[] { (OemPlus, 1), (Windows.System.VirtualKey.Add, 1), (OemMinus, -1), (Windows.System.VirtualKey.Subtract, -1), (Windows.System.VirtualKey.Number0, 0), (Windows.System.VirtualKey.NumberPad0, 0) })
        {
            var accelerator = new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = key };
            accelerator.Invoked += (_, a) =>
            {
                a.Handled = true;
                Zoom(step);
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>+1 / -1 one step, 0 back to 100 %, on the active pane.</summary>
    private void Zoom(int step)
    {
        var pane = ViewFor(ViewModel.ActivePane);
        if (step == 0) pane.ResetZoom();
        else pane.ZoomBy(step);
    }

    private void OnZoomInMenuClick(object sender, RoutedEventArgs e) => Zoom(1);

    private void OnZoomOutMenuClick(object sender, RoutedEventArgs e) => Zoom(-1);

    private void OnZoomResetMenuClick(object sender, RoutedEventArgs e) => Zoom(0);

    private void OnHiddenItemsAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (TextBoxHasFocus) return;
        a.Handled = true;
        ViewModel.ShowHiddenItems = !ViewModel.ShowHiddenItems;
        ViewModel.ActivePane.ShowMessage(ViewModel.ShowHiddenItems ? "Hidden items are shown." : "Hidden items are hidden.", isError: false);
    }

    /// <summary>Tab commands change the listing: keep the keyboard in the list afterwards.</summary>
    private void RunAndFocus(KeyboardAcceleratorInvokedEventArgs args, Action<FilePaneViewModel> action)
    {
        Run(args, action);
        if (args.Handled) DispatcherQueue.TryEnqueue(() => ViewFor(ViewModel.ActivePane).FocusList());
    }

    private void OnFocusSidebarAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        FocusSidebar();
    }

    private void FocusSidebar()
    {
        if (Nav.SelectedItem is NavigationViewItem selected) selected.Focus(FocusState.Keyboard);
        else if (Nav.MenuItems.FirstOrDefault() is NavigationViewItem first) first.Focus(FocusState.Keyboard);
    }
}
