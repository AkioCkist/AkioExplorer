using AkioExplorer.Core.Logic;

namespace AkioExplorer.Tests;

public sealed class QuickSearchHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Short_prefix_of_a_past_query_finds_the_item_and_ranks_it_first()
    {
        var history = new List<QuickSearchHistoryEntry>();
        QuickSearchHistory.Record(history, "unikey", @"C:\Tools\UniKey\UniKeyNT.exe", "UniKeyNT.exe", QuickCandidateKind.File, Now);
        var unikey = history[0];

        Assert.True(QuickSearchHistory.IsLearned(unikey, "uni"));
        Assert.True(QuickSearchHistory.IsLearned(unikey, "U"));

        // An app that also starts with "uni" and is otherwise favoured (apps rank above files).
        int unikeyScore = QuickSearchRanker.Score("uni", unikey.Name, unikey.Path, QuickCandidateKind.File, 1) + QuickSearchHistory.Boost(unikey, "uni", Now);
        int uninstaller = QuickSearchRanker.Score("uni", "Uninstall Tool", @"C:\ProgramData\Start Menu\Uninstall Tool.lnk", QuickCandidateKind.App, 0);
        Assert.True(unikeyScore > uninstaller, $"{unikeyScore} <= {uninstaller}");
    }

    [Fact]
    public void Forget_drops_the_history_entry_and_launch_count_only_for_that_path()
    {
        const string unikey = @"C:\Tools\UniKey\UniKeyNT.exe";
        const string code = @"C:\VS Code\Code.exe";
        var history = new List<QuickSearchHistoryEntry>();
        QuickSearchHistory.Record(history, "unikey", unikey, "UniKeyNT.exe", QuickCandidateKind.File, Now);
        QuickSearchHistory.Record(history, "code", code, "Visual Studio Code", QuickCandidateKind.App, Now);
        var launches = new Dictionary<string, int> { [unikey.ToLowerInvariant()] = 4, [code.ToLowerInvariant()] = 2, [@"C:\Old\UNIKEYNT.EXE"] = 1 };

        Assert.True(QuickSearchHistory.IsRemembered(history, launches, unikey.ToUpperInvariant()));
        Assert.True(QuickSearchHistory.Forget(history, launches, unikey.ToUpperInvariant()));

        Assert.False(QuickSearchHistory.IsRemembered(history, launches, unikey));
        Assert.Equal(code, Assert.Single(history).Path);
        Assert.Equal(2, launches.Count); // VS Code kept, and the unrelated C:\Old path too
        Assert.False(QuickSearchHistory.Forget(history, launches, unikey)); // nothing left to forget
    }

    [Fact]
    public void Text_that_isnt_the_start_of_a_past_query_isnt_learned()
    {
        var history = new List<QuickSearchHistoryEntry>();
        QuickSearchHistory.Record(history, "vs", @"C:\VS\devenv.exe", "Visual Studio", QuickCandidateKind.App, Now);
        Assert.False(QuickSearchHistory.IsLearned(history[0], "vsc")); // longer than what was typed: maybe VS Code
        Assert.False(QuickSearchHistory.IsLearned(history[0], "code"));
        Assert.False(QuickSearchHistory.IsLearned(history[0], " "));
    }

    [Fact]
    public void Of_two_learned_items_the_more_used_and_recent_wins()
    {
        var history = new List<QuickSearchHistoryEntry>();
        QuickSearchHistory.Record(history, "report", @"C:\Docs\report-2024.docx", "report-2024.docx", QuickCandidateKind.File, Now.AddDays(-20));
        for (int i = 0; i < 3; i++)
            QuickSearchHistory.Record(history, "rep", @"C:\Docs\report-2026.docx", "report-2026.docx", QuickCandidateKind.File, Now.AddMinutes(-5));

        var old = history.Single(e => e.Path.EndsWith("2024.docx"));
        var recent = history.Single(e => e.Path.EndsWith("2026.docx"));
        Assert.True(QuickSearchHistory.Boost(recent, "re", Now) > QuickSearchHistory.Boost(old, "re", Now));
    }

    [Fact]
    public void Records_count_and_most_recent_queries_without_duplicates()
    {
        var history = new List<QuickSearchHistoryEntry>();
        QuickSearchHistory.Record(history, "UniKey", @"C:\a.exe", "a", QuickCandidateKind.App, Now);
        QuickSearchHistory.Record(history, "uni", @"c:\A.EXE", "a", QuickCandidateKind.App, Now);
        QuickSearchHistory.Record(history, "unikey ", @"C:\a.exe", "a", QuickCandidateKind.App, Now);

        var entry = Assert.Single(history);
        Assert.Equal(3, entry.Count);
        Assert.Equal(["unikey", "uni"], entry.Queries);
    }

    [Fact]
    public void Keeps_the_history_bounded_dropping_the_least_used()
    {
        var history = new List<QuickSearchHistoryEntry>();
        QuickSearchHistory.Record(history, "fav", @"C:\fav.exe", "fav", QuickCandidateKind.App, Now);
        QuickSearchHistory.Record(history, "fav", @"C:\fav.exe", "fav", QuickCandidateKind.App, Now);
        for (int i = 0; i < QuickSearchHistory.MaxEntries + 20; i++)
            QuickSearchHistory.Record(history, "f" + i, $@"C:\files\{i}.txt", $"{i}.txt", QuickCandidateKind.File, Now.AddDays(-60));

        Assert.Equal(QuickSearchHistory.MaxEntries, history.Count);
        Assert.Contains(history, e => e.Path == @"C:\fav.exe");
    }
}
