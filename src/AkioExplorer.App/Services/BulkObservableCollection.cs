using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AkioExplorer.App.Services;

/// <summary>ObservableCollection that can replace its whole content with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        if (Count == 0)
        {
            // First batch: one Reset is far cheaper for the ListView than N Add notifications.
            ReplaceAll(items);
            return;
        }

        // Later batches append individually so the user's scroll position is kept.
        foreach (var item in items) Add(item);
    }
}
