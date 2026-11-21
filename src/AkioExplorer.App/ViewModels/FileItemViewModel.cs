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

    [ObservableProperty] private StorageItem _item;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _modifiedText = string.Empty;
    [ObservableProperty] private string _secondaryText;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private ImageSource? _largeIcon;

    /// <summary>Picture thumbnail for grid view (photo, video frame…); null shows <see cref="LargeIcon"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private ImageSource? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Set once the thumbnail was fetched or found not to exist (see ThumbnailService).</summary>
    public bool ThumbnailRequested { get; set; }
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
            Thumbnail = null;
        }
        Item = item;
        if (!IsRenaming) Name = item.Name;
        RefreshText();
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Path));
        if (iconChanged)
        {
            _smallRequested = _largeRequested = false;
            Icon = LargeIcon = null;
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
        else
        {
            if (_largeRequested) return;
            _largeRequested = true;
            LargeIcon = await _icons.GetAsync(Item, size);
        }
    }

    private void RefreshText()
    {
        var item = Item;
        SizeText = item.Kind switch
        {
            StorageItemKind.File when item.Size is { } size => Formatting.FormatSize(size),
            StorageItemKind.Drive when item.TotalSize is not null => $"{Formatting.FormatSize(item.FreeSpace ?? 0)} free",
            _ => string.Empty,
        };
        ModifiedText = item.Modified is { } modified ? modified.LocalDateTime.ToString("g") : string.Empty;
    }
}
