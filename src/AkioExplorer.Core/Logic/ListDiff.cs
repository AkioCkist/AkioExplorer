namespace AkioExplorer.Core.Logic;

/// <summary>
/// Applies a new ordered list onto an existing observable list with minimal changes so the UI keeps
/// scroll position, selection and realised containers (no flashing on refresh).
/// </summary>
public static class ListDiff
{
    public static void Apply<TItem, TSource>(
        IList<TItem> target,
        IReadOnlyList<TSource> source,
        Func<TItem, string> itemKey,
        Func<TSource, string> sourceKey,
        Func<TSource, TItem> create,
        Action<TItem, TSource>? update = null,
        Action<int, int>? move = null)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var sourceKeys = new HashSet<string>(source.Count, comparer);
        foreach (var s in source) sourceKeys.Add(sourceKey(s));

        // 1. Remove items that no longer exist.
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!sourceKeys.Contains(itemKey(target[i])))
                target.RemoveAt(i);
        }

        // 2. Walk the source order, moving / inserting as needed.
        var positions = new Dictionary<string, int>(target.Count, comparer);
        for (int i = 0; i < target.Count; i++) positions[itemKey(target[i])] = i;

        for (int i = 0; i < source.Count; i++)
        {
            var s = source[i];
            string key = sourceKey(s);

            if (i < target.Count && comparer.Equals(itemKey(target[i]), key))
            {
                update?.Invoke(target[i], s);
                continue;
            }

            int existing = -1;
            if (positions.ContainsKey(key))
            {
                for (int j = i + 1; j < target.Count; j++)
                {
                    if (comparer.Equals(itemKey(target[j]), key)) { existing = j; break; }
                }
            }

            if (existing >= 0)
            {
                var item = target[existing];
                if (move is not null)
                {
                    move(existing, i);
                }
                else
                {
                    target.RemoveAt(existing);
                    target.Insert(i, item);
                }
                update?.Invoke(item, s);
            }
            else
            {
                target.Insert(i, create(s));
            }
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
}
