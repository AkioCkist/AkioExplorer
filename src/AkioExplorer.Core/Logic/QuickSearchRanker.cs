namespace AkioExplorer.Core.Logic;

/// <summary>What a quick-search candidate is; apps (Start Menu shortcuts) rank above loose files.</summary>
public enum QuickCandidateKind
{
    App,
    File,
    Folder,
}

/// <summary>
/// Scores quick-search candidates (launcher popup) against what was typed. Higher is better, 0 = not a match.
/// Order of importance: how well the name matches (exact &gt; prefix &gt; word start &gt; initials &gt; anywhere),
/// how often the user opened it before, apps over files, short names and shallow paths over long ones, and
/// noise folders (temp, caches, package stores) last.
/// </summary>
public static class QuickSearchRanker
{
    private static readonly string[] NoisyFolders =
    [
        @"\AppData\Local\Temp\", @"\Windows\WinSxS\", @"\Windows\servicing\", @"\Windows\Installer\",
        @"\node_modules\", @"\.git\", @"\$Recycle.Bin\", @"\Package Cache\", @"\AppData\Local\Microsoft\",
        @"\obj\", @"\bin\Debug\", @"\bin\Release\", @"\.nuget\", @"\site-packages\", @"\Windows\assembly\",
        @"\AppData\Local\Packages\", @"\Windows\System32\DriverStore\",
    ];

    public static int Score(string query, string name, string path, QuickCandidateKind kind, int launches)
    {
        if (query.Length == 0) return 0;

        string stem = kind == QuickCandidateKind.Folder ? name : StripExtension(name);
        int match = MatchScore(query, stem);
        if (match == 0 && !ReferenceEquals(stem, name)) match = MatchScore(query, name);
        if (match == 0) match = WordsScore(query, stem);
        if (match == 0) return 0;

        int score = match;
        score += Math.Min(launches, 10) * 60;
        score += kind switch
        {
            QuickCandidateKind.App => 300,
            QuickCandidateKind.Folder => 40,
            _ => IsExecutable(name) ? 120 : 0,
        };
        score -= Math.Min((stem.Length - query.Length) * 2, 100);
        score -= Math.Min(Depth(path) * 6, 60);
        if (IsNoisy(path)) score -= 300;
        return Math.Max(score, 1);
    }

    /// <summary>1000 exact, 800 prefix, 600 word start, 500 initials, 400 anywhere, 0 none.</summary>
    public static int MatchScore(string query, string text)
    {
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 800;

        int index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        while (index > 0)
        {
            if (IsWordStart(text, index)) return 600;
            index = text.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        if (query.Length >= 2 && !query.Contains(' ') && MatchesInitials(query, text)) return 500;
        return text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 400 : 0;
    }

    /// <summary>
    /// "visual code" matches "Visual Studio Code": every word must match on its own; the result ranks below a
    /// match of the whole phrase.
    /// </summary>
    private static int WordsScore(string query, string text)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return 0;
        int total = 0;
        foreach (var word in words)
        {
            int score = MatchScore(word, text);
            if (score == 0) return 0;
            total += Math.Min(score, 800);
        }
        return total / words.Length - 100;
    }

    /// <summary>"vsc" matches "Visual Studio Code", "ps" matches "PowerShell".</summary>
    private static bool MatchesInitials(string query, string text)
    {
        int q = 0;
        for (int i = 0; i < text.Length && q < query.Length; i++)
        {
            if (IsWordStart(text, i) && char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(query[q])) q++;
        }
        return q == query.Length;
    }

    private static bool IsWordStart(string text, int i)
    {
        if (i == 0) return char.IsLetterOrDigit(text[0]);
        char prev = text[i - 1], c = text[i];
        if (!char.IsLetterOrDigit(c)) return false;
        if (!char.IsLetterOrDigit(prev)) return true;               // after space, '-', '_', '.', '('
        if (char.IsUpper(c) && char.IsLower(prev)) return true;     // camelCase / PascalCase
        return char.IsDigit(c) != char.IsDigit(prev);               // "win11" → "11"
    }

    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static bool IsExecutable(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    private static int Depth(string path)
    {
        int depth = 0;
        foreach (char c in path)
            if (c == '\\') depth++;
        return depth;
    }

    private static bool IsNoisy(string path)
    {
        foreach (var folder in NoisyFolders)
            if (path.Contains(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
