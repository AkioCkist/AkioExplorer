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

    /// <summary>Switching tabs: show the tab's folder (from cache when possible) without touching history.</summary>
    Restore,
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
    private readonly IShellMenuService _shellMenus;
    private readonly IArchiverService _archivers;
    private readonly ISettingsService _settings;
    private readonly FavoritesService _favorites;
    private readonly DialogService _dialogs;
    private readonly IFileOperationService _operations;
    private readonly ClipboardService _clipboard;
    private readonly UndoService _undo;
    private readonly HashSet<string> _pendingSelection = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths being deleted in the background: a refresh meanwhile must not bring their rows back.</summary>
    private readonly HashSet<string> _deleting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<FilePaneViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _watchDebounce;
    private readonly DispatcherQueueTimer _loadingDelay;

    private readonly List<FileItemViewModel> _selected = new();
    private List<FileItemViewModel> _all = new();
    private Dictionary<string, FileItemViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private IDisposable? _watcher;
    private int _loadVersion;
    private bool _suppressFilter;
    private SearchQuery _activeQuery = SearchQuery.Empty;
    private SearchMatcher? _viewMatcher;
    private const int MaxRecentSearches = 10;

    public FilePaneViewModel(
        IStorageProviderRegistry registry,
        IListingCache cache,
        IconService icons,
        IShellIconExtractor shell,
        IShellActions shellActions,
        IShellMenuService shellMenus,
        IArchiverService archivers,
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
        _shellMenus = shellMenus;
        _archivers = archivers;
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
        FileItemViewModel.ShowExtensions = settings.Current.ShowFileExtensions;
        Formatting.DecimalUnits = settings.Current.DecimalSizeUnits;
        Formatting.DateDisplay = settings.Current.DateDisplay;
        InitializeTabs();
        settings.Changed += (_, _) => OnSettingsChanged();
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
    [ObservableProperty] private bool _foldersFirst = true;
    [ObservableProperty] private bool _isSearchMode;
    [ObservableProperty] private bool _isSearching;

    /// <summary>Search filters (type, date, size, words, scope) without the name, which lives in <see cref="FilterText"/>.</summary>
    [ObservableProperty] private SearchQuery _searchFilters = SearchQuery.Empty;
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

    public bool HasSearchFilters => SearchFilters.HasFilters;

    /// <summary>"Has the words" works here (the provider searches inside files).</summary>
    public bool SupportsContentSearch => Supports(StorageCapabilities.ContentSearch);

    public bool CanSearch => Supports(StorageCapabilities.Search);

    /// <summary>Name of the folder a "this folder" search covers, and of the whole provider ("Everywhere").</summary>
    public string SearchFolderName => Provider is null || Location is null ? string.Empty : Provider.GetDisplayName(Location.Path);

    public string SearchProviderName => Provider?.DisplayName ?? string.Empty;

    public bool SupportsSharingSearch => Supports(StorageCapabilities.SearchSharing);

    public bool SupportsTrashSearch => Supports(StorageCapabilities.SearchTrash);

    public bool SupportsStarredSearch => Supports(StorageCapabilities.SearchStarred);

    /// <summary>Where a search can look, for the Location chip and the advanced form. The first entry is the default.</summary>
    public IReadOnlyList<(string Label, SearchScope Scope)> SearchScopeOptions
    {
        get
        {
            var options = new List<(string, SearchScope)>();
            if (Provider is null || Location is null) return options;
            bool whole = Supports(StorageCapabilities.AccountWideSearch) || Location.Path == Provider.RootPath;
            if (whole)
            {
                options.Add(($"Anywhere in {SearchProviderName}", SearchScope.CurrentFolder));
            }
            else
            {
                options.Add(($"{SearchFolderName} and subfolders", SearchScope.CurrentFolder));
                options.Add(($"Everywhere in {SearchProviderName}", SearchScope.Everywhere));
            }
            if (SupportsSharingSearch) options.Add(("Shared with me", SearchScope.SharedWithMe));
            return options;
        }
    }

    /// <summary>The search box text plus the filters: what Enter would search for.</summary>
    public SearchQuery CurrentQuery => SearchFilters with { Name = FilterText };

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

    /// <summary>Opens the advanced search form for this pane (Ctrl+Shift+F).</summary>
    public void RequestAdvancedSearch() => AdvancedSearchRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? AdvancedSearchRequested;

    public void RequestListFocus() => FocusListRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ navigation

    /// <param name="focusPath">Item to select once listed (default: the folder we came from, when going up).</param>
    public async Task NavigateAsync(StorageLocation target, NavigationKind kind = NavigationKind.New, string? focusPath = null)
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
        string? focus = focusPath ?? previous?.Path;
        bool committed = false;
        bool fromCache = false;
        if (kind != NavigationKind.Refresh) IsErrorOpen = false;

        if (kind != NavigationKind.Refresh && _cache.TryGet(target, out var cached))
        {
            Commit(provider, target, kind);
            committed = fromCache = true;
            ApplyListing(cached, replace: true, focusPath: focus);
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
                    ApplyListing(collected, replace: true, focusPath: focus);
                }
            }

            if (version != _loadVersion) return;

            if (!committed)
            {
                Commit(provider, target, kind);
                ApplyListing(collected, replace: true, focusPath: focus);
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
            StartAutoFolderSizes();
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
        UpdateActiveTab(provider, target);

        if (IsSearchMode) ExitSearch(applyView: false);
        if (locationChanged)
        {
            _suppressFilter = true;
            FilterText = string.Empty;
            SearchFilters = SearchQuery.Empty;
            _suppressFilter = false;
            _selected.Clear();
            ResetFolderSizes();

            Breadcrumb.Clear();
            foreach (var segment in provider.GetBreadcrumb(target.Path)) Breadcrumb.Add(segment);

            _watcher?.Dispose();
            _watcher = provider.Watch(target.Path, OnWatchedFolderChanged);
        }

        Title = provider.GetDisplayName(target.Path);
        OnPropertyChanged(nameof(FilterPlaceholder));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(SupportsContentSearch));
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(SearchFolderName));
        OnPropertyChanged(nameof(SearchProviderName));
        OnPropertyChanged(nameof(SearchScopeOptions));
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
        foreach (var listed in items)
        {
            if (_deleting.Count > 0 && _deleting.Contains(listed.Path)) continue;
            var item = WithKnownFolderSize(listed);
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
        ApplyView(replace, fromRefresh: true);

        if (focusPath is not null && newMap.TryGetValue(focusPath, out var focus))
            SelectionRequested?.Invoke(this, [focus]);

        TrySelectPending();
    }

    private void SortList(List<FileItemViewModel> list)
    {
        var comparer = new StorageItemComparer(new SortOptions(SortField, SortDescending, FoldersFirst), _shell.GetTypeName);
        list.Sort((a, b) => comparer.Compare(a.Item, b.Item));
    }

    private bool IsShownBySettings(FileItemViewModel vm)
    {
        var settings = _settings.Current;
        if (!settings.ShowHiddenItems && (vm.Item.IsHidden || settings.TreatDotFilesAsHidden && vm.Name.StartsWith('.'))) return false;
        return !(settings.HideJunkFiles && IsJunkFile(vm));
    }

    private bool IsVisible(FileItemViewModel vm)
    {
        var settings = _settings.Current;
        if (!settings.ShowHiddenItems && (vm.Item.IsHidden || settings.TreatDotFilesAsHidden && vm.Name.StartsWith('.'))) return false;
        if (settings.HideJunkFiles && IsJunkFile(vm)) return false;
        if (_viewMatcher is not null && !_viewMatcher.Matches(vm.Item)) return false;
        return FilterText.Length == 0 || vm.Name.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Office lock files, temp files and Windows folder metadata (Settings › Hide temporary files).</summary>
    private static bool IsJunkFile(FileItemViewModel vm) =>
        vm.Item.Kind == StorageItemKind.File &&
        (vm.Name.StartsWith("~$", StringComparison.Ordinal)
         || vm.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
         || vm.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
         || vm.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Incremental list updates cost one ListView notification (and one array shift) per row. Past this many changed
    /// rows a single Reset is far cheaper: typing "a" in the filter box of a 10 000-item folder would otherwise remove
    /// thousands of rows one by one.
    /// </summary>
    private const int MaxIncrementalChanges = 64;

    /// <param name="fromRefresh">
    /// A re-listing (watcher, refresh, cached navigation): keep the scroll position with row-by-row updates unless
    /// most of the list changed. Otherwise the user changed what is shown (filter, chips, settings).
    /// </param>
    private void ApplyView(bool replace, bool fromRefresh = false)
    {
        if (IsSearchMode) return;

        var visible = new List<FileItemViewModel>(_all.Count);
        foreach (var vm in _all)
        {
            if (IsVisible(vm)) visible.Add(vm);
        }

        if (replace)
        {
            var keep = KeptSelection(visible);
            Items.ReplaceAll(visible);
            if (keep.Count > 0) SelectionRequested?.Invoke(this, keep);
        }
        else if (IsLargeChange(visible, fromRefresh ? Math.Max(MaxIncrementalChanges, Items.Count / 2) : MaxIncrementalChanges))
        {
            // Same rows kept selected, but without scrolling or taking focus (the user may be typing in the filter box).
            var keep = KeptSelection(visible);
            Items.ReplaceAll(visible);
            if (keep.Count > 0) SelectionRestoreRequested?.Invoke(this, keep);
        }
        else
        {
            ListDiff.Apply(Items, visible, i => i.Path, s => s.Path, s => s, move: (from, to) => Items.Move(from, to));
        }

        UpdateStatus();
    }

    private List<FileItemViewModel> KeptSelection(List<FileItemViewModel> visible)
    {
        if (_selected.Count == 0) return [];
        var shown = new HashSet<FileItemViewModel>(visible);
        return _selected.Where(shown.Contains).ToList();
    }

    /// <summary>True when more than <paramref name="limit"/> rows would be added or removed to show <paramref name="visible"/>.</summary>
    private bool IsLargeChange(List<FileItemViewModel> visible, int limit)
    {
        if (Math.Abs(visible.Count - Items.Count) > limit) return true;
        var shown = new HashSet<FileItemViewModel>(Items);
        int added = 0;
        foreach (var vm in visible)
        {
            if (!shown.Contains(vm) && ++added > limit) return true;
        }
        int removed = Items.Count - (visible.Count - added);
        return added + removed > limit;
    }

    /// <summary>Asks the view to select these items again after a list reset, without scrolling or moving focus.</summary>
    public event EventHandler<IReadOnlyList<FileItemViewModel>>? SelectionRestoreRequested;

    /// <summary>Type / date / size filters also narrow the folder being browsed, so the chips always mean what they show.</summary>
    partial void OnSearchFiltersChanged(SearchQuery value)
    {
        var folderFilters = value with { Name = string.Empty, Content = string.Empty, Scope = SearchScope.CurrentFolder };
        _viewMatcher = folderFilters.HasCriteria ? folderFilters.CreateMatcher(DateTimeOffset.Now) : null;
        OnPropertyChanged(nameof(HasSearchFilters));
        if (_suppressFilter) return;
        if (!IsSearchMode) ApplyView(replace: false);
    }

    partial void OnFilterTextChanged(string value)
    {
        if (_suppressFilter) return;
        if (IsSearchMode) ExitSearch();
        else ApplyView(replace: false);
    }

    partial void OnSortFieldChanged(SortField value) => Resort();

    partial void OnSortDescendingChanged(bool value) => Resort();

    partial void OnFoldersFirstChanged(bool value) => Resort();

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
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after <see cref="SelectedItems"/> changes (drives the preview pane).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Localised type name ("JPEG image", "Google Docs"…), the same text as the Type column.</summary>
    public string GetTypeName(StorageItem item) => _shell.GetTypeName(item);

    private void UpdateStatus()
    {
        if (IsSearchMode)
        {
            StatusText = IsSearching ? $"Searching… {Items.Count:N0} found" : $"{Items.Count:N0} results";
            IsEmpty = Items.Count == 0 && !IsSearching;
            EmptyText = _activeQuery.HasFilters
                ? "No results. Try removing a filter or searching everywhere."
                : $"No results for \"{_activeQuery.NameText}\"";
        }
        else
        {
            int total = _all.Count(IsShownBySettings);
            bool narrowed = FilterText.Length > 0 || _viewMatcher is not null;
            StatusText = narrowed
                ? $"{Items.Count:N0} of {total:N0} items"
                : $"{Items.Count:N0} {(Items.Count == 1 ? "item" : "items")}";
            IsEmpty = Items.Count == 0 && !IsLoading && Location is not null;
            EmptyText = FilterText.Length > 0 ? $"No items match \"{FilterText}\". Press Enter to search subfolders."
                : _viewMatcher is not null ? "Nothing here matches the filters. Press Enter in the search box to search subfolders."
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
            if (vm.Item is { Kind: not StorageItemKind.Drive, Size: { } size })
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
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(HasMovableSelection));
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();
        OpenTrashCommand.NotifyCanExecuteChanged();
        UpdateCloudCommandStates();
        OnPropertyChanged(nameof(TrashLabel));
        OpenSelectedCommand.NotifyCanExecuteChanged();
        OpenWithCommand.NotifyCanExecuteChanged();
        UpdateToolCommandStates();
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

        // Google Docs, Sheets, Forms…: they live in the browser (downloading would only give an export).
        if (Provider is IWebViewProvider web && web.OpensInBrowser(vm.Item))
        {
            await OpenInBrowserAsync(vm.Item);
            return;
        }

        if ((Provider.Capabilities & StorageCapabilities.OpenWithShell) == 0)
        {
            if (Provider is IFileContentProvider) OpenRemoteRequested?.Invoke(this, vm.Item);
            else ShowError("Files in this location can't be opened directly. Copy them to This PC first.");
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

    /// <summary>At a root that only lists drives/buckets (This PC, an S3 account): nothing can be created or deleted there.
    /// A cloud drive's root is an ordinary folder.</summary>
    private bool IsAtRoot => Location is null || Provider is null ||
        Location.Path == Provider.RootPath && Supports(StorageCapabilities.VirtualRoot);

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

    /// <summary>"Rubbish bin…" / "Trash…" / "Recycle Bin…" for the current service.</summary>
    public string TrashLabel => Provider is ITrashProvider trash ? $"{trash.TrashName}…" : "Trash…";

    private bool CanOpenTrash() => Provider is ITrashProvider;

    /// <summary>Shows what's in the trash (restore, delete for good, empty); the view builds the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTrash))]
    private void OpenTrash() => RequestDialog(PaneDialog.Trash);

    /// <summary>After the trash dialog restored or deleted something: restored items may belong in this folder, and
    /// storage usage changed.</summary>
    public void OnTrashChanged()
    {
        _ = RefreshSilentlyAsync();
        TrashChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after items were restored from or deleted in the trash (storage usage changed).</summary>
    public event EventHandler? TrashChanged;

    private async Task DeleteSelectionAsync(bool permanent)
    {
        if (Provider is null || !CanDelete()) return;
        var targets = _selected.ToList();

        if (!permanent && _settings.Current.ConfirmRecycle)
        {
            string title = targets.Count == 1 ? $"Move \"{targets[0].Name}\" to the Recycle Bin?" : $"Move {targets.Count:N0} items to the Recycle Bin?";
            if (!await _dialogs.ConfirmAsync(title, "You can restore them from the Recycle Bin or with Ctrl+Z.", "Delete"))
            {
                FocusListRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

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
        var provider = Provider;
        var location = Location;

        // Optimistic: the rows disappear right away (one list update, not one per item); the Recycle Bin / disk work
        // runs in the background. If it fails, a refresh brings back whatever is still there.
        var gone = targets.ToHashSet();
        foreach (var t in targets)
        {
            _byPath.Remove(t.Path);
            _deleting.Add(t.Path);
        }
        _all = _all.Where(v => !gone.Contains(v)).ToList();
        _selected.Clear();
        Items.ReplaceAll(Items.Where(v => !gone.Contains(v)).ToList());
        if (location is not null) _cache.Invalidate(location);
        UpdateStatus();
        if (Items.Count > 0)
            SelectionRequested?.Invoke(this, [Items[Math.Clamp(index, 0, Items.Count - 1)]]);
        else
            FocusListRequested?.Invoke(this, EventArgs.Empty);

        _loadingDelay.Start();
        try
        {
            await provider.DeleteAsync(targets.Select(t => t.Path).ToList(), permanent, CancellationToken.None);
            foreach (var t in targets) _deleting.Remove(t.Path);
        }
        catch (OperationCanceledException)
        {
            foreach (var t in targets) _deleting.Remove(t.Path);
            _ = RefreshSilentlyAsync();
        }
        catch (StorageException ex)
        {
            foreach (var t in targets) _deleting.Remove(t.Path);
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

    /// <summary>Raised when a cloud file is opened: the shell downloads it to a cache and opens it.</summary>
    public event EventHandler<StorageItem>? OpenRemoteRequested;

    /// <summary>The pane shows a cloud account (no Windows shell behind it).</summary>
    public bool IsRemote => Provider is not null && (Provider.Capabilities & StorageCapabilities.OpenWithShell) == 0;

    public bool CanUpload => IsRemote && CanWriteHere;

    public bool CanDownload => IsRemote && CanCopySelection();

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
        var found = new List<FileItemViewModel>();
        foreach (var path in _pendingSelection)
        {
            if (_byPath.TryGetValue(path, out var vm)) found.Add(vm);
        }
        if (found.Count == 0) return;
        if (found.Count > 8)
        {
            // Batch rename / extract can select thousands: Items.Contains per item would be O(n·k).
            var shown = new HashSet<FileItemViewModel>(Items);
            found.RemoveAll(vm => !shown.Contains(vm));
        }
        else
        {
            found.RemoveAll(vm => !Items.Contains(vm));
        }
        if (found.Count == 0) return;
        _pendingSelection.Clear();
        SelectionRequested?.Invoke(this, found);
    }

    // ------------------------------------------------------------------ search

    [RelayCommand]
    public Task StartSearchAsync() => SearchAsync(CurrentQuery);

    /// <summary>Applies new filters (chips, advanced search) and searches right away, like Google Drive.</summary>
    public async Task ApplySearchAsync(SearchQuery query)
    {
        _suppressFilter = true;
        FilterText = query.Name;
        _suppressFilter = false;
        SearchFilters = query with { Name = string.Empty };

        if (CurrentQuery.HasCriteria)
            await SearchAsync(CurrentQuery);
        else if (IsSearchMode)
            ExitSearch();
        else
            ApplyView(replace: false);
    }

    /// <summary>Clears the search box and all filters, back to the plain folder.</summary>
    public void ClearSearch()
    {
        _suppressFilter = true;
        FilterText = string.Empty;
        SearchFilters = SearchQuery.Empty;
        _suppressFilter = false;
        if (IsSearchMode) ExitSearch();
        else ApplyView(replace: false);
    }

    private async Task SearchAsync(SearchQuery query)
    {
        if (Provider is null || Location is null) return;
        query = query.RestrictTo(Provider.Capabilities);
        if (!query.HasCriteria) return;
        if (!Supports(StorageCapabilities.Search))
        {
            ShowError("Search isn't available for this location.");
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var provider = Provider;
        string root = query.Scope == SearchScope.Everywhere ? provider.RootPath : Location.Path;

        _activeQuery = query;
        RememberSearch(query.NameText);
        IsSearchMode = true;
        IsSearching = true;
        Title = SearchLabels.Describe(query, DateTime.Today);
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
                if (item.IsHidden && !_settings.Current.ShowHiddenItems) continue;
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

    // ------------------------------------------------------------------ recent searches

    /// <summary>Recent search terms (newest first) containing <paramref name="text"/>; prefix matches first.</summary>
    public IReadOnlyList<string> RecentSearches(string text)
    {
        text = text.Trim();
        var all = _settings.Current.RecentSearches;
        if (text.Length == 0) return all.Take(8).ToList();
        return all
            .Where(s => s.Contains(text, StringComparison.CurrentCultureIgnoreCase) && !s.Equals(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(s => s.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
            .Take(8)
            .ToList();
    }

    private void RememberSearch(string text)
    {
        if (text.Length == 0) return;
        var list = _settings.Current.RecentSearches;
        list.RemoveAll(s => s.Equals(text, StringComparison.CurrentCultureIgnoreCase));
        list.Insert(0, text);
        if (list.Count > MaxRecentSearches) list.RemoveRange(MaxRecentSearches, list.Count - MaxRecentSearches);
        _settings.Save();
    }

    public void ForgetRecentSearch(string text)
    {
        _settings.Current.RecentSearches.RemoveAll(s => s.Equals(text, StringComparison.CurrentCultureIgnoreCase));
        _settings.Save();
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
        var format = _settings.Current.CopyPathFormat;
        package.SetText(string.Join(Environment.NewLine, paths.Select(p => format switch
        {
            CopyPathFormat.Plain => p,
            CopyPathFormat.ForwardSlashes => p.Replace('\\', '/'),
            _ => p.Contains(' ') ? $"\"{p}\"" : p,
        })));
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
        FoldersFirst = FoldersFirst,
        ViewMode = ViewMode,
        Tabs = Tabs.Count > 1 ? CaptureTabs() : new(),
        ActiveTab = Math.Max(0, ActiveTabIndex),
    };

    public void RestoreState(PaneState state)
    {
        _sortDescending = state.SortDescending;
        _sortField = state.SortField;
        _foldersFirst = state.FoldersFirst;
        _viewMode = state.ViewMode;
        OnPropertyChanged(nameof(FoldersFirst));
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsGridView));
        RestoreTabs(state);
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _searchCts?.Cancel();
        _sizeCts?.Cancel();
        _watcher?.Dispose();
    }
}
