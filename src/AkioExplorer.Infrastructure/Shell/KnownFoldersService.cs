using System.Runtime.InteropServices;
using AkioExplorer.Core.Abstractions;
using static AkioExplorer.Infrastructure.Shell.NativeMethods;

namespace AkioExplorer.Infrastructure.Shell;

public sealed class KnownFoldersService : IKnownFoldersService
{
    private readonly Lazy<IReadOnlyList<KnownFolder>> _folders = new(Resolve);

    public IReadOnlyList<KnownFolder> GetKnownFolders() => _folders.Value;

    private static IReadOnlyList<KnownFolder> Resolve()
    {
        var list = new List<KnownFolder>();
        void Add(string name, string? path, string glyph)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new KnownFolder(name, path, glyph));
        }

        // Segoe Fluent Icons glyphs.
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "");
        Add("Downloads", GetDownloadsPath(), "");
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "");
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "");
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "");
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "");

        // iCloud has no public file API; iCloud for Windows syncs iCloud Drive into this folder (files on demand).
        Add("iCloud Drive", ICloudDrivePath, "");
        return list;
    }

    /// <summary>Where iCloud for Windows keeps iCloud Drive.</summary>
    public static string ICloudDrivePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive");

    private static string? GetDownloadsPath()
    {
        if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, IntPtr.Zero, out IntPtr ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr); }
            finally { CoTaskMemFree(ptr); }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
