using System.IO.Enumeration;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AkioExplorer.App.ViewModels;

/// <summary>Dialogs a pane command needs; the view shows them (view models don't build UI).</summary>
public enum PaneDialog
{
    BatchRename,
    SelectByPattern,
    DeselectByPattern,
    Checksum,
    Trash,
    Versions,
    OpenLink,
    Description,
    Share,
    Comments,
    Activity,
}

/// <summary>Power-user tools: selection helpers, new file, batch rename, ZIP, folder sizes, attributes, terminal.</summary>
public sealed partial class FilePaneViewModel
{
    private readonly Dictionary<string, long> _folderSizes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _sizeCts;

    /// <summary>Asks the view to show one of the pane's dialogs.</summary>
    public event EventHandler<PaneDialog>? DialogRequested;

    public void RequestDialog(PaneDialog dialog) => DialogRequested?.Invoke(this, dialog);

    /// <summary>The pane shows a real folder on this PC (the tools that need Windows paths work here).</summary>
    public bool IsLocalFolder => Provider?.Id == LocalStorageProvider.ProviderId && !IsAtRoot && !IsSearchMode;

    private bool IsLocalProvider => Provider?.Id == LocalStorageProvider.ProviderId;

    private List<FileItemViewModel> SelectedNonDrives => _selected.Where(s => !s.IsDrive).ToList();

    // CanExecute checks run on every selection change: test without building lists.
    private bool HasNonDriveSelection => _selected.Exists(s => !s.IsDrive);

    private static bool IsZip(FileItemViewModel s) => !s.IsContainer && s.Item.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private void UpdateToolCommandStates()
    {
        NewTextFileCommand.NotifyCanExecuteChanged();
        BatchRenameCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        CompressCommand.NotifyCanExecuteChanged();
        ExtractHereCommand.NotifyCanExecuteChanged();
        ExtractToFolderCommand.NotifyCanExecuteChanged();
        ChecksumCommand.NotifyCanExecuteChanged();
        CalculateFolderSizesCommand.NotifyCanExecuteChanged();
        ToggleHiddenAttributeCommand.NotifyCanExecuteChanged();
        ToggleReadOnlyAttributeCommand.NotifyCanExecuteChanged();
        OpenTerminalCommand.NotifyCanExecuteChanged();
        OpenInEditorCommand.NotifyCanExecuteChanged();
        NewFolderFromSelectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLocalFolder));
    }

    private void OnSettingsChanged()
    {
        FileItemViewModel.ShowExtensions = _settings.Current.ShowFileExtensions;
        bool formatChanged = Formatting.DecimalUnits != _settings.Current.DecimalSizeUnits || Formatting.DateDisplay != _settings.Current.DateDisplay;
        Formatting.DecimalUnits = _settings.Current.DecimalSizeUnits;
        Formatting.DateDisplay = _settings.Current.DateDisplay;
        foreach (var vm in _all)
        {
            vm.RefreshDisplayName();
            if (formatChanged) vm.RefreshFormatting();
        }
        ApplyView(replace: false);
        StartAutoFolderSizes();
    }

    // ------------------------------------------------------------------ selection

    /// <summary>Selects these items in the list (empty clears the selection).</summary>
    public void SelectItems(IReadOnlyList<FileItemViewModel> items) => SelectionRequested?.Invoke(this, items);

    [RelayCommand]
    private void SelectAll() => SelectItems(Items.ToList());

    [RelayCommand]
    private void SelectNone() => SelectItems([]);

    [RelayCommand]
    private void InvertSelection()
    {
        var selected = new HashSet<FileItemViewModel>(_selected);
        SelectItems(Items.Where(i => !selected.Contains(i)).ToList());
    }

    /// <summary>Selects (or, with <paramref name="add"/> false, deselects) items matching "*.jpg; *.png" style patterns.</summary>
    public int SelectByPattern(string patterns, bool add)
    {
        var parts = patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.IndexOfAny(['*', '?']) >= 0 ? p : $"*{p}*")
            .ToList();
        if (parts.Count == 0) return 0;

        bool Matches(FileItemViewModel vm) => parts.Any(p => FileSystemName.MatchesSimpleExpression(p, vm.Name, ignoreCase: true));
        var matched = Items.Where(Matches).ToList();
        var result = new HashSet<FileItemViewModel>(_selected);
        foreach (var vm in matched)
        {
            if (add) result.Add(vm);
            else result.Remove(vm);
        }
        SelectItems(Items.Where(result.Contains).ToList());
        return matched.Count;
    }

    /// <summary>Selects every item with the same extension (or every folder) as the selection.</summary>
    [RelayCommand]
    private void SelectSameType()
    {
        if (_selected.Count == 0) return;
        var exts = _selected.Where(s => !s.IsContainer).Select(s => s.Item.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool folders = _selected.Any(s => s.IsContainer);
        SelectItems(Items.Where(i => i.IsContainer ? folders : exts.Contains(i.Item.Extension)).ToList());
    }

    // ------------------------------------------------------------------ new file / duplicate / names

    private bool CanCreateFile() => IsLocalFolder;

    /// <summary>Ctrl+Alt+N: "New Text Document.txt", then inline rename (name selected without the extension).</summary>
    [RelayCommand(CanExecute = nameof(CanCreateFile))]
    private async Task NewTextFileAsync()
    {
        if (Provider is null || Location is null || !CanCreateFile()) return;
        var names = new HashSet<string>(_all.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        string name = FileNameValidator.GetUniqueName("New Text Document.txt", names);
        try
        {
            var item = await LocalFileTools.CreateFileAsync(Location.Path, name, CancellationToken.None);
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
                AddSorted(vm);
            }
            ApplyView(replace: false);
            _cache.Invalidate(Location);
            SelectionRequested?.Invoke(this, [vm]);
            var provider = Provider;
            string path = item.Path;
            _undo.Push("New file", () => provider.DeleteAsync([path], permanent: false, CancellationToken.None));
            BeginRename(vm);
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
    }

    private bool CanDuplicate() => CanCopySelection() && CanWriteHere;

    /// <summary>Copies the selection next to itself ("name (2).ext"), like Google Drive's "Make a copy".</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate()
    {
        if (Provider is null || Location is null || !CanDuplicate()) return;
        _operations.Enqueue(OperationKind.Copy, Provider.Id, _selected.Select(s => s.Item).ToList(), Location,
            new TransferOptions { Conflict = ConflictResolution.KeepBoth });
    }

    [RelayCommand]
    private void CopyName()
    {
        var names = _selected.Count > 0 ? _selected.Select(s => s.Name).ToList()
            : Title.Length > 0 ? [Title] : [];
        if (names.Count == 0) return;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, names));
        Clipboard.SetContent(package);
    }

    private bool CanOpenTerminal() => IsLocalFolder || (IsLocalProvider && _selected.Count == 1 && _selected[0].IsContainer);

    /// <summary>Opens Windows Terminal in the selected folder, or in the current one.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
    private void OpenTerminal()
    {
        string? folder = _selected.Count == 1 && _selected[0].IsContainer ? _selected[0].Path : Location?.Path;
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            _shellActions.OpenTerminal(folder, _settings.Current.Terminal);
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
    }

    // ------------------------------------------------------------------ code editors, open with, shell menu

    /// <summary>Shell context menus, "Open with" apps and installed editors (for the view's menus).</summary>
    public IShellMenuService ShellMenus => _shellMenus;

    private bool CanOpenInEditor() => IsLocalFolder || (IsLocalProvider && _selected.Count > 0 && !_selected.Any(s => s.IsDrive && s.Path.Length > 3));

    /// <summary>Paths an editor should open: the selection, or the current folder when nothing is selected.</summary>
    public IReadOnlyList<string> EditorTargets() =>
        _selected.Count > 0 ? _selected.Select(s => s.Path).ToList() :
        IsLocalFolder && Location is not null ? [Location.Path] : [];

    public async Task<CodeEditor?> GetPreferredEditorAsync()
    {
        var editors = await _shellMenus.GetCodeEditorsAsync();
        string id = _settings.Current.PreferredEditor;
        return editors.FirstOrDefault(e => e.Id == id) ?? editors.FirstOrDefault();
    }

    /// <summary>Ctrl+Shift+E: opens the selection (or this folder) in VS Code / the preferred editor.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInEditor))]
    private async Task OpenInEditorAsync()
    {
        var editor = await GetPreferredEditorAsync();
        if (editor is null)
        {
            ShowError("No code editor found. Install VS Code (or Cursor, Zed, Sublime Text) and try again.");
            return;
        }
        OpenInEditor(editor);
    }

    public void OpenInEditor(CodeEditor editor)
    {
        var targets = EditorTargets();
        if (targets.Count == 0) return;
        try
        {
            // Several files: one window each would be noisy; editors accept many paths but folders are opened one by one.
            foreach (var path in targets.Take(8)) _shellMenus.OpenInEditor(editor, path);
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't start {editor.Name}: {ex.Message}");
        }
    }

    public async Task OpenWithAppAsync(OpenWithApp app)
    {
        var files = _selected.Where(s => !s.IsContainer).Select(s => s.Path).ToList();
        if (files.Count == 0) return;
        try
        {
            await _shellMenus.OpenWithAsync(app, files);
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't open with {app.Name}: {ex.Message}");
        }
    }

    /// <summary>Settings › "Hidden menu items": captions or verbs, comma separated.</summary>
    public bool IsShellEntryHidden(ShellMenuEntry entry)
    {
        string list = _settings.Current.HiddenShellMenuItems;
        if (string.IsNullOrWhiteSpace(list) || entry.IsSeparator) return false;
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals(entry.Text, StringComparison.OrdinalIgnoreCase) || raw.Equals(entry.Verb, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Windows context menu entries (shell extensions) for the selection, or for the folder background.</summary>
    public Task<IShellMenu?> GetShellMenuAsync(bool background, bool extended)
    {
        if (!_settings.Current.ShowShellMenuItems || !IsLocalProvider || IsAtRoot && background) return Task.FromResult<IShellMenu?>(null);
        if (background)
            return Location is { Path.Length: > 0 } location && !IsSearchMode ? _shellMenus.GetBackgroundMenuAsync(location.Path, extended) : Task.FromResult<IShellMenu?>(null);
        var paths = _selected.Select(s => s.Path).ToList();
        if (paths.Count == 0) return Task.FromResult<IShellMenu?>(null);
        // One folder only: IContextMenu needs a common parent (search results can mix folders).
        string? parent = Path.GetDirectoryName(paths[0]);
        paths = paths.Where(p => string.Equals(Path.GetDirectoryName(p), parent, StringComparison.OrdinalIgnoreCase)).ToList();
        return _shellMenus.GetItemMenuAsync(paths, extended);
    }

    private bool CanNewFolderFromSelection() => CanCreateFolder() && HasMovableSelection && HasNonDriveSelection;

    /// <summary>Moves the selection into a new folder (named after the item when only one is selected), then renames it.</summary>
    [RelayCommand(CanExecute = nameof(CanNewFolderFromSelection))]
    private async Task NewFolderFromSelectionAsync()
    {
        if (Provider is null || Location is null) return;
        var items = SelectedNonDrives;
        if (items.Count == 0) return;
        var names = new HashSet<string>(_all.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        string baseName = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : "New folder";
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "New folder";
        string name = FileNameValidator.GetUniqueName(baseName, names);
        try
        {
            var folder = await Provider.CreateFolderAsync(Location.Path, name, CancellationToken.None);
            _operations.Enqueue(OperationKind.Move, Provider.Id, items.Select(i => i.Item).ToList(), new StorageLocation(Provider.Id, folder.Path));
            if (!_byPath.TryGetValue(folder.Path, out var vm))
            {
                vm = CreateItem(folder);
                _byPath[folder.Path] = vm;
                AddSorted(vm);
            }
            ApplyView(replace: false);
            _cache.Invalidate(Location);
            SelectionRequested?.Invoke(this, [vm]);
            BeginRename(vm);
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
        }
    }

    // ------------------------------------------------------------------ batch rename

    private bool CanBatchRename() => _selected.Count > 0 && !IsSearchMode && Supports(StorageCapabilities.Rename) && !_selected.Any(s => s.IsDrive);

    /// <summary>Ctrl+M (or F2 with several items): the multi-rename dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanBatchRename))]
    private void BatchRename()
    {
        if (CanBatchRename()) RequestDialog(PaneDialog.BatchRename);
    }

    /// <summary>Names in the current listing that are not part of <paramref name="items"/> (for clash checks).</summary>
    public IReadOnlyList<string> SiblingNames => _all.Select(v => v.Name).ToList();

    public string FolderName => Provider is not null && Location is not null ? Provider.GetDisplayName(Location.Path) : string.Empty;

    /// <summary>Applies a rename plan. One Ctrl+Z undoes every rename. Returns how many items were renamed.</summary>
    public async Task<int> ApplyBatchRenameAsync(IReadOnlyList<RenamePlanEntry> plan)
    {
        if (Provider is not { } provider) return 0;
        var changes = plan.Where(p => p.IsChange).Select(p => (p.Item.Path, p.Item.Name, p.NewName)).ToList();
        if (changes.Count == 0) return 0;

        _loadingDelay.Start();
        var (done, error) = await RenameManyAsync(provider, changes);
        _loadingDelay.Stop();
        IsLoading = false;

        if (done.Count > 0)
        {
            var reverse = done.Select(d => (d.NewPath, d.NewName, d.OldName)).ToList();
            _undo.Push(done.Count == 1 ? $"Rename \"{done[0].OldName}\"" : $"Rename {done.Count:N0} items", async () =>
            {
                var (_, undoError) = await RenameManyAsync(provider, reverse);
                if (undoError is not null) throw undoError;
            });
        }

        if (Location is not null) _cache.Invalidate(Location);
        SelectWhenListed(done.Select(d => d.NewPath));
        if (error is not null) ShowStorageError(error);
        else ShowMessage(done.Count == 1 ? "Renamed 1 item." : $"Renamed {done.Count:N0} items.", isError: false);
        return done.Count;
    }

    /// <summary>
    /// Renames in two phases (via temporary names) when one item takes another's old name, e.g. swapping "a" and "b".
    /// On failure, items already moved to a temporary name go back to their original name.
    /// </summary>
    private static async Task<(List<(string NewPath, string OldName, string NewName)> Done, StorageException? Error)> RenameManyAsync(
        IStorageProvider provider, IReadOnlyList<(string Path, string OldName, string NewName)> renames)
    {
        var done = new List<(string, string, string)>();
        var sources = renames.Select(r => r.OldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool twoPhase = renames.Any(r => sources.Contains(r.NewName) && !string.Equals(r.OldName, r.NewName, StringComparison.OrdinalIgnoreCase));
        var work = renames.ToList();

        if (twoPhase)
        {
            var staged = new List<(string Path, string OldName, string NewName)>();
            foreach (var r in work)
            {
                try
                {
                    var temp = await provider.RenameAsync(r.Path, $"~akio-{Guid.NewGuid():N}"[..20] + Path.GetExtension(r.OldName), CancellationToken.None);
                    staged.Add((temp.Path, r.OldName, r.NewName));
                }
                catch (StorageException ex)
                {
                    foreach (var s in staged) await TryRenameAsync(provider, s.Path, s.OldName);
                    return (done, ex);
                }
            }
            work = staged;
        }

        for (int i = 0; i < work.Count; i++)
        {
            var r = work[i];
            try
            {
                var item = await provider.RenameAsync(r.Path, r.NewName, CancellationToken.None);
                done.Add((item.Path, r.OldName, r.NewName));
            }
            catch (StorageException ex)
            {
                if (twoPhase)
                {
                    for (int j = i; j < work.Count; j++) await TryRenameAsync(provider, work[j].Path, work[j].OldName);
                }
                return (done, ex);
            }
        }
        return (done, null);
    }

    private static async Task TryRenameAsync(IStorageProvider provider, string path, string name)
    {
        try
        {
            await provider.RenameAsync(path, name, CancellationToken.None);
        }
        catch (StorageException)
        {
        }
    }

    // ------------------------------------------------------------------ ZIP

    private bool CanCompress() => IsLocalProvider && HasNonDriveSelection && !IsAtRoot;

    [RelayCommand(CanExecute = nameof(CanCompress))]
    private async Task CompressAsync()
    {
        if (!CanCompress()) return;
        var items = SelectedNonDrives;
        string folder = Path.GetDirectoryName(items[0].Path.TrimEnd('\\'))!;
        string zipName = FileNameValidator.GetUniqueName(ArchiveBaseName(items, folder) + ".zip", ExistingNames(folder));
        string zipPath = Path.Combine(folder, zipName);

        await RunWithProgressAsync($"Compressing to \"{zipName}\"", async (progress, ct) =>
        {
            await LocalFileTools.CompressAsync(items.Select(i => i.Path).ToList(), zipPath, progress, ct);
            _undo.Push($"Compress to \"{zipName}\"", () => _registry.Get(LocalStorageProvider.ProviderId).DeleteAsync([zipPath], permanent: false, CancellationToken.None));
            SelectWhenListed([zipPath]);
            return $"Created \"{zipName}\".";
        });
    }

    /// <summary>"Photos" for one item (folder name, or file name without extension), else the parent folder's name.</summary>
    private static string ArchiveBaseName(IReadOnlyList<FileItemViewModel> items, string folder) =>
        items.Count == 1
            ? (items[0].IsContainer ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name))
            : Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } parent ? parent : "Archive";

    // ------------------------------------------------------------------ installed archivers (7-Zip, WinRAR…)

    /// <summary>7-Zip, WinRAR, NanaZip, Bandizip, PeaZip when installed (only for files on this PC).</summary>
    public Task<IReadOnlyList<ArchiverApp>> GetArchiversAsync() =>
        IsLocalProvider ? _archivers.GetArchiversAsync() : Task.FromResult<IReadOnlyList<ArchiverApp>>([]);

    /// <summary>Selected items that can go into an archive: not drives, and all in the first item's folder.</summary>
    private List<FileItemViewModel> ArchivableSelection()
    {
        var items = SelectedNonDrives;
        if (items.Count == 0) return items;
        string? folder = Path.GetDirectoryName(items[0].Path.TrimEnd('\\'));
        return items.Where(i => string.Equals(Path.GetDirectoryName(i.Path.TrimEnd('\\')), folder, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool CanCompressWithArchiver => CanCompress();

    /// <summary>Selected archive files of any format an archiver opens (.rar, .7z, .tar.gz, .iso…).</summary>
    public IReadOnlyList<FileItemViewModel> SelectedArchives =>
        IsLocalProvider ? _selected.Where(s => !s.IsContainer && ArchiverCommands.IsArchive(s.Path)).ToList() : [];

    /// <summary>Free archive name for the selection in this format: "Photos.rar", or "Photos (2).rar" when taken.</summary>
    public string ArchiveNameFor(string format)
    {
        var items = ArchivableSelection();
        if (items.Count == 0) return string.Empty;
        string folder = Path.GetDirectoryName(items[0].Path.TrimEnd('\\'))!;
        // The current folder's names are already in memory; search results may sit elsewhere.
        var names = Location is not null && string.Equals(folder, Location.Path, StringComparison.OrdinalIgnoreCase)
            ? _all.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : ExistingNames(folder);
        return FileNameValidator.GetUniqueName($"{ArchiveBaseName(items, folder)}.{format}", names);
    }

    /// <summary>Compresses the selection with an installed archiver into <c>Name.format</c>, or opens its own dialog.</summary>
    public void CompressWith(ArchiverApp app, string? format)
    {
        var items = ArchivableSelection();
        if (items.Count == 0) return;
        string folder = Path.GetDirectoryName(items[0].Path.TrimEnd('\\'))!;
        string name = ArchiveNameFor(format ?? ArchiverCommands.CompressFormats(app.Kind).FirstOrDefault() ?? "7z");
        string archive = Path.Combine(folder, name);
        if (RunArchiver(app, ArchiverCommands.Compress(app, items.Select(i => i.Path).ToList(), archive, showDialog: format is null))
            && format is not null)
        {
            SelectWhenListed([archive]);
        }
    }

    /// <summary>Extracts the selected archives with an installed archiver, here or each into its own folder.</summary>
    public void ExtractWith(ArchiverApp app, bool toFolder)
    {
        var archives = SelectedArchives;
        var created = new List<string>();
        foreach (var archive in archives)
        {
            if (!RunArchiver(app, ArchiverCommands.Extract(app, archive.Path, toFolder))) return;
            if (toFolder) created.Add(Path.Combine(Path.GetDirectoryName(archive.Path)!, ArchiverCommands.ExtractFolderName(archive.Path)));
        }
        if (created.Count > 0) SelectWhenListed(created);
    }

    private bool RunArchiver(ArchiverApp app, ArchiverInvocation invocation)
    {
        try
        {
            // The archiver shows its own progress, password and overwrite prompts; the folder watcher shows the result.
            _archivers.Start(invocation);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowError($"Couldn't start {app.Name}: {ex.Message}");
            return false;
        }
    }

    private List<FileItemViewModel> SelectedZips => _selected.Where(IsZip).ToList();

    private bool CanExtract() => IsLocalProvider && _selected.Exists(IsZip);

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private Task ExtractHereAsync() => ExtractAsync(intoOwnFolder: false);

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private Task ExtractToFolderAsync() => ExtractAsync(intoOwnFolder: true);

    private async Task ExtractAsync(bool intoOwnFolder)
    {
        var zips = SelectedZips;
        if (zips.Count == 0) return;
        await RunWithProgressAsync(zips.Count == 1 ? $"Extracting \"{zips[0].Name}\"" : $"Extracting {zips.Count} archives", async (progress, ct) =>
        {
            int files = 0;
            var created = new List<string>();
            for (int i = 0; i < zips.Count; i++)
            {
                string zip = zips[i].Path;
                string parent = Path.GetDirectoryName(zip)!;
                string target = parent;
                if (intoOwnFolder)
                {
                    target = Path.Combine(parent, FileNameValidator.GetUniqueName(Path.GetFileNameWithoutExtension(zip), ExistingNames(parent)));
                    created.Add(target);
                }
                int index = i;
                var part = new Progress<double>(p => ((IProgress<double>)progress).Report((index + p) / zips.Count));
                files += await LocalFileTools.ExtractAsync(zip, target, part, ct);
            }
            if (created.Count > 0)
            {
                _undo.Push("Extract", () => _registry.Get(LocalStorageProvider.ProviderId).DeleteAsync(created, permanent: false, CancellationToken.None));
                SelectWhenListed(created);
            }
            return files == 1 ? "Extracted 1 file." : $"Extracted {files:N0} files.";
        });
    }

    private static HashSet<string> ExistingNames(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Runs a local job with a percent message in the pane's info bar.</summary>
    private async Task RunWithProgressAsync(string title, Func<Progress<double>, CancellationToken, Task<string>> job)
    {
        int lastPercent = -1;
        var progress = new Progress<double>(p =>
        {
            int percent = (int)(Math.Clamp(p, 0, 1) * 100);
            if (percent == lastPercent) return;
            lastPercent = percent;
            ShowMessage($"{title}… {percent}%", isError: false);
        });
        ShowMessage($"{title}…", isError: false);
        try
        {
            string done = await job(progress, CancellationToken.None);
            lastPercent = int.MaxValue; // late progress callbacks must not overwrite the result
            ShowMessage(done, isError: false);
            _ = RefreshSilentlyAsync();
        }
        catch (StorageException ex)
        {
            lastPercent = int.MaxValue;
            ShowStorageError(ex);
        }
    }

    // ------------------------------------------------------------------ checksum

    private bool CanChecksum() => IsLocalProvider && _selected.Count == 1 && !_selected[0].IsContainer;

    [RelayCommand(CanExecute = nameof(CanChecksum))]
    private void Checksum()
    {
        if (CanChecksum()) RequestDialog(PaneDialog.Checksum);
    }

    // ------------------------------------------------------------------ attributes

    private bool CanToggleAttribute() => IsLocalProvider && HasNonDriveSelection;

    [RelayCommand(CanExecute = nameof(CanToggleAttribute))]
    private Task ToggleHiddenAttributeAsync() => ToggleAttributeAsync(FileAttributes.Hidden, StorageItemAttributes.Hidden);

    [RelayCommand(CanExecute = nameof(CanToggleAttribute))]
    private Task ToggleReadOnlyAttributeAsync() => ToggleAttributeAsync(FileAttributes.ReadOnly, StorageItemAttributes.ReadOnly);

    /// <summary>True when every selected item has the attribute (the menu shows a check mark).</summary>
    public bool SelectionHas(StorageItemAttributes attribute) =>
        _selected.Count > 0 && _selected.All(s => (s.Item.Attributes & attribute) != 0);

    private async Task ToggleAttributeAsync(FileAttributes attribute, StorageItemAttributes mapped)
    {
        var items = SelectedNonDrives;
        if (items.Count == 0) return;
        bool on = !items.All(i => (i.Item.Attributes & mapped) != 0);
        var paths = items.Select(i => i.Path).ToList();
        try
        {
            await LocalFileTools.SetAttributeAsync(paths, attribute, on);
            _undo.Push(on ? $"Set {attribute}" : $"Clear {attribute}", () => LocalFileTools.SetAttributeAsync(paths, attribute, !on));
            if (Location is not null) _cache.Invalidate(Location);
            SelectWhenListed(paths);
            if (attribute == FileAttributes.Hidden && on && !_settings.Current.ShowHiddenItems)
                ShowMessage(items.Count == 1 ? "The item is hidden now. Ctrl+H shows hidden items." : $"{items.Count} items are hidden now. Ctrl+H shows hidden items.", isError: false);
        }
        catch (StorageException ex)
        {
            ShowStorageError(ex);
            _ = RefreshSilentlyAsync();
        }
    }

    // ------------------------------------------------------------------ folder sizes

    private bool CanCalculateFolderSizes() => IsLocalFolder || (IsLocalProvider && _selected.Any(s => s.IsContainer && !s.IsDrive));

    /// <summary>Fills the Size column for the selected folders, or for every folder here (Ctrl+Alt+S).</summary>
    [RelayCommand(CanExecute = nameof(CanCalculateFolderSizes))]
    private void CalculateFolderSizes()
    {
        var folders = _selected.Where(s => s.IsContainer && !s.IsDrive).ToList();
        if (folders.Count == 0) folders = _all.Where(v => v.Item.Kind == StorageItemKind.Folder).ToList();
        StartFolderSizes(folders, announce: true);
    }

    private void StartAutoFolderSizes()
    {
        if (!_settings.Current.AutoFolderSizes || !IsLocalFolder) return;
        var pending = _all.Where(v => v.Item.Kind == StorageItemKind.Folder && !_folderSizes.ContainsKey(v.Path)).ToList();
        if (pending.Count > 0) StartFolderSizes(pending, announce: false);
    }

    private void ResetFolderSizes()
    {
        _sizeCts?.Cancel();
        _sizeCts = null;
        _folderSizes.Clear();
    }

    private StorageItem WithKnownFolderSize(StorageItem item) =>
        item.Kind == StorageItemKind.Folder && item.Size is null && _folderSizes.TryGetValue(item.Path, out long size)
            ? item with { Size = size }
            : item;

    private void StartFolderSizes(IReadOnlyList<FileItemViewModel> folders, bool announce)
    {
        if (folders.Count == 0) return;
        _sizeCts?.Cancel();
        var cts = _sizeCts = new CancellationTokenSource();
        var location = Location;
        var paths = folders.Select(f => f.Path).ToList();
        if (announce) ShowMessage(paths.Count == 1 ? "Calculating folder size…" : $"Calculating {paths.Count:N0} folder sizes…", isError: false);

        _ = Task.Run(async () =>
        {
            long total = 0;
            await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token }, (path, ct) =>
            {
                var size = LocalFileTools.GetFolderSize(path, ct);
                Interlocked.Add(ref total, size.Bytes);
                _dispatcher.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested || !Equals(location, Location)) return;
                    _folderSizes[path] = size.Bytes;
                    if (_byPath.TryGetValue(path, out var vm)) vm.Update(vm.Item with { Size = size.Bytes });
                });
                return ValueTask.CompletedTask;
            });
            _dispatcher.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || !Equals(location, Location)) return;
                if (SortField == SortField.Size) Resort();
                UpdateSelectionText();
                if (announce) ShowMessage(paths.Count == 1
                    ? $"Folder size: {Formatting.FormatSize(total)}"
                    : $"{paths.Count:N0} folders · {Formatting.FormatSize(total)} in total", isError: false);
            });
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
