using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.CloudProviders.OneDrive;
using Microsoft.Extensions.Logging.Abstractions;

namespace AkioExplorer.Tests;

public sealed class CloudAccountNamingTests
{
    [Fact]
    public async Task New_account_is_named_service_plus_email()
    {
        var (service, connector) = Create();
        connector.Next = new CloudIdentity("me@example.com", "Me", "id-1");

        var account = await service.AddAccountAsync(CloudProviderType.OneDrive, CancellationToken.None);

        Assert.Equal("OneDrive me@example.com", account.DisplayName);
    }

    [Fact]
    public async Task Falls_back_to_user_name_then_service_name()
    {
        var (service, connector) = Create();
        connector.Next = new CloudIdentity(null, "Me", "id-1");
        Assert.Equal("OneDrive Me", (await service.AddAccountAsync(CloudProviderType.OneDrive, CancellationToken.None)).DisplayName);

        connector.Next = new CloudIdentity(null, null, "id-2");
        Assert.Equal("OneDrive", (await service.AddAccountAsync(CloudProviderType.OneDrive, CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task Clashing_name_gets_a_number()
    {
        var (service, connector) = Create();
        connector.Next = new CloudIdentity("me@example.com", null, "id-1");
        var first = await service.AddAccountAsync(CloudProviderType.OneDrive, CancellationToken.None);
        service.Rename(first.Id, "OneDrive other@example.com");

        connector.Next = new CloudIdentity("other@example.com", null, "id-2");
        var second = await service.AddAccountAsync(CloudProviderType.OneDrive, CancellationToken.None);

        Assert.Equal("OneDrive other@example.com (2)", second.DisplayName);
    }

    private static (CloudAccountService, FakeConnector) Create()
    {
        var connector = new FakeConnector();
        var service = new CloudAccountService(new FakeSettings(), new StorageProviderRegistry([]), new FakeCredentials(),
            [connector], NullLogger<CloudAccountService>.Instance);
        return (service, connector);
    }

    private sealed class FakeConnector : ICloudConnector
    {
        public CloudIdentity Next { get; set; } = new(null, null, null);
        public CloudProviderType Type => CloudProviderType.OneDrive;
        public string Name => "OneDrive";
        public bool IsConfigured => true;
        public string SetupHint => string.Empty;
        public Task<CloudIdentity> AuthenticateAsync(CloudAccount account, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => Task.FromResult(Next);
        public ICloudStorageProvider CreateProvider(CloudAccount account) => new OneDriveProvider(account, new HttpClient(), new StaticTokens());
        public Task SignOutAsync(CloudAccount account, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event EventHandler? Changed;
        public void Save() { }
        public void Flush() { }
        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeCredentials : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new();
        public void Save(string key, string secret) => _values[key] = secret;
        public string? Load(string key) => _values.GetValueOrDefault(key);
        public void Delete(string key) => _values.Remove(key);
    }
}
