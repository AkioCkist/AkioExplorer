using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using static AkioExplorer.Infrastructure.Shell.NativeMethods;

namespace AkioExplorer.Infrastructure.Local;

/// <summary>
/// The local machine. Root path "" is "This PC" (known folders + drives).
/// Enumeration uses <see cref="FileSystemEnumerable{TResult}"/> (no FileSystemInfo allocations) on a background
/// thread and streams pages through a channel.
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    public const string ProviderId = "local";
    private const int PageSize = 2000;
    private static readonly TimeSpan DriveQueryTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IKnownFoldersService _knownFolders;
    private readonly ILogger<LocalStorageProvider> _logger;
    private readonly ConcurrentDictionary<string, string> _driveNames = new(StringComparer.OrdinalIgnoreCase);

    public LocalStorageProvider(IKnownFoldersService knownFolders, ILogger<LocalStorageProvider> logger)
    {
        _knownFolders = knownFolders;
        _logger = logger;
    }

    public string Id => ProviderId;

    public string DisplayName => "This PC";

    public StorageCapabilities Capabilities =>
        StorageCapabilities.CreateFolder | StorageCapabilities.Rename | StorageCapabilities.Delete |
        StorageCapabilities.RecycleBin | StorageCapabilities.Search | StorageCapabilities.Watch |
        StorageCapabilities.OpenWithShell | StorageCapabilities.VirtualRoot;

    public string RootPath => string.Empty;

    private static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    // ---------------------------------------------------------------- listing

    public async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (IsRoot(path))
        {
            yield return await GetRootItemsAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }

        string folder = path;
        var channel = Channel.CreateBounded<IReadOnlyList<StorageItem>>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });

        _ = Task.Run(() =>
        {
            try
            {
                // Probe first so "not found / access denied" surface as friendly errors instead of an empty list.
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException(folder);

                var enumerable = new FileSystemEnumerable<StorageItem>(folder, static (ref FileSystemEntry e) => ToItem(ref e), ListOptions);
                var page = new List<StorageItem>(256);
                foreach (var item in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    page.Add(item);
                    if (page.Count >= PageSize)
                    {
                        channel.Writer.WriteAsync(page, cancellationToken).AsTask().GetAwaiter().GetResult();
                        page = new List<StorageItem>(PageSize);
                    }
                }

                channel.Writer.WriteAsync(page, cancellationToken).AsTask().GetAwaiter().GetResult();
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex is OperationCanceledException ? ex : MapException(ex, folder));
            }
        }, CancellationToken.None);

        await foreach (var page in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return page;
        }
    }

    private static StorageItem ToItem(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;
        bool isDirectory = entry.IsDirectory;
        return new StorageItem
        {
            Name = entry.FileName.ToString(),
            Path = entry.ToFullPath(),
            Kind = isDirectory ? StorageItemKind.Folder : StorageItemKind.File,
            Size = isDirectory ? null : entry.Length,
            Modified = entry.LastWriteTimeUtc.ToLocalTime(),
            Created = entry.CreationTimeUtc.ToLocalTime(),
            Attributes = MapAttributes(attributes),
        };
    }

    private static StorageItemAttributes MapAttributes(FileAttributes a)
    {
        var result = StorageItemAttributes.None;
        if ((a & FileAttributes.Hidden) != 0) result |= StorageItemAttributes.Hidden;
        if ((a & FileAttributes.System) != 0) result |= StorageItemAttributes.System;
        if ((a & FileAttributes.ReadOnly) != 0) result |= StorageItemAttributes.ReadOnly;
        if ((a & FileAttributes.ReparsePoint) != 0) result |= StorageItemAttributes.ReparsePoint;
        return result;
    }

    private async Task<IReadOnlyList<StorageItem>> GetRootItemsAsync(CancellationToken cancellationToken)
    {
        var items = new List<StorageItem>();
        foreach (var folder in _knownFolders.GetKnownFolders())
        {
            items.Add(new StorageItem
            {
                Name = folder.Name,
                Path = folder.Path,
                Kind = StorageItemKind.Folder,
                TypeDescription = "System folder",
            });
        }

        items.AddRange(await GetDrivesAsync(cancellationToken).ConfigureAwait(false));
        return items;
    }

    /// <summary>Drive list. Each drive is queried in parallel with a timeout so a dead network drive can't stall the UI.</summary>
    public async Task<IReadOnlyList<StorageItem>> GetDrivesAsync(CancellationToken cancellationToken)
    {
        DriveInfo[] drives = await Task.Run(DriveInfo.GetDrives, cancellationToken).ConfigureAwait(false);
        var tasks = drives.Select(d => QueryDriveAsync(d, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<StorageItem> QueryDriveAsync(DriveInfo drive, CancellationToken cancellationToken)
    {
        string root = drive.Name; // "C:\"
        var query = Task.Run(() =>
        {
            if (!drive.IsReady) return (Label: (string?)null, Total: (long?)null, Free: (long?)null);
            return (Label: drive.VolumeLabel, Total: (long?)drive.TotalSize, Free: (long?)drive.AvailableFreeSpace);
        }, cancellationToken);

        (string? label, long? total, long? free) info = default;
        try
        {
            if (await Task.WhenAny(query, Task.Delay(DriveQueryTimeout, cancellationToken)).ConfigureAwait(false) == query)
                info = await query.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Drive {Drive} not ready", root);
        }

        string typeName = drive.DriveType switch
        {
            DriveType.Fixed => "Local Disk",
            DriveType.Removable => "USB Drive",
            DriveType.Network => "Network Drive",
            DriveType.CDRom => "CD Drive",
            DriveType.Ram => "RAM Disk",
            _ => "Drive",
        };

        string letter = root.TrimEnd('\\');
        string name = $"{(string.IsNullOrWhiteSpace(info.label) ? typeName : info.label)} ({letter})";
        _driveNames[root] = name;

        return new StorageItem
        {
            Name = name,
            Path = root,
            Kind = StorageItemKind.Drive,
            TotalSize = info.total,
            FreeSpace = info.free,
            TypeDescription = typeName,
        };
    }

    public Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (IsRoot(path)) return null;
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            bool isDrive = info.Parent is null;
            return new StorageItem
            {
                Name = isDrive ? GetDisplayName(path) : info.Name,
                Path = info.FullName,
                Kind = isDrive ? StorageItemKind.Drive : StorageItemKind.Folder,
                Modified = isDrive ? null : info.LastWriteTime,
                Attributes = isDrive ? StorageItemAttributes.None : MapAttributes(info.Attributes),
            };
        }

        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return new StorageItem
            {
                Name = info.Name,
                Path = info.FullName,
                Kind = StorageItemKind.File,
                Size = info.Length,
                Modified = info.LastWriteTime,
                Created = info.CreationTime,
                Attributes = MapAttributes(info.Attributes),
            };
        }

        return (StorageItem?)null;
    }, cancellationToken);

    // ---------------------------------------------------------------- mutations

    public Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken) => Task.Run(() =>
    {
        EnsureValidName(name);
        if (IsRoot(parentPath))
            throw new StorageException(StorageErrorKind.NotSupported, "Folders can't be created in This PC. Open a drive or folder first.");

        string full = Path.Combine(parentPath, name);
        if (Directory.Exists(full) || File.Exists(full))
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{name}\" here.");

        try
        {
            var info = Directory.CreateDirectory(full);
            return new StorageItem
            {
                Name = info.Name,
                Path = info.FullName,
                Kind = StorageItemKind.Folder,
                Modified = info.LastWriteTime,
                Created = info.CreationTime,
            };
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw MapException(ex, full);
        }
    }, cancellationToken);

    public Task<StorageItem> RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        EnsureValidName(newName);
        string? parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        if (parent is null)
            throw new StorageException(StorageErrorKind.NotSupported, "Drives can't be renamed here. Use Windows Settings to change a drive label.");

        string target = Path.Combine(parent, newName);
        bool caseOnly = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(path, target, StringComparison.Ordinal))
            return (await GetItemAsync(path, cancellationToken).ConfigureAwait(false))!;

        if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
            throw new StorageException(StorageErrorKind.AlreadyExists, $"There's already an item named \"{newName}\" here.");

        try
        {
            if (Directory.Exists(path))
            {
                if (caseOnly)
                {
                    string temp = Path.Combine(parent, "~akio-rename-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(path, temp);
                    Directory.Move(temp, target);
                }
                else
                {
                    Directory.Move(path, target);
                }
            }
            else if (File.Exists(path))
            {
                File.Move(path, target);
            }
            else
            {
                throw new StorageException(StorageErrorKind.NotFound, $"\"{Path.GetFileName(path)}\" no longer exists. It may have been moved or deleted.");
            }
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw MapException(ex, path);
        }

        return (await GetItemAsync(target, cancellationToken).ConfigureAwait(false))!;
    }, cancellationToken);

    public Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (paths.Count == 0) return;
        if (paths.Any(p => IsRoot(p) || Path.GetDirectoryName(p.TrimEnd('\\')) is null))
            throw new StorageException(StorageErrorKind.NotSupported, "Drives and This PC can't be deleted.");

        if (permanent)
        {
            foreach (var p in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    DeletePermanently(p);
                }
                catch (Exception ex) when (ex is not StorageException)
                {
                    throw MapException(ex, p);
                }
            }
            return;
        }

        SendToRecycleBin(paths);
    }, cancellationToken);

    private static void DeletePermanently(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only files block deletion: clear the flag and retry once.
                foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void SendToRecycleBin(IReadOnlyList<string> paths)
    {
        // pFrom is a double-null-terminated list.
        string from = string.Join('\0', paths) + "\0\0";
        IntPtr buffer = Marshal.StringToHGlobalUni(from);
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = buffer,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING),
            };
            int result = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted)
                throw new OperationCanceledException();
            if (result != 0)
            {
                string name = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0])}\"" : $"{paths.Count} items";
                string reason = result switch
                {
                    0x20 or 0x21 => " It's open in another program. Close it and try again.",
                    0x5 or 0x78 => " You don't have permission.",
                    0x2 or 0x3 or 0x402 => " It no longer exists.",
                    _ => $" (error 0x{result:X}).",
                };
                throw new StorageException(StorageErrorKind.Unknown, $"Couldn't delete {name}.{reason}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------------------------------------------------------- search

    public async IAsyncEnumerable<StorageItem> SearchAsync(string path, string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0) yield break;

        bool wildcard = query.IndexOfAny(['*', '?']) >= 0;
        var roots = IsRoot(path)
            ? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name).ToArray()
            : [path];

        var channel = Channel.CreateBounded<StorageItem>(new BoundedChannelOptions(512) { SingleReader = true });
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var root in roots)
                {
                    var enumerable = new FileSystemEnumerable<StorageItem>(root, static (ref FileSystemEntry e) => ToItem(ref e), options)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) => wildcard
                            ? FileSystemName.MatchesSimpleExpression(query, e.FileName, ignoreCase: true)
                            : e.FileName.Contains(query, StringComparison.OrdinalIgnoreCase),
                        ShouldRecursePredicate = static (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
                    };

                    foreach (var item in enumerable)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        channel.Writer.WriteAsync(item, cancellationToken).AsTask().GetAwaiter().GetResult();
                    }
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex is OperationCanceledException ? ex : MapException(ex, path));
            }
        }, CancellationToken.None);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    // ---------------------------------------------------------------- paths

    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path)
    {
        var segments = new List<BreadcrumbSegment> { new(DisplayName, string.Empty) };
        if (IsRoot(path)) return segments;

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return segments;

        segments.Add(new BreadcrumbSegment(GetDisplayName(root), root));
        string rest = path[root.Length..].Trim('\\');
        if (rest.Length == 0) return segments;

        string current = root;
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add(new BreadcrumbSegment(part, current));
        }

        return segments;
    }

    public string? GetParentPath(string path)
    {
        if (IsRoot(path)) return null;
        string trimmed = path.Length > 3 ? path.TrimEnd('\\') : path;
        return Path.GetDirectoryName(trimmed) ?? string.Empty;
    }

    public string GetDisplayName(string path)
    {
        if (IsRoot(path)) return DisplayName;
        string? root = Path.GetPathRoot(path);
        if (root is not null && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            string key = root.EndsWith('\\') ? root : root + "\\";
            return _driveNames.TryGetValue(key, out var name) ? name : root.TrimEnd('\\');
        }
        return Path.GetFileName(path.TrimEnd('\\'));
    }

    public string CombinePath(string parentPath, string name) => Path.Combine(parentPath, name);

    public bool IsSameOrDescendant(string path, string ancestor)
    {
        if (string.IsNullOrEmpty(ancestor)) return true;
        string a = ancestor.TrimEnd(Path.DirectorySeparatorChar);
        string p = path.TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(a, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public string? TryNormalizePath(string input)
    {
        string text = input.Trim().Trim('"');
        if (text.Length == 0 || text.Equals(DisplayName, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        text = Environment.ExpandEnvironmentVariables(text);
        if (text.Length == 2 && text[1] == ':') text += "\\";
        if (!Path.IsPathFullyQualified(text)) return null;

        try
        {
            string full = Path.GetFullPath(text);
            string? root = Path.GetPathRoot(full);
            return root is not null && full.Length > root.Length ? full.TrimEnd('\\') : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IDisposable? Watch(string path, Action onChanged)
    {
        if (IsRoot(path) || !Directory.Exists(path)) return null;
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler handler = (_, _) => onChanged();
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Changed += handler;
            watcher.Renamed += (_, _) => onChanged();
            watcher.Error += (_, _) => onChanged();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Can't watch {Path}", path);
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsRoot(string path) => string.IsNullOrEmpty(path);

    private static void EnsureValidName(string name)
    {
        if (FileNameValidator.Validate(name) is { } error)
            throw new StorageException(StorageErrorKind.InvalidName, error);
    }

    private static Exception MapException(Exception ex, string path)
    {
        if (ex is StorageException) return ex;
        string name = Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = path;

        return ex switch
        {
            DirectoryNotFoundException or FileNotFoundException =>
                new StorageException(StorageErrorKind.NotFound, $"Can't find \"{name}\". It may have been moved, renamed or deleted.", ex),
            UnauthorizedAccessException =>
                new StorageException(StorageErrorKind.AccessDenied, $"You don't have permission to access \"{name}\".", ex),
            IOException io when (io.HResult & 0xFFFF) is 21 =>
                new StorageException(StorageErrorKind.Unavailable, $"\"{name}\" isn't ready. Insert media or check the connection, then try again.", ex),
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 =>
                new StorageException(StorageErrorKind.InUse, $"\"{name}\" is open in another program. Close it and try again.", ex),
            IOException io when (io.HResult & 0xFFFF) is 53 or 67 or 1231 =>
                new StorageException(StorageErrorKind.Unavailable, $"Can't reach \"{name}\". Check the network connection.", ex),
            _ => new StorageException(StorageErrorKind.Unknown, $"Something went wrong with \"{name}\": {ex.Message}", ex),
        };
    }
}
