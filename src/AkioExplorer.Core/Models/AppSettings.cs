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
    public bool ShowHiddenItems { get; set; }
    public bool RestoreLastLocations { get; set; } = true;
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Ask;
    public int MaxSimultaneousOperations { get; set; } = 2;
    public List<FavoriteEntry> Favorites { get; set; } = new();
    public PaneState LeftPane { get; set; } = new();
    public PaneState RightPane { get; set; } = new();
    public WindowPlacement Window { get; set; } = new();
}
