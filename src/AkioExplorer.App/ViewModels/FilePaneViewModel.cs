using System.Collections.ObjectModel;
using System.Diagnostics;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace AkioExplorer.App.ViewModels;

public enum NavigationKind
{
    New,
    Back,
    Forward,
    Up,
    Refresh,
}

/// <summary>
/// One independent file browser (address, history, listing, selection). Works against any <see cref="IStorageProvider"/>,
/// so the same pane browses local disks and cloud accounts. All I/O is async; the UI thread only touches view models.
/// </summary>
public sealed partial class FilePaneViewModel : ObservableObject, IDisposable
{
    private const int LoadingIndicatorDelayMs = 250;
    private const int WatchDebounceMs = 300;
    private const int MaxSearchResults = 20_000;

    private readonly IStorageProviderRegistry _registry;
    private readonly IListingCache _cache;
    private readonly IconService _icons;
    private readonly IShellIconExtractor _shell;
    private readonly IShellActions _shellActions;
    private readonly ISettingsService _settings;
    private readonly FavoritesService _favorites;
    private readonly DialogService _dialogs;
    private readonly IFileOperationService _operations;
    private readonly ClipboardService _clipboard;
    private readonly UndoService _undo;
    private readonly HashSet<string> _pendingSelection = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<FilePaneViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _watchDebounce;
    private readonly DispatcherQueueTimer _loadingDelay;

    private readonly Stack<StorageLocation> _back = new();
    private readonly Stack<StorageLocation> _forward = new();
    private readonly List<FileItemViewModel> _selected = new();
    private List<FileItemViewModel> _all = new();
    private Dictionary<string, FileItemViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private IDisposable? _watcher;
    private int _loadVersion;
    private bool _suppressFilter;
    private string _searchQuery = string.Empty;

    public FilePaneViewModel(
        IStorageProviderRegistry registry,
        IListingCache cache,
        IconService icons,
        IShellIconExtractor shell,
        IShellActions shellActions,
        ISettingsService settings,
        FavoritesService favorites,
        DialogService dialogs,
        IFileOperationService operations,
        ClipboardService clipboard,
        UndoService undo,
        DispatcherQueue dispatcher,
        ILogger<FilePaneViewModel> logger)
    {
        _registry = registry;
        _cache = cache;
        _icons = icons;
        _shell = shell;
        _shellActions = shellActions;
        _settings = settings;
        _favorites = favorites;
        _dialogs = dialogs;
        _operations = operations;
        _clipboard = clipboard;
        _undo = undo;
        clipboard.Changed += (_, _) => OnClipboardChanged();
        _logger = logger;

        _dispatcher = dispatcher;
        _watchDebounce = dispatcher.CreateTimer();
        _watchDebounce.Interval = TimeSpan.FromMilliseconds(WatchDebounceMs);
        _watchDebounce.IsRepeating = false;
        _watchDebounce.Tick += (_, _) => _ = RefreshSilentlyAsync();

        _loadingDelay = dispatcher.CreateTimer();
        _loadingDelay.Interval = TimeSpan.FromMilliseconds(LoadingIndicatorDelayMs);
        _loadingDelay.IsRepeating = false;
        _loadingDelay.Tick += (_, _) => IsLoading = true;

        _viewMode = settings.Current.DefaultViewMode;
        settings.Changed += (_, _) => ApplyView(replace: false);
    }

    // ------------------------------------------------------------------ state

    public BulkObservableCollection<FileItemViewModel> Items { get; } = new();

    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = new();

    public ColumnLayout Columns { get; } = new();

    public IStorageProvider? Provider { get; private set; }

    public IReadOnlyList<FileItemViewModel> SelectedItems => _selected;

    [ObservableProperty] private StorageLocation? _location;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _showActiveIndicator;
    [ObservableProperty] private bool _isErrorOpen;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isMessageError = true;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private FileViewMode _viewMode;
    [ObservableProperty] private SortField _sortField = SortField.Name;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _selectionText = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = string.Empty;

    public bool IsDetailsView => ViewMode == FileViewMode.Details;

    public bool IsGridView => ViewMode == FileViewMode.Grid;

    public string SecondaryColumnHeader => IsSearchMode ? "Folder" : "Type";

    public string FilterPlaceholder => Location is null || Provider is null
        ? "Search"
        : $"Search {Provider.GetDisplayName(Location.Path)}";

    public bool IsFavorite => Location is not null && _favorites.Contains(Location);

    /// <summary>Raised when the user interacts with the pane, so the shell can make it the active pane.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Asks the view to select these items, scroll the first into view and focus it.</summary>
    public event EventHandler<IReadOnlyList<FileItemViewModel>>? SelectionRequested;

    public event EventHandler<FileItemViewModel>? RenameRequested;

    public event EventHandler? FocusListRequested;

    public event EventHandler? AddressEditRequested;

    public event EventHandler? FilterFocusRequested;

    public event EventHandler<StorageLocation>? OpenInOtherPaneRequested;

    public void RequestOpenInOtherPane()
    {
        if (Provider is null) return;
        var target = _selected.FirstOrDefault(s => s.IsContainer);
        var location = target is not null ? new StorageLocation(Provider.Id, target.Path) : Location;
        if (location is not null) OpenInOtherPaneRequested?.Invoke(this, location);
    }

    /// <summary>Address bar autocomplete: sub-folders of the typed parent whose name starts with the typed prefix.</summary>
    public async Task<IReadOnlyList<string>> GetPathSuggestionsAsync(string text, CancellationToken cancellationToken)
    {
        if (Provider is null || string.IsNullOrWhiteSpace(text)) return [];
        int cut = text.LastIndexOfAny(['\\', '/']);
        if (cut < 0) return [];

        string parentText = text[..(cut + 1)];
        string prefix = text[(cut + 1)..];
        string? parent = Provider.TryNormalizePath(parentText);
        if (string.IsNullOrEmpty(parent)) return [];

        var results = new List<string>();
        try
        {
            await foreach (var page in Provider.ListAsync(parent, cancellationToken))
            {
                foreach (var item in page)
                {
                    if (item.IsContainer && !item.IsHidden && item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                        results.Add(item.Path);
                }
            }
        }
        catch (Exception ex) when (ex is StorageException or OperationCanceledException)
        {
            return [];
        }

        results.Sort(NaturalStringComparer.Instance);
        return results.Take(12).ToList();
    }

    public void RequestActivation() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    public void RequestAddressEdit() => AddressEditRequested?.Invoke(this, EventArgs.Empty);

    public void RequestFilterFocus() => FilterFocusRequested?.Invoke(this, EventArgs.Empty);

    public void RequestListFocus() => FocusListRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ navigation

    public async Task NavigateAsync(StorageLocation target, NavigationKind kind = NavigationKind.New)
    {
        var provider = _registry.Find(target.ProviderId);
        if (provider is null)
        {
            ShowError("That storage account isn't connected any more.");
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        int version = ++_loadVersion;
        var previous = Location;
        bool committed = false;
        bool fromCache = false;
        if (kind != NavigationKind.Refresh) IsErrorOpen = false;

        if (kind != NavigationKind.Refresh && _cache.TryGet(target, out var cached))
        {
            Commit(provider, target, kind);
            committed = fromCache = true;
            ApplyListing(cached, replace: true, focusPath: previous?.Path);
        }

        _loadingDelay.Start();
        var collected = new List<StorageItem>();
        int pages = 0;
        try
        {
            await foreach (var page in provider.ListAsync(target.Path, ct))
            {
                if (version != _loadVersion) return;
                collected.AddRange(page);
                pages++;
                if (!committed)
                {
                    Commit(provider, target, kind);
                    committed = true;
                    ApplyListing(collected, replace: true, focusPath: previous?.Path);
                }
            }

            if (version != _loadVersion) return;

            if (!committed)
            {
                Commit(provider, target, kind);
                ApplyListing(collected, replace: true, focusPath: previous?.Path);
            }
            else if (fromCache || kind == NavigationKind.Refresh)
            {
                ApplyListing(collected, replace: false);
            }
            else if (pages > 1)
            {
                ApplyListing(collected, replace: true);
            }

            _cache.Set(target, collected.ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (StorageException ex)
        {
            if (version != _loadVersion) return;
            _logger.LogInformation("Navigation to {Path} failed: {Message}", target.Path, ex.Message);
            _cache.Invalidate(target);
            ShowStorageError(ex, target.ProviderId);

            if (Location is null)
            {
                // First navigation failed (remembered folder deleted, cloud sign-in expired…): fall back to the provider
                // root, and from a failing root to This PC, so the pane is never left empty.
                var fallback = target.Path != provider.RootPath
                    ? new StorageLocation(provider.Id, provider.RootPath)
                    : provider.Id != "local" ? new StorageLocation("local", string.Empty) : null;
                if (fallback is not null)
                {
                    await NavigateAsync(fallback);
                    ShowStorageError(ex, target.ProviderId);
                }
            }
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            _logger.LogError(ex, "Unexpected error listing {Path}", target.Path);
            ShowError($"Couldn't open this folder: {ex.Message}");
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loadingDelay.Stop();
                IsLoading = false;
                UpdateStatus();
            }
        }
    }

    private void Commit(IStorageProvider provider, StorageLocation target, NavigationKind kind)
    {
        var current = Location;
        if (current is not null && !current.Equals(target))
        {
            switch (kind)
            {
                case NavigationKind.New:
                case NavigationKind.Up:
                    _back.Push(current);
                    _forward.Clear();
                    break;
                case NavigationKind.Back:
                    if (_back.Count > 0) _back.Pop();
                    _forward.Push(current);
                    break;
                case NavigationKind.Forward:
                    if (_forward.Count > 0) _forward.Pop();
                    _back.Push(current);
                    break;
            }
        }

        bool locationChanged = current is null || !current.Equals(target);
        Provider = provider;
        Location = target;

        if (IsSearchMode) ExitSearch(applyView: false);
        if (locationChanged)
        {
            _suppressFilter = true;
            FilterText = string.Empty;
            _suppressFilter = false;
            _selected.Clear();

            Breadcrumb.Clear();
            foreach (var segment in provider.GetBreadcrumb(target.Path)) Breadcrumb.Add(segment);

            _watcher?.Dispose();
            _watcher = provider.Watch(target.Path, OnWatchedFolderChanged);
        }

        Title = provider.GetDisplayName(target.Path);
        OnPropertyChanged(nameof(FilterPlaceholder));
        OnPropertyChanged(nameof(IsFavorite));
        UpdateCommandStates();
    }

    private void OnWatchedFolderChanged()
    {
        // FileSystemWatcher thread → UI thread, debounced so a burst of changes costs one refresh.
        _dispatcher.TryEnqueue(() =>
        {
            _watchDebounce.Stop();
            _watchDebounce.Start();
        });
    }

    private async Task RefreshSilentlyAsync()
    {
        if (Location is not { } location || Provider is not { } provider || IsLoading) return;
        int version = _loadVersion;
        var ct = _loadCts?.Token ?? CancellationToken.None;
        try
        {
            var collected = new List<StorageItem>();
            await foreach (var page in provider.ListAsync(location.Path, ct)) collected.AddRange(page);
            if (version != _loadVersion || !location.Equals(Location)) return;
            ApplyListing(collected, replace: false);
            _cache.Set(location, collected.ToArray());
        }
        catch (OperationCanceledException)
        {
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            if (version != _loadVersion) return;
            // The folder we are showing was deleted or renamed elsewhere: go to the closest existing parent.
            _cache.Invalidate(location);
            if (provider.GetParentPath(location.Path) is { } parent)
                await NavigateAsync(new StorageLocation(provider.Id, parent), NavigationKind.Up);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent refresh failed");
        }
    }

    public bool CanGoBack => _back.Count > 0 || IsSearchMode;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => Location is not null && Provider?.GetParentPath(Location.Path) is not null;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (IsSearchMode)
        {
            ExitSearch();
            return;
        }
        if (_back.Count > 0) await NavigateAsync(_back.Peek(), NavigationKind.Back);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        if (_forward.Count > 0) await NavigateAsync(_forward.Peek(), NavigationKind.Forward);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task GoUpAsync()
    {
        if (Location is null || Provider is null) return;
        if (Provider.GetParentPath(Location.Path) is { } parent)
            await NavigateAsync(new StorageLocation(Provider.Id, parent), NavigationKind.Up);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Location is null) return;
        if (IsSearchMode)
        {
            await StartSearchAsync();
            return;
        }
        await NavigateAsync(Location, NavigationKind.Refresh);
    }

    /// <summary>Address bar entry. Accepts folders, files (opens parent and selects it) and env variables.</summary>
    public async Task NavigateToTypedPathAsync(string text)
    {
        if (Provider is null) return;
        string? path = Provider.TryNormalizePath(text);
        if (path is null)
        {
            ShowError($"Can't find \"{text.Trim()}\". Check the spelling and try again.");
            return;
        }

        if (path.Length == 0)
        {
            await NavigateAsync(new StorageLocation(Provider.Id, path));
            return;
        }

        var item = await Provider.GetItemAsync(path, CancellationToken.None);
        if (item is null)
        {
            ShowError($"Can't find \"{text.Trim()}\". Check the spelling and try again.");
            return;
        }

        if (item.IsContainer)
        {
            await NavigateAsync(new StorageLocation(Provider.Id, item.Path));
        }
        else if (Provider.GetParentPath(item.Path) is { } parent)
        {
            await NavigateAsync(new StorageLocation(Provider.Id, parent));
            if (_byPath.TryGetValue(item.Path, out var vm)) SelectionRequested?.Invoke(this, [vm]);
        }
    }

    // ------------------------------------------------------------------ listing → view

    private FileItemViewModel CreateItem(StorageItem item) =>
        new(item, _icons, SecondaryTextFor(item), Columns) { IsCut = _clipboard.CutPaths.Contains(item.Path) };

    private void OnClipboardChanged()
    {
        var cut = _clipboard.CutPaths;
        foreach (var vm in _all) vm.IsCut = cut.Contains(vm.Path);
        PasteCommand.NotifyCanExecuteChanged();
    }

    private string SecondaryTextFor(StorageItem item) => item is { Kind: StorageItemKind.Drive, TotalSize: { } total }
        ? $"{_shell.GetTypeName(item)} · {Formatting.FormatSize(total)}"
        : _shell.GetTypeName(item);

    private void ApplyListing(IReadOnlyList<StorageItem> items, bool replace, string? focusPath = null)
    {
        var newAll = new List<FileItemViewModel>(items.Count);
        var newMap = new Dictionary<string, FileItemViewModel>(items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (newMap.ContainsKey(item.Path)) continue;
            if (_byPath.TryGetValue(item.Path, out var vm))
            {
                vm.Update(item);
                vm.SecondaryText = SecondaryTextFor(item);
            }
            else
            {
                vm = CreateItem(item);
            }
            newAll.Add(vm);
            newMap[item.Path] = vm;
        }

        SortList(newAll);
        _all = newAll;
        _byPath = newMap;
        ApplyView(replace);

        if (focusPath is not null && newMap.TryGetValue(focusPath, out var focus))
            SelectionRequested?.Invoke(this, [focus]);

        TrySelectPending();
    }

    private void SortList(List<FileItemViewModel> list)
    {
        var comparer = new StorageItemComparer(new SortOptions(SortField, SortDescending), _shell.GetTypeName);
        list.Sort((a, b) => comparer.Compare(a.Item, b.Item));
    }

    private bool IsVisible(FileItemViewModel vm)
    {
        if (vm.Item.IsHidden && !_settings.Current.ShowHiddenItems) return false;
        return FilterText.Length == 0 || vm.Name.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplyView(bool replace)
    {
        if (IsSearchMode) return;

        var visible = new List<FileItemViewModel>(_all.Count);
        foreach (var vm in _all)
        {
            if (IsVisible(vm)) visible.Add(vm);
        }

        if (replace)
        {
            var keep = _selected.Where(visible.Contains).ToList();
            Items.ReplaceAll(visible);
            if (keep.Count > 0) SelectionRequested?.Invoke(this, keep);
        }
        else
        {
            ListDiff.Apply(Items, visible, i => i.Path, s => s.Path, s => s, move: (from, to) => Items.Move(from, to));
        }

        UpdateStatus();
    }

    partial void OnFilterTextChanged(string value)
    {
        if (_suppressFilter) return;
        if (IsSearchMode) ExitSearch();
        else ApplyView(replace: false);
    }

    partial void OnSortFieldChanged(SortField value) => Resort();

    partial void OnSortDescendingChanged(bool value) => Resort();

    partial void OnViewModeChanged(FileViewMode value)
    {
        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsGridView));
    }

    partial void OnIsSearchModeChanged(bool value)
    {
        OnPropertyChanged(nameof(SecondaryColumnHeader));
        UpdateCommandStates();
    }

    partial void OnIsLoadingChanged(bool value) => UpdateStatus();

    private void Resort()
    {
        if (IsSearchMode)
        {
            var results = Items.ToList();
            SortList(results);
            Items.ReplaceAll(results);
            return;
        }

        SortList(_all);
        ApplyView(replace: true);
    }

    [RelayCommand]
    private void SortBy(SortField field)
    {
        if (SortField == field)
        {
            SortDescending = !SortDescending;
            return;
        }

        // Newest / largest first is what people want when they sort by date or size.
        _sortDescending = field is SortField.Modified or SortField.Size;
        OnPropertyChanged(nameof(SortDescending));
        SortField = field;
    }

    [RelayCommand]
    private void SetViewMode(FileViewMode mode) => ViewMode = mode;

    // ------------------------------------------------------------------ selection & status

    public void UpdateSelection(IEnumerable<FileItemViewModel> selected)
    {
        _selected.Clear();
        _selected.AddRange(selected);
        UpdateSelectionText();
        UpdateCommandStates();
    }

    private void UpdateStatus()
    {
        if (IsSearchMode)
        {
            StatusText = IsSearching ? $"Searching… {Items.Count:N0} found" : $"{Items.Count:N0} results";
            IsEmpty = Items.Count == 0 && !IsSearching;
            EmptyText = $"No results for \"{_searchQuery}\"";
        }
        else
        {
            int total = _settings.Current.ShowHiddenItems ? _all.Count : _all.Count(v => !v.Item.IsHidden);
            StatusText = FilterText.Length > 0
                ? $"{Items.Count:N0} of {total:N0} items"
                : $"{Items.Count:N0} {(Items.Count == 1 ? "item" : "items")}";
            IsEmpty = Items.Count == 0 && !IsLoading && Location is not null;
            EmptyText = FilterText.Length > 0
                ? $"No items match \"{FilterText}\". Press Enter to search subfolders."
                : "This folder is empty";
        }
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        int count = _selected.Count;
        if (count == 0)
        {
            SelectionText = string.Empty;
            return;
        }

        long bytes = 0;
        bool anyFile = false;
        foreach (var vm in _selected)
        {
            if (vm.Item is { Kind: StorageItemKind.File, Size: { } size })
            {
                bytes += size;
                anyFile = true;
            }
        }

        string text = count == 1 ? "1 item selected" : $"{count:N0} items selected";
        SelectionText = anyFile ? $"{text}  ·  {Formatting.FormatSize(bytes)}" : text;
    }

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        RequestCopyToOtherPaneCommand.NotifyCanExecuteChanged();
        RequestMoveToOtherPaneCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanWriteHere));
        OnPropertyChanged(nameof(HasCopyableSelection));
        OnPropertyChanged(nameof(HasMovableSelection));
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
        OpenWithCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ open

    private bool HasSelection() => _selected.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenSelectedAsync()
    {
        var targets = _selected.ToList();
        var folder = targets.FirstOrDefault(t => t.IsContainer);
        if (targets.Count == 1 || folder is not null && targets.All(t => t.IsContainer))
        {
            await OpenAsync(folder ?? targets[0]);
            return;
        }

        foreach (var file in targets.Where(t => !t.IsContainer).Take(16)) await OpenAsync(file);
    }

    public async Task OpenAsync(FileItemViewModel vm)
    {
        if (Provider is null) return;
        if (vm.IsContainer)
        {
            await NavigateAsync(new StorageLocation(Provider.Id, vm.Path));
            return;
        }

        if ((Provider.Capabilities & StorageCapabilities.OpenWithShell) == 0)
        {
            ShowError("Opening cloud files directly will be available with the transfer engine. Download the file first.");
            return;
        }

        try
        {
            await Task.Run(() => _shellActions.Open(vm.Path));
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
    }

    private bool CanOpenWith() => _selected.Count == 1 && !_selected[0].IsContainer && Supports(StorageCapabilities.OpenWithShell);

    [RelayCommand(CanExecute = nameof(CanOpenWith))]
    private void OpenWith()
    {
        if (CanOpenWith()) _shellActions.OpenWith(_selected[0].Path);
    }

    // ------------------------------------------------------------------ create / rename / delete

    private bool Supports(StorageCapabilities capability) => Provider is not null && (Provider.Capabilities & capability) != 0;

    private bool IsAtRoot => Location is null || Provider is null || Location.Path == Provider.RootPath;

    private bool CanCreateFolder() => Supports(StorageCapabilities.CreateFolder) && !IsAtRoot && !IsSearchMode;

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private async Task NewFolderAsync()
    {
        if (Provider is null || Location is null) return;
        var names = new HashSet<string>(_all.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        string name = FileNameValidator.GetUniqueName("New folder", names);

        try
        {
            var item = await Provider.CreateFolderAsync(Location.Path, name, CancellationToken.None);
            if (FilterText.Length > 0)
            {
                _suppressFilter = true;
                FilterText = string.Empty;
                _suppressFilter = false;
            }

            if (!_byPath.TryGetValue(item.Path, out var vm))
            {
                vm = CreateItem(item);
                _byPath[item.Path] = vm;
                _all.Add(vm);
                SortList(_all);
            }

            ApplyView(replace: false);
            _cache.Invalidate(Location);
            SelectionRequested?.Invoke(this, [vm]);
            var provider = Provider;
            _undo.Push("New folder", () => provider.DeleteAsync([vm.Path], permanent: false, CancellationToken.None));
            BeginRename(vm);
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
    }

    private bool CanRename() => _selected.Count == 1 && Supports(StorageCapabilities.Rename) && !_selected[0].IsDrive;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        if (CanRename()) BeginRename(_selected[0]);
    }

    public void BeginRename(FileItemViewModel vm)
    {
        if (!Supports(StorageCapabilities.Rename) || vm.IsDrive) return;
        foreach (var other in Items.Where(i => i.IsRenaming && i != vm)) other.IsRenaming = false;
        vm.EditName = vm.Name;
        vm.IsRenaming = true;
        RenameRequested?.Invoke(this, vm);
    }

    public void CancelRename(FileItemViewModel vm)
    {
        if (!vm.IsRenaming) return;
        vm.IsRenaming = false;
        SelectionRequested?.Invoke(this, [vm]);
    }

    public async Task CommitRenameAsync(FileItemViewModel vm)
    {
        if (!vm.IsRenaming || Provider is null) return;
        vm.IsRenaming = false; // guards against Enter + LostFocus double commit

        string newName = vm.EditName.Trim();
        if (newName == vm.Name)
        {
            SelectionRequested?.Invoke(this, [vm]);
            return;
        }

        if (FileNameValidator.Validate(newName) is { } error)
        {
            ShowError(error);
            SelectionRequested?.Invoke(this, [vm]);
            return;
        }

        string oldPath = vm.Path;
        vm.Name = newName; // optimistic: feels instant
        try
        {
            var item = await Provider.RenameAsync(oldPath, newName, CancellationToken.None);
            _byPath.Remove(oldPath);
            vm.Update(item);
            vm.SecondaryText = _shell.GetTypeName(item);
            _byPath[item.Path] = vm;
            if (Location is not null) _cache.Invalidate(Location);

            var provider = Provider;
            string oldName = provider.GetDisplayName(oldPath);
            string renamedPath = item.Path;
            _undo.Push($"Rename \"{oldName}\"", () => provider.RenameAsync(renamedPath, oldName, CancellationToken.None));
        }
        catch (StorageException ex)
        {
            vm.Name = vm.Item.Name;
            ShowStorageError(ex);
        }

        SelectionRequested?.Invoke(this, [vm]);
    }

    private bool CanDelete() => _selected.Count > 0 && Supports(StorageCapabilities.Delete) && !_selected.Any(s => s.IsDrive) && !IsAtRoot;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => DeleteSelectionAsync(permanent: !Supports(StorageCapabilities.RecycleBin));

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeletePermanentlyAsync() => DeleteSelectionAsync(permanent: true);

    private async Task DeleteSelectionAsync(bool permanent)
    {
        if (Provider is null || !CanDelete()) return;
        var targets = _selected.ToList();

        if (permanent)
        {
            string title = targets.Count == 1 ? $"Permanently delete \"{targets[0].Name}\"?" : $"Permanently delete {targets.Count:N0} items?";
            bool confirmed = await _dialogs.ConfirmAsync(title, "This can't be undone. The items won't go to the Recycle Bin.", "Delete");
            if (!confirmed)
            {
                FocusListRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        int index = Items.IndexOf(targets[0]);
        _loadingDelay.Start();
        try
        {
            await Provider.DeleteAsync(targets.Select(t => t.Path).ToList(), permanent, CancellationToken.None);

            foreach (var t in targets)
            {
                _byPath.Remove(t.Path);
                _all.Remove(t);
                Items.Remove(t);
            }
            if (Location is not null) _cache.Invalidate(Location);
            UpdateStatus();

            if (Items.Count > 0)
                SelectionRequested?.Invoke(this, [Items[Math.Clamp(index, 0, Items.Count - 1)]]);
            else
                FocusListRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
            _ = RefreshSilentlyAsync();
        }
        finally
        {
            _loadingDelay.Stop();
            IsLoading = false;
        }
    }

    // ------------------------------------------------------------------ copy / move / clipboard

    /// <summary>True when items can be pasted or dropped into the current folder.</summary>
    public bool CanWriteHere =>
        Provider is not null && Location is not null && !IsSearchMode &&
        !(Location.Path == Provider.RootPath && Supports(StorageCapabilities.VirtualRoot));

    /// <summary>Raised when the user asks to copy/move the selection to the other pane.</summary>
    public event EventHandler<OperationKind>? TransferToOtherPaneRequested;

    public bool HasCopyableSelection => CanCopySelection();

    public bool HasMovableSelection => CanCutSelection();

    private bool CanCopySelection() => _selected.Count > 0 && !_selected.Any(s => s.IsDrive);

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void Copy()
    {
        if (Provider is not null && CanCopySelection())
            _clipboard.SetFiles(Provider.Id, _selected.Select(s => s.Item).ToList(), cut: false);
    }

    private bool CanCutSelection() => CanCopySelection() && Supports(StorageCapabilities.Delete);

    [RelayCommand(CanExecute = nameof(CanCutSelection))]
    private void Cut()
    {
        if (Provider is not null && CanCutSelection())
            _clipboard.SetFiles(Provider.Id, _selected.Select(s => s.Item).ToList(), cut: true);
    }

    private bool CanPaste() => _clipboard.HasFiles && CanWriteHere;

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (!CanWriteHere || Location is null) return;
        var files = await _clipboard.GetFilesAsync();
        if (files is null)
        {
            ShowError("There are no files on the clipboard.");
            return;
        }

        Transfer(files.IsCut ? OperationKind.Move : OperationKind.Copy, files.ProviderId, files.Items, Location.Path);
        if (files.IsCut) _clipboard.ClearCut();
    }

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void RequestCopyToOtherPane() => TransferToOtherPaneRequested?.Invoke(this, OperationKind.Copy);

    [RelayCommand(CanExecute = nameof(CanCutSelection))]
    private void RequestMoveToOtherPane() => TransferToOtherPaneRequested?.Invoke(this, OperationKind.Move);

    /// <summary>
    /// Queues a copy/move into <paramref name="folderPath"/> (default: the current folder) of this pane.
    /// The new items get selected when they show up in the listing.
    /// </summary>
    public FileOperation? Transfer(OperationKind kind, string sourceProviderId, IReadOnlyList<StorageItem> items, string? folderPath = null)
    {
        if (Provider is null || Location is null || items.Count == 0) return null;
        string target = folderPath ?? Location.Path;
        if (target == Location.Path && !CanWriteHere)
        {
            ShowError("Open a drive or folder first - items can't be placed here.");
            return null;
        }

        if (!_operations.CanTransfer(sourceProviderId, Provider.Id))
        {
            string from = _registry.Find(sourceProviderId)?.DisplayName ?? "that location";
            ShowError($"Copying between {from} and {Provider.DisplayName} isn't available yet. It arrives with the transfer engine (upload/download).");
            return null;
        }

        return _operations.Enqueue(kind, sourceProviderId, items, new StorageLocation(Provider.Id, target));
    }

    /// <summary>Selects these paths as soon as they appear in this pane (after a copy/move finishes).</summary>
    public void SelectWhenListed(IEnumerable<string> paths)
    {
        _pendingSelection.Clear();
        foreach (var p in paths) _pendingSelection.Add(p);
        TrySelectPending();
        if (_pendingSelection.Count > 0) _ = RefreshSilentlyAsync();
    }

    private void TrySelectPending()
    {
        if (_pendingSelection.Count == 0) return;
        var found = _pendingSelection.Where(_byPath.ContainsKey).Select(p => _byPath[p]).Where(Items.Contains).ToList();
        if (found.Count == 0) return;
        _pendingSelection.Clear();
        SelectionRequested?.Invoke(this, found);
    }

    // ------------------------------------------------------------------ search

    [RelayCommand]
    public async Task StartSearchAsync()
    {
        string query = (IsSearchMode ? _searchQuery : FilterText).Trim();
        if (query.Length == 0 || Provider is null || Location is null) return;
        if (!Supports(StorageCapabilities.Search))
        {
            ShowError("Search isn't available for this location.");
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var provider = Provider;
        string root = Location.Path;

        _searchQuery = query;
        IsSearchMode = true;
        IsSearching = true;
        Title = $"Search results for \"{query}\"";
        _selected.Clear();
        Items.ReplaceAll(Array.Empty<FileItemViewModel>());
        UpdateStatus();

        var buffer = new List<FileItemViewModel>();
        var clock = Stopwatch.StartNew();
        int count = 0;
        try
        {
            await foreach (var item in provider.SearchAsync(root, query, cts.Token))
            {
                buffer.Add(new FileItemViewModel(item, _icons, RelativeFolder(provider, root, item.Path), Columns));
                if (buffer.Count >= 250 || clock.ElapsedMilliseconds > 120)
                {
                    Items.AddRange(buffer);
                    buffer = new List<FileItemViewModel>();
                    clock.Restart();
                    UpdateStatus();
                }
                if (++count >= MaxSearchResults) break;
            }
            Items.AddRange(buffer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
        finally
        {
            if (_searchCts == cts)
            {
                IsSearching = false;
                UpdateStatus();
            }
        }
    }

    /// <summary>"Sub A\Docs" relative to the search root – shorter and more scannable than a full path.</summary>
    private static string RelativeFolder(IStorageProvider provider, string root, string itemPath)
    {
        string parent = provider.GetParentPath(itemPath) ?? string.Empty;
        if (root.Length > 0 && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string rest = parent[root.Length..].Trim(['\\', '/']);
            return rest.Length == 0 ? provider.GetDisplayName(root) : rest;
        }
        return parent;
    }

    public void ExitSearch(bool applyView = true)
    {
        if (!IsSearchMode) return;
        _searchCts?.Cancel();
        IsSearching = false;
        IsSearchMode = false;
        if (Provider is not null && Location is not null) Title = Provider.GetDisplayName(Location.Path);
        if (applyView) ApplyView(replace: true);
    }

    // ------------------------------------------------------------------ misc commands

    private IReadOnlyList<string> TargetPaths() =>
        _selected.Count > 0 ? _selected.Select(s => s.Path).ToList() :
        Location is { Path.Length: > 0 } location ? [location.Path] : [];

    [RelayCommand]
    private void CopyPath()
    {
        var paths = TargetPaths();
        if (paths.Count == 0) return;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p)));
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (Provider is null) return;
        var target = _selected.Count == 1 && _selected[0].IsContainer
            ? new StorageLocation(Provider.Id, _selected[0].Path)
            : Location;
        if (target is null) return;

        if (_favorites.Contains(target)) _favorites.Remove(target);
        else _favorites.Add(Provider.GetDisplayName(target.Path), target);
        OnPropertyChanged(nameof(IsFavorite));
    }

    public bool IsSelectionFavorite => Provider is not null && _selected.Count == 1 && _selected[0].IsContainer &&
                                      _favorites.Contains(new StorageLocation(Provider.Id, _selected[0].Path));

    [RelayCommand]
    private void ShowProperties()
    {
        if (!Supports(StorageCapabilities.OpenWithShell)) return;
        foreach (var path in TargetPaths().Take(8)) _shellActions.ShowProperties(path);
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        if (!Supports(StorageCapabilities.OpenWithShell)) return;
        var paths = TargetPaths();
        _shellActions.RevealInExplorer(paths.Count > 0 ? paths[0] : string.Empty);
    }

    public void ShowError(string message) => ShowMessage(message, isError: true);

    [ObservableProperty] private bool _canReconnect;

    /// <summary>Raised when a cloud account's sign-in expired; the shell offers to reconnect it.</summary>
    public event EventHandler<string>? ReconnectRequested;

    public event EventHandler<string>? AuthenticationExpired;

    private string? _errorProviderId;

    public void RequestReconnect()
    {
        if (_errorProviderId is not null) ReconnectRequested?.Invoke(this, _errorProviderId);
    }

    /// <param name="providerId">The provider that failed (defaults to the current one; navigation errors pass the target).</param>
    public void ShowStorageError(StorageException ex, string? providerId = null)
    {
        ShowMessage(ex.Message, isError: true);
        _errorProviderId = providerId ?? Provider?.Id;
        CanReconnect = ex.Kind == StorageErrorKind.AuthenticationRequired && _errorProviderId is not null;
        if (CanReconnect) AuthenticationExpired?.Invoke(this, _errorProviderId!);
    }

    /// <summary>True when items from <paramref name="sourceProviderId"/> can be copied into this pane.</summary>
    public bool CanAcceptFrom(string sourceProviderId) =>
        Provider is not null && _operations.CanTransfer(sourceProviderId, Provider.Id);

    public void ShowMessage(string message, bool isError)
    {
        CanReconnect = false;
        IsMessageError = isError;
        ErrorMessage = message;
        IsErrorOpen = true;
    }

    public PaneState CaptureState() => new()
    {
        ProviderId = Location?.ProviderId ?? "local",
        Path = Location?.Path ?? string.Empty,
        SortField = SortField,
        SortDescending = SortDescending,
        ViewMode = ViewMode,
    };

    public void RestoreState(PaneState state)
    {
        _sortDescending = state.SortDescending;
        _sortField = state.SortField;
        _viewMode = state.ViewMode;
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsGridView));
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _searchCts?.Cancel();
        _watcher?.Dispose();
    }
}
