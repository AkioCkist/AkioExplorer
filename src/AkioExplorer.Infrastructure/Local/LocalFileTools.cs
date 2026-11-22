using System.IO.Compression;
using System.Security.Cryptography;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Infrastructure.Local;

public sealed record FolderSize(long Bytes, int Files, int Folders);

public sealed record FileHashes(string Md5, string Sha1, string Sha256, string Sha512);

/// <summary>Power-user tools that only make sense on real Windows paths: new file, attributes, ZIP, checksums, folder sizes.</summary>
public static class LocalFileTools
{
    private const int BufferSize = 1 << 20;

    public static Task<StorageItem> CreateFileAsync(string parentPath, string name, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (Core.Logic.FileNameValidator.Validate(name) is { } error)
            throw new StorageException(StorageErrorKind.InvalidName, error);
        string full = Path.Combine(parentPath, name);
        if (File.Exists(full) || Directory.Exists(full))
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{name}\" here.");
        try
        {
            using (new FileStream(full, FileMode.CreateNew, FileAccess.Write)) { }
            var info = new FileInfo(full);
            return new StorageItem
            {
                Name = info.Name,
                Path = info.FullName,
                Kind = StorageItemKind.File,
                Size = 0,
                Modified = info.LastWriteTime,
                Created = info.CreationTime,
            };
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw Map(ex, full);
        }
    }, cancellationToken);

    /// <summary>Turns Hidden or ReadOnly on/off for each path.</summary>
    public static Task SetAttributeAsync(IReadOnlyList<string> paths, FileAttributes attribute, bool on) => Task.Run(() =>
    {
        foreach (var path in paths)
        {
            try
            {
                var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                var attrs = info.Attributes;
                info.Attributes = on ? attrs | attribute : attrs & ~attribute;
            }
            catch (Exception ex)
            {
                throw Map(ex, path);
            }
        }
    });

    /// <summary>Zips files and folders (folders keep their name as the top entry). Progress is 0..1 by bytes.</summary>
    public static Task CompressAsync(IReadOnlyList<string> paths, string zipPath, IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var entries = new List<(string Full, string Entry, long Size)>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                string root = Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
                var dir = new DirectoryInfo(path);
                entries.Add((dir.FullName, Relative(root, dir.FullName) + "/", 0));
                foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (info is FileInfo f) entries.Add((f.FullName, Relative(root, f.FullName), f.Length));
                    else entries.Add((info.FullName, Relative(root, info.FullName) + "/", 0));
                }
            }
            else if (File.Exists(path))
            {
                entries.Add((path, Path.GetFileName(path), new FileInfo(path).Length));
            }
        }

        long total = Math.Max(1, entries.Sum(e => e.Size));
        long done = 0;
        string temp = zipPath + ".partial";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var buffer = new byte[BufferSize];
                foreach (var (full, entryName, _) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entryName.EndsWith('/'))
                    {
                        zip.CreateEntry(entryName);
                        continue;
                    }
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(full);
                    using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
                    using var output = entry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((double)done / total);
                    }
                }
            }
            File.Move(temp, zipPath, overwrite: false);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            if (ex is OperationCanceledException) throw;
            throw Map(ex, zipPath);
        }
    }, cancellationToken);

    /// <summary>Extracts into <paramref name="destination"/> (created if needed). Existing files are not overwritten.</summary>
    public static Task<int> ExtractAsync(string zipPath, string destination, IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            string root = Path.GetFullPath(destination);
            if (!root.EndsWith('\\')) root += "\\";
            Directory.CreateDirectory(root);
            long total = Math.Max(1, zip.Entries.Sum(e => e.Length));
            long done = 0;
            int count = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // "zip slip": entry points outside the folder
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) target = UniquePath(target);
                entry.ExtractToFile(target, overwrite: false);
                done += entry.Length;
                count++;
                progress?.Report((double)done / total);
            }
            return count;
        }
        catch (InvalidDataException)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"\"{Path.GetFileName(zipPath)}\" isn't a ZIP file or is damaged.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageException)
        {
            throw Map(ex, zipPath);
        }
    }, cancellationToken);

    /// <summary>MD5, SHA-1, SHA-256 and SHA-512 in one read of the file.</summary>
    public static Task<FileHashes> ComputeHashesAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            long total = Math.Max(1, stream.Length);
            var buffer = new byte[BufferSize];
            long done = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var span = buffer.AsSpan(0, read);
                md5.AppendData(span);
                sha1.AppendData(span);
                sha256.AppendData(span);
                sha512.AppendData(span);
                done += read;
                progress?.Report((double)done / total);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Map(ex, path);
        }
        return new FileHashes(Hex(md5), Hex(sha1), Hex(sha256), Hex(sha512));
    }, cancellationToken);

    /// <summary>Total size of everything below <paramref name="path"/> (inaccessible parts are skipped).</summary>
    public static FolderSize GetFolderSize(string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        int files = 0, folders = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (info is FileInfo f)
                {
                    bytes += f.Length;
                    files++;
                }
                else
                {
                    folders++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new FolderSize(bytes, files, folders);
    }

    private static string Hex(IncrementalHash hash) => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    private static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string UniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static StorageException Map(Exception ex, string path) => ex switch
    {
        StorageException s => s,
        UnauthorizedAccessException => new StorageException(StorageErrorKind.AccessDenied, $"Access denied – you don't have permission to change \"{Path.GetFileName(path)}\".", ex),
        FileNotFoundException or DirectoryNotFoundException => new StorageException(StorageErrorKind.NotFound, $"\"{Path.GetFileName(path)}\" no longer exists.", ex),
        IOException io when io.HResult == unchecked((int)0x80070020) => new StorageException(StorageErrorKind.InUse, $"\"{Path.GetFileName(path)}\" is in use by another program.", ex),
        _ => new StorageException(StorageErrorKind.Unknown, $"Couldn't finish with \"{Path.GetFileName(path)}\": {ex.Message}", ex),
    };
}
