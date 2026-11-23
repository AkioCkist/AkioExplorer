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
    /// <summary>Pixel size at 100 % zoom (tiles show ~64 DIP; this stays sharp at 150 % scale).</summary>
    public const int PixelSize = 128;

    /// <summary>
    /// Cache budget in 128 px thumbnails (~64 KB of pixels each): 1 500 = about 96 MB worst case. A 256 px thumbnail
    /// costs four units, a 512 px one sixteen, so zooming in keeps fewer pictures instead of using more memory.
    /// </summary>
    private const int MaxCost = 1500;

    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Key, int Cost)> _order = new();
    private int _cost;
    private readonly Dictionary<FileItemViewModel, (CancellationTokenSource Cts, int Size)> _pending = new();
    private readonly SemaphoreSlim _localGate = new(4);
    private readonly SemaphoreSlim _cloudGate = new(3);

    /// <summary>Thumbnail pixel size for a tile picture box <paramref name="boxDip"/> tall (128, 256, 384 or 512).</summary>
    public static int PixelSizeFor(double boxDip) => (boxDip * 1.5) switch
    {
        <= PixelSize => PixelSize,
        <= 256 => 256,
        <= 384 => 384,
        _ => 512,
    };

    public async void Request(FileItemViewModel vm, IStorageProvider? provider, int pixelSize = PixelSize)
    {
        if (provider is null || vm.IsContainer) return;
        if (vm.ThumbnailRequested && vm.ThumbnailPixelSize >= pixelSize) return; // already this sharp (or none exists)
        if (_pending.TryGetValue(vm, out var running))
        {
            if (running.Size >= pixelSize) return;
            running.Cts.Cancel(); // zoomed in while a smaller one was queued: ask for the bigger one instead
            _pending.Remove(vm);
        }
        var item = vm.Item;
        bool isLocal = (provider.Capabilities & StorageCapabilities.OpenWithShell) != 0;
        var thumbnails = provider as IThumbnailProvider;
        if (isLocal ? !PreviewClassifier.HasVisualThumbnail(item) : thumbnails?.MayHaveThumbnail(item) != true)
        {
            NoThumbnail(vm); // nothing to fetch: never ask again for this item
            return;
        }

        string key = $"{provider.Id}|{item.Path}|{item.Modified?.UtcTicks}|{item.Size}|{pixelSize}";
        if (_cache.TryGetValue(key, out var cached))
        {
            Apply(vm, cached, pixelSize);
            return;
        }

        var cts = new CancellationTokenSource();
        _pending[vm] = (cts, pixelSize);
        var gate = isLocal ? _localGate : _cloudGate;
        try
        {
            await gate.WaitAsync(cts.Token);
            ImageSource? source;
            try
            {
                // A cloud thumbnail is a few KB: once started, let it finish and cache it (scrolling back is then
                // instant) rather than abort the connection. Cancelling still drops requests that haven't started.
                source = isLocal
                    ? await LocalAsync(item.Path, pixelSize, cts.Token)
                    : await CloudAsync(thumbnails!, item, pixelSize, CancellationToken.None);
            }
            finally
            {
                gate.Release();
            }

            Remember(key, source, pixelSize);
            if (ReferenceEquals(vm.Item, item)) Apply(vm, source, pixelSize);
        }
        catch (OperationCanceledException)
        {
            // Tile scrolled away: it asks again when it comes back.
        }
        catch (Exception)
        {
            if (vm.Thumbnail is null) NoThumbnail(vm); // unreadable / offline: keep the icon
        }
        finally
        {
            if (_pending.TryGetValue(vm, out var current) && current.Cts == cts) _pending.Remove(vm);
            cts.Dispose();
        }
    }

    private static void Apply(FileItemViewModel vm, ImageSource? source, int pixelSize)
    {
        if (source is null)
        {
            if (vm.Thumbnail is null) NoThumbnail(vm);
            return;
        }
        if (vm.Thumbnail is not null && vm.ThumbnailPixelSize >= pixelSize) return; // never swap for a blurrier one
        vm.ThumbnailRequested = true;
        vm.ThumbnailPixelSize = pixelSize;
        vm.Thumbnail = source;
    }

    private static void NoThumbnail(FileItemViewModel vm)
    {
        vm.ThumbnailRequested = true;
        vm.ThumbnailPixelSize = int.MaxValue;
    }

    /// <summary>The tile went off screen before its thumbnail arrived.</summary>
    public void Cancel(FileItemViewModel vm)
    {
        if (_pending.Remove(vm, out var running)) running.Cts.Cancel();
    }

    private static async Task<ImageSource?> LocalAsync(string path, int pixelSize, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        ct.ThrowIfCancellationRequested();
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)pixelSize, ThumbnailOptions.ResizeThumbnail);
        ct.ThrowIfCancellationRequested();
        // Without a thumbnail handler Windows returns the type icon: the tile already shows that.
        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0) return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(thumbnail);
        return bitmap;
    }

    private static async Task<ImageSource?> CloudAsync(IThumbnailProvider provider, StorageItem item, int pixelSize, CancellationToken ct)
    {
        await using var stream = await provider.OpenThumbnailAsync(item, pixelSize, ct);
        ct.ThrowIfCancellationRequested();
        if (stream is null) return null;
        var bitmap = new BitmapImage { DecodePixelWidth = pixelSize, DecodePixelType = DecodePixelType.Physical };
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private void Remember(string key, ImageSource? source, int pixelSize)
    {
        if (_cache.ContainsKey(key)) return;
        int cost = source is null ? 0 : (pixelSize * pixelSize) / (PixelSize * PixelSize);
        _cache[key] = source;
        _order.Enqueue((key, cost));
        _cost += cost;
        while (_cost > MaxCost && _order.TryDequeue(out var oldest))
        {
            _cache.Remove(oldest.Key);
            _cost -= oldest.Cost;
        }
    }
}
