using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.App.Views;
using AkioExplorer.CloudProviders;
using AkioExplorer.CloudProviders.Dropbox;
using AkioExplorer.CloudProviders.GoogleDrive;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.CloudProviders.OneDrive;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Infrastructure.Security;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Infrastructure.Local;
using AkioExplorer.Infrastructure.Logging;
using AkioExplorer.Infrastructure.Settings;
using AkioExplorer.Infrastructure.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AkioExplorer.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private ILogger<App>? _logger;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services not built");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services = ConfigureServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("AkioExplorer starting");

        // Register stored cloud accounts before panes restore their last folders (no network involved).
        _services.GetRequiredService<ICloudAccountService>().Initialize();
        _ = _services.GetRequiredService<ITransferHistory>(); // starts recording finished transfers
#if DEBUG
        // Developer aid (Debug builds only): a folder-backed fake cloud for testing transfer UI without OAuth.
        if (Environment.GetEnvironmentVariable("AKIO_DEV_CLOUD") is { Length: > 0 } devFolder)
            _services.GetRequiredService<IStorageProviderRegistry>().Register(new DevFolderCloud(devFolder));
#endif
        CleanOpenCache();

        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    /// <summary>Files downloaded only to be opened are kept for a week.</summary>
    private static void CleanOpenCache()
    {
        _ = Task.Run(() =>
        {
            try
            {
                string folder = Path.Combine(AppPaths.DataFolder, "Cache", "Open");
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (File.GetLastAccessTime(file) < DateTime.Now.AddDays(-7)) File.Delete(file);
                }
            }
            catch
            {
                // housekeeping only
            }
        });
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddProvider(new FileLoggerProvider(AppPaths.LogFolder, System.Diagnostics.Debugger.IsAttached || Environment.GetEnvironmentVariable("AKIO_DEBUG_LOG") == "1" ? LogLevel.Debug : LogLevel.Information));
        });

        services.AddSingleton(DispatcherQueue.GetForCurrentThread());

        // Infrastructure
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IKnownFoldersService, KnownFoldersService>();
        services.AddSingleton<IShellIconExtractor, ShellIconExtractor>();
        services.AddSingleton<IShellActions, ShellActions>();
        services.AddSingleton<IListingCache>(_ => new ListingCache(capacity: 64));

        // Storage providers: cloud providers register here in Phase 3.
        services.AddSingleton<LocalStorageProvider>();
        services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<LocalStorageProvider>());
        services.AddSingleton<IStorageProviderRegistry, StorageProviderRegistry>();

        // Cloud accounts: secrets in Windows Credential Manager, one connector per service.
        services.AddSingleton(_ => CloudAppsConfig.Load());
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<ICloudConnector>(sp => new GoogleDriveConnector(sp.GetRequiredService<CloudAppsConfig>(), sp.GetRequiredService<ICredentialStore>(), CloudHttp.Client));
        services.AddSingleton<ICloudConnector>(sp => new DropboxConnector(sp.GetRequiredService<CloudAppsConfig>(), sp.GetRequiredService<ICredentialStore>(), CloudHttp.Client));
        services.AddSingleton<ICloudConnector>(sp => new OneDriveConnector(sp.GetRequiredService<CloudAppsConfig>(), sp.GetRequiredService<ICredentialStore>(), CloudHttp.Client));
        services.AddSingleton<ICloudAccountService, CloudAccountService>();
        services.AddSingleton<CloudAccountsViewModel>();

        // File operations engine (transfer executors per provider pair; cloud executors arrive in Phase 4)
        // Executors are tried in order: OS copy engine (local↔local), server-side copy (same cloud account),
        // streaming (upload, download, cloud↔cloud).
        services.AddSingleton<ITransferExecutor, LocalTransferExecutor>();
        services.AddSingleton<ITransferExecutor, ServerSideTransferExecutor>();
        services.AddSingleton<ITransferExecutor, StreamingTransferExecutor>();
        services.AddSingleton<JsonTransferHistory>();
        services.AddSingleton<ITransferHistory>(sp => sp.GetRequiredService<JsonTransferHistory>());
        services.AddSingleton<TransferHistoryViewModel>();
        services.AddSingleton<IConflictResolver, ConflictResolver>();
        services.AddSingleton<IFileOperationService, FileOperationService>();

        // App services
        services.AddSingleton<IconService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<FavoritesService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<DragDropBroker>();
        services.AddSingleton<OperationsViewModel>();

        // View models & views
        services.AddTransient<FilePaneViewModel>();
        services.AddSingleton<Func<FilePaneViewModel>>(sp => () => sp.GetRequiredService<FilePaneViewModel>());
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception");
        // Keep the app alive for non-fatal UI errors; the failing operation already surfaced its own message.
        e.Handled = true;
    }
}
