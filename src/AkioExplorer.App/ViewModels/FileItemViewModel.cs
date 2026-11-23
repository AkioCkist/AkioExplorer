using AkioExplorer.App.Services;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace AkioExplorer.App.ViewModels;

/// <summary>One row / tile. Kept lightweight: text is precomputed once, icons load when the row is realised.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly IconService _icons;
    private bool _smallRequested;
    private bool _largeRequested;
    private bool _jumboRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarred), nameof(HasLabel), nameof(LabelBrush), nameof(LabelName), nameof(IsMedia), nameof(ShowMediaBadge))]
    private StorageItem _item;

    /// <summary>
    /// Videos and music get their player's icon on the thumbnail: a video frame or album cover alone looks like a photo.
    /// </summary>
    public bool IsMedia => Item.Kind == StorageItemKind.File && FileCategories.Of(Item) is FileCategory.Video or FileCategory.Audio;

    /// <summary>Only on picture thumbnails; the plain type icon already says what the file is.</summary>
    public bool ShowMediaBadge => IsMedia && HasThumbnail;

    /// <summary>Favourite / starred on the cloud service (star after the name).</summary>
    public bool IsStarred => Item.IsStarred;

    public bool HasLabel => Item.Label != ItemLabel.None;

    /// <summary>MEGA's label colours.</summary>
    public Brush? LabelBrush => LabelColors.Brush(Item.Label);

    public string LabelName => HasLabel ? $"Label: {Item.Label}" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    /// <summary>App-wide "File name extensions" (View menu / Settings).</summary>
    public static bool ShowExtensions { get; set; } = true;

    /// <summary>Name as shown in the list: without the extension when extensions are hidden.</summary>
    public string DisplayName
    {
        get
        {
            if (ShowExtensions || Item.Kind != StorageItemKind.File) return Name;
            string stem = System.IO.Path.GetFileNameWithoutExtension(Name);
            return stem.Length == 0 ? Name : stem; // ".gitignore" keeps its name
        }
    }

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));

    /// <summary>Re-formats size and date after the size-unit or date setting changed.</summary>
    public void RefreshFormatting() => RefreshText();
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _modifiedText = string.Empty;
    [ObservableProperty] private string _secondaryText;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    public bool HasIcon => Icon is not null;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileIcon))]
    private ImageSource? _largeIcon;

    /// <summary>256 px icon for zoomed-in tiles (null when the type has none: <see cref="TileIcon"/> falls back).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileIcon))]
    private ImageSource? _jumboIcon;

    /// <summary>What a grid tile shows when there is no picture thumbnail.</summary>
    public ImageSource? TileIcon => JumboIcon ?? LargeIcon;

    /// <summary>Picture thumbnail for grid view (photo, video frame…); null shows <see cref="LargeIcon"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail), nameof(ShowMediaBadge))]
    private ImageSource? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Set once the thumbnail was fetched or found not to exist (see ThumbnailService).</summary>
    public bool ThumbnailRequested { get; set; }

    /// <summary>Pixel size of the thumbnail fetched so far; zooming in past it fetches a bigger one.</summary>
    public int ThumbnailPixelSize { get; set; }
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>Cut to the clipboard: shown dimmed until pasted (Explorer convention).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private bool _isCut;

    public FileItemViewModel(StorageItem item, IconService icons, string secondaryText, ColumnLayout columns)
    {
        _icons = icons;
        Columns = columns;
        _item = item;
        _name = item.Name;
        _secondaryText = secondaryText;
        RefreshText();
        if (icons.TryGet(item, IconSize.Small, out var small)) { _icon = small; _smallRequested = true; }
        if (icons.TryGet(item, IconSize.Large, out var large)) { _largeIcon = large; _largeRequested = true; }
    }

    /// <summary>Shared per-pane column widths (details view).</summary>
    public ColumnLayout Columns { get; }

    public string Path => Item.Path;

    public bool IsContainer => Item.IsContainer;

    public bool IsDrive => Item.Kind == StorageItemKind.Drive;

    public double Opacity => Item.IsHidden || IsCut ? 0.55 : 1.0;

    /// <summary>Used space in percent (drives only).</summary>
    public double DriveUsage => Item is { TotalSize: > 0 } d ? 100.0 * (d.TotalSize!.Value - (d.FreeSpace ?? 0)) / d.TotalSize.Value : 0;

    public bool IsDriveAlmostFull => DriveUsage >= 90;

    public string FolderGlyph => Item.Kind switch
    {
        StorageItemKind.Drive => "",
        StorageItemKind.Folder => "",
        _ => "",
    };

    public void Update(StorageItem item)
    {
        if (ReferenceEquals(item, Item) || item == Item) return;
        bool iconChanged = item.Kind != Item.Kind || !string.Equals(item.Extension, Item.Extension, StringComparison.OrdinalIgnoreCase);
        if (item.Modified != Item.Modified || item.Size != Item.Size || iconChanged)
        {
            ThumbnailRequested = false; // content changed: fetch a fresh thumbnail when shown
            ThumbnailPixelSize = 0;
            Thumbnail = null;
        }
        Item = item;
        if (!IsRenaming) Name = item.Name;
        RefreshText();
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Path));
        if (iconChanged)
        {
            _smallRequested = _largeRequested = _jumboRequested = false;
            Icon = LargeIcon = JumboIcon = null;
        }
    }

    /// <summary>Called when the row becomes visible.</summary>
    public async void EnsureIcon(IconSize size)
    {
        if (size == IconSize.Small)
        {
            if (_smallRequested) return;
            _smallRequested = true;
            Icon = await _icons.GetAsync(Item, size);
        }
        else if (size == IconSize.Large)
        {
            if (_largeRequested) return;
            _largeRequested = true;
            LargeIcon = await _icons.GetAsync(Item, size);
        }
        else
        {
            if (_jumboRequested) return;
            _jumboRequested = true;
            JumboIcon = await _icons.GetAsync(Item, size);
        }
    }

    private void RefreshText()
    {
        var item = Item;
        SizeText = item.Kind switch
        {
            StorageItemKind.File or StorageItemKind.Folder when item.Size is { } size => Formatting.FormatSize(size),
            StorageItemKind.Drive when item.TotalSize is not null => $"{Formatting.FormatSize(item.FreeSpace ?? 0)} free",
            _ => string.Empty,
        };
        ModifiedText = item.Modified is { } modified ? Formatting.FormatDate(modified) : string.Empty;
    }
}

/// <summary>Colours of <see cref="ItemLabel"/> (MEGA's palette), shared by the list and the menus.</summary>
public static class LabelColors
{
    private static readonly Dictionary<ItemLabel, SolidColorBrush> Brushes = new();

    public static Windows.UI.Color Color(ItemLabel label) => label switch
    {
        ItemLabel.Red => Windows.UI.Color.FromArgb(255, 0xF0, 0x37, 0x3A),
        ItemLabel.Orange => Windows.UI.Color.FromArgb(255, 0xF9, 0x89, 0x1F),
        ItemLabel.Yellow => Windows.UI.Color.FromArgb(255, 0xF5, 0xC3, 0x0F),
        ItemLabel.Green => Windows.UI.Color.FromArgb(255, 0x4C, 0xC8, 0x4A),
        ItemLabel.Blue => Windows.UI.Color.FromArgb(255, 0x3E, 0x8B, 0xF6),
        ItemLabel.Purple => Windows.UI.Color.FromArgb(255, 0xA3, 0x5E, 0xD8),
        ItemLabel.Grey => Windows.UI.Color.FromArgb(255, 0x9A, 0x9A, 0x9A),
        _ => Windows.UI.Color.FromArgb(0, 0, 0, 0),
    };

    public static SolidColorBrush? Brush(ItemLabel label)
    {
        if (label == ItemLabel.None) return null;
        if (!Brushes.TryGetValue(label, out var brush)) Brushes[label] = brush = new SolidColorBrush(Color(label));
        return brush;
    }
}
