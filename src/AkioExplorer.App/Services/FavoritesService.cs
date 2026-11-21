using System.Collections.ObjectModel;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.App.Services;

/// <summary>User pinned folders (any provider), persisted in settings.</summary>
public sealed class FavoritesService
{
    private readonly ISettingsService _settings;

    public FavoritesService(ISettingsService settings)
    {
        _settings = settings;
        Items = new ObservableCollection<FavoriteEntry>(settings.Current.Favorites);
    }

    public ObservableCollection<FavoriteEntry> Items { get; }

    public bool Contains(StorageLocation location) =>
        Items.Any(f => f.ProviderId == location.ProviderId && string.Equals(f.Path, location.Path, StringComparison.OrdinalIgnoreCase));

    public void Add(string name, StorageLocation location)
    {
        if (Contains(location)) return;
        Items.Add(new FavoriteEntry { Name = name, ProviderId = location.ProviderId, Path = location.Path });
        Persist();
    }

    public void Remove(FavoriteEntry entry)
    {
        if (Items.Remove(entry)) Persist();
    }

    public void Remove(StorageLocation location)
    {
        var match = Items.FirstOrDefault(f => f.ProviderId == location.ProviderId && string.Equals(f.Path, location.Path, StringComparison.OrdinalIgnoreCase));
        if (match is not null) Remove(match);
    }

    private void Persist()
    {
        _settings.Current.Favorites = Items.ToList();
        _settings.Save();
    }
}
