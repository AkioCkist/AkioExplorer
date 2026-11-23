using System.Diagnostics;
using System.Runtime.InteropServices;
using AkioExplorer.Infrastructure.Shell;

namespace AkioExplorer.App.Services;

/// <summary>
/// Win+E without AkioExplorer running: a tiny second process ("AkioExplorer.exe --win-e-agent", no XAML loaded)
/// that holds the <see cref="WinEHook"/> and opens AkioExplorer on Win+E. Windows ignores the registry override
/// for Win+E on recent builds, so something must be running to take the key; this keeps that apart from the
/// app, so quitting AkioExplorer doesn't hand Win+E back to File Explorer. Started at sign-in (Run key written by
/// <see cref="ShellIntegration.SetDefaultFileManager"/>) and by the app; stops when the replacement is turned off.
/// </summary>
public static class WinEAgent
{
    private static readonly string MutexName = @"Local\" + SingleInstance.Id + "-WinE";
    private static readonly string QuitEventName = @"Local\" + SingleInstance.Id + "-WinE-quit";

    public static bool IsAgentLaunch(IReadOnlyList<string> args) =>
        args.Any(a => a.Equals(ShellIntegration.WinEAgentArgument, StringComparison.OrdinalIgnoreCase));

    private static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
    }

    /// <summary>Starts the agent unless it already runs (cheap: one named-mutex lookup).</summary>
    public static void EnsureRunning()
    {
        if (IsRunning) return;
        try
        {
            Process.Start(new ProcessStartInfo(ShellIntegration.ExecutablePath, ShellIntegration.WinEAgentArgument) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            // Win+E falls back to File Explorer; folders still open here through the registry.
        }
    }

    public static void Stop()
    {
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            using (quit) quit.Set();
    }

    /// <summary>The agent process: hook Win+E and pump messages until told to quit.</summary>
    public static void Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (!created || !new ShellIntegration().IsDefaultFileManager) return;

        using var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        quit.Reset();
        uint threadId = GetCurrentThreadId();
        new Thread(() =>
        {
            quit.WaitOne();
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "Win+E agent quit" }.Start();

        var integration = new ShellIntegration();
        using var hook = new WinEHook();
        hook.Pressed += (_, _) => ThreadPool.QueueUserWorkItem(_ => OpenApp());
        // Someone else became the default (e.g. OneCommander turned back on): give Win+E back and quit.
        hook.ShouldTake = () =>
        {
            if (integration.IsDefaultFileManager) return true;
            quit.Set();
            return false;
        };
        hook.Enabled = true;
        if (!hook.Enabled) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void OpenApp()
    {
        AllowSetForegroundWindow(ASFW_ANY); // the window that opens may take the foreground, like Win+E's own
        var request = new LaunchRequest(null, false);
        if (SingleInstance.TrySend(request)) return;
        try
        {
            Process.Start(new ProcessStartInfo(ShellIntegration.ExecutablePath) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private const uint WM_QUIT = 0x0012;
    private const uint ASFW_ANY = unchecked((uint)-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
