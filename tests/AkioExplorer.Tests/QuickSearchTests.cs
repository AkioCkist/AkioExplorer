using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Tests;

public sealed class QuickSearchTests
{
    [Theory]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20, "Alt+Space")]
    [InlineData("ctrl + alt + k", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x4B, "Ctrl+Alt+K")]
    [InlineData("Win+Shift+F12", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x7B, "Win+Shift+F12")]
    [InlineData("Ctrl+`", HotkeyModifiers.Ctrl, 0xC0, "Ctrl+`")]
    public void Parses_hotkeys(string text, HotkeyModifiers modifiers, int key, string normalized)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(normalized, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("Space")]          // needs a modifier
    [InlineData("Alt")]            // needs a key
    [InlineData("Alt+K+J")]        // one key only
    [InlineData("Alt+Banana")]
    [InlineData("")]
    public void Rejects_bad_hotkeys(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Default_is_alt_space() => Assert.Equal("Alt+Space", HotkeyGesture.Default.ToString());

    [Theory]
    [InlineData("unikeynt", "UniKeyNT", 1000)]
    [InlineData("unik", "UniKeyNT", 800)]
    [InlineData("code", "Visual Studio Code", 600)]
    [InlineData("key", "UniKeyNT", 600)]          // PascalCase word start
    [InlineData("vsc", "Visual Studio Code", 500)]
    [InlineData("udi", "Studio", 400)]
    [InlineData("xyz", "Studio", 0)]
    public void Match_quality(string query, string text, int expected) =>
        Assert.Equal(expected, QuickSearchRanker.MatchScore(query, text));

    [Fact]
    public void Apps_and_prefixes_beat_deep_files()
    {
        string query = "unik";
        int app = QuickSearchRanker.Score(query, "UniKeyNT", @"C:\Users\me\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\UniKeyNT.lnk", QuickCandidateKind.App, 0);
        int exe = QuickSearchRanker.Score(query, "UniKeyNT.exe", @"C:\Users\me\Desktop\UniKeyNT.exe", QuickCandidateKind.File, 0);
        int song = QuickSearchRanker.Score(query, "01 danjou ineunikku.mp3", @"C:\Users\me\Music\x\01 danjou ineunikku.mp3", QuickCandidateKind.File, 0);
        int temp = QuickSearchRanker.Score(query, "unikey.log", @"C:\Users\me\AppData\Local\Temp\unikey.log", QuickCandidateKind.File, 0);

        Assert.True(app > exe, $"app {app} vs exe {exe}");
        Assert.True(exe > song, $"exe {exe} vs song {song}");
        Assert.True(exe > temp, $"exe {exe} vs temp {temp}");
    }

    [Fact]
    public void Launch_history_lifts_a_result()
    {
        int plain = QuickSearchRanker.Score("rep", "report.docx", @"C:\Docs\report.docx", QuickCandidateKind.File, 0);
        int used = QuickSearchRanker.Score("rep", "repo-notes.txt", @"C:\Docs\repo-notes.txt", QuickCandidateKind.File, 5);
        Assert.True(used > plain);
    }

    [Fact]
    public void Several_words_match_separately()
    {
        Assert.True(QuickSearchRanker.Score("visual code", "Visual Studio Code", @"C:\x\Visual Studio Code.lnk", QuickCandidateKind.App, 0) > 0);
        Assert.Equal(0, QuickSearchRanker.Score("visual banana", "Visual Studio Code", @"C:\x\Visual Studio Code.lnk", QuickCandidateKind.App, 0));
    }
}
