namespace AkioExplorer.Core.Abstractions;

/// <summary>
/// Where a shared link leads inside the app: a folder of <paramref name="Provider"/> to show, and optionally the item
/// to select there (a link to a single file opens its folder with the file selected).
/// </summary>
public sealed record SharedLinkTarget(IStorageProvider Provider, string FolderPath, string? SelectPath = null)
{
    /// <summary>
    /// True when <see cref="Provider"/> was made for this link only (not a signed-in account): the caller registers it
    /// so the pane can browse it, and it disappears when the app closes.
    /// </summary>
    public bool IsTemporary { get; init; }
}

/// <summary>
/// Opens links other people shared (drive.google.com/drive/folders/…, mega.nz/folder/…#…, mediafire.com/folder/…)
/// so they can be browsed like any folder. Implemented by signed-in account providers (the link is opened with that
/// account's access) and by stand-alone sources for services whose public links need no account.
/// </summary>
public interface ISharedLinkSource
{
    /// <summary>Quick check, no network: is this a link of this service?</summary>
    bool CanOpenSharedLink(Uri link);

    /// <summary>
    /// Resolves the link. Throws <see cref="Models.StorageException"/> when it can't be opened (deleted, no access).
    /// </summary>
    Task<SharedLinkTarget> OpenSharedLinkAsync(Uri link, CancellationToken cancellationToken);
}
