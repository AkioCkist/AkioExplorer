using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Abstractions;

/// <summary>Public links ("anyone with the link can view/download").</summary>
public interface IShareLinkProvider
{
    /// <summary>Creates (or returns the existing) public link of <paramref name="item"/>.</summary>
    Task<string> CreateShareLinkAsync(StorageItem item, CancellationToken cancellationToken);

    /// <summary>Turns the public link off; the old URL stops working.</summary>
    Task RemoveShareLinkAsync(StorageItem item, CancellationToken cancellationToken);
}

/// <summary>Marks the service keeps on its side (shown on every device): favourite/star, colour label, hidden.</summary>
public interface IItemMarksProvider
{
    bool SupportsLabels { get; }

    /// <summary>Labels only apply to folders (Google Drive folder colours).</summary>
    bool LabelsFoldersOnly => false;

    /// <summary>"Hide" (MEGA's sensitive items): hidden in lists unless View › Hidden items is on.</summary>
    bool SupportsHiding { get; }

    Task SetStarredAsync(IReadOnlyList<StorageItem> items, bool starred, CancellationToken cancellationToken);

    Task SetLabelAsync(IReadOnlyList<StorageItem> items, ItemLabel label, CancellationToken cancellationToken);

    Task SetHiddenAsync(IReadOnlyList<StorageItem> items, bool hidden, CancellationToken cancellationToken);
}

/// <summary>One stored version of a file (newest first in lists).</summary>
public sealed record FileVersion(string Id, string Name, DateTimeOffset? Modified, long? Size, bool IsCurrent)
{
    /// <summary>Kept until deleted (Google Drive deletes other versions after 30 days / 100 versions).</summary>
    public bool? KeepForever { get; init; }

    /// <summary>Who saved it, when the service says.</summary>
    public string? Author { get; init; }
}

/// <summary>Previous versions the service keeps when a file is replaced.</summary>
public interface IVersionsProvider
{
    Task<IReadOnlyList<FileVersion>> ListVersionsAsync(StorageItem file, CancellationToken cancellationToken);

    /// <summary>Makes <paramref name="version"/> the current one (the current one becomes a version).</summary>
    Task RestoreVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken);

    Task DeleteVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken);

    /// <summary>The bytes of an old version (to save a copy).</summary>
    Task<Stream> OpenVersionAsync(StorageItem file, FileVersion version, CancellationToken cancellationToken);

    bool SupportsKeepForever => false;

    Task SetKeepForeverAsync(StorageItem file, FileVersion version, bool keep, CancellationToken cancellationToken) =>
        throw new StorageException(StorageErrorKind.NotSupported, "This service keeps every version until you delete it.");
}

/// <summary>Thumbnail (square, 120 px) and preview (≤ 1000 px) JPEGs for services that store them with the file.</summary>
public sealed record GeneratedThumbnails(byte[] Thumbnail, byte[] Preview);

/// <summary>Makes thumbnails on this PC (Windows imaging / shell), for uploads to services that don't make their own.</summary>
public interface IThumbnailGenerator
{
    /// <summary>From a local file (pictures, videos, anything the shell can render) or from image bytes; null when it can't.</summary>
    Task<GeneratedThumbnails?> CreateAsync(string? localPath, byte[]? imageBytes, CancellationToken cancellationToken);
}

/// <summary>What a link from someone else points to.</summary>
public sealed record PublicLinkInfo(string Name, bool IsFolder, long Size, int FileCount);

/// <summary>Opening links other people shared (e.g. mega.nz/file/…): see what's behind them and save a copy to this account.</summary>
public interface IPublicLinkProvider
{
    bool CanOpenLink(string link);

    Task<PublicLinkInfo> InspectLinkAsync(string link, CancellationToken cancellationToken);

    /// <summary>Copies what the link points to into <paramref name="targetFolderPath"/>; reports item names as it goes.</summary>
    Task<StorageItem?> ImportLinkAsync(string link, string targetFolderPath, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>Files that live in the browser (Google Docs, Sheets…) and the service's web page of any item.</summary>
public interface IWebViewProvider
{
    /// <summary>True for documents that only exist online: opening them goes to the browser instead of downloading.</summary>
    bool OpensInBrowser(StorageItem item);

    Task<string> GetWebUrlAsync(StorageItem item, CancellationToken cancellationToken);
}

/// <summary>A kind of online document the service can create (Google Docs, Sheets…).</summary>
public sealed record NativeDocumentType(string Id, string Name, string DefaultName, string Glyph);

public interface INativeDocumentsProvider
{
    IReadOnlyList<NativeDocumentType> DocumentTypes { get; }

    Task<StorageItem> CreateDocumentAsync(string parentPath, string name, NativeDocumentType type, CancellationToken cancellationToken);

    /// <summary>Office/text files that can be turned into an online document (a converted copy).</summary>
    bool CanConvert(StorageItem item);

    Task<StorageItem> ConvertAsync(StorageItem item, CancellationToken cancellationToken);
}

/// <summary>A format an online document can be downloaded as.</summary>
public sealed record ExportOption(string Name, string Extension, string MimeType);

public interface IExportProvider
{
    IReadOnlyList<ExportOption> ExportOptions(StorageItem item);

    Task<Stream> OpenExportAsync(StorageItem item, ExportOption format, CancellationToken cancellationToken);
}

public interface IDescriptionProvider
{
    Task<string?> GetDescriptionAsync(StorageItem item, CancellationToken cancellationToken);

    Task SetDescriptionAsync(StorageItem item, string description, CancellationToken cancellationToken);
}

public interface IShortcutProvider
{
    /// <summary>A shortcut to <paramref name="target"/> inside <paramref name="parentPath"/> (Drive "Add shortcut").</summary>
    Task<StorageItem> CreateShortcutAsync(StorageItem target, string parentPath, CancellationToken cancellationToken);
}

/// <summary>Locking a file against edits (Drive "content restriction").</summary>
public interface ILockProvider
{
    Task<bool> IsLockedAsync(StorageItem item, CancellationToken cancellationToken);

    Task SetLockedAsync(StorageItem item, bool locked, CancellationToken cancellationToken);
}

public enum ShareRole
{
    Viewer,
    Commenter,
    Editor,
    Owner,
}

public enum LinkAccess
{
    /// <summary>Only people added by name.</summary>
    Restricted,

    AnyoneWithLink,
}

/// <summary>Someone (or a group/domain) with access to an item.</summary>
public sealed record SharePermission(string Id, string DisplayName, string? Email, ShareRole Role, bool IsYou, bool Inherited);

public sealed record ShareSettings(IReadOnlyList<SharePermission> People, LinkAccess Link, ShareRole LinkRole, string? WebLink);

/// <summary>Sharing with people by email, and the "General access" link setting.</summary>
public interface ISharingProvider
{
    Task<ShareSettings> GetSharingAsync(StorageItem item, CancellationToken cancellationToken);

    Task AddPersonAsync(StorageItem item, string email, ShareRole role, bool notify, string? message, CancellationToken cancellationToken);

    /// <summary>Changes a person's role; <see cref="ShareRole.Owner"/> transfers ownership.</summary>
    Task SetRoleAsync(StorageItem item, string permissionId, ShareRole role, CancellationToken cancellationToken);

    Task RemovePersonAsync(StorageItem item, string permissionId, CancellationToken cancellationToken);

    Task SetLinkAccessAsync(StorageItem item, LinkAccess access, ShareRole role, CancellationToken cancellationToken);
}

public sealed record CommentReply(string Author, string Text, DateTimeOffset? When);

public sealed record FileComment(string Id, string Author, string Text, DateTimeOffset? When, bool Resolved, string? Quote,
    IReadOnlyList<CommentReply> Replies);

public interface ICommentsProvider
{
    Task<IReadOnlyList<FileComment>> ListCommentsAsync(StorageItem item, CancellationToken cancellationToken);

    Task AddCommentAsync(StorageItem item, string text, CancellationToken cancellationToken);

    /// <summary>Replies to a comment; <paramref name="resolve"/> also marks it resolved (text may then be empty).</summary>
    Task ReplyAsync(StorageItem item, string commentId, string text, bool resolve, CancellationToken cancellationToken);
}

public sealed record ActivityEntry(DateTimeOffset? When, string Who, string What);

/// <summary>Who did what to an item (Drive Activity).</summary>
public interface IActivityProvider
{
    Task<IReadOnlyList<ActivityEntry>> ListActivityAsync(StorageItem item, CancellationToken cancellationToken);
}
