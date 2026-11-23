using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using static AkioExplorer.Infrastructure.Shell.NativeMethods;

namespace AkioExplorer.Infrastructure.Shell;

/// <summary>
/// Extracts real Windows shell icons. Icons are resolved per extension (no disk access) so a folder with
/// 10 000 .jpg files costs one shell call; only files with per-file icons (.exe, .lnk, ...) are looked up individually.
/// </summary>
public sealed class ShellIconExtractor : IShellIconExtractor, IDisposable
{
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".cpl", ".msc", ".scr", ".appref-ms",
    };

    private readonly StaWorker _worker = new("Shell icon worker");
    private readonly ConcurrentDictionary<string, string> _typeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ShellIconExtractor> _logger;

    public ShellIconExtractor(ILogger<ShellIconExtractor> logger)
    {
        _logger = logger;
    }

    /// <remarks>Keys are case-insensitive (callers cache with OrdinalIgnoreCase), so the extension is used as is: no per-call allocation.</remarks>
    public string GetIconKey(StorageItem item) => item.Kind switch
    {
        StorageItemKind.Folder => "<folder>",
        StorageItemKind.Drive => "<drive>" + item.Path,
        _ when PerFileIconExtensions.Contains(item.Extension) && IsLocalPath(item.Path) => item.Path,
        _ => string.IsNullOrEmpty(item.Extension) ? "<file>" : item.Extension,
    };

    public Task<IconBitmap?> GetIconAsync(StorageItem item, IconSize size)
    {
        string key = GetIconKey(item);
        bool useAttributesOnly = !(item.Kind == StorageItemKind.Drive || (key == item.Path));
        string query = item.Kind switch
        {
            StorageItemKind.Folder => "folder",
            StorageItemKind.Drive => item.Path,
            _ => useAttributesOnly ? (string.IsNullOrEmpty(item.Extension) ? "file" : "file" + item.Extension) : item.Path,
        };
        uint attributes = item.Kind == StorageItemKind.Folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        return _worker.InvokeAsync(() => Extract(query, attributes, useAttributesOnly, size));
    }

    public string GetTypeName(StorageItem item)
    {
        if (item.TypeDescription is { } description) return description;
        if (item.Kind == StorageItemKind.Folder) return "File folder";

        string ext = item.Extension;
        if (string.IsNullOrEmpty(ext)) return "File";

        return _typeNames.GetOrAdd(ext, static e =>
        {
            var info = new SHFILEINFOW();
            SHGetFileInfo("file" + e, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return string.IsNullOrWhiteSpace(info.szTypeName)
                ? e.TrimStart('.').ToUpperInvariant() + " File"
                : info.szTypeName;
        });
    }

    private IconBitmap? Extract(string path, uint attributes, bool useAttributesOnly, IconSize size)
    {
        var info = new SHFILEINFOW();
        uint flags = SHGFI_SYSICONINDEX | (useAttributesOnly ? SHGFI_USEFILEATTRIBUTES : 0);
        IntPtr result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
        if (result == IntPtr.Zero)
            return null;

        var iid = IID_IImageList;
        int list = size switch { IconSize.Jumbo => SHIL_JUMBO, IconSize.Large => SHIL_EXTRALARGE, _ => SHIL_LARGE };
        if (SHGetImageList(list, ref iid, out var imageList) != 0 || imageList is null)
            return null;

        try
        {
            if (imageList.GetIcon(info.iIcon, ILD_TRANSPARENT, out IntPtr hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;

            try
            {
                var bitmap = ToBitmap(hIcon);
                // Types without a 256 px icon come back as their 48 px icon in the corner of a 256 canvas:
                // treat that as "no jumbo icon" so the caller keeps the sharp 48 px one, scaled.
                if (size == IconSize.Jumbo && bitmap is not null && OpaqueExtent(bitmap) <= 48) return null;
                return bitmap;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Icon extraction failed for {Path}", path);
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(imageList);
        }
    }

    /// <summary>Largest x or y (exclusive) that holds a visible pixel.</summary>
    private static int OpaqueExtent(IconBitmap bitmap)
    {
        int extent = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            int row = y * bitmap.Width * 4;
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.Pixels[row + x * 4 + 3] != 0) extent = Math.Max(extent, Math.Max(x, y) + 1);
            }
        }
        return extent;
    }

    internal static unsafe IconBitmap? ToBitmap(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var iconInfo)) return null;

        try
        {
            if (iconInfo.hbmColor == IntPtr.Zero) return null;
            if (GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;

            int width = bmp.bmWidth, height = bmp.bmHeight;
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };

            var pixels = new byte[width * height * 4];
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                fixed (byte* p = pixels)
                {
                    if (GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)height, p, ref header, 0) == 0) return null;
                }

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0) { hasAlpha = true; break; }
                }

                if (!hasAlpha && iconInfo.hbmMask != IntPtr.Zero)
                {
                    // Legacy icon without alpha channel: derive alpha from the AND mask.
                    var maskHeader = header;
                    var mask = new byte[width * height * 4];
                    fixed (byte* m = mask)
                    {
                        GetDIBits(hdc, iconInfo.hbmMask, 0, (uint)height, m, ref maskHeader, 0);
                    }
                    for (int i = 0; i < pixels.Length; i += 4)
                        pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }

            // WinUI WriteableBitmap expects premultiplied alpha.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a == 255) continue;
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }

            return new IconBitmap(width, height, pixels);
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
        }
    }

    private static bool IsLocalPath(string path) => path.Length >= 2 && (path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal));

    public void Dispose() => _worker.Dispose();
}
