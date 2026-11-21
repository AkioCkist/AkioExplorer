using System.Runtime.InteropServices.WindowsRuntime;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AkioExplorer.App.Services;

/// <summary>
/// UI-thread cache of shell icons as <see cref="ImageSource"/>. All rows with the same icon key share one bitmap,
/// so memory and shell calls scale with the number of distinct file types, not with item count.
/// </summary>
public sealed class IconService
{
    private readonly IShellIconExtractor _extractor;
    private readonly Dictionary<string, ImageSource?> _small = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _large = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);

    public IconService(IShellIconExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>Synchronous cache lookup (UI thread). Returns false if the icon still needs loading.</summary>
    public bool TryGet(StorageItem item, IconSize size, out ImageSource? icon)
    {
        var cache = size == IconSize.Small ? _small : _large;
        return cache.TryGetValue(_extractor.GetIconKey(item), out icon);
    }

    /// <summary>Loads an icon (UI thread only; extraction itself runs on the shell worker thread).</summary>
    public Task<ImageSource?> GetAsync(StorageItem item, IconSize size)
    {
        string key = _extractor.GetIconKey(item);
        var cache = size == IconSize.Small ? _small : _large;
        if (cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);

        string pendingKey = (size == IconSize.Small ? "s|" : "l|") + key;
        if (_pending.TryGetValue(pendingKey, out var pending)) return pending;

        var task = LoadAsync(item, size, key, cache, pendingKey);
        _pending[pendingKey] = task;
        return task;
    }

    private async Task<ImageSource?> LoadAsync(StorageItem item, IconSize size, string key, Dictionary<string, ImageSource?> cache, string pendingKey)
    {
        ImageSource? source = null;
        try
        {
            var bitmap = await _extractor.GetIconAsync(item, size); // resumes on UI thread
            if (bitmap is not null)
            {
                var writeable = new WriteableBitmap(bitmap.Width, bitmap.Height);
                using (var stream = writeable.PixelBuffer.AsStream())
                {
                    stream.Write(bitmap.Pixels, 0, bitmap.Pixels.Length);
                }
                writeable.Invalidate();
                source = writeable;
            }
        }
        catch
        {
            // Icon failures are cosmetic; the row keeps its glyph placeholder.
        }

        cache[key] = source;
        _pending.Remove(pendingKey);
        return source;
    }
}
