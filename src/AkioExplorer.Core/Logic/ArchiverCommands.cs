using AkioExplorer.Core.Abstractions;

namespace AkioExplorer.Core.Logic;

/// <summary>
/// Command lines for the installed archivers (7-Zip, NanaZip, WinRAR, Bandizip, PeaZip). Each app runs its own GUI
/// program, so it shows its own progress window and asks for passwords and overwrites itself.
/// Items are passed by name with the working directory set to their folder, so archives store "Folder\file", not
/// the full path.
/// </summary>
public static class ArchiverCommands
{
    /// <summary>Extensions the archivers open (the built-in extractor only handles .zip).</summary>
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".zst", ".lz",
        ".lzma", ".z", ".cab", ".iso", ".arj", ".lzh", ".lha", ".wim", ".001", ".alz", ".egg",
    };

    /// <summary>Two-part extensions: "photos.tar.gz" extracts to "photos", not "photos.tar".</summary>
    private static readonly string[] DoubleExtensions = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz"];

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    /// <summary>Folder name for "Extract to": "photos.tar.gz" → "photos", "movie.part1.rar" → "movie".</summary>
    public static string ExtractFolderName(string archivePath)
    {
        string name = Path.GetFileName(archivePath);
        foreach (var ext in DoubleExtensions)
        {
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        }
        string stem = Path.GetFileNameWithoutExtension(name);
        int part = stem.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (part > 0 && stem[(part + 5)..] is { Length: > 0 } digits && digits.All(char.IsAsciiDigit)) stem = stem[..part];
        return stem.Length > 0 ? stem : name;
    }

    /// <summary>Formats the app can create directly, its own format first. Empty: the format is picked in the app's dialog.</summary>
    public static IReadOnlyList<string> CompressFormats(ArchiverKind kind) => kind switch
    {
        ArchiverKind.WinRar => ["rar", "zip"],
        ArchiverKind.SevenZip or ArchiverKind.NanaZip => ["7z", "zip"],
        ArchiverKind.Bandizip => ["zip", "7z"],
        _ => [],
    };

    /// <summary>True when the app can open its own "add to archive" dialog (format, password, split…) from the command line.</summary>
    public static bool HasAddDialog(ArchiverKind kind) => kind is ArchiverKind.SevenZip or ArchiverKind.NanaZip or ArchiverKind.PeaZip;

    /// <summary>
    /// Compresses <paramref name="paths"/> (all in one folder) into <paramref name="archivePath"/> (its extension picks
    /// the format). With <paramref name="showDialog"/> the app's own "add to archive" dialog opens instead, pre-filled
    /// with that name where the app allows it (see <see cref="HasAddDialog"/>).
    /// </summary>
    public static ArchiverInvocation Compress(ArchiverApp app, IReadOnlyList<string> paths, string archivePath, bool showDialog = false)
    {
        if (paths.Count == 0) throw new ArgumentException("Nothing to compress.", nameof(paths));
        string folder = ParentOf(paths[0]);
        var names = paths.Select(p => Path.GetFileName(p.TrimEnd('\\'))).ToList();
        string format = Path.GetExtension(archivePath).TrimStart('.').ToLowerInvariant();

        List<string> args = app.Kind switch
        {
            ArchiverKind.SevenZip or ArchiverKind.NanaZip => showDialog
                // -ad: show the Add to archive dialog; -saa: it appends the extension of the format picked there.
                ? ["a", "-ad", "-saa", "--", Path.ChangeExtension(archivePath, null), .. names]
                : ["a", $"-t{format}", "--", archivePath, .. names],
            ArchiverKind.WinRar => ["a", format == "zip" ? "-afzip" : "-afrar", "--", archivePath, .. names],
            ArchiverKind.Bandizip => ["c", archivePath, .. names],
            ArchiverKind.PeaZip => ["-add2archive", .. paths],
            _ => throw new ArgumentOutOfRangeException(nameof(app)),
        };
        return new ArchiverInvocation(app.ExecutablePath, args, folder);
    }

    /// <summary>Extracts one archive next to itself ("Extract here") or into a new folder named after it.</summary>
    public static ArchiverInvocation Extract(ArchiverApp app, string archivePath, bool toFolder)
    {
        string folder = ParentOf(archivePath);
        string target = toFolder ? Path.Combine(folder, ExtractFolderName(archivePath)) : folder;

        List<string> args = app.Kind switch
        {
            ArchiverKind.SevenZip or ArchiverKind.NanaZip => ["x", $"-o{target}", "--", archivePath],
            // A trailing backslash tells WinRAR the last argument is the destination folder (created if missing).
            ArchiverKind.WinRar => ["x", "--", archivePath, target.TrimEnd('\\') + "\\"],
            ArchiverKind.Bandizip => ["x", $"-o:{target}", archivePath],
            ArchiverKind.PeaZip => [toFolder ? "-ext2folder" : "-ext2here", archivePath],
            _ => throw new ArgumentOutOfRangeException(nameof(app)),
        };
        return new ArchiverInvocation(app.ExecutablePath, args, folder);
    }

    private static string ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd('\\')) ?? throw new ArgumentException("Drives can't be archived this way.", nameof(path));
}
