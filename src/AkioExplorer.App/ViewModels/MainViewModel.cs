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
    private readonly IFileOperationService _operations;
    private readonly IShellActions _shell;
    private DateTime _lastDriveRefresh = DateTime.MinValue;

    public MainViewModel(
        Func<FilePaneViewModel> paneFactory,
        ISettingsService settings,
        FavoritesService favorites,
        IKnownFoldersService knownFolders,
        LocalStorageProvider local,
        OperationsViewModel operations,
        UndoService undo,
        IStorageProviderRegistry registry,
        IFileOperationService operationsService,
        IShellActions shell)
    {
        _registry = registry;
        _operations = operationsService;
        _shell = shell;
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
        LeftPane.OpenRemoteRequested += (_, item) => OpenRemoteFile(LeftPane, item);
        RightPane.OpenRemoteRequested += (_, item) => OpenRemoteFile(RightPane, item);

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
                var target = _settings.Current.RestoreLastLocations && (s.Path.Length > 0 || s.ProviderId != "local") && _registry.Find(s.ProviderId) is not null
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

    /// <summary>Preview pane (Alt+P, File Explorer's shortcut). Remembered across sessions.</summary>
    public bool IsPreviewOpen
    {
        get => _settings.Current.ShowPreviewPane;
        set
        {
            if (_settings.Current.ShowPreviewPane == value) return;
            _settings.Current.ShowPreviewPane = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public double PreviewWidth
    {
        get => _settings.Current.PreviewPaneWidth;
        set
        {
            _settings.Current.PreviewPaneWidth = value;
            _settings.Save();
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
        if (!_operations.CanTransfer(source.Provider.Id, destination.ProviderId))
        {
            source.ShowError("Items can't be copied to that location.");
            return;
        }
        _operations.Enqueue(kind, source.Provider.Id, source.SelectedItems.Select(s => s.Item).ToList(), destination);
    }

    /// <summary>Download folder from Settings, or the Windows Downloads folder.</summary>
    public string DownloadFolder
    {
        get
        {
            string configured = _settings.Current.DownloadFolder;
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            return KnownFolders.FirstOrDefault(k => k.Name == "Downloads")?.Path
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public void DownloadSelection(string? folder = null)
    {
        string target = folder ?? DownloadFolder;
        Directory.CreateDirectory(target);
        TransferSelectionTo(OperationKind.Copy, new StorageLocation("local", target));
    }

    /// <summary>
    /// Double-clicking a cloud file downloads it to a private cache and opens it with its Windows app.
    /// (A read-only working copy: edits aren't uploaded back automatically.)
    /// </summary>
    private void OpenRemoteFile(FilePaneViewModel pane, StorageItem item)
    {
        if (pane.Provider is null) return;
        string cache = Path.Combine(Infrastructure.Settings.AppPaths.DataFolder, "Cache", "Open",
            string.Concat(pane.Provider.Id.Where(char.IsLetterOrDigit)));
        Directory.CreateDirectory(cache);

        var op = _operations.Enqueue(OperationKind.Copy, pane.Provider.Id, [item], new StorageLocation("local", cache),
            new TransferOptions { Conflict = ConflictResolution.Replace, Internal = true });
        op.Finished += (_, _) =>
        {
            if (op.Status != OperationStatus.Completed || op.CreatedPaths.Count == 0) return;
            string path = op.CreatedPaths[0];
            try
            {
                _shell.Open(path);
            }
            catch (StorageException ex)
            {
                RemoteOpenFailed?.Invoke(this, (pane, ex.Message));
            }
        };
    }

    public event EventHandler<(FilePaneViewModel Pane, string Message)>? RemoteOpenFailed;

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
