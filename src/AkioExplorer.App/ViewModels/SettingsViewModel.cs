using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Settings;
using AkioExplorer.Infrastructure.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AkioExplorer.App.ViewModels;

/// <summary>Only settings that actually work today are exposed; transfer settings arrive with the transfer engine.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly FavoritesService _favorites;

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _defaultViewIndex;
    [ObservableProperty] private bool _dualPaneOnStartup;
    [ObservableProperty] private bool _showHiddenItems;
    [ObservableProperty] private bool _showFileExtensions;
    [ObservableProperty] private bool _autoFolderSizes;
    [ObservableProperty] private bool _restoreLastLocations;
    [ObservableProperty] private int _conflictBehaviorIndex;
    [ObservableProperty] private double _maxSimultaneousOperations;
    [ObservableProperty] private string _downloadFolderText = string.Empty;

    // Appearance
    [ObservableProperty] private int _backdropIndex;

    // Files and folders
    [ObservableProperty] private int _dateDisplayIndex;
    [ObservableProperty] private bool _decimalSizeUnits;
    [ObservableProperty] private bool _treatDotFilesAsHidden;
    [ObservableProperty] private bool _hideJunkFiles;
    [ObservableProperty] private bool _confirmRecycle;

    // Menus and apps
    [ObservableProperty] private int _editorIndex;
    [ObservableProperty] private int _terminalIndex;
    [ObservableProperty] private int _copyPathFormatIndex;
    [ObservableProperty] private bool _showShellMenuItems;
    [ObservableProperty] private string _hiddenShellMenuItems = string.Empty;

    // Windows integration
    [ObservableProperty] private bool _replaceFileExplorer;
    [ObservableProperty] private bool _contextMenuEntry;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _keepRunningInBackground;
    [ObservableProperty] private int _externalOpenIndex;
    [ObservableProperty] private bool _onPath;
    [ObservableProperty] private string _integrationMessage = string.Empty;

    // Quick search
    [ObservableProperty] private bool _quickSearchEnabled;
    [ObservableProperty] private string _quickSearchHotkey = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickSearchStatusNeedsAction))]
    private string _quickSearchStatus = string.Empty;
    private readonly GlobalHotkey _hotkey;

    /// <summary>Key taken by another app (warning) rather than paused on purpose (information).</summary>
    public bool QuickSearchStatusNeedsAction => _hotkey.StatusNeedsAction;

    private readonly IShellMenuService _shellMenus;
    private readonly ShellIntegration _integration = new();
    private IReadOnlyList<CodeEditor> _editors = [];
    private bool _loadingIntegration;

    public SettingsViewModel(ISettingsService settings, ThemeService theme, FavoritesService favorites, IShellMenuService shellMenus,
        GlobalHotkey hotkey)
    {
        _settings = settings;
        _theme = theme;
        _favorites = favorites;
        _shellMenus = shellMenus;
        _hotkey = hotkey;
        _quickSearchEnabled = settings.Current.QuickSearchEnabled;
        _quickSearchHotkey = settings.Current.QuickSearchHotkey;
        _quickSearchStatus = hotkey.Status;
        hotkey.StatusChanged += (_, _) => QuickSearchStatus = hotkey.Status;

        var s = settings.Current;
        _themeIndex = (int)s.Theme;
        _defaultViewIndex = (int)s.DefaultViewMode;
        _dualPaneOnStartup = s.DualPaneOnStartup;
        _showHiddenItems = s.ShowHiddenItems;
        _showFileExtensions = s.ShowFileExtensions;
        _autoFolderSizes = s.AutoFolderSizes;
        // The View menu toggles these too: keep the switches in sync.
        settings.Changed += (_, _) =>
        {
            ShowHiddenItems = settings.Current.ShowHiddenItems;
            ShowFileExtensions = settings.Current.ShowFileExtensions;
            AutoFolderSizes = settings.Current.AutoFolderSizes;
        };
        _restoreLastLocations = s.RestoreLastLocations;
        _conflictBehaviorIndex = (int)s.ConflictBehavior;
        _maxSimultaneousOperations = s.MaxSimultaneousOperations;
        _downloadFolderText = string.IsNullOrWhiteSpace(s.DownloadFolder) ? "Windows Downloads folder" : s.DownloadFolder;

        _backdropIndex = (int)s.Backdrop;
        _dateDisplayIndex = (int)s.DateDisplay;
        _decimalSizeUnits = s.DecimalSizeUnits;
        _treatDotFilesAsHidden = s.TreatDotFilesAsHidden;
        _hideJunkFiles = s.HideJunkFiles;
        _confirmRecycle = s.ConfirmRecycle;
        _terminalIndex = (int)s.Terminal;
        _copyPathFormatIndex = (int)s.CopyPathFormat;
        _showShellMenuItems = s.ShowShellMenuItems;
        _hiddenShellMenuItems = s.HiddenShellMenuItems;
        _keepRunningInBackground = s.KeepRunningInBackground;
        _externalOpenIndex = (int)s.ExternalOpenTarget;
        ReadIntegrationState();
        _ = LoadEditorsAsync();
    }

    // ------------------------------------------------------------------ appearance, files, menus

    partial void OnBackdropIndexChanged(int value) => Set(x => x.Backdrop = (WindowBackdrop)Math.Clamp(value, 0, 3), notify: true);

    partial void OnDateDisplayIndexChanged(int value) => Set(x => x.DateDisplay = (DateDisplay)Math.Clamp(value, 0, 4), notify: true);

    public int SizeUnitsIndex
    {
        get => DecimalSizeUnits ? 1 : 0;
        set => DecimalSizeUnits = value == 1;
    }

    partial void OnDecimalSizeUnitsChanged(bool value) => Set(x => x.DecimalSizeUnits = value, notify: true);

    partial void OnTreatDotFilesAsHiddenChanged(bool value) => Set(x => x.TreatDotFilesAsHidden = value, notify: true);

    partial void OnHideJunkFilesChanged(bool value) => Set(x => x.HideJunkFiles = value, notify: true);

    partial void OnConfirmRecycleChanged(bool value) => Set(x => x.ConfirmRecycle = value);

    partial void OnTerminalIndexChanged(int value) => Set(x => x.Terminal = (TerminalApp)Math.Clamp(value, 0, 3));

    partial void OnCopyPathFormatIndexChanged(int value) => Set(x => x.CopyPathFormat = (CopyPathFormat)Math.Clamp(value, 0, 2));

    partial void OnShowShellMenuItemsChanged(bool value) => Set(x => x.ShowShellMenuItems = value);

    partial void OnHiddenShellMenuItemsChanged(string value) => Set(x => x.HiddenShellMenuItems = value ?? string.Empty);

    private void Set(Action<AppSettings> apply, bool notify = false)
    {
        apply(_settings.Current);
        if (notify) _settings.NotifyChanged();
        _settings.Save();
    }

    /// <summary>"Automatic" first, then every editor found on this PC.</summary>
    public ObservableCollection<string> EditorNames { get; } = ["Automatic (VS Code first)"];

    private async Task LoadEditorsAsync()
    {
        _editors = await _shellMenus.GetCodeEditorsAsync();
        foreach (var editor in _editors) EditorNames.Add(editor.Name);
        int index = _editors.ToList().FindIndex(e => e.Id == _settings.Current.PreferredEditor);
        EditorIndex = index < 0 ? 0 : index + 1;
    }

    partial void OnEditorIndexChanged(int value)
    {
        if (value < 0) return;
        Set(x => x.PreferredEditor = value == 0 || value > _editors.Count ? string.Empty : _editors[value - 1].Id);
    }

    // ------------------------------------------------------------------ Windows integration

    public string ExecutablePath => ShellIntegration.ExecutablePath;

    private void ReadIntegrationState()
    {
        _loadingIntegration = true;
        try
        {
            ReplaceFileExplorer = _integration.IsDefaultFileManager;
            ContextMenuEntry = _integration.IsContextMenuInstalled;
            StartWithWindows = _integration.IsAutostart;
            OnPath = _integration.IsOnPath(ExecutablePath);
        }
        finally
        {
            _loadingIntegration = false;
        }
    }

    private void Integrate(Action action, string done)
    {
        if (_loadingIntegration) return;
        try
        {
            action();
            IntegrationMessage = done;
        }
        catch (Exception ex)
        {
            IntegrationMessage = "Windows didn't accept the change: " + ex.Message;
        }
        ReadIntegrationState();
    }

    partial void OnReplaceFileExplorerChanged(bool value)
    {
        if (_loadingIntegration) return;
        Integrate(
            () => _integration.SetDefaultFileManager(value, ExecutablePath),
            value ? "Folders, drives and Win+E now open AkioExplorer, also when it isn't running. \"Open in File Explorer\" stays in the Windows menu."
                  : "File Explorer (or your previous file manager) is back as the default.");
        // Win+E needs a small background process (Windows ignores the registry for the key); it outlives the app.
        if (_integration.IsDefaultFileManager) WinEAgent.EnsureRunning();
        else WinEAgent.Stop();
    }

    partial void OnContextMenuEntryChanged(bool value)
    {
        if (_loadingIntegration) return;
        Integrate(
            () => _integration.SetContextMenu(value, ExecutablePath),
            value ? "\"Open in AkioExplorer\" added to the Windows right-click menu." : "\"Open in AkioExplorer\" removed.");
        if (!_integration.IsDefaultFileManager) WinEAgent.Stop(); // removing the menu item also ends the replacement
    }

    partial void OnStartWithWindowsChanged(bool value) => Integrate(() =>
    {
        _integration.SetAutostart(value, ExecutablePath);
        if (value && !KeepRunningInBackground) KeepRunningInBackground = true; // autostart is only useful in the tray
    }, value ? "AkioExplorer starts hidden in the notification area when you sign in." : "AkioExplorer no longer starts with Windows.");

    partial void OnKeepRunningInBackgroundChanged(bool value)
    {
        Set(x => x.KeepRunningInBackground = value, notify: true);
        if (!value && QuickSearchEnabled) QuickSearchEnabled = false; // the hotkey dies with the window otherwise
    }

    partial void OnQuickSearchEnabledChanged(bool value)
    {
        Set(x => x.QuickSearchEnabled = value, notify: true);
        if (value && !KeepRunningInBackground) KeepRunningInBackground = true; // the hotkey only works while we run
    }

    /// <summary>The hotkey box got focus: let keys through to it instead of opening the popup.</summary>
    public void BeginHotkeyEdit() => _hotkey.Suspend();

    /// <summary>
    /// The hotkey box lost focus: keep the new hotkey when it parses ("Ctrl+Alt+K"), otherwise go back to the old one.
    /// </summary>
    public void EndHotkeyEdit()
    {
        if (HotkeyGesture.TryParse(QuickSearchHotkey, out var gesture))
        {
            QuickSearchHotkey = gesture.ToString();
            Set(x => x.QuickSearchHotkey = gesture.ToString(), notify: true);
        }
        else
        {
            QuickSearchHotkey = _settings.Current.QuickSearchHotkey;
        }
        _hotkey.Resume();
    }

    partial void OnExternalOpenIndexChanged(int value) => Set(x => x.ExternalOpenTarget = (ExternalOpenTarget)Math.Clamp(value, 0, 1));

    partial void OnOnPathChanged(bool value) => Integrate(
        () => _integration.SetOnPath(value, ExecutablePath),
        value ? "Open a new terminal and type: AkioExplorer ." : "Removed from PATH (new terminals only).");

    public ObservableCollection<FavoriteEntry> Favorites => _favorites.Items;

    public string Version => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");

    public string DataFolder => AppPaths.DataFolder;

    partial void OnThemeIndexChanged(int value)
    {
        _settings.Current.Theme = (AppTheme)value;
        _theme.Apply((AppTheme)value);
        _settings.Save();
    }

    partial void OnDefaultViewIndexChanged(int value)
    {
        _settings.Current.DefaultViewMode = (FileViewMode)value;
        _settings.Save();
    }

    partial void OnDualPaneOnStartupChanged(bool value)
    {
        _settings.Current.DualPaneOnStartup = value;
        _settings.Save();
    }

    partial void OnShowHiddenItemsChanged(bool value)
    {
        if (_settings.Current.ShowHiddenItems == value) return;
        _settings.Current.ShowHiddenItems = value;
        _settings.NotifyChanged(); // panes re-filter live
    }

    partial void OnShowFileExtensionsChanged(bool value)
    {
        if (_settings.Current.ShowFileExtensions == value) return;
        _settings.Current.ShowFileExtensions = value;
        _settings.NotifyChanged();
    }

    partial void OnAutoFolderSizesChanged(bool value)
    {
        if (_settings.Current.AutoFolderSizes == value) return;
        _settings.Current.AutoFolderSizes = value;
        _settings.NotifyChanged();
    }

    partial void OnRestoreLastLocationsChanged(bool value)
    {
        _settings.Current.RestoreLastLocations = value;
        _settings.Save();
    }

    partial void OnConflictBehaviorIndexChanged(int value)
    {
        _settings.Current.ConflictBehavior = (Core.Transfers.ConflictBehavior)Math.Clamp(value, 0, 3);
        _settings.Save();
    }

    partial void OnMaxSimultaneousOperationsChanged(double value)
    {
        if (double.IsNaN(value)) return;
        _settings.Current.MaxSimultaneousOperations = (int)Math.Clamp(Math.Round(value), 1, 8);
        _settings.Save();
    }

    public void SetDownloadFolder(string? path)
    {
        _settings.Current.DownloadFolder = path ?? string.Empty;
        DownloadFolderText = string.IsNullOrWhiteSpace(path) ? "Windows Downloads folder" : path;
        _settings.Save();
    }

    [RelayCommand]
    private void RemoveFavorite(FavoriteEntry entry) => _favorites.Remove(entry);

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\"") { UseShellExecute = true })?.Dispose();
    }
}
