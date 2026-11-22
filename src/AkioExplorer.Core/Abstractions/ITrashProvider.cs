namespace AkioExplorer.Core.Abstractions;

/// <summary>One top-level item in a trash (the contents of a deleted folder go with it).</summary>
/// <param name="Id">Provider-specific handle (node id, file id, $I file path).</param>
/// <param name="Deleted">When it was deleted, if the service says.</param>
/// <param name="Modified">Last change before it was deleted, for services that don't keep the deletion time.</param>
/// <param name="OriginalLocation">Where Restore puts it back; null when the service doesn't remember (then: the root).</param>
public sealed record TrashItem(string Id, string Name, bool IsFolder, long? Size, DateTimeOffset? Deleted, DateTimeOffset? Modified,
    string? OriginalLocation);

/// <summary>A provider whose deleted items wait in a trash (Recycle Bin, MEGA's Rubbish bin…) that can be browsed and emptied.</summary>
public interface ITrashProvider
{
    /// <summary>What the service calls it, for menus and dialogs ("Recycle Bin", "Rubbish bin", "Trash").</summary>
    string TrashName { get; }

    /// <summary>Where restored items go when the original folder isn't known ("Cloud drive", "My Drive").</summary>
    string RestoreFallbackName { get; }

    /// <summary>What's in the trash, top-level items only.</summary>
    Task<IReadOnlyList<TrashItem>> ListTrashAsync(CancellationToken cancellationToken);

    /// <summary>Puts items back where they were (or in the root when that isn't known).</summary>
    Task RestoreAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken);

    /// <summary>Deletes these items for good.</summary>
    Task DeleteFromTrashAsync(IReadOnlyList<TrashItem> items, CancellationToken cancellationToken);

    /// <summary>Deletes everything in the trash for good.</summary>
    Task EmptyTrashAsync(CancellationToken cancellationToken);
}
