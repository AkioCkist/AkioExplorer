using System.Diagnostics;
using System.Runtime.InteropServices;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using static AkioExplorer.Infrastructure.Shell.NativeMethods;

namespace AkioExplorer.Infrastructure.Shell;

public sealed class ShellActions : IShellActions
{
    public void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1155) // no association
        {
            OpenWith(path);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // user cancelled (UAC)
        {
        }
        catch (Exception ex)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Windows couldn't open \"{Path.GetFileName(path)}\". {ex.Message}", ex);
        }
    }

    public void OpenWith(string path)
    {
        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"") { UseShellExecute = false })?.Dispose();
    }

    public void ShowProperties(string path)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };
        ShellExecuteEx(ref info);
    }

    public void RevealInExplorer(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            return;
        }
        // Explorer's own "open in new window" verb: still File Explorer when AkioExplorer is the default file manager
        // (plain "explorer.exe <folder>" would follow the default verb and come straight back here).
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            lpVerb = "opennewwindow",
            lpFile = path.Length == 0 ? "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" : path, // empty = This PC
            nShow = SW_SHOW,
        };
        if (!ShellExecuteEx(ref info))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    public void OpenTerminal(string folder, TerminalApp app = TerminalApp.WindowsTerminal)
    {
        if (!Directory.Exists(folder))
            throw new StorageException(StorageErrorKind.NotFound, "Open a folder first, then open the terminal there.");

        try
        {
            // wt.exe / pwsh.exe are app execution aliases in %LocalAppData%\Microsoft\WindowsApps when installed from the Store.
            string aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
            if (app == TerminalApp.WindowsTerminal && File.Exists(Path.Combine(aliases, "wt.exe")))
            {
                // ArgumentList quotes correctly, including a trailing backslash ("C:\").
                var start = new ProcessStartInfo(Path.Combine(aliases, "wt.exe")) { UseShellExecute = false };
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(folder);
                Process.Start(start)?.Dispose();
                return;
            }

            string exe = app switch
            {
                TerminalApp.PowerShell7 => FindPwsh(aliases) ?? "powershell.exe",
                TerminalApp.CommandPrompt => "cmd.exe",
                _ => "powershell.exe",
            };
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Couldn't open a terminal: {ex.Message}", ex);
        }
    }

    public bool RunCommand(string commandLine, string folder)
    {
        if (!AddressCommand.TryParse(commandLine, out var program, out var arguments)) return false;
        RefreshPath();
        try
        {
            // ShellExecute, like Win+R: finds programs on PATH (with PATHEXT, so code.cmd) and in App Paths,
            // opens URLs and documents, and shows UAC for programs that need it.
            Process.Start(new ProcessStartInfo(program, AddressCommand.TerminalArguments(program, arguments, folder))
            {
                UseShellExecute = true,
                WorkingDirectory = folder,
            })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) // file / path not found
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // UAC cancelled
        {
            return true;
        }
        catch (Exception ex)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Couldn't run \"{program}\". {ex.Message}", ex);
        }
    }

    /// <summary>
    /// PATH as it is now in the registry: programs installed after AkioExplorer started (or kept running in the tray)
    /// are found without a restart, the way Explorer picks up environment changes.
    /// </summary>
    private static void RefreshPath()
    {
        try
        {
            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty;
            string user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
            string fresh = Environment.ExpandEnvironmentVariables(string.Join(';', new[] { machine, user }.Where(p => p.Length > 0)));
            if (fresh.Length > 0) Environment.SetEnvironmentVariable("Path", fresh);
        }
        catch (System.Security.SecurityException)
        {
            // Keep the inherited PATH.
        }
    }

    private static string? FindPwsh(string aliases)
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(installed)) return installed;
        string alias = Path.Combine(aliases, "pwsh.exe");
        return File.Exists(alias) ? alias : null;
    }
}
