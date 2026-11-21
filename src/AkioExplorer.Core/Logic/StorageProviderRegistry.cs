using AkioExplorer.Core.Abstractions;

namespace AkioExplorer.Core.Logic;

public sealed class StorageProviderRegistry : IStorageProviderRegistry
{
    private readonly List<IStorageProvider> _providers = new();
    private readonly object _gate = new();

    public StorageProviderRegistry(IEnumerable<IStorageProvider> providers)
    {
        _providers.AddRange(providers);
    }

    public IReadOnlyList<IStorageProvider> Providers
    {
        get { lock (_gate) return _providers.ToArray(); }
    }

    public event EventHandler? ProvidersChanged;

    public IStorageProvider? Find(string providerId)
    {
        lock (_gate) return _providers.FirstOrDefault(p => p.Id == providerId);
    }

    public IStorageProvider Get(string providerId) =>
        Find(providerId) ?? throw new KeyNotFoundException($"Storage provider '{providerId}' is not registered.");

    public void Unregister(string providerId)
    {
        lock (_gate) _providers.RemoveAll(p => p.Id == providerId);
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Register(IStorageProvider provider)
    {
        lock (_gate)
        {
            _providers.RemoveAll(p => p.Id == provider.Id);
            _providers.Add(provider);
        }
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }
}
