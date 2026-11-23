using System.Diagnostics;
using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Local;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace AkioExplorer.App.ViewModels;

public enum QuickResultKind
{
    App,
    File,
    Folder,
    Web,
    Location,
    Hint,
    /// <summary>The typed text is arithmetic; <see cref="QuickSearchResult.Path"/> holds the answer to copy.</summary>
    Calculator,
    /// <summary>A link the app browses itself (Google Drive, MEGA, ftp://…); <see cref="QuickSearchResult.Path"/> is the link.</summary>
    Link,
}

/// <summary>One row of the quick-search popup.</summary>
public sealed partial class QuickSearchResult : ObservableObject
{
    public required QuickResultKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    /// <summary>File, folder or shortcut path; the URL for <see cref="QuickResultKind.Web"/>.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Segoe Fluent glyph shown until (or instead of) the shell icon.</summary>
    public string Glyph { get; init; } = "";

    internal StorageItem? Item { get; init; }

    internal int Score { get; init; }

    [ObservableProperty]
    private ImageSource? _icon;

    /// <summary>"Alt+1" … "Alt+9" for the first nine rows.</summary>
    [ObservableProperty]
    private string _shortcut = string.Empty;

    /// <summary>The glyph shows until the shell icon arrives (or for rows that have none).</summary>
    public Microsoft.UI.Xaml.Visibility GlyphVisibility =>
        Icon is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(GlyphVisibility));

    public bool IsOpenable => Kind != QuickResultKind.Hint;

    /// <summary>Files, folders and shortcuts (they have a containing folder and a path to copy).</summary>
    public bool IsOnDisk => Kind is QuickResultKind.App or QuickResultKind.File or QuickResultKind.Folder or QuickResultKind.Location;
}

/// <summary>
/// Launcher popup logic (Alt+Space): ranks Start Menu apps and files/folders from Everything's index as you type,
/// plus a web search row. Every keystroke cancels the previous search; Everything answers in tens of ms.
/// </summary>
public sealed partial class QuickSearchViewModel : ObservableObject
{
    private const int MaxResults = 30;
    private const int MaxRemembered = 300;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(35);

    private readonly ISettingsService _settings;
    private readonly IconService _icons;
    private readonly IShellActions _shell;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<QuickSearchViewModel> _logger;
    private IReadOnlyList<LauncherApp> _apps = [];
    private CancellationTokenSource? _searchCts;
    private int _generation;

    public QuickSearchViewModel(ISettingsService settings, IconService icons, IShellActions shell, DispatcherQueue dispatcher,
        ILogger<QuickSearchViewModel> logger)
    {
        _settings = settings;
        _icons = icons;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public BulkObservableCollection<QuickSearchResult> Results { get; } = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex = -1;

    /// <summary>Rest of the selected result's name after what was typed (grey autocomplete text; Tab accepts).</summary>
    [ObservableProperty]
    private string _completion = string.Empty;

    /// <summary>Opens a folder (or a file's folder, file selected) in the AkioExplorer window.</summary>
    public Action<string>? ShowInExplorer { get; set; }

    /// <summary>Opens a pasted link (Google Drive, MEGA, ftp://…) in a new AkioExplorer tab.</summary>
    public Action<string>? OpenLinkInExplorer { get; set; }

    /// <summary>Raised after something was opened, so the popup can hide.</summary>
    public event EventHandler? Opened;

    public QuickSearchResult? Selected => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>
    /// Refreshes the app list in the background (the popup is about to show). The very first time there is no list
    /// yet, so a search already shown is redone once the apps are in.
    /// </summary>
    public void Prepare() => _ = Task.Run(() =>
    {
        bool first = _apps.Count == 0;
        try { _apps = StartMenuApps.Load(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Loading Start Menu apps failed"); return; }
        if (first && _apps.Count > 0) _dispatcher.TryEnqueue(() => { if (Query.Trim().Length > 0) _ = SearchAsync(Query); });
    });

    partial void OnQueryChanged(string value)
    {
        UpdateCompletion(); // keep the ghost in step with typing until new results arrive
        _ = SearchAsync(value);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Selected));
        UpdateCompletion();
    }

    // ------------------------------------------------------------------ search

    private async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        int generation = ++_generation;
        string query = text.Trim();

        if (query.Length == 0)
        {
            Show([], generation);
            return;
        }

        try
        {
            await Task.Delay(Debounce, cts.Token);
            var launches = _settings.Current.QuickSearchLaunches;
            var apps = _apps;
            // Snapshot: the history is changed on this thread when something is opened, read on the search thread.
            var history = _settings.Current.QuickSearchHistory.Select(e => new QuickSearchHistoryEntry
            {
                Path = e.Path, Name = e.Name, Kind = e.Kind, LastOpened = e.LastOpened, Count = e.Count, Queries = [.. e.Queries],
            }).ToList();
            var found = await Task.Run(() => Collect(query, apps, launches, history, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) Show(found, generation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick search failed for {Query}", query);
        }
    }

    private static List<QuickSearchResult> Collect(string query, IReadOnlyList<LauncherApp> apps, Dictionary<string, int> launches,
        List<QuickSearchHistoryEntry> history, CancellationToken cancellationToken)
    {
        var results = new List<QuickSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int Launches(string path) => launches.GetValueOrDefault(path.ToLowerInvariant());

        // A typed path or web address goes straight there.
        if (LooksLikePath(query) && (Directory.Exists(query) || File.Exists(query)))
        {
            bool folder = Directory.Exists(query);
            results.Add(new QuickSearchResult
            {
                Kind = QuickResultKind.Location,
                Title = query,
                Subtitle = folder ? "Open folder" : "Open file",
                Path = query,
                Glyph = folder ? "" : "",
                Item = ToItem(query, folder),
                Score = int.MaxValue,
            });
            seen.Add(query);
        }

        // Opened before: found from the history itself (Everything may not return it for a short prefix), and first
        // when the typed text starts what was typed back then ("uni" after "unikey").
        var now = DateTimeOffset.Now;
        foreach (var entry in history)
        {
            if (seen.Contains(entry.Path)) continue;
            bool learned = QuickSearchHistory.IsLearned(entry, query);
            int match = QuickSearchRanker.Score(query, entry.Name, entry.Path, entry.Kind, Launches(entry.Path));
            if (!learned && match <= 0) continue;
            bool folder = entry.Kind == QuickCandidateKind.Folder;
            if (folder ? !Directory.Exists(entry.Path) : !File.Exists(entry.Path)) continue; // moved or deleted since
            seen.Add(entry.Path);
            results.Add(new QuickSearchResult
            {
                Kind = entry.Kind switch
                {
                    QuickCandidateKind.App => QuickResultKind.App,
                    QuickCandidateKind.Folder => QuickResultKind.Folder,
                    _ => QuickResultKind.File,
                },
                Title = entry.Name,
                Subtitle = entry.Kind == QuickCandidateKind.App ? entry.Path : System.IO.Path.GetDirectoryName(entry.Path) ?? entry.Path,
                Path = entry.Path,
                Glyph = folder ? "" : entry.Kind == QuickCandidateKind.App ? "" : "",
                Item = ToItem(entry.Path, folder),
                Score = Math.Max(match, 0) + QuickSearchHistory.Boost(entry, query, now),
            });
        }

        foreach (var app in apps)
        {
            if (seen.Contains(app.Path)) continue;
            int score = QuickSearchRanker.Score(query, app.Name, app.Path, QuickCandidateKind.App, Launches(app.Path));
            if (score <= 0) continue;
            seen.Add(app.Path);
            results.Add(new QuickSearchResult
            {
                Kind = QuickResultKind.App,
                Title = app.Name,
                Subtitle = app.Path,
                Path = app.Path,
                Glyph = "",
                Item = ToItem(app.Path, false),
                Score = score,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A link names no file on this PC: no index search (and no "start Everything" hint under it).
        var files = SharedLinks.TryParseLink(query, out _) && !LooksLikePath(query) ? [] : FindFiles(query, cancellationToken);
        if (files is null)
        {
            results.Add(new QuickSearchResult
            {
                Kind = QuickResultKind.Hint,
                Title = "Start Everything for instant file search",
                Subtitle = "Files and folders are found through the Everything index (voidtools). Apps still work.",
                Glyph = "",
                Score = 0,
            });
        }
        else
        {
            foreach (var item in files)
            {
                if (!seen.Add(item.Path)) continue;
                var kind = item.IsContainer ? QuickCandidateKind.Folder : QuickCandidateKind.File;
                int score = QuickSearchRanker.Score(query, item.Name, item.Path, kind, Launches(item.Path));
                if (score <= 0) continue;
                results.Add(new QuickSearchResult
                {
                    Kind = item.IsContainer ? QuickResultKind.Folder : QuickResultKind.File,
                    Title = item.Name,
                    Subtitle = System.IO.Path.GetDirectoryName(item.Path) ?? item.Path,
                    Path = item.Path,
                    Glyph = item.IsContainer ? "" : "",
                    Item = item,
                    Score = score,
                });
            }
        }

        var ranked = results.Where(r => r.Kind != QuickResultKind.Hint).OrderByDescending(r => r.Score).ThenBy(r => r.Title.Length)
            .Take(MaxResults).ToList();

        // Web search sits right under the best match, so it's always one key away (Alt+2).
        var web = new QuickSearchResult
        {
            Kind = QuickResultKind.Web,
            Title = query,
            Subtitle = "Search Google",
            Path = "https://www.google.com/search?q=" + Uri.EscapeDataString(query),
            Glyph = "",
        };
        ranked.Insert(Math.Min(1, ranked.Count), web);
        ranked.AddRange(results.Where(r => r.Kind == QuickResultKind.Hint));

        // A typed or pasted link: open it (in the app when it can browse it, else the browser) before anything else.
        if (!LooksLikePath(query) && SharedLinks.TryParseLink(query, out var link))
            ranked.InsertRange(0, LinkResults(link));

        // Arithmetic ("2+2*0.231314/2"): the answer goes on top, Enter copies it.
        if (Calculator.TryEvaluate(query, out double value))
        {
            string answer = Calculator.Format(value);
            ranked.Insert(0, new QuickSearchResult
            {
                Kind = QuickResultKind.Calculator,
                Title = "= " + answer,
                Subtitle = query.TrimStart('=').Trim() + "  ·  Enter to copy, Tab to keep calculating",
                Path = answer,
                Glyph = "",
            });
        }
        return ranked;
    }

    /// <summary>
    /// Rows for a link: "Open in AkioExplorer" for links the app browses (cloud shares, ftp://, sftp://, file:///),
    /// "Open in browser" for http(s) pages. A cloud share gets both, the app first.
    /// </summary>
    private static List<QuickSearchResult> LinkResults(Uri link)
    {
        var rows = new List<QuickSearchResult>();
        string url = link.OriginalString;

        if (SharedLinks.TryGetLocalPath(link) is { } local)
        {
            bool folder = Directory.Exists(local);
            if (folder || File.Exists(local))
            {
                rows.Add(new QuickSearchResult
                {
                    Kind = QuickResultKind.Location,
                    Title = local,
                    Subtitle = folder ? "Open folder" : "Open file",
                    Path = local,
                    Glyph = folder ? "" : "",
                    Item = ToItem(local, folder),
                });
            }
            return rows;
        }

        if (SharedLinks.BrowsableService(link) is { } service)
        {
            bool server = link.Scheme is not ("http" or "https");
            rows.Add(new QuickSearchResult
            {
                Kind = QuickResultKind.Link,
                Title = server ? $"{link.Scheme}://{link.Host}{(link.IsDefaultPort ? "" : ":" + link.Port)}{Uri.UnescapeDataString(link.AbsolutePath)}" : url,
                Subtitle = server ? $"Connect to {service} server in AkioExplorer" : $"Open {service} link in AkioExplorer",
                Path = url,
                Glyph = server ? "" : "",
            });
        }

        if (link.Scheme is "http" or "https")
        {
            rows.Add(new QuickSearchResult
            {
                Kind = QuickResultKind.Web,
                Title = url,
                Subtitle = "Open in browser",
                Path = link.AbsoluteUri,
                Glyph = "",
            });
        }
        return rows;
    }

    /// <summary>
    /// Names starting with the query first (Everything sorts by name, so a plain "contains" search could bury
    /// "Steam" under hundreds of "…steam…" files), then names containing it. Several words: the longest one
    /// goes to Everything, the rest are checked here.
    /// </summary>
    private static List<StorageItem>? FindFiles(string query, CancellationToken cancellationToken)
    {
        string[] words = query.Replace("\"", string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        string longest = words.OrderByDescending(w => w.Length).First();
        bool wildcard = longest.IndexOfAny(['*', '?']) >= 0;

        var found = new List<StorageItem>();
        if (!wildcard && words.Length == 1)
        {
            var prefix = IndexedFileSearch.Search(new SearchQuery { Name = longest + "*" }, 150, cancellationToken);
            if (prefix is null) return null;
            found.AddRange(prefix);
        }

        var contains = IndexedFileSearch.Search(new SearchQuery { Name = longest }, 400, cancellationToken);
        if (contains is null) return found.Count > 0 ? found : null;
        found.AddRange(contains);

        return words.Length == 1
            ? found
            : found.Where(i => words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void Show(List<QuickSearchResult> results, int generation)
    {
        if (generation != _generation) return;
        for (int i = 0; i < results.Count; i++) results[i].Shortcut = i < 9 ? $"Alt+{i + 1}" : string.Empty;
        Results.ReplaceAll(results);
        SelectedIndex = results.Count > 0 ? 0 : -1;
        UpdateCompletion();
        foreach (var result in results.Take(12)) _ = LoadIconAsync(result);
    }

    private async Task LoadIconAsync(QuickSearchResult result)
    {
        if (result.Item is not { } item) return;
        if (_icons.TryGet(item, IconSize.Large, out var cached)) result.Icon = cached;
        else result.Icon = await _icons.GetAsync(item, IconSize.Large);
    }

    /// <summary>Loads icons for rows scrolled into view.</summary>
    public void EnsureIcon(QuickSearchResult result)
    {
        if (result.Icon is null) _ = LoadIconAsync(result);
    }

    private void UpdateCompletion()
    {
        string typed = Query;
        Completion = Selected is { Kind: not QuickResultKind.Web and not QuickResultKind.Hint and not QuickResultKind.Calculator and not QuickResultKind.Link } s &&
                     typed.Length > 0 && s.Title.Length > typed.Length && s.Title.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
            ? s.Title[typed.Length..]
            : string.Empty;
    }

    // ------------------------------------------------------------------ keys and actions

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = (Math.Max(SelectedIndex, 0) + delta + Results.Count) % Results.Count;
    }

    /// <summary>Tab: takes the selected name into the box (to keep typing a longer path or refine).</summary>
    public bool AcceptCompletion()
    {
        if (Selected is not { } s || s.Kind is QuickResultKind.Web or QuickResultKind.Hint or QuickResultKind.Link) return false;
        string text = s.Kind == QuickResultKind.Calculator ? s.Path : s.Title; // the answer, to keep calculating
        if (string.Equals(Query, text, StringComparison.Ordinal)) return false;
        Query = text;
        return true;
    }

    public void OpenAt(int index)
    {
        if (index >= 0 && index < Results.Count) Open(Results[index]);
    }

    public void Open(QuickSearchResult? result, bool inFolder = false, bool asAdmin = false)
    {
        if (result is null || !result.IsOpenable) return;
        try
        {
            if (result.Kind == QuickResultKind.Calculator)
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(result.Path);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            }
            else if (result.Kind == QuickResultKind.Web)
            {
                _shell.Open(result.Path);
            }
            else if (result.Kind == QuickResultKind.Link)
            {
                OpenLinkInExplorer?.Invoke(result.Path);
            }
            else if (inFolder || result.Kind == QuickResultKind.Folder ||
                     (result.Kind == QuickResultKind.Location && Directory.Exists(result.Path)))
            {
                ShowInExplorer?.Invoke(result.Path);
            }
            else if (asAdmin)
            {
                Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            }
            else
            {
                _shell.Open(result.Path);
            }

            if (result.IsOnDisk) Remember(result);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return; // UAC prompt declined
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick search could not open {Path}", result.Path);
            return;
        }

        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void Remember(QuickSearchResult result)
    {
        string path = result.Path;
        var kind = result.Kind switch
        {
            QuickResultKind.App => QuickCandidateKind.App,
            QuickResultKind.Folder => QuickCandidateKind.Folder,
            QuickResultKind.Location when Directory.Exists(path) => QuickCandidateKind.Folder,
            _ => QuickCandidateKind.File,
        };
        string name = result.Kind == QuickResultKind.Location
            ? System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path
            : result.Title;
        QuickSearchHistory.Record(_settings.Current.QuickSearchHistory, Query, path, name, kind, DateTimeOffset.Now);

        var launches = _settings.Current.QuickSearchLaunches;
        string key = path.ToLowerInvariant();
        launches[key] = launches.GetValueOrDefault(key) + 1;
        if (launches.Count > MaxRemembered)
        {
            foreach (var stale in launches.OrderBy(p => p.Value).Take(launches.Count - MaxRemembered).Select(p => p.Key).ToList())
                launches.Remove(stale);
        }
        _settings.Save();
    }

    private static bool LooksLikePath(string text) =>
        text.Length >= 2 && ((char.IsLetter(text[0]) && text[1] == ':') || text.StartsWith(@"\\", StringComparison.Ordinal));

    private static StorageItem ToItem(string path, bool folder) => new()
    {
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path,
        Path = path,
        Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
    };
}
