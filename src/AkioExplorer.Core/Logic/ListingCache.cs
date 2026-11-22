using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Logic;

/// <summary>
/// Small thread-safe LRU of recent folder listings for instant back/forward. Bounded by folder count and by the total
/// number of items held (a few huge folders must not pin hundreds of MB); the newest listing is always kept.
/// </summary>
public sealed class ListingCache : IListingCache
{
    private readonly int _capacity;
    private readonly int _maxItems;
    private int _itemCount;
    private readonly LinkedList<(StorageLocation Key, IReadOnlyList<StorageItem> Items)> _order = new();
    private readonly Dictionary<StorageLocation, LinkedListNode<(StorageLocation, IReadOnlyList<StorageItem>)>> _map = new();
    private readonly object _gate = new();

    public ListingCache(int capacity = 64, int maxItems = 200_000)
    {
        _capacity = Math.Max(1, capacity);
        _maxItems = Math.Max(1, maxItems);
    }

    public bool TryGet(StorageLocation location, out IReadOnlyList<StorageItem> items)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(location, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                items = node.Value.Item2;
                return true;
            }
        }

        items = Array.Empty<StorageItem>();
        return false;
    }

    public void Set(StorageLocation location, IReadOnlyList<StorageItem> items)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(location, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(location);
                _itemCount -= existing.Value.Item2.Count;
            }

            var node = _order.AddFirst((location, items));
            _map[location] = node;
            _itemCount += items.Count;

            while ((_map.Count > _capacity || _itemCount > _maxItems) && _order.Last is { } last && last != node)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _itemCount -= last.Value.Items.Count;
            }
        }
    }

    public void Invalidate(StorageLocation location)
    {
        lock (_gate)
        {
            if (_map.Remove(location, out var node))
            {
                _order.Remove(node);
                _itemCount -= node.Value.Item2.Count;
            }
        }
    }
}
