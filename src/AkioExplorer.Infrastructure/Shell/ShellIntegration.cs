using Microsoft.Win32;

namespace AkioExplorer.Infrastructure.Shell;

/// <summary>
/// Per-user Windows integration (HKCU only, no admin): default file manager (folders, drives and Win+E open
/// AkioExplorer), the "Open in AkioExplorer" menu item, start with Windows and the PATH entry.
/// Turning the default file manager on backs up what was there (File Explorer or another manager such as
/// OneCommander) and turning it off puts that back exactly.
/// </summary>
public sealed class ShellIntegration
{
    public const string Verb = "AkioExplorer";
    private const string ExplorerVerb = "OpenInExplorer";

    /// <summary>Command-line switch of the small process that takes Win+E (Windows ignores the registry for it).</summary>
    public const string WinEAgentArgument = "--win-e-agent";
    private const string WinEAgentRunValue = "AkioExplorerWinE";

    /// <summary>"This PC" — Win+E and "File Explorer" shortcuts run its opennewwindow verb.</summary>
    private const string ThisPcClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

    /// <summary>File Explorer's own "open in new window" handler (keeps "Open in File Explorer" working).</summary>
    private const string ExplorerDelegate = "{11dbb47c-a525-400b-9e80-a54615a090c0}";

    private const string Missing = "\0missing";

    private readonly string _classes;
    private readonly string _state;
    private readonly string _runKey;
    private readonly bool _touchPath;

    /// <param name="classesRoot">HKCU-relative classes key; tests pass a scratch key.</param>
    public ShellIntegration(string classesRoot = @"Software\Classes", string stateKey = @"Software\AkioExplorer\Integration",
        string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run", bool touchPath = true)
    {
        _classes = classesRoot;
        _state = stateKey;
        _runKey = runKey;
        _touchPath = touchPath;
    }

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AkioExplorer.exe");

    private static string Quote(string path) => $"\"{path}\"";

    // ------------------------------------------------------------------ default file manager

    public bool IsDefaultFileManager
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{_classes}\Directory\shell");
            return string.Equals(key?.GetValue(null) as string, Verb, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Folders and drives open in AkioExplorer by default; Win+E opens AkioExplorer.</summary>
    public void SetDefaultFileManager(bool enable, string exe)
    {
        if (enable == IsDefaultFileManager && !enable) return;
        using var state = Registry.CurrentUser.CreateSubKey(_state);
        string thisPc = $@"{_classes}\CLSID\{ThisPcClsid}\shell\opennewwindow\command";

        if (enable)
        {
            if (!IsDefaultFileManager)
            {
                // Back up what is there now, once, so "off" restores it (File Explorer or another file manager).
                state.SetValue("PrevDirectoryShell", ReadDefault($@"{_classes}\Directory\shell"));
                state.SetValue("PrevDriveShell", ReadDefault($@"{_classes}\Drive\shell"));
                state.SetValue("PrevThisPcCommand", ReadDefault(thisPc));
                state.SetValue("PrevThisPcDelegate", ReadValue(thisPc, "DelegateExecute"));
                state.SetValue("CreatedExplorerVerb", Registry.CurrentUser.OpenSubKey($@"{_classes}\Directory\shell\{ExplorerVerb}") is null ? 1 : 0);
            }

            WriteVerbs(exe);
            foreach (var cls in new[] { "Directory", "Drive" })
            {
                using var shell = Registry.CurrentUser.CreateSubKey($@"{_classes}\{cls}\shell");
                shell.SetValue(null, Verb);
                // Keep File Explorer one click away in the Windows menu.
                using var explorer = Registry.CurrentUser.CreateSubKey($@"{_classes}\{cls}\shell\{ExplorerVerb}");
                if (explorer.GetValue(null) is null) explorer.SetValue(null, "Open in File Explorer");
                if (explorer.GetValue("Icon") is null) explorer.SetValue("Icon", @"%SystemRoot%\Explorer.exe", RegistryValueKind.ExpandString);
                using var explorerCommand = Registry.CurrentUser.CreateSubKey($@"{_classes}\{cls}\shell\{ExplorerVerb}\command");
                if (explorerCommand.GetValue("DelegateExecute") is null) explorerCommand.SetValue("DelegateExecute", ExplorerDelegate);
            }

            using var command = Registry.CurrentUser.CreateSubKey(thisPc);
            command.SetValue(null, Quote(exe));
            command.SetValue("DelegateExecute", string.Empty); // empty = run the command above, not the shell handler
            state.SetValue("Exe", exe);

            // Recent Windows 11 builds open File Explorer on Win+E regardless of the key above: a small process
            // started at sign-in takes the key instead.
            using var run = Registry.CurrentUser.CreateSubKey(_runKey);
            run.SetValue(WinEAgentRunValue, $"{Quote(exe)} {WinEAgentArgument}");
        }
        else
        {
            RestoreDefault($@"{_classes}\Directory\shell", state.GetValue("PrevDirectoryShell") as string);
            RestoreDefault($@"{_classes}\Drive\shell", state.GetValue("PrevDriveShell") as string);
            string? prevCommand = state.GetValue("PrevThisPcCommand") as string;
            string? prevDelegate = state.GetValue("PrevThisPcDelegate") as string;
            if (prevCommand is null or Missing && prevDelegate is null or Missing)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{_classes}\CLSID\{ThisPcClsid}", throwOnMissingSubKey: false);
            }
            else
            {
                using var command = Registry.CurrentUser.CreateSubKey(thisPc);
                RestoreValue(command, null, prevCommand);
                RestoreValue(command, "DelegateExecute", prevDelegate);
            }
            if (state.GetValue("CreatedExplorerVerb") is 1)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{_classes}\Directory\shell\{ExplorerVerb}", false);
                Registry.CurrentUser.DeleteSubKeyTree($@"{_classes}\Drive\shell\{ExplorerVerb}", false);
            }
            foreach (var name in new[] { "PrevDirectoryShell", "PrevDriveShell", "PrevThisPcCommand", "PrevThisPcDelegate", "CreatedExplorerVerb" })
                state.DeleteValue(name, throwOnMissingValue: false);
            using var run = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
            run?.DeleteValue(WinEAgentRunValue, throwOnMissingValue: false);
        }
        NotifyShell();
    }

    /// <summary>
    /// While AkioExplorer is the default file manager, makes sure the Win+E agent starts at sign-in for this exe
    /// (setups made before the agent existed, or an exe that moved). Returns whether the agent is wanted.
    /// </summary>
    public bool EnsureWinEAgentAtSignIn(string exe)
    {
        if (!IsDefaultFileManager) return false;
        string wanted = $"{Quote(exe)} {WinEAgentArgument}";
        using var run = Registry.CurrentUser.CreateSubKey(_runKey);
        if (!string.Equals(run.GetValue(WinEAgentRunValue) as string, wanted, StringComparison.OrdinalIgnoreCase))
            run.SetValue(WinEAgentRunValue, wanted);
        return true;
    }

    // ------------------------------------------------------------------ "Open in AkioExplorer"

    public bool IsContextMenuInstalled => Registry.CurrentUser.OpenSubKey($@"{_classes}\Directory\shell\{Verb}\command") is { } key && Dispose(key);

    public void SetContextMenu(bool enable, string exe)
    {
        if (enable)
        {
            WriteVerbs(exe);
        }
        else
        {
            if (IsDefaultFileManager) SetDefaultFileManager(false, exe); // the default verb needs the menu item
            foreach (var cls in new[] { "Directory", "Drive", @"Directory\Background" })
                Registry.CurrentUser.DeleteSubKeyTree($@"{_classes}\{cls}\shell\{Verb}", throwOnMissingSubKey: false);
        }
        NotifyShell();
    }

    private void WriteVerbs(string exe)
    {
        foreach (var (cls, arg) in new[] { ("Directory", "%1"), ("Drive", "%1"), (@"Directory\Background", "%V") })
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{_classes}\{cls}\shell\{Verb}");
            verb.SetValue(null, "Open in AkioExplorer");
            verb.SetValue("Icon", Quote(exe) + ",0");
            using var command = Registry.CurrentUser.CreateSubKey($@"{_classes}\{cls}\shell\{Verb}\command");
            command.SetValue(null, $"{Quote(exe)} \"{arg}\"");
        }
    }

    // ------------------------------------------------------------------ start with Windows

    public bool IsAutostart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey);
            return key?.GetValue(Verb) is string;
        }
    }

    /// <summary>Starts hidden in the notification area at sign-in, so opening a folder is instant.</summary>
    public void SetAutostart(bool enable, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey);
        if (enable) key.SetValue(Verb, $"{Quote(exe)} --background");
        else key.DeleteValue(Verb, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ PATH

    public bool IsOnPath(string exe)
    {
        string dir = Path.GetDirectoryName(exe) ?? string.Empty;
        return ReadUserPath().Any(p => SamePath(p, dir));
    }

    /// <summary>Adds the app folder to the user PATH so "AkioExplorer ." works in any terminal (new terminals only).</summary>
    public void SetOnPath(bool enable, string exe)
    {
        if (!_touchPath) return;
        string dir = Path.GetDirectoryName(exe) ?? string.Empty;
        var parts = ReadUserPath().Where(p => !SamePath(p, dir)).ToList();
        if (enable) parts.Add(dir);
        Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.User); // broadcasts WM_SETTINGCHANGE
    }

    private static List<string> ReadUserPath() =>
        (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ helpers

    private static string ReadDefault(string key) => ReadValue(key, null);

    private static string ReadValue(string key, string? name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? Missing;
    }

    private static void RestoreDefault(string key, string? previous)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        if (!string.Equals(k.GetValue(null) as string, Verb, StringComparison.OrdinalIgnoreCase)) return; // someone else took over since
        RestoreValue(k, null, previous);
    }

    private static void RestoreValue(RegistryKey key, string? name, string? previous)
    {
        if (previous is null or Missing) key.DeleteValue(name ?? string.Empty, throwOnMissingValue: false);
        else key.SetValue(name, previous);
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }

    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
