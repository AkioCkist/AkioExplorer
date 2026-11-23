using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace AkioExplorer.App.ViewModels;

/// <summary>
/// Per-pane layout shared by every row and tile of that pane: details-view column widths and the zoom level.
/// Narrow panes drop the least important columns first so the Name column always stays readable (dual-pane on a
/// laptop screen). Zoom works like a browser's (Ctrl+wheel, Ctrl +/-/0): rows, tiles, icons, pictures and text
/// grow together; the step list is the browsers' so the percentages feel familiar.
/// </summary>
public sealed partial class ColumnLayout : ObservableObject
{
    private static readonly GridLength Hidden = new(0);

    /// <summary>Zoom steps in percent (the Chrome / Edge list, 25 % to 500 %).</summary>
    public static readonly IReadOnlyList<int> ZoomSteps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];

    public const int DefaultZoom = 100;

    [ObservableProperty] private GridLength _dateWidth = new(150);
    [ObservableProperty] private GridLength _typeWidth = new(150);
    [ObservableProperty] private GridLength _sizeWidth = new(110);

    private int _zoom = DefaultZoom;

    /// <summary>Zoom in percent, one of <see cref="ZoomSteps"/>.</summary>
    public int Zoom
    {
        get => _zoom;
        set
        {
            value = Snap(value);
            if (value == _zoom) return;
            _zoom = value;
            OnPropertyChanged(nameof(Zoom));
            // Every size below is derived; one notification each, raised only when the zoom really changes.
            foreach (var name in DerivedNames) OnPropertyChanged(name);
        }
    }

    private static readonly string[] DerivedNames =
    [
        nameof(ZoomText), nameof(IsDefaultZoom), nameof(RowHeight), nameof(RowIconSize), nameof(RowFontSize),
        nameof(TileWidth), nameof(TileHeight), nameof(TilePictureRow), nameof(TilePictureHeight), nameof(TilePictureWidth),
        nameof(TileIconSize), nameof(TileFontSize), nameof(UsesJumboIcons), nameof(TilePadding), nameof(ShowTileNames),
    ];

    private double Scale => _zoom / 100.0;

    public string ZoomText => $"{_zoom}%";

    public bool IsDefaultZoom => _zoom == DefaultZoom;

    // Details rows (32 epx, 20 px icon, 14 px text at 100 %). Text never drops below 7 px.
    public double RowHeight => Math.Max(12, Math.Round(32 * Scale));
    public double RowIconSize => Math.Max(8, Math.Round(20 * Scale));
    public double RowFontSize => Math.Max(7, Math.Round(14 * Scale, 1));

    // Grid tiles (108 x 132 with a 96 x 64 picture box and a 48 px icon at 100 %).
    public double TilePictureHeight => Math.Round(64 * Scale);
    public double TilePictureWidth => Math.Round(96 * Scale);
    public GridLength TilePictureRow => new(TilePictureHeight);
    public double TileIconSize => Math.Round(48 * Scale);
    public double TileFontSize => Math.Max(9, Math.Round(12 * Scale, 1));
    public double TileWidth => Math.Round(108 * Scale);

    /// <summary>Below 40 % names would be unreadable: tiles become a picture overview.</summary>
    public bool ShowTileNames => Scale >= 0.4;

    public Thickness TilePadding => new(Math.Max(1, Math.Round(6 * Scale)), Math.Max(1, Math.Round(8 * Scale)), Math.Max(1, Math.Round(6 * Scale)), Math.Max(1, Math.Round(4 * Scale)));

    /// <summary>Picture + two lines of name + padding, so small zoom levels never clip the name.</summary>
    public double TileHeight => ShowTileNames
        ? Math.Max(Math.Round(132 * Scale), TilePictureHeight + Math.Ceiling(TileFontSize * 1.34 * 2) + 20)
        : TilePictureHeight + TilePadding.Top + TilePadding.Bottom + 4; // + the template's row spacing

    /// <summary>Above ~64 px the 48 px shell icon starts to look soft: ask for the 256 px one.</summary>
    public bool UsesJumboIcons => TileIconSize > 64;

    /// <summary>One step in or out; returns false at the end of the range.</summary>
    public bool Step(int direction)
    {
        int index = IndexOf(_zoom) + Math.Sign(direction);
        if (index < 0 || index >= ZoomSteps.Count) return false;
        Zoom = ZoomSteps[index];
        return true;
    }

    private static int IndexOf(int zoom)
    {
        for (int i = 0; i < ZoomSteps.Count; i++)
        {
            if (ZoomSteps[i] == zoom) return i;
        }
        return ZoomSteps.Count / 2;
    }

    /// <summary>Nearest step (settings files may hold anything).</summary>
    private static int Snap(int zoom) => ZoomSteps.MinBy(step => Math.Abs(step - zoom));

    public void Update(double paneWidth)
    {
        if (paneWidth >= 820)
        {
            Set(new GridLength(150), new GridLength(150), new GridLength(110));
        }
        else if (paneWidth >= 640)
        {
            Set(new GridLength(140), new GridLength(120), new GridLength(96));
        }
        else if (paneWidth >= 500)
        {
            Set(new GridLength(140), Hidden, new GridLength(90));
        }
        else
        {
            Set(Hidden, Hidden, new GridLength(84));
        }
    }

    private void Set(GridLength date, GridLength type, GridLength size)
    {
        if (DateWidth != date) DateWidth = date;
        if (TypeWidth != type) TypeWidth = type;
        if (SizeWidth != size) SizeWidth = size;
    }
}
