using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Transfers;

namespace AkioExplorer.Core.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum FileViewMode
{
    Details,
    Grid,
}

/// <summary>How the Date modified column is written.</summary>
public enum DateDisplay
{
    /// <summary>Windows short date and time ("22/11/2026 14:23").</summary>
    System,
    DayMonthYear,
    YearMonthDay,
    MonthDayYear,

    /// <summary>"5 min ago", "3 h ago", "2 days ago", then the date (OneCommander-style age).</summary>
    Relative,
}

public enum TerminalApp
{
    WindowsTerminal,
    PowerShell7,
    WindowsPowerShell,
    CommandPrompt,
}

public enum CopyPathFormat
{
    /// <summary>C:\My Files\a.txt → "C:\My Files\a.txt" (quotes only when there are spaces).</summary>
    QuoteWhenNeeded,
    Plain,
    ForwardSlashes,
}

/// <summary>Where a folder opened from outside (Win+E, Explorer, command line) appears.</summary>
public enum ExternalOpenTarget
{
    NewTab,
    CurrentTab,
}

public enum WindowBackdrop
{
    Mica,
    MicaAlt,
    Acrylic,
    Solid,
}

public sealed class FavoriteEntry
{
    public string Name { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class PaneState
{
    public string ProviderId { get; set; } = "local";
    public string Path { get; set; } = string.Empty;
    public SortField SortField { get; set; } = SortField.Name;
    public bool SortDescending { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public FileViewMode ViewMode { get; set; } = FileViewMode.Details;

    /// <summary>Browser-style zoom of this pane's rows and tiles, in percent (Ctrl+wheel).</summary>
    public int Zoom { get; set; } = 100;

    /// <summary>Open tabs of this pane (left to right); empty = one tab at <see cref="ProviderId"/>/<see cref="Path"/>.</summary>
    public List<TabState> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
}

public sealed class TabState
{
    public string ProviderId { get; set; } = "local";
    public string Path { get; set; } = string.Empty;
}

public sealed class WindowPlacement
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool IsMaximized { get; set; }
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public FileViewMode DefaultViewMode { get; set; } = FileViewMode.Details;
    public bool DualPaneOnStartup { get; set; }

    /// <summary>Preview pane (Alt+P) open, and its width in DIPs.</summary>
    public bool ShowPreviewPane { get; set; }
    public double PreviewPaneWidth { get; set; } = 360;
    public bool ShowHiddenItems { get; set; }

    /// <summary>Show ".txt", ".jpg"… after file names (File Explorer's "File name extensions").</summary>
    public bool ShowFileExtensions { get; set; } = true;

    /// <summary>Calculate folder sizes automatically when a local folder opens (OneCommander style).</summary>
    public bool AutoFolderSizes { get; set; }
    public bool RestoreLastLocations { get; set; } = true;

    /// <summary>Editor used by "Open in editor" (Ctrl+Shift+E): a <c>CodeEditor.Id</c>; empty = first one found (VS Code first).</summary>
    public string PreferredEditor { get; set; } = string.Empty;
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Ask;
    public int MaxSimultaneousOperations { get; set; } = 2;

    /// <summary>Connections per file for cloud downloads (segmented, IDM-style). 1 = plain single stream.</summary>
    public int DownloadConnections { get; set; } = 8;

    /// <summary>Files transferred at once inside one cloud copy/move (local ↔ local stays one at a time).</summary>
    public int ParallelFileTransfers { get; set; } = 4;

    /// <summary>Where "Download" saves files; empty = the Windows Downloads folder.</summary>
    public string DownloadFolder { get; set; } = string.Empty;
    public List<FavoriteEntry> Favorites { get; set; } = new();

    /// <summary>Search terms, newest first (shown under the search box).</summary>
    public List<string> RecentSearches { get; set; } = new();
    public List<CloudAccount> CloudAccounts { get; set; } = new();
    public PaneState LeftPane { get; set; } = new();
    public PaneState RightPane { get; set; } = new();
    public WindowPlacement Window { get; set; } = new();

    // ---- Appearance
    public WindowBackdrop Backdrop { get; set; } = WindowBackdrop.Mica;

    // ---- Files and folders
    public DateDisplay DateDisplay { get; set; } = DateDisplay.System;

    /// <summary>1 KB = 1000 bytes (drive makers, macOS) instead of 1024 (Windows).</summary>
    public bool DecimalSizeUnits { get; set; }

    /// <summary>".git", ".godot", ".vscode"… count as hidden items (Linux/macOS convention).</summary>
    public bool TreatDotFilesAsHidden { get; set; }

    /// <summary>Hide Office lock files (~$*), *.tmp, Thumbs.db and desktop.ini even when hidden items are shown.</summary>
    public bool HideJunkFiles { get; set; }

    /// <summary>Ask before moving items to the Recycle Bin (permanent delete always asks).</summary>
    public bool ConfirmRecycle { get; set; }

    // ---- Apps and menus
    public TerminalApp Terminal { get; set; } = TerminalApp.WindowsTerminal;
    public CopyPathFormat CopyPathFormat { get; set; } = CopyPathFormat.QuoteWhenNeeded;

    /// <summary>Show Windows shell extension items (7-Zip, Git, VLC, Send to…) in the right-click menu.</summary>
    public bool ShowShellMenuItems { get; set; } = true;

    /// <summary>Comma-separated captions or verbs of shell items to leave out ("Share, Give access to").</summary>
    public string HiddenShellMenuItems { get; set; } = string.Empty;

    // ---- Windows integration (registry state is the source of truth for the rest)
    /// <summary>Closing the window keeps AkioExplorer in the notification area, so it reopens instantly.</summary>
    public bool KeepRunningInBackground { get; set; }
    public ExternalOpenTarget ExternalOpenTarget { get; set; } = ExternalOpenTarget.NewTab;

    // ---- Quick search (launcher popup)
    /// <summary>A global hotkey opens a search box over any app: apps, files and folders (Everything index), web.</summary>
    public bool QuickSearchEnabled { get; set; } = true;

    /// <summary>Hotkey as text, e.g. "Alt+Space" (<see cref="HotkeyGesture"/>).</summary>
    public string QuickSearchHotkey { get; set; } = HotkeyGesture.Default.ToString();

    /// <summary>How often each path was opened from quick search (ranks favourites first). Trimmed to the top 300.</summary>
    public Dictionary<string, int> QuickSearchLaunches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
