using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AkioExplorer.App.ViewModels;

/// <summary>Cloud-service features: public links, favourite / label / hide, versions, links from other people.</summary>
public sealed partial class FilePaneViewModel
{
    private void UpdateCloudCommandStates()
    {
        CopyLinkCommand.NotifyCanExecuteChanged();
        RemoveLinkCommand.NotifyCanExecuteChanged();
        ToggleStarCommand.NotifyCanExecuteChanged();
        SetLabelCommand.NotifyCanExecuteChanged();
        ToggleCloudHiddenCommand.NotifyCanExecuteChanged();
        ShowVersionsCommand.NotifyCanExecuteChanged();
        OpenPublicLinkCommand.NotifyCanExecuteChanged();
        OpenInBrowserCommand.NotifyCanExecuteChanged();
        ConvertToOnlineCommand.NotifyCanExecuteChanged();
        DownloadAsCommand.NotifyCanExecuteChanged();
        EditDescriptionCommand.NotifyCanExecuteChanged();
        AddShortcutCommand.NotifyCanExecuteChanged();
        ToggleLockCommand.NotifyCanExecuteChanged();
        ShareCommand.NotifyCanExecuteChanged();
        ShowCommentsCommand.NotifyCanExecuteChanged();
        ShowActivityCommand.NotifyCanExecuteChanged();
        NewOnlineDocumentCommand.NotifyCanExecuteChanged();
    }

    private List<StorageItem> SelectedCloudItems() => _selected.Where(s => !s.IsDrive).Select(s => s.Item).ToList();

    // ------------------------------------------------------------------ links

    private bool CanShareLink() => Provider is IShareLinkProvider && _selected.Count == 1;

    [RelayCommand(CanExecute = nameof(CanShareLink))]
    private async Task CopyLinkAsync()
    {
        if (Provider is not IShareLinkProvider links || _selected.Count != 1) return;
        var item = _selected[0].Item;
        await RunCloudAsync(async () =>
        {
            string link = await links.CreateShareLinkAsync(item, CancellationToken.None);
            var package = new DataPackage();
            package.SetText(link);
            Clipboard.SetContent(package);
            ShowMessage($"Link to \"{item.Name}\" copied. Anyone with it can open the {(item.IsContainer ? "folder" : "file")}.", isError: false);
        });
    }

    [RelayCommand(CanExecute = nameof(CanShareLink))]
    private async Task RemoveLinkAsync()
    {
        if (Provider is not IShareLinkProvider links || _selected.Count != 1) return;
        var item = _selected[0].Item;
        if (!await _dialogs.ConfirmAsync($"Turn off the link to \"{item.Name}\"?",
                "People who have the link can't open it any more. You can create a new link later (it will be a different address).", "Turn off"))
            return;
        await RunCloudAsync(async () =>
        {
            await links.RemoveShareLinkAsync(item, CancellationToken.None);
            ShowMessage($"The link to \"{item.Name}\" is off.", isError: false);
        });
    }

    // ------------------------------------------------------------------ favourite, label, hide

    private bool CanMark() => Provider is IItemMarksProvider && _selected.Count > 0;

    private bool CanLabel() => Provider is IItemMarksProvider { SupportsLabels: true } marks && _selected.Count > 0 &&
                               (!marks.LabelsFoldersOnly || _selected.All(s => s.IsContainer));

    private bool CanHideOnService() => Provider is IItemMarksProvider { SupportsHiding: true } && _selected.Count > 0;

    /// <summary>True when every selected item is a favourite (the menu then offers "Remove from favourites").</summary>
    public bool IsSelectionStarred => _selected.Count > 0 && _selected.All(s => s.Item.IsStarred);

    public bool IsSelectionHiddenOnService => _selected.Count > 0 && _selected.All(s => s.Item.IsHidden);

    public ItemLabel SelectionLabel => _selected.Select(s => s.Item.Label).Distinct().Count() == 1 ? _selected[0].Item.Label : ItemLabel.None;

    [RelayCommand(CanExecute = nameof(CanMark))]
    private async Task ToggleStarAsync()
    {
        if (Provider is not IItemMarksProvider marks) return;
        bool star = !IsSelectionStarred;
        var items = SelectedCloudItems();
        await RunCloudAsync(async () =>
        {
            await marks.SetStarredAsync(items, star, CancellationToken.None);
            await RefreshSilentlyAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanLabel))]
    private async Task SetLabelAsync(ItemLabel label)
    {
        if (Provider is not IItemMarksProvider marks) return;
        var items = SelectedCloudItems();
        await RunCloudAsync(async () =>
        {
            await marks.SetLabelAsync(items, label, CancellationToken.None);
            await RefreshSilentlyAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanHideOnService))]
    private async Task ToggleCloudHiddenAsync()
    {
        if (Provider is not IItemMarksProvider marks) return;
        bool hide = !IsSelectionHiddenOnService;
        var items = SelectedCloudItems();
        await RunCloudAsync(async () =>
        {
            await marks.SetHiddenAsync(items, hide, CancellationToken.None);
            if (hide && !_settings.Current.ShowHiddenItems)
                ShowMessage(items.Count == 1 ? $"\"{items[0].Name}\" is hidden. View › Hidden items (Ctrl+H) shows it again."
                    : $"{items.Count:N0} items are hidden. View › Hidden items (Ctrl+H) shows them again.", isError: false);
            await RefreshSilentlyAsync();
        });
    }

    // ------------------------------------------------------------------ versions, links from others

    private bool CanShowVersions() => Provider is IVersionsProvider && _selected.Count == 1 && !_selected[0].Item.IsContainer;

    [RelayCommand(CanExecute = nameof(CanShowVersions))]
    private void ShowVersions() => RequestDialog(PaneDialog.Versions);

    private bool CanOpenPublicLink() => Provider is IPublicLinkProvider && Location is not null;

    /// <summary>"Open a link…": paste a link someone shared, see what it is, save it into this folder.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenPublicLink))]
    private void OpenPublicLink() => RequestDialog(PaneDialog.OpenLink);

    // ------------------------------------------------------------------ views built from search

    /// <summary>MEGA's "Recents": files changed in the last 30 days anywhere in the account, newest first.</summary>
    [RelayCommand]
    public async Task ShowRecentFilesAsync()
    {
        if (Provider is null) return;
        SortField = SortField.Modified;
        SortDescending = true;
        await ApplySearchAsync(SearchQuery.Empty with { Modified = DateFilter.Last30Days, Scope = SearchScope.Everywhere });
    }

    /// <summary>MEGA's "Media": every photo and video in the account as pictures, newest first.</summary>
    [RelayCommand]
    public async Task ShowMediaAsync()
    {
        if (Provider is null) return;
        SortField = SortField.Modified;
        SortDescending = true;
        ViewMode = FileViewMode.Grid;
        await ApplySearchAsync(SearchQuery.Empty with { Category = FileCategory.Media, Scope = SearchScope.Everywhere });
    }

    /// <summary>"Storage" view: files of 10 MB and more anywhere in the account, biggest first.</summary>
    [RelayCommand]
    public async Task ShowLargestFilesAsync()
    {
        if (Provider is null) return;
        SortField = SortField.Size;
        SortDescending = true;
        ViewMode = FileViewMode.Details;
        await ApplySearchAsync(SearchQuery.Empty with { Size = SizeFilter.Custom, MinSize = 10L * 1024 * 1024, Scope = SearchScope.Everywhere });
    }

    /// <summary>Starred / favourite items anywhere in the account.</summary>
    [RelayCommand]
    public async Task ShowStarredAsync()
    {
        if (Provider is null || !Supports(StorageCapabilities.SearchStarred)) return;
        await ApplySearchAsync(SearchQuery.Empty with { Starred = true, Scope = SearchScope.Everywhere });
    }

    // ------------------------------------------------------------------ browser, online documents, export

    private bool CanOpenInBrowser() => Provider is IWebViewProvider && _selected.Count <= 1;

    /// <summary>The selected item (or the open folder) on the service's website.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInBrowser))]
    private async Task OpenInBrowserAsync()
    {
        if (Provider is null) return;
        StorageItem? item = _selected.Count == 1 ? _selected[0].Item : null;
        if (item is null && Location is { } here)
            item = await Provider.GetItemAsync(here.Path, CancellationToken.None)
                   ?? new StorageItem { Name = Provider.DisplayName, Path = here.Path, Kind = StorageItemKind.Folder };
        await OpenInBrowserAsync(item);
    }

    private async Task OpenInBrowserAsync(StorageItem? item)
    {
        if (item is null || Provider is not IWebViewProvider web) return;
        await RunCloudAsync(async () =>
        {
            string url = await web.GetWebUrlAsync(item, CancellationToken.None);
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        });
    }

    /// <summary>Online document types this service can create here (New ▸ Google Docs…).</summary>
    public IReadOnlyList<NativeDocumentType> OnlineDocumentTypes =>
        Provider is INativeDocumentsProvider docs && CanWriteHere && !IsSearchMode ? docs.DocumentTypes : [];

    private bool CanCreateOnline(NativeDocumentType? type) => type is not null && Provider is INativeDocumentsProvider && CanWriteHere && !IsSearchMode;

    [RelayCommand(CanExecute = nameof(CanCreateOnline))]
    private async Task NewOnlineDocumentAsync(NativeDocumentType? type)
    {
        if (type is null || Provider is not INativeDocumentsProvider docs || Location is not { } here) return;
        string name = Core.Logic.FileNameValidator.GetUniqueName(type.DefaultName, _all.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        await RunCloudAsync(async () =>
        {
            var created = await docs.CreateDocumentAsync(here.Path, name, type, CancellationToken.None);
            await RefreshSilentlyAsync();
            await OpenInBrowserAsync(created);
        });
    }

    private bool CanConvert() => Provider is INativeDocumentsProvider docs && _selected.Count > 0 && _selected.All(s => docs.CanConvert(s.Item));

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertToOnlineAsync()
    {
        if (Provider is not INativeDocumentsProvider docs) return;
        var items = SelectedCloudItems();
        await RunCloudAsync(async () =>
        {
            foreach (var item in items) await docs.ConvertAsync(item, CancellationToken.None);
            ShowMessage(items.Count == 1 ? $"Made a Google copy of \"{items[0].Name}\"; the original stays." : $"Made Google copies of {items.Count:N0} files.", isError: false);
            await RefreshSilentlyAsync();
        });
    }

    /// <summary>Formats the selected online document can be downloaded as (Download as ▸).</summary>
    public IReadOnlyList<ExportOption> SelectionExportOptions =>
        Provider is IExportProvider export && _selected.Count == 1 ? export.ExportOptions(_selected[0].Item) : [];

    private bool CanDownloadAs(ExportOption? format) => format is not null && Provider is IExportProvider && _selected.Count == 1;

    /// <summary>Saves the document in the chosen format to the Downloads folder.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAs))]
    private async Task DownloadAsAsync(ExportOption? format)
    {
        if (format is null || Provider is not IExportProvider export || _selected.Count != 1) return;
        var item = _selected[0].Item;
        await RunCloudAsync(async () =>
        {
            string target = await SaveToDownloadsAsync(System.IO.Path.GetFileNameWithoutExtension(item.Name) + format.Extension,
                ct => export.OpenExportAsync(item, format, ct));
            ShowMessage($"Saved \"{System.IO.Path.GetFileName(target)}\" in Downloads.", isError: false);
        });
    }

    /// <summary>Writes a stream to the user's Downloads folder under a free name; returns the path.</summary>
    public static async Task<string> SaveToDownloadsAsync(string fileName, Func<CancellationToken, Task<System.IO.Stream>> open)
    {
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        System.IO.Directory.CreateDirectory(folder);
        var taken = System.IO.Directory.EnumerateFileSystemEntries(folder).Select(System.IO.Path.GetFileName).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string target = System.IO.Path.Combine(folder, Core.Logic.FileNameValidator.GetUniqueName(fileName, taken));
        string partial = target + ".akiopart";
        try
        {
            await using (var source = await open(CancellationToken.None))
            await using (var file = new System.IO.FileStream(partial, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None, 1 << 20, true))
                await source.CopyToAsync(file, 1 << 20);
            System.IO.File.Move(partial, target);
        }
        catch
        {
            System.IO.File.Delete(partial);
            throw;
        }
        return target;
    }

    // ------------------------------------------------------------------ description, shortcut, lock, share, comments, activity

    private bool CanSingle<T>() => Provider is T && _selected.Count == 1 && !_selected[0].IsDrive;

    private bool CanEditDescription() => CanSingle<IDescriptionProvider>();

    [RelayCommand(CanExecute = nameof(CanEditDescription))]
    private void EditDescription() => RequestDialog(PaneDialog.Description);

    private bool CanAddShortcut() => Provider is IShortcutProvider && _selected.Count > 0;

    /// <summary>Drive "Add shortcut": shortcuts in My Drive to the selected items (typically ones shared with you).</summary>
    [RelayCommand(CanExecute = nameof(CanAddShortcut))]
    private async Task AddShortcutAsync()
    {
        if (Provider is not IShortcutProvider shortcuts) return;
        var items = SelectedCloudItems();
        string root = Provider.RootPath;
        string where = Provider.DisplayName;
        await RunCloudAsync(async () =>
        {
            foreach (var item in items) await shortcuts.CreateShortcutAsync(item, root, CancellationToken.None);
            ShowMessage(items.Count == 1 ? $"Added a shortcut to \"{items[0].Name}\" in {where}." : $"Added {items.Count:N0} shortcuts in {where}.", isError: false);
        });
    }

    private bool CanLock() => CanSingle<ILockProvider>() && !_selected[0].IsContainer;

    [RelayCommand(CanExecute = nameof(CanLock))]
    private async Task ToggleLockAsync()
    {
        if (Provider is not ILockProvider locks || _selected.Count != 1) return;
        var item = _selected[0].Item;
        await RunCloudAsync(async () =>
        {
            bool locked = !await locks.IsLockedAsync(item, CancellationToken.None);
            await locks.SetLockedAsync(item, locked, CancellationToken.None);
            ShowMessage(locked ? $"\"{item.Name}\" is locked: nobody can edit it until it's unlocked." : $"\"{item.Name}\" is unlocked.", isError: false);
        });
    }

    private bool CanShare() => CanSingle<ISharingProvider>();

    [RelayCommand(CanExecute = nameof(CanShare))]
    private void Share() => RequestDialog(PaneDialog.Share);

    private bool CanComment() => CanSingle<ICommentsProvider>() && !_selected[0].IsContainer;

    [RelayCommand(CanExecute = nameof(CanComment))]
    private void ShowComments() => RequestDialog(PaneDialog.Comments);

    private bool CanShowActivity() => CanSingle<IActivityProvider>();

    [RelayCommand(CanExecute = nameof(CanShowActivity))]
    private void ShowActivity() => RequestDialog(PaneDialog.Activity);

    /// <summary>Runs a cloud call with the pane's loading indicator and error bar.</summary>
    private async Task RunCloudAsync(Func<Task> work)
    {
        _loadingDelay.Start();
        try
        {
            await work();
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loadingDelay.Stop();
            IsLoading = false;
        }
    }
}
