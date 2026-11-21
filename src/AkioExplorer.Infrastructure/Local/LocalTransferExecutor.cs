using System.ComponentModel;
using System.Runtime.InteropServices;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using AkioExplorer.Core.Transfers;

namespace AkioExplorer.Infrastructure.Local;

/// <summary>
/// Local ↔ local transfers through the Windows copy engine (CopyFileEx): kernel-side copy, preserves timestamps and
/// attributes, unbuffered I/O for large files, progress callbacks and clean cancellation (partial files are removed).
/// </summary>
public sealed class LocalTransferExecutor : ITransferExecutor
{
    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
    private const uint COPY_FILE_NO_BUFFERING = 0x00001000;
    private const uint PROGRESS_CONTINUE = 0;
    private const uint PROGRESS_CANCEL = 1;
    private const int ERROR_REQUEST_ABORTED = 1235;
    private const long UnbufferedThreshold = 256L * 1024 * 1024;

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
        IntPtr data, ref int cancel, uint copyFlags);

    public bool CanTransfer(IStorageProvider source, IStorageProvider destination) =>
        source.Id == LocalStorageProvider.ProviderId && destination.Id == LocalStorageProvider.ProviderId;

    public Task CopyFileAsync(IStorageProvider source, StorageItem file, IStorageProvider destination, string destinationPath,
        bool overwrite, Action<long> onBytes, CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(destinationPath))
            {
                var attributes = File.GetAttributes(destinationPath);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                    File.SetAttributes(destinationPath, FileAttributes.Normal);
            }

            long last = 0;
            int cancel = 0;
            CopyProgressRoutine progress = (total, transferred, _, _, _, _, _, _, _) =>
            {
                long delta = transferred - last;
                last = transferred;
                if (delta != 0) onBytes(delta);
                return cancellationToken.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
            };

            uint flags = overwrite ? 0 : COPY_FILE_FAIL_IF_EXISTS;
            if ((file.Size ?? 0) >= UnbufferedThreshold) flags |= COPY_FILE_NO_BUFFERING;

            bool ok = CopyFileEx(file.Path, destinationPath, progress, IntPtr.Zero, ref cancel, flags);
            GC.KeepAlive(progress);
            if (ok) return;

            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_REQUEST_ABORTED || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw Map(error, file.Name);
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public Task<bool> TryMoveAsync(IStorageProvider source, StorageItem item, IStorageProvider destination, string destinationPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!SameVolume(item.Path, destinationPath)) return false;
            try
            {
                if (item.IsContainer) Directory.Move(item.Path, destinationPath);
                else File.Move(item.Path, destinationPath);
                return true;
            }
            catch (IOException)
            {
                return false; // e.g. locked file – the engine falls back to copy + delete and reports per-file errors
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }, cancellationToken);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static StorageException Map(int error, string name) => error switch
    {
        2 or 3 => new StorageException(StorageErrorKind.NotFound, $"\"{name}\" no longer exists."),
        5 => new StorageException(StorageErrorKind.AccessDenied, "Access denied."),
        32 or 33 => new StorageException(StorageErrorKind.InUse, "The file is open in another program."),
        80 or 183 => new StorageException(StorageErrorKind.AlreadyExists, "A file with this name already exists."),
        39 or 112 => new StorageException(StorageErrorKind.Unknown, "There isn't enough space on the destination drive."),
        21 or 53 or 64 or 67 or 1231 => new StorageException(StorageErrorKind.Unavailable, "The drive or network location isn't available."),
        _ => new StorageException(StorageErrorKind.Unknown, new Win32Exception(error).Message),
    };
}
