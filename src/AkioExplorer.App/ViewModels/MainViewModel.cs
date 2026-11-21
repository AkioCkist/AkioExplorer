using System.Collections.ObjectModel;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AkioExplorer.App.ViewModels;

/// <summary>Shell state: the two panes, which one is active, sidebar data.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly LocalStorageProvider _local;
    private readonly UndoService _undo;
    private readonly IStorageProviderRegistry _registry;
    private DateTime _lastDriveRefresh = DateTime.MinValue;

    public MainViewModel(
        Func<FilePaneViewModel> paneFactory,
        ISettingsService settings,
        FavoritesService favorites,
        IKnownFoldersService knownFolders,
        LocalStorageProvider local,
        OperationsViewModel operations,
        UndoService undo,
        IStorageProviderRegistry registry)
    {
        _registry = registry;
        _settings = settings;
        _local = local;
        _undo = undo;
        Operations = operations;
        operations.OperationCompleted += OnOperationCompleted;
        undo.Changed += (_, _) => UndoCommand.NotifyCanExecuteChanged();
        Favorites = favorites.Items;
        KnownFolders = knownFolders.GetKnownFolders();

        LeftPane = paneFactory();
        RightPane = paneFactory();
        LeftPane.ActivationRequested += (_, _) => ActivePane = LeftPane;
        RightPane.ActivationRequested += (_, _) => ActivePane = RightPane;
        LeftPane.TransferToOtherPaneRequested += (_, kind) => TransferToOtherPane(LeftPane, kind);
        RightPane.TransferToOtherPaneRequested += (_, kind) => TransferToOtherPane(RightPane, kind);

        _activePane = LeftPane;
        LeftPane.IsActive = true;
    }

    public FilePaneViewModel LeftPane { get; }

    public OperationsViewModel Operations { get; }

    public FilePaneViewModel RightPane { get; }

    public ObservableCollection<FavoriteEntry> Favorites { get; }

    public IReadOnlyList<KnownFolder> KnownFolders { get; }

    public ObservableCollection<StorageItem> Drives { get; } = new();

    [ObservableProperty] private FilePaneViewModel _activePane;
    [ObservableProperty] private bool _isDualPane;

    /// <summary>Raised when keyboard focus should move into the (new) active pane.</summary>
    public event EventHandler<FilePaneViewModel>? FocusPaneRequested;

    public async Task InitializeAsync()
    {
        var s = _settings.Current;
        LeftPane.RestoreState(s.LeftPane);
        RightPane.RestoreState(s.RightPane);

        var left = s.RestoreLastLocations && _registry.Find(s.LeftPane.ProviderId) is not null
            ? new StorageLocation(s.LeftPane.ProviderId, s.LeftPane.Path)
            : Home;
        var leftTask = LeftPane.NavigateAsync(left);
        var drivesTask = RefreshDrivesAsync(force: true);

        if (s.DualPaneOnStartup) IsDualPane = true;

        await Task.WhenAll(leftTask, drivesTask);
    }

    public static StorageLocation Home => new(LocalStorageProvider.ProviderId, string.Empty);

    partial void OnActivePaneChanged(FilePaneViewModel? oldValue, FilePaneViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
    }

    partial void OnIsDualPaneChanged(bool value)
    {
        LeftPane.ShowActiveIndicator = value;
        RightPane.ShowActiveIndicator = value;

        if (value)
        {
            if (RightPane.Location is null)
            {
                var s = _settings.Current.RightPane;
                var target = _settings.Current.RestoreLastLocations && s.Path.Length > 0 && _registry.Find(s.ProviderId) is not null
                    ? new StorageLocation(s.ProviderId, s.Path)
                    : LeftPane.Location ?? Home;
                _ = RightPane.NavigateAsync(target);
            }
        }
        else
        {
            ActivePane = LeftPane;
        }
    }

    [RelayCommand]
    private void ToggleDualPane()
    {
        IsDualPane = !IsDualPane;
        if (IsDualPane) ActivePane = RightPane;
        FocusPaneRequested?.Invoke(this, ActivePane);
    }

    /// <summary>F6: move focus to the other pane.</summary>
    [RelayCommand]
    private void SwitchPane()
    {
        if (!IsDualPane) return;
        ActivePane = ActivePane == LeftPane ? RightPane : LeftPane;
        FocusPaneRequested?.Invoke(this, ActivePane);
    }

    public FilePaneViewModel OtherPane => ActivePane == LeftPane ? RightPane : LeftPane;

    /// <summary>Opens a folder in the other pane (switching to dual-pane if needed).</summary>
    public void OpenInOtherPane(StorageLocation location)
    {
        var source = ActivePane;
        if (!IsDualPane) IsDualPane = true;
        var other = source == LeftPane ? RightPane : LeftPane;
        _ = other.NavigateAsync(location);
    }

    public Task NavigateActiveAsync(StorageLocation location) => ActivePane.NavigateAsync(location);

    // ------------------------------------------------------------------ transfers

    /// <summary>Copies/moves the source pane's selection into the folder shown in the other pane.</summary>
    public void TransferToOtherPane(FilePaneViewModel source, OperationKind kind)
    {
        var target = source == LeftPane ? RightPane : LeftPane;
        if (!IsDualPane || target.Location is null)
        {
            source.ShowError("Turn on dual pane (Ctrl+Shift+D) and open the destination folder in the other pane.");
            return;
        }
        if (source.Provider is null || source.SelectedItems.Count == 0) return;
        if (!target.CanWriteHere)
        {
            source.ShowError("Open a drive or folder in the other pane first.");
            return;
        }

        target.Transfer(kind, source.Provider.Id, source.SelectedItems.Select(s => s.Item).ToList());
    }

    /// <summary>Copy/move the active pane's selection to a folder chosen in a picker.</summary>
    public void TransferSelectionTo(OperationKind kind, StorageLocation destination)
    {
        var source = ActivePane;
        if (source.Provider is null || source.SelectedItems.Count == 0) return;
        var items = source.SelectedItems.Select(s => s.Item).ToList();
        var pane = LeftPane.Location?.Equals(destination) == true ? LeftPane
            : RightPane.Location?.Equals(destination) == true ? RightPane
            : source;
        pane.Transfer(kind, source.Provider.Id, items, destination.Path);
    }

    private void OnOperationCompleted(object? sender, FileOperation op)
    {
        if (op.CreatedPaths.Count == 0) return;
        foreach (var pane in new[] { LeftPane, RightPane })
        {
            if (pane.Location?.Equals(op.Destination) == true) pane.SelectWhenListed(op.CreatedPaths);
        }
    }

    private bool CanUndo() => _undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        try
        {
            string? what = await _undo.UndoAsync();
            if (what is not null) ActivePane.ShowMessage($"Undone: {what}", isError: false);
        }
        catch (StorageException ex)
        {
            ActivePane.ShowError($"Couldn't undo: {ex.Message}");
        }
    }

    public async Task RefreshDrivesAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastDriveRefresh < TimeSpan.FromSeconds(5)) return;
        _lastDriveRefresh = DateTime.UtcNow;

        var drives = await _local.GetDrivesAsync(CancellationToken.None);
        bool changed = drives.Count != Drives.Count || drives.Where((d, i) => d.Path != Drives[i].Path || d.Name != Drives[i].Name).Any();
        if (!changed) return;

        Drives.Clear();
        foreach (var d in drives) Drives.Add(d);
    }

    public void SaveState()
    {
        var s = _settings.Current;
        s.LeftPane = LeftPane.CaptureState();
        if (RightPane.Location is not null) s.RightPane = RightPane.CaptureState();
        _settings.Save();
    }
}
