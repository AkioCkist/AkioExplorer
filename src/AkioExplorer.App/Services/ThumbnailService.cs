using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AkioExplorer.App.Services;

/// <summary>
/// Picture thumbnails for grid view (photos, video frames, album art, document pages) instead of the type icon.
/// Only tiles on screen ask; a recycled tile cancels its request; a few run at a time so scrolling stays smooth.
/// Local files come from the Windows thumbnail cache, cloud files from the service's own renditions.
/// UI thread only.
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>Pixel size requested (tiles show ~64 DIP; this stays sharp at 150% scale).</summary>
    public const int PixelSize = 128;

    private const int MaxCached = 4000;

    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<FileItemViewModel, CancellationTokenSource> _pending = new();
    private readonly SemaphoreSlim _localGate = new(4);
    private readonly SemaphoreSlim _cloudGate = new(3);

    public async void Request(FileItemViewModel vm, IStorageProvider? provider)
    {
        if (provider is null || vm.IsContainer || vm.ThumbnailRequested || _pending.ContainsKey(vm)) return;
        var item = vm.Item;
        bool isLocal = (provider.Capabilities & StorageCapabilities.OpenWithShell) != 0;
        var thumbnails = provider as IThumbnailProvider;
        if (isLocal ? !PreviewClassifier.HasVisualThumbnail(item) : thumbnails?.MayHaveThumbnail(item) != true)
        {
            vm.ThumbnailRequested = true; // nothing to fetch: never ask again for this item
            return;
        }

        string key = $"{provider.Id}|{item.Path}|{item.Modified?.UtcTicks}|{item.Size}";
        if (_cache.TryGetValue(key, out var cached))
        {
            vm.ThumbnailRequested = true;
            vm.Thumbnail = cached;
            return;
        }

        var cts = new CancellationTokenSource();
        _pending[vm] = cts;
        var gate = isLocal ? _localGate : _cloudGate;
        try
        {
            await gate.WaitAsync(cts.Token);
            ImageSource? source;
            try
            {
                source = isLocal ? await LocalAsync(item.Path, cts.Token) : await CloudAsync(thumbnails!, item, cts.Token);
            }
            finally
            {
                gate.Release();
            }

            Remember(key, source);
            if (ReferenceEquals(vm.Item, item))
            {
                vm.ThumbnailRequested = true;
                vm.Thumbnail = source;
            }
        }
        catch (OperationCanceledException)
        {
            // Tile scrolled away: it asks again when it comes back.
        }
        catch (Exception)
        {
            vm.ThumbnailRequested = true; // unreadable / offline: keep the icon
        }
        finally
        {
            if (_pending.TryGetValue(vm, out var current) && current == cts) _pending.Remove(vm);
            cts.Dispose();
        }
    }

    /// <summary>The tile went off screen before its thumbnail arrived.</summary>
    public void Cancel(FileItemViewModel vm)
    {
        if (_pending.Remove(vm, out var cts)) cts.Cancel();
    }

    private static async Task<ImageSource?> LocalAsync(string path, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        ct.ThrowIfCancellationRequested();
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, PixelSize, ThumbnailOptions.ResizeThumbnail);
        ct.ThrowIfCancellationRequested();
        // Without a thumbnail handler Windows returns the type icon: the tile already shows that.
        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0) return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(thumbnail);
        return bitmap;
    }

    private static async Task<ImageSource?> CloudAsync(IThumbnailProvider provider, StorageItem item, CancellationToken ct)
    {
        await using var stream = await provider.OpenThumbnailAsync(item, PixelSize, ct);
        ct.ThrowIfCancellationRequested();
        if (stream is null) return null;
        var bitmap = new BitmapImage { DecodePixelWidth = PixelSize, DecodePixelType = DecodePixelType.Physical };
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private void Remember(string key, ImageSource? source)
    {
        if (_cache.ContainsKey(key)) return;
        _cache[key] = source;
        _order.Enqueue(key);
        while (_order.Count > MaxCached) _cache.Remove(_order.Dequeue());
    }
}
