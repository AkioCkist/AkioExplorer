using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Settings;
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

    public SettingsViewModel(ISettingsService settings, ThemeService theme, FavoritesService favorites)
    {
        _settings = settings;
        _theme = theme;
        _favorites = favorites;

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
    }

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
