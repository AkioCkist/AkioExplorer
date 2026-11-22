using System.Diagnostics;
using System.Runtime.InteropServices;
using AkioExplorer.Core.Abstractions;
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
        string args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
    }

    public void OpenTerminal(string folder)
    {
        if (!Directory.Exists(folder))
            throw new StorageException(StorageErrorKind.NotFound, "Open a folder first, then open the terminal there.");

        // wt.exe is an app execution alias in %LocalAppData%\Microsoft\WindowsApps when Windows Terminal is installed.
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            if (File.Exists(wt))
            {
                // ArgumentList quotes correctly, including a trailing backslash ("C:\").
                var start = new ProcessStartInfo(wt) { UseShellExecute = false };
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(folder);
                Process.Start(start)?.Dispose();
                return;
            }
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            throw new StorageException(StorageErrorKind.Unknown, $"Couldn't open a terminal: {ex.Message}", ex);
        }
    }
}
