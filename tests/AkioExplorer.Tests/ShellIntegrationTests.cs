using AkioExplorer.Infrastructure.Shell;
using Microsoft.Win32;

namespace AkioExplorer.Tests;

/// <summary>Registry round trips against a scratch HKCU key (never the real Software\Classes).</summary>
public sealed class ShellIntegrationTests : IDisposable
{
    private const string Root = @"Software\AkioExplorerTests";
    private const string Exe = @"C:\Apps\Akio Explorer\AkioExplorer.exe";
    private readonly ShellIntegration _integration = new($@"{Root}\Classes", $@"{Root}\State", $@"{Root}\Run", touchPath: false);

    private static string? Read(string key, string? name = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\Classes\{key}");
        return k?.GetValue(name) as string;
    }

    private static void Write(string key, string? name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{Root}\Classes\{key}");
        k.SetValue(name, value);
    }

    [Fact]
    public void Default_file_manager_takes_over_and_restores_the_previous_manager()
    {
        // Another file manager (OneCommander-style) is the current default.
        Write(@"Directory\shell", null, "OpenInOneCommander");
        Write(@"Drive\shell", null, "OpenInOneCommander");
        const string pc = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command";
        Write(pc, null, @"C:\Program Files\OneCommander\OneCommander.exe");
        Write(pc, "DelegateExecute", "");

        _integration.SetDefaultFileManager(true, Exe);
        Assert.True(_integration.IsDefaultFileManager);
        Assert.True(_integration.IsContextMenuInstalled);
        Assert.Equal("AkioExplorer", Read(@"Drive\shell"));
        Assert.Equal($"\"{Exe}\"", Read(pc));
        Assert.Equal($"\"{Exe}\" \"%1\"", Read(@"Directory\shell\AkioExplorer\command"));
        Assert.Equal($"\"{Exe}\" \"%V\"", Read(@"Directory\Background\shell\AkioExplorer\command"));

        _integration.SetDefaultFileManager(false, Exe);
        Assert.False(_integration.IsDefaultFileManager);
        Assert.Equal("OpenInOneCommander", Read(@"Directory\shell"));
        Assert.Equal("OpenInOneCommander", Read(@"Drive\shell"));
        Assert.Equal(@"C:\Program Files\OneCommander\OneCommander.exe", Read(pc));
        Assert.Equal("", Read(pc, "DelegateExecute"));
    }

    [Fact]
    public void Default_file_manager_off_restores_plain_File_Explorer()
    {
        _integration.SetDefaultFileManager(true, Exe);
        _integration.SetDefaultFileManager(false, Exe);

        Assert.Null(Read(@"Directory\shell"));
        Assert.Null(Read(@"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command"));
        Assert.Null(Read(@"Directory\shell\OpenInExplorer"));
    }

    private static string? ReadRun(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\Run");
        return k?.GetValue(name) as string;
    }

    [Fact]
    public void Win_E_agent_starts_at_sign_in_only_while_default_file_manager()
    {
        Assert.False(_integration.EnsureWinEAgentAtSignIn(Exe));
        Assert.Null(ReadRun("AkioExplorerWinE"));

        _integration.SetDefaultFileManager(true, Exe);
        Assert.Equal($"\"{Exe}\" --win-e-agent", ReadRun("AkioExplorerWinE"));

        // Setups made before the agent existed get the entry back on the next start.
        using (var run = Registry.CurrentUser.OpenSubKey($@"{Root}\Run", writable: true)) run!.DeleteValue("AkioExplorerWinE");
        Assert.True(_integration.EnsureWinEAgentAtSignIn(Exe));
        Assert.Equal($"\"{Exe}\" --win-e-agent", ReadRun("AkioExplorerWinE"));

        _integration.SetDefaultFileManager(false, Exe);
        Assert.Null(ReadRun("AkioExplorerWinE"));
        Assert.False(_integration.IsAutostart); // the app's own autostart is a separate switch
    }

    [Fact]
    public void Context_menu_and_autostart_toggle()
    {
        _integration.SetContextMenu(true, Exe);
        Assert.True(_integration.IsContextMenuInstalled);
        _integration.SetContextMenu(false, Exe);
        Assert.False(_integration.IsContextMenuInstalled);

        _integration.SetAutostart(true, Exe);
        Assert.True(_integration.IsAutostart);
        _integration.SetAutostart(false, Exe);
        Assert.False(_integration.IsAutostart);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
}
