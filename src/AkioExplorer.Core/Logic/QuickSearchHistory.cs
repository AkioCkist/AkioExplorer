namespace AkioExplorer.Core.Logic;

/// <summary>Something opened from quick search, with what was typed to find it.</summary>
public sealed class QuickSearchHistoryEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Name as shown in the results ("UniKey", "report.docx").</summary>
    public string Name { get; set; } = string.Empty;

    public QuickCandidateKind Kind { get; set; }

    public DateTimeOffset LastOpened { get; set; }

    public int Count { get; set; }

    /// <summary>What was typed when it was opened, most recent first ("unikey", "uni"). Lower case.</summary>
    public List<string> Queries { get; set; } = new();
}

/// <summary>
/// Remembers what quick search opened and for which text, so the next time the start of that text is enough:
/// "unikey" once, then "uni" (or just "u") puts UniKey first. The item is found from this history itself, so it shows
/// even when the app list or the file index doesn't return it for the shorter text.
/// </summary>
public static class QuickSearchHistory
{
    public const int MaxEntries = 300;
    public const int MaxQueriesPerEntry = 8;

    /// <summary>Above any name match (max ~2000 with launches and kind), so a learned item comes first.</summary>
    public const int LearnedBoost = 3000;

    /// <summary>Records an opening. Keeps the list bounded: the least used, oldest entries go first.</summary>
    public static void Record(List<QuickSearchHistoryEntry> history, string query, string path, string name, QuickCandidateKind kind, DateTimeOffset now)
    {
        var entry = history.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new QuickSearchHistoryEntry { Path = path };
            history.Add(entry);
        }
        entry.Name = name;
        entry.Kind = kind;
        entry.LastOpened = now;
        entry.Count++;

        string typed = query.Trim().ToLowerInvariant();
        if (typed.Length > 0)
        {
            entry.Queries.RemoveAll(q => q == typed);
            entry.Queries.Insert(0, typed);
            if (entry.Queries.Count > MaxQueriesPerEntry) entry.Queries.RemoveRange(MaxQueriesPerEntry, entry.Queries.Count - MaxQueriesPerEntry);
        }

        if (history.Count > MaxEntries)
        {
            foreach (var stale in history.OrderBy(e => Weight(e, now)).Take(history.Count - MaxEntries).ToList())
                history.Remove(stale);
        }
    }

    /// <summary>True when <paramref name="query"/> is the start of something typed before to open this entry.</summary>
    public static bool IsLearned(QuickSearchHistoryEntry entry, string query)
    {
        string typed = query.Trim();
        if (typed.Length == 0) return false;
        foreach (var past in entry.Queries)
            if (past.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Extra score for an entry: <see cref="LearnedBoost"/> when the typed text starts a past query (more for an
    /// exact repeat), plus how often and how recently it was opened, so among several learned items the usual one wins.
    /// </summary>
    public static int Boost(QuickSearchHistoryEntry entry, string query, DateTimeOffset now)
    {
        int score = 0;
        string typed = query.Trim();
        if (IsLearned(entry, typed))
        {
            score += LearnedBoost;
            if (entry.Queries.Any(q => q.Equals(typed, StringComparison.OrdinalIgnoreCase))) score += 300;
            // The longer the typed part of the past query, the surer the match ("unik" beats "u").
            int best = entry.Queries.Where(q => q.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Min(q => q.Length);
            score += Math.Min(typed.Length * 200 / Math.Max(best, 1), 200);
        }
        return score + Weight(entry, now);
    }

    /// <summary>Use and recency: up to 300 for use, up to 300 for having been opened lately.</summary>
    private static int Weight(QuickSearchHistoryEntry entry, DateTimeOffset now)
    {
        var age = now - entry.LastOpened;
        int recency = age < TimeSpan.FromHours(1) ? 300 : age < TimeSpan.FromDays(1) ? 250 : age < TimeSpan.FromDays(7) ? 150 : age < TimeSpan.FromDays(30) ? 60 : 0;
        return Math.Min(entry.Count, 10) * 30 + recency;
    }
}
