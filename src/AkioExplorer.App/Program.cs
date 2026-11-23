using AkioExplorer.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AkioExplorer.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): a second launch hands its folder to the running instance
/// before any XAML loads, so opening folders from Explorer or Win+E costs only a tiny process start.
/// </summary>
public static class Program
{
    public static LaunchRequest StartupRequest { get; private set; } = new(null, false);

    [STAThread]
    private static void Main(string[] args)
    {
        if (WinEAgent.IsAgentLaunch(args))
        {
            WinEAgent.Run(); // tiny background process for Win+E; never loads XAML
            return;
        }

        StartupRequest = LaunchRequest.Parse(args, Environment.CurrentDirectory);
        if (SingleInstance.TryHandOff(StartupRequest)) return;
        StartXaml();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void StartXaml()
    {
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    [System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();
}
