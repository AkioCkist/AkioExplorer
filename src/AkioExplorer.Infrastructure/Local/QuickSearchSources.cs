using AkioExplorer.Core.Models;

namespace AkioExplorer.Infrastructure.Local;

/// <summary>
/// File search for the quick-search popup: Everything's index only. Unlike <see cref="LocalStorageProvider.SearchAsync"/>
/// it never falls back to walking the disk — a keystroke must not start a minutes-long scan.
/// </summary>
public static class IndexedFileSearch
{
    /// <summary>True when a running Everything can answer (cheap enough to call per keystroke).</summary>
    public static bool IsAvailable => EverythingSession.IsAvailable();

    /// <summary>
    /// Up to <paramref name="max"/> items on fixed drives matching <paramref name="query"/>, or null when Everything
    /// isn't running. Blocking; call it off the UI thread.
    /// </summary>
    public static List<StorageItem>? Search(SearchQuery query, int max, CancellationToken cancellationToken)
    {
        if (!query.HasCriteria) return [];
        var roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name).ToArray();
        var results = new List<StorageItem>();
        var matcher = query.CreateMatcher(DateTimeOffset.Now);
        bool answered = LocalSearch.TryIndexOnly(roots, query, matcher, item =>
        {
            results.Add(item);
            return results.Count < max;
        }, cancellationToken);
        return answered ? results : null;
    }
}

/// <summary>An app the launcher can start: a Start Menu or desktop shortcut.</summary>
public sealed record LauncherApp(string Name, string Path);

/// <summary>
/// Shortcuts in the Start Menu (user + all users) and on the desktop — what people mean by "apps".
/// A few hundred files, so listing them again whenever the popup opens is cheap.
/// </summary>
public static class StartMenuApps
{
    private static readonly string[] Extensions = [".lnk", ".url", ".appref-ms"];

    public static IReadOnlyList<LauncherApp> Load()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        var byName = new Dictionary<string, LauncherApp>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            bool desktop = folder.Contains("Desktop", StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", desktop ? new EnumerationOptions { IgnoreInaccessible = true } : options); }
            catch { continue; }

            foreach (var file in files)
            {
                if (!Extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (IsUninstaller(name)) continue;
                byName.TryAdd(name, new LauncherApp(name, file)); // user Start Menu wins over all-users and desktop
            }
        }

        return byName.Values.ToList();
    }

    private static bool IsUninstaller(string name) =>
        name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
}
