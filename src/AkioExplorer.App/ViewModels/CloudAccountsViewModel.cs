using System.Collections.ObjectModel;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace AkioExplorer.App.ViewModels;

/// <summary>Connected cloud accounts and available services, shared by the sidebar and Settings.</summary>
public sealed partial class CloudAccountsViewModel : ObservableObject
{
    private readonly ICloudAccountService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<CloudAccountsViewModel> _logger;
    private readonly CloudProviders.CloudAppsConfig _config;

    public CloudAccountsViewModel(ICloudAccountService service, DispatcherQueue dispatcher, ILogger<CloudAccountsViewModel> logger, CloudProviders.CloudAppsConfig config)
    {
        _config = config;
        _service = service;
        _dispatcher = dispatcher;
        _logger = logger;
        Services = service.Connectors.Select(c => new CloudServiceItem(c)).ToList();
        service.AccountsChanged += (_, _) => _dispatcher.TryEnqueue(Rebuild);
        Rebuild();
    }

    public ObservableCollection<CloudAccountItem> Accounts { get; } = new();

    public IReadOnlyList<CloudServiceItem> Services { get; }

    /// <summary>Picks up edits to cloud-apps.json without restarting.</summary>
    public void ReloadConfiguration() => _config.Reload();

    /// <summary>Saves a client id the user entered in the add-account dialog (one-time setup per service).</summary>
    public void SaveRegistration(CloudProviderType type, string clientId, string? clientSecret) =>
        _config.Save(type, clientId, clientSecret);

    /// <summary>True when the user saved an app id for this service in the add-account dialog.</summary>
    public bool HasSavedRegistration(CloudProviderType type) => CloudProviders.CloudAppsConfig.HasSaved(type);

    /// <summary>Forgets the app id the user saved for this service; the next sign-in asks for one again.</summary>
    public void ClearRegistration(CloudProviderType type) => _config.Clear(type);

    /// <summary>True when connected accounts of this service depend on its app id to refresh their sign-in.</summary>
    public bool HasAccountsOf(CloudProviderType type) => Accounts.Any(a => a.Account.Type == type);

    [ObservableProperty] private bool _hasAccounts;

    /// <summary>Raised when an account needs the user to sign in again (sidebar shows a warning).</summary>
    public event EventHandler? Changed;

    public Task<CloudAccount> AddAsync(CloudProviderType type, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        _service.AddAccountAsync(type, values, cancellationToken);

    public CloudServiceItem? ServiceOf(CloudProviderType type) => Services.FirstOrDefault(s => s.Type == type);

    public async Task ReconnectAsync(Guid accountId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        await _service.ReconnectAsync(accountId, values, cancellationToken);
        if (Find(accountId) is { } item) item.NeedsReconnect = false;
    }

    public void Rename(Guid accountId, string name) => _service.Rename(accountId, name);

    public Task RemoveAsync(Guid accountId) => _service.RemoveAsync(accountId);

    public void MarkNeedsReconnect(string providerId)
    {
        var item = Accounts.FirstOrDefault(a => a.Account.ProviderId == providerId);
        if (item is null || item.NeedsReconnect) return;
        item.NeedsReconnect = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public CloudAccountItem? Find(Guid accountId) => Accounts.FirstOrDefault(a => a.Account.Id == accountId);

    public CloudAccountItem? FindByProvider(string providerId) => Accounts.FirstOrDefault(a => a.Account.ProviderId == providerId);

    public Core.Abstractions.IStorageProvider? ProviderOf(CloudAccount account) => _service.GetProvider(account.Id);

    public StorageLocation RootOf(CloudAccount account) =>
        new(account.ProviderId, _service.GetProvider(account.Id)?.RootPath ?? string.Empty);

    /// <summary>Fetches storage usage for all accounts (Settings page); failures just leave the text empty.</summary>
    public async Task LoadQuotasAsync()
    {
        foreach (var item in Accounts.ToList())
        {
            if (_service.GetProvider(item.Account.Id) is not { } provider) continue;
            try
            {
                var quota = await provider.GetQuotaAsync(CancellationToken.None);
                item.SetQuota(quota);
            }
            catch (StorageException ex)
            {
                if (ex.Kind == StorageErrorKind.AuthenticationRequired) MarkNeedsReconnect(item.Account.ProviderId);
                item.QuotaText = ex.Kind == StorageErrorKind.AuthenticationRequired ? "Sign-in expired" : "Storage usage unavailable";
                _logger.LogInformation("Quota for {Id} failed: {Message}", item.Account.Id, ex.Message);
            }
        }
    }

    private void Rebuild()
    {
        var reconnect = Accounts.Where(a => a.NeedsReconnect).Select(a => a.Account.Id).ToHashSet();
        Accounts.Clear();
        foreach (var account in _service.Accounts)
            Accounts.Add(new CloudAccountItem(account, _service.GetConnector(account.Type)?.Name ?? account.Type.ToString())
            {
                NeedsReconnect = reconnect.Contains(account.Id),
            });
        HasAccounts = Accounts.Count > 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Real logos for the first three services; a coloured badge (PNG) for the rest.</summary>
    public static string IconUri(CloudProviderType type) => type switch
    {
        CloudProviderType.GoogleDrive or CloudProviderType.Dropbox or CloudProviderType.OneDrive =>
            $"ms-appx:///Assets/Providers/{type.ToString().ToLowerInvariant()}.svg",
        _ => $"ms-appx:///Assets/Providers/{type.ToString().ToLowerInvariant()}.png",
    };

    public static Microsoft.UI.Xaml.Media.ImageSource IconSource(string uri) =>
        uri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(uri))
            : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(uri));
}

public sealed partial class CloudAccountItem : ObservableObject
{
    public CloudAccountItem(CloudAccount account, string serviceName)
    {
        Account = account;
        ServiceName = serviceName;
    }

    public CloudAccount Account { get; }

    public string Name => Account.DisplayName;

    public string Subtitle => Account.Email ?? Account.UserName ?? ServiceName;

    public string ServiceName { get; }

    public string IconUri => CloudAccountsViewModel.IconUri(Account.Type);

    public Microsoft.UI.Xaml.Media.ImageSource Icon => CloudAccountsViewModel.IconSource(IconUri);

    [ObservableProperty] private bool _needsReconnect;
    [ObservableProperty] private string _quotaText = string.Empty;
    [ObservableProperty] private double _quotaPercent;
    [ObservableProperty] private bool _hasQuota;

    public void SetQuota(StorageQuota? quota)
    {
        if (quota?.Used is not { } used)
        {
            QuotaText = string.Empty;
            HasQuota = false;
            return;
        }

        if (quota.Total is { } total && total > 0)
        {
            QuotaText = $"{Formatting.FormatSize(used)} of {Formatting.FormatSize(total)} used";
            QuotaPercent = 100.0 * used / total;
            HasQuota = true;
        }
        else
        {
            QuotaText = $"{Formatting.FormatSize(used)} used";
            HasQuota = false;
        }

        // MEGA paid plans: bandwidth limit for the period.
        if (quota.TransferTotal is { } transferTotal && transferTotal > 0)
            QuotaText += $" · transfer {Formatting.FormatSize(quota.TransferUsed ?? 0)} of {Formatting.FormatSize(transferTotal)}";
    }
}

public sealed class CloudServiceItem(ICloudConnector connector)
{
    public CloudProviderType Type => connector.Type;

    public string Name => connector.Name;

    public bool IsConfigured => connector.IsConfigured;

    public string SetupHint => connector.SetupHint;

    public CloudCategory Category => connector.Category;

    public string Description => connector.Description;

    public IReadOnlyList<CloudField> Fields => connector.Fields;

    public bool UsesBrowserSignIn => connector.UsesBrowserSignIn;

    public string IconUri => CloudAccountsViewModel.IconUri(connector.Type);

    /// <summary>Name, description and keywords contain every word of <paramref name="query"/>.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string haystack = $"{Name} {Description} {connector.Keywords} {CategoryName(Category)}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static string CategoryName(CloudCategory category) => category switch
    {
        CloudCategory.Popular => "Popular",
        CloudCategory.Business => "Work and business",
        CloudCategory.Servers => "Your own server or NAS",
        CloudCategory.ObjectStorage => "Object storage",
        _ => category.ToString(),
    };
}
