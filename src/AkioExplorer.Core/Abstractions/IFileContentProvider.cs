using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Abstractions;

/// <summary>
/// File contents of a provider: streaming read/write plus optional server-side copy/move inside the same account.
/// Implemented by the local disk and every cloud provider; the transfer engine combines any two of them, so
/// upload, download and cloud-to-cloud are the same code path.
/// </summary>
public interface IFileContentProvider
{
    /// <summary>Name a file gets when copied elsewhere (e.g. a Google Doc becomes "Plan.docx").</summary>
    string GetTransferName(StorageItem file) => file.Name;

    /// <summary>Opens a forward-only stream of the file contents. Dispose it to release the connection.</summary>
    Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="content"/> (<paramref name="length"/> bytes) as <paramref name="name"/> in
    /// <paramref name="parentPath"/>. Large files use the service's resumable/chunked upload with per-chunk retries.
    /// Must not leave a partial file behind when cancelled or failed.
    /// </summary>
    Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Server-side copy within this account (no bytes through this PC). Null when not supported for the item.</summary>
    Task<StorageItem?> CopyWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken);

    /// <summary>Server-side move/rename within this account (instant, works for folders). Null when not supported.</summary>
    Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken);
}
