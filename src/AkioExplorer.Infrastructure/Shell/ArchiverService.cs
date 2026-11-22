using System.Diagnostics;
using AkioExplorer.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using static AkioExplorer.Infrastructure.Shell.NativeMethods;
using static AkioExplorer.Infrastructure.Shell.ShellMenuNative;

namespace AkioExplorer.Infrastructure.Shell;

/// <summary>
/// Finds 7-Zip, NanaZip, WinRAR, Bandizip and PeaZip (registry install keys, App Paths, default folders, Store
/// execution aliases) and starts their GUI programs. Detection runs once, in the background.
/// </summary>
public sealed class ArchiverService : IArchiverService
{
    private readonly ILogger<ArchiverService> _logger;
    private Task<IReadOnlyList<ArchiverApp>>? _archivers;

    public ArchiverService(ILogger<ArchiverService> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<ArchiverApp>> GetArchiversAsync() =>
        _archivers ??= Task.Run(() => (IReadOnlyList<ArchiverApp>)Detect());

    public void Start(ArchiverInvocation invocation)
    {
        var start = new ProcessStartInfo(invocation.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = invocation.WorkingDirectory,
        };
        foreach (var arg in invocation.Arguments) start.ArgumentList.Add(arg);
        _logger.LogInformation("Archiver: {File} {Args}", invocation.FileName, string.Join(' ', invocation.Arguments));
        Process.Start(start)?.Dispose();
    }

    private List<ArchiverApp> Detect()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");

        var candidates = new (ArchiverKind Kind, string Name, Func<IEnumerable<string?>> Paths)[]
        {
            (ArchiverKind.SevenZip, "7-Zip", () =>
            [
                InFolder(RegistryValue(@"SOFTWARE\7-Zip", "Path64"), "7zG.exe"),
                InFolder(RegistryValue(@"SOFTWARE\7-Zip", "Path"), "7zG.exe"),
                InFolder(AppPath("7zFM.exe") is { } fm ? Path.GetDirectoryName(fm) : null, "7zG.exe"),
                Path.Combine(pf, "7-Zip", "7zG.exe"),
                Path.Combine(pf86, "7-Zip", "7zG.exe"),
            ]),
            // Microsoft Store app: reachable through its execution alias.
            (ArchiverKind.NanaZip, "NanaZip", () => [Path.Combine(aliases, "NanaZipG.exe")]),
            (ArchiverKind.WinRar, "WinRAR", () =>
            [
                RegistryValue(@"SOFTWARE\WinRAR", "exe64"),
                RegistryValue(@"SOFTWARE\WinRAR", "exe32"),
                AppPath("WinRAR.exe"),
                Path.Combine(pf, "WinRAR", "WinRAR.exe"),
                Path.Combine(pf86, "WinRAR", "WinRAR.exe"),
            ]),
            (ArchiverKind.Bandizip, "Bandizip", () =>
            [
                AppPath("Bandizip.exe"),
                InFolder(RegistryValue(@"SOFTWARE\Bandizip", "ProgramFolder"), "Bandizip.exe"),
                Path.Combine(pf, "Bandizip", "Bandizip.exe"),
                Path.Combine(pf86, "Bandizip", "Bandizip.exe"),
            ]),
            (ArchiverKind.PeaZip, "PeaZip", () =>
            [
                AppPath("peazip.exe"),
                Path.Combine(pf, "PeaZip", "peazip.exe"),
                Path.Combine(pf86, "PeaZip", "peazip.exe"),
            ]),
        };

        var found = new List<ArchiverApp>();
        foreach (var (kind, name, paths) in candidates)
        {
            try
            {
                string? exe = paths().FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
                if (exe is null) continue;
                found.Add(new ArchiverApp(kind, name, exe, IconOf(exe)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Archiver detection failed for {Name}", name);
            }
        }
        _logger.LogInformation("Archivers found: {Names}", found.Count == 0 ? "none" : string.Join(", ", found.Select(a => a.Name)));
        return found;
    }

    private static string? InFolder(string? folder, string file) => string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, file);

    /// <summary>A value from HKLM (64-bit, then 32-bit view) or HKCU.</summary>
    private static string? RegistryValue(string key, string name)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var sub = root.OpenSubKey(key);
            if (sub?.GetValue(name) is string value && value.Length > 0) return Environment.ExpandEnvironmentVariables(value.Trim('"'));
        }
        return null;
    }

    private static string? AppPath(string exe) => RegistryValue($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}", string.Empty);

    private static IconBitmap? IconOf(string exe)
    {
        if (SHDefExtractIconW(exe, 0, 0, out var large, IntPtr.Zero, 32) != 0 || large == IntPtr.Zero) return null;
        try
        {
            return ShellIconExtractor.ToBitmap(large);
        }
        finally
        {
            DestroyIcon(large);
        }
    }
}
