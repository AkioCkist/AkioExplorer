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
}
