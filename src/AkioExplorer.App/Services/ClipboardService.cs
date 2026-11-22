using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Local;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AkioExplorer.App.Services;

public sealed record ClipboardFiles(string ProviderId, IReadOnlyList<StorageItem> Items, bool IsCut);

/// <summary>
/// Copy / cut / paste of files through the Windows clipboard, interoperable with File Explorer in both directions.
/// Our own entries keep full metadata (and later cloud items) in memory; local files are also published as
/// StorageItems (rendered lazily) so Explorer can paste them.
/// </summary>
public sealed class ClipboardService
{
    private const string OwnerProperty = "AkioClipboardId";

    private readonly IStorageProviderRegistry _registry;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<ClipboardService> _logger;
    private ClipboardFiles? _own;
    private string? _ownId;
    private HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);

    public ClipboardService(IStorageProviderRegistry registry, DispatcherQueue dispatcher, ILogger<ClipboardService> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _logger = logger;
        Clipboard.ContentChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    public bool HasFiles { get; private set; }

    public IReadOnlySet<string> CutPaths => _cutPaths;

    /// <summary>Raised on the UI thread when paste availability or the set of cut items changes.</summary>
    public event EventHandler? Changed;

    public void SetFiles(string providerId, IReadOnlyList<StorageItem> items, bool cut)
    {
        if (items.Count == 0) return;
        string id = Guid.NewGuid().ToString("N");
        var package = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        package.Properties.Add(OwnerProperty, id);

        var paths = items.Select(i => i.Path).ToArray();
        if (providerId == LocalStorageProvider.ProviderId)
        {
            package.SetDataProvider(StandardDataFormats.StorageItems, request => ProvideStorageItems(request, paths));
            package.SetText(string.Join(Environment.NewLine, paths));
        }

        _own = new ClipboardFiles(providerId, items, cut);
        _ownId = id;
        _cutPaths = cut ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HasFiles = true;

        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard is busy");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static async void ProvideStorageItems(DataProviderRequest request, IReadOnlyList<string> paths)
    {
        var deferral = request.GetDeferral();
        try
        {
            request.SetData(await ToStorageItemsAsync(paths));
        }
        catch
        {
            // Item vanished between copy and paste: the target app simply gets nothing.
        }
        finally
        {
            deferral.Complete();
        }
    }

    public static async Task<IReadOnlyList<IStorageItem>> ToStorageItemsAsync(IReadOnlyList<string> paths)
    {
        var result = new List<IStorageItem>(paths.Count);
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) result.Add(await StorageFolder.GetFolderFromPathAsync(path));
            else if (File.Exists(path)) result.Add(await StorageFile.GetFileFromPathAsync(path));
        }
        return result;
    }

    /// <summary>Reads files from the clipboard (ours or File Explorer's). Null if the clipboard holds no files.</summary>
    public async Task<ClipboardFiles?> GetFilesAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (_own is not null && view.Properties.TryGetValue(OwnerProperty, out var owner) && owner as string == _ownId)
                return _own;

            if (!view.Contains(StandardDataFormats.StorageItems)) return null;
            var storageItems = await view.GetStorageItemsAsync();
            bool cut = (view.RequestedOperation & DataPackageOperation.Move) != 0;
            var items = await ResolveLocalAsync(storageItems.Select(s => s.Path));
            return items.Count == 0 ? null : new ClipboardFiles(LocalStorageProvider.ProviderId, items, cut);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't read files from the clipboard");
            return null;
        }
    }

    /// <summary>Turns external file paths (clipboard, drag & drop from Explorer) into storage items.</summary>
    public async Task<IReadOnlyList<StorageItem>> ResolveLocalAsync(IEnumerable<string> paths)
    {
        // All lookups at once (order kept): pasting 1,000 files from Explorer shouldn't wait for 1,000 round trips.
        var local = _registry.Get(LocalStorageProvider.ProviderId);
        var found = await Task.WhenAll(paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => local.GetItemAsync(p, CancellationToken.None)));
        return found.OfType<StorageItem>().ToList();
    }

    /// <summary>After a cut is pasted the clipboard is emptied, like File Explorer.</summary>
    public void ClearCut()
    {
        if (_own is not { IsCut: true }) return;
        try { Clipboard.Clear(); } catch { /* best effort */ }
        _own = null;
        _ownId = null;
        _cutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HasFiles = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        bool hasFiles;
        string? owner = null;
        try
        {
            var view = Clipboard.GetContent();
            hasFiles = view.Contains(StandardDataFormats.StorageItems) || view.Properties.ContainsKey(OwnerProperty);
            if (view.Properties.TryGetValue(OwnerProperty, out var o)) owner = o as string;
        }
        catch
        {
            hasFiles = HasFiles;
            owner = _ownId;
        }

        if (owner != _ownId)
        {
            // Someone else put something on the clipboard: our cut marks no longer apply.
            _own = null;
            _ownId = null;
            _cutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        HasFiles = hasFiles || _own is not null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
