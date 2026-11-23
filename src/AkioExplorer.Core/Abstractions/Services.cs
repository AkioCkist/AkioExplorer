using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Persists <see cref="Current"/>. Writes are coalesced and done off the calling thread.</summary>
    void Save();

    /// <summary>Flushes pending writes synchronously (used on app exit).</summary>
    void Flush();

    event EventHandler? Changed;

    void NotifyChanged();
}

public interface IListingCache
{
    bool TryGet(StorageLocation location, out IReadOnlyList<StorageItem> items);

    void Set(StorageLocation location, IReadOnlyList<StorageItem> items);

    void Invalidate(StorageLocation location);
}

public sealed record KnownFolder(string Name, string Path, string Glyph);

public interface IKnownFoldersService
{
    IReadOnlyList<KnownFolder> GetKnownFolders();
}

/// <summary>Icon pixels: premultiplied BGRA, top-down.</summary>
public sealed record IconBitmap(int Width, int Height, byte[] Pixels);

public enum IconSize
{
    Small,
    Large,

    /// <summary>256 px, for zoomed-in grid tiles. Types without a real 256 px icon return nothing (keep Large).</summary>
    Jumbo,
}

public interface IShellIconExtractor
{
    /// <summary>Returns the cache key that identifies the icon of an item (items with the same key share an icon).</summary>
    string GetIconKey(StorageItem item);

    Task<IconBitmap?> GetIconAsync(StorageItem item, IconSize size);

    /// <summary>Localised type name such as "Text Document" (cached per extension).</summary>
    string GetTypeName(StorageItem item);
}

public interface IShellActions
{
    void Open(string path);

    void OpenWith(string path);

    void ShowProperties(string path);

    void RevealInExplorer(string path);

    /// <summary>Opens Windows Terminal (or PowerShell when it isn't installed) in <paramref name="folder"/>.</summary>
    void OpenTerminal(string folder, TerminalApp app = TerminalApp.WindowsTerminal);

    /// <summary>
    /// Runs address-bar text as a command (<c>cmd</c>, <c>code .</c>, a URL…) in <paramref name="folder"/>.
    /// False when Windows can't find the program.
    /// </summary>
    bool RunCommand(string commandLine, string folder);
}
