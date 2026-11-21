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
    public bool RestoreLastLocations { get; set; } = true;
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
}
