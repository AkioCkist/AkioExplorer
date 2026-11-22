using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Infrastructure.Local;

/// <summary>
/// The current user's Recycle Bin, read straight from disk: every drive has X:\$Recycle.Bin\&lt;user SID&gt;\, where a
/// deleted item is a pair – $R…  (the item itself) and $I… (original path, size, deletion time). Reading the pairs is
/// much faster than the shell's Recycle Bin folder and needs no COM. Restore moves $R back and removes $I, which is
/// what Explorer does too.
/// </summary>
internal static class RecycleBin
{
    public static IReadOnlyList<TrashItem> List()
    {
        var items = new List<TrashItem>();
        foreach (string folder in Folders())
        {
            IEnumerable<string> infos;
            try
            {
                infos = Directory.EnumerateFiles(folder, "$I*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string info in infos)
            {
                if (Read(info) is not { } entry) continue;
                string data = DataPath(info);
                bool isFolder = Directory.Exists(data);
                if (!isFolder && !File.Exists(data)) continue; // orphan $I: nothing left to restore
                items.Add(new TrashItem(info, Path.GetFileName(entry.OriginalPath), isFolder, entry.Size, entry.Deleted, null,
                    Path.GetDirectoryName(entry.OriginalPath)));
            }
        }
        return items;
    }

    public static void Restore(TrashItem item)
    {
        var entry = Read(item.Id) ?? throw new StorageException(StorageErrorKind.NotFound, $"\"{item.Name}\" is no longer in the Recycle Bin.");
        string data = DataPath(item.Id);
        string target = entry.OriginalPath;
        if (File.Exists(target) || Directory.Exists(target))
            throw new StorageException(StorageErrorKind.AlreadyExists,
                $"Couldn't restore \"{item.Name}\": there's already an item with that name in {Path.GetDirectoryName(target)}.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(data)) Directory.Move(data, target);
            else File.Move(data, target);
            File.Delete(item.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Couldn't restore \"{item.Name}\": {ex.Message}", ex);
        }
        Changed(Path.GetDirectoryName(target));
    }

    public static void Delete(TrashItem item)
    {
        string data = DataPath(item.Id);
        try
        {
            if (Directory.Exists(data))
            {
                foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(data, recursive: true);
            }
            else if (File.Exists(data))
            {
                File.SetAttributes(data, FileAttributes.Normal);
                File.Delete(data);
            }
            File.Delete(item.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Couldn't delete \"{item.Name}\": {ex.Message}", ex);
        }
        Changed(Path.GetDirectoryName(item.Id));
    }

    public static void Empty()
    {
        int result = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // S_OK, or E_UNEXPECTED when it was already empty.
        if (result != 0 && result != unchecked((int)0x8000FFFF))
            throw new StorageException(StorageErrorKind.Unknown, $"Couldn't empty the Recycle Bin (error 0x{result:X8}).");
    }

    // ---------------------------------------------------------------- $I files

    internal sealed record Entry(string OriginalPath, long Size, DateTimeOffset? Deleted);

    /// <summary>
    /// $I layout: int64 version, int64 size, int64 FILETIME deleted, then the original path – version 1 (Vista–8.1):
    /// 260 UTF-16 chars; version 2 (Windows 10+): int32 length in chars, then that many (with the terminating NUL).
    /// </summary>
    internal static Entry? Read(string infoPath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(infoPath);
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static Entry? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24) return null;
        long version = BitConverter.ToInt64(bytes[..8]);
        long size = BitConverter.ToInt64(bytes[8..16]);
        long fileTime = BitConverter.ToInt64(bytes[16..24]);
        ReadOnlySpan<byte> name;
        if (version == 2)
        {
            if (bytes.Length < 28) return null;
            int chars = BitConverter.ToInt32(bytes[24..28]);
            if (chars <= 0 || 28 + chars * 2 > bytes.Length) return null;
            name = bytes.Slice(28, chars * 2);
        }
        else if (version == 1)
        {
            name = bytes[24..Math.Min(bytes.Length, 24 + 520)];
        }
        else
        {
            return null;
        }
        string path = Encoding.Unicode.GetString(name).TrimEnd('\0');
        int nul = path.IndexOf('\0');
        if (nul >= 0) path = path[..nul];
        if (path.Length == 0) return null;
        DateTimeOffset? deleted = fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null;
        return new Entry(path, size, deleted);
    }

    private static string DataPath(string infoPath) =>
        Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + Path.GetFileName(infoPath)[2..]);

    private static IEnumerable<string> Folders()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) yield break;
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }
            if (!ready) continue;
            string folder = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(folder)) yield return folder;
        }
    }

    /// <summary>Tells Explorer windows showing this folder that its contents changed.</summary>
    private static void Changed(string? folder)
    {
        const int SHCNE_UPDATEDIR = 0x1000, SHCNF_PATHW = 0x5;
        if (folder is null) return;
        IntPtr p = Marshal.StringToHGlobalUni(folder);
        try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, p, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(p); }
    }

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
