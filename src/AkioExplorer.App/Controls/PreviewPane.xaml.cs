using System.ComponentModel;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace AkioExplorer.App.Controls;

public sealed record PreviewDetail(string Label, string Value);

/// <summary>
/// Preview of the active pane's selection (Alt+P). Basic details show instantly; content and file properties load
/// after a short pause so arrowing through a folder stays fast, and every load is cancelled by the next selection.
/// Local files are read in place; cloud files are fetched into memory only when small enough.
/// </summary>
public sealed partial class PreviewPane : UserControl
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    /// <summary>Windows properties shown for local files when present (canonical property names).</summary>
    private static readonly string[] PropertyKeys =
    [
        "System.Media.Duration", "System.Title", "System.Music.Artist", "System.Music.AlbumArtist", "System.Music.AlbumTitle",
        "System.Music.TrackNumber", "System.Music.Genre", "System.Media.Year", "System.Audio.EncodingBitrate",
        "System.Audio.SampleRate", "System.Audio.ChannelCount", "System.Video.FrameWidth", "System.Video.FrameHeight",
        "System.Video.FrameRate", "System.Image.HorizontalSize", "System.Image.VerticalSize", "System.Photo.CameraManufacturer",
        "System.Photo.CameraModel", "System.Photo.DateTaken", "System.Document.PageCount", "System.Document.WordCount",
        "System.Author", "System.Company", "System.Comment", "System.FileDescription", "System.FileVersion",
        "System.Link.TargetParsingPath",
    ];

    private CancellationTokenSource? _cts;
    private MediaPlayerElement? _media;
    private FileItemViewModel? _headerItem;
    private string? _shownKey;
    private readonly List<PreviewDetail> _details = new();

    public PreviewPane()
    {
        InitializeComponent();
        ShowEmpty();
    }

    /// <summary>Stops playback and forgets the current item (pane hidden).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _shownKey = null;
        StopMedia();
        ShowEmpty();
    }

    /// <summary>Shows the selection of <paramref name="pane"/>. Re-selecting the same unchanged file does nothing.</summary>
    public async void Show(FilePaneViewModel? pane, bool force = false)
    {
        var selected = pane?.SelectedItems ?? [];
        var provider = pane?.Provider;
        string key = selected.Count == 1 && provider is not null
            ? $"{provider.Id}|{selected[0].Path}|{selected[0].Item.Modified?.UtcTicks}|{selected[0].Item.Size}"
            : $"multi|{selected.Count}|{string.Join('|', selected.Take(3).Select(s => s.Path))}";
        if (!force && key == _shownKey) return;
        _shownKey = key;

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        StopMedia();
        ResetContent();

        if (pane is null || provider is null || selected.Count == 0)
        {
            ShowEmpty();
            return;
        }
        if (selected.Count > 1)
        {
            ShowMultiple(selected);
            return;
        }

        var vm = selected[0];
        var item = vm.Item;
        bool isLocal = (provider.Capabilities & StorageCapabilities.OpenWithShell) != 0;
        SetHeader(vm, pane.GetTypeName(item));
        SetBasicDetails(item, pane.GetTypeName(item), isLocal);

        var kind = PreviewClassifier.Classify(item, isLocal);
        bool sniff = isLocal && kind == PreviewKind.ShellThumbnail && PreviewClassifier.ShouldSniffText(item);
        // Cloud files that can't be rendered here (Word, video, Google Docs…): the service's own large rendition.
        bool cloudRendition = !isLocal && kind == PreviewKind.None && provider is IThumbnailProvider tp && tp.MayHaveThumbnail(item);
        if (item.IsContainer)
        {
            SetContentVisible(false);
        }
        else if (kind == PreviewKind.None && !sniff && !cloudRendition)
        {
            ShowMessage("", isLocal ? "No preview for this file type" : "No preview for this file type in the cloud.\nDouble-click to download and open it.");
        }
        else
        {
            Loading.IsActive = true;
        }

        try
        {
            await Task.Delay(Debounce, cts.Token);
            if (item.IsContainer)
            {
                if (isLocal) await AddFolderDetailsAsync(item.Path, cts.Token);
                return;
            }

            var properties = isLocal ? LoadPropertiesAsync(item.Path, cts.Token) : Task.FromResult<IReadOnlyList<PreviewDetail>>([]);
            if (sniff && await TryShowTextAsync(item, provider, isLocal, cts.Token, requireText: true)) kind = PreviewKind.Text;
            else if (cloudRendition) await ShowCloudRenditionAsync((IThumbnailProvider)provider, item, cts.Token);
            else await ShowContentAsync(kind, item, provider, isLocal, cts.Token);

            AddDetails(await properties);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ResetContent();
            ShowMessage("", ex is TooLargeException ? "Too large to preview from the cloud.\nDouble-click to download and open it." : $"Couldn't preview this file.\n{ex.Message}");
        }
        finally
        {
            if (!cts.IsCancellationRequested) Loading.IsActive = false;
        }
    }

    // ------------------------------------------------------------------ content

    private async Task ShowContentAsync(PreviewKind kind, StorageItem item, IStorageProvider provider, bool isLocal, CancellationToken ct)
    {
        switch (kind)
        {
            case PreviewKind.Image:
            {
                using var stream = await OpenRandomAccessAsync(item, provider, isLocal, PreviewClassifier.CloudImageLimit, ct);
                var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = DecodeWidth() };
                await bitmap.SetSourceAsync(stream);
                ct.ThrowIfCancellationRequested();
                ImageView.Source = bitmap;
                ImageView.Visibility = Visibility.Visible;
                break;
            }
            case PreviewKind.Svg:
            {
                using var stream = await OpenRandomAccessAsync(item, provider, isLocal, PreviewClassifier.CloudImageLimit, ct);
                var svg = new SvgImageSource();
                await svg.SetSourceAsync(stream);
                ct.ThrowIfCancellationRequested();
                ImageView.Source = svg;
                ImageView.Visibility = Visibility.Visible;
                break;
            }
            case PreviewKind.Text:
                await TryShowTextAsync(item, provider, isLocal, ct, requireText: false);
                break;
            case PreviewKind.Pdf:
                await ShowPdfAsync(item, provider, isLocal, ct);
                break;
            case PreviewKind.Media:
                await ShowMediaAsync(item.Path, ct);
                break;
            case PreviewKind.ShellThumbnail:
                await ShowThumbnailAsync(item.Path, ct);
                break;
        }
    }

    private async Task<bool> TryShowTextAsync(StorageItem item, IStorageProvider provider, bool isLocal, CancellationToken ct, bool requireText)
    {
        byte[] bytes;
        await using (var stream = await OpenStreamAsync(item, provider, isLocal, ct))
            bytes = await PreviewClassifier.ReadPrefixAsync(stream, PreviewClassifier.TextPrefixBytes + 1, ct);
        ct.ThrowIfCancellationRequested();

        bool truncated = bytes.Length > PreviewClassifier.TextPrefixBytes;
        var span = bytes.AsSpan(0, Math.Min(bytes.Length, PreviewClassifier.TextPrefixBytes));
        if (!PreviewClassifier.TryDecodeText(span, out string text))
        {
            if (requireText) return false;
            ShowMessage("", "This file isn't text");
            return true;
        }

        TextView.Text = text.Length == 0 ? "(empty file)" : text;
        TextScroller.Visibility = Visibility.Visible;
        TextScroller.ChangeView(0, 0, null, true);
        if (truncated) ShowNote($"First {PreviewClassifier.TextPrefixBytes / 1024} KB");
        return true;
    }

    private async Task ShowPdfAsync(StorageItem item, IStorageProvider provider, bool isLocal, CancellationToken ct)
    {
        const int maxPages = 5;
        using var stream = await OpenRandomAccessAsync(item, provider, isLocal, PreviewClassifier.CloudPdfLimit, ct);
        PdfDocument document;
        try
        {
            document = await PdfDocument.LoadFromStreamAsync(stream);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) // wrong password
        {
            ShowMessage("", "This PDF is password protected");
            return;
        }
        ct.ThrowIfCancellationRequested();

        uint width = (uint)(DecodeWidth() * (XamlRoot?.RasterizationScale ?? 1));
        PdfScroller.Visibility = Visibility.Visible;
        PdfScroller.ChangeView(0, 0, null, true);
        for (uint i = 0; i < Math.Min(document.PageCount, maxPages); i++)
        {
            using var page = document.GetPage(i);
            using var rendered = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = width });
            ct.ThrowIfCancellationRequested();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(rendered);
            ct.ThrowIfCancellationRequested();
            PdfPages.Children.Add(new Image { Source = bitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
            if (i == 0) Loading.IsActive = false; // first page is up: the rest stream in below it
        }

        AddDetails([new PreviewDetail("Pages", document.PageCount.ToString("N0"))]);
        if (document.PageCount > maxPages) ShowNote($"First {maxPages} of {document.PageCount} pages");
    }

    /// <summary>
    /// Music: the cover on top (whole, never under the controls) and the transport bar below it.
    /// Video: the player sized to the video's own shape inside the pane, so there are no black bars and the controls
    /// sit on the picture, whatever the pane's width or height.
    /// </summary>
    private async Task ShowMediaAsync(string path, CancellationToken ct)
    {
        EnsureMediaElements();
        bool audio = FileCategories.Of(Path.GetFileName(path), isFolder: false) == FileCategory.Audio;
        _audio = audio;
        _videoAspect = 16.0 / 9;

        var file = await StorageFile.GetFileFromPathAsync(path);
        ct.ThrowIfCancellationRequested();
        var picture = await ThumbnailAsync(file, ThumbnailMode.SingleItem, ct);
        ct.ThrowIfCancellationRequested();

        if (audio)
        {
            // A cover only when the file has one; otherwise a note glyph instead of the shell's generic icon.
            bool hasCover = picture is not null && await HasPictureAsync(file);
            _art!.Source = hasCover ? picture : null;
            _art.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
            _artGlyph!.Visibility = hasCover ? Visibility.Collapsed : Visibility.Visible;
            _media!.PosterSource = null;
        }
        else
        {
            _art!.Visibility = Visibility.Collapsed;
            _artGlyph!.Visibility = Visibility.Collapsed;
            _media!.PosterSource = picture;
            if (picture is { PixelWidth: > 0, PixelHeight: > 0 }) _videoAspect = (double)picture.PixelWidth / picture.PixelHeight;
        }
        _media.Source = MediaSource.CreateFromStorageFile(file);
        MediaHost.Visibility = Visibility.Visible;
        LayoutMedia();
    }

    private bool _audio;
    private double _videoAspect = 16.0 / 9;
    private Image? _art;
    private FontIcon? _artGlyph;

    /// <summary>Bar height of the compact transport controls (play, seek, time, volume).</summary>
    private const double ControlsHeight = 52;

    private void EnsureMediaElements()
    {
        if (_media is not null) return;
        MediaHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        MediaHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _art = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Margin = new Thickness(8, 8, 8, 0) };
        _artGlyph = new FontIcon { Glyph = "", FontSize = 56, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
        MediaHost.Children.Add(_art);
        MediaHost.Children.Add(_artGlyph);

        _media = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            AutoPlay = false,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _media.TransportControls.IsCompact = true;
        _media.TransportControls.IsZoomButtonVisible = false;
        MediaHost.Children.Add(_media);
        MediaHost.SizeChanged += (_, _) => LayoutMedia();
        _media.MediaPlayer.MediaOpened += (player, _) =>
        {
            // The real frame size (the thumbnail can be letterboxed by the shell).
            uint w = player.PlaybackSession.NaturalVideoWidth, h = player.PlaybackSession.NaturalVideoHeight;
            if (w > 0 && h > 0)
                DispatcherQueue.TryEnqueue(() =>
                {
                    _videoAspect = (double)w / h;
                    LayoutMedia();
                });
        };
    }

    private void LayoutMedia()
    {
        if (_media is null) return;
        double width = MediaHost.ActualWidth, height = MediaHost.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (_audio)
        {
            Grid.SetRow(_media, 1);
            Grid.SetRowSpan(_media, 1);
            _media.Width = double.NaN;
            _media.Height = ControlsHeight;
            _media.HorizontalAlignment = HorizontalAlignment.Stretch;
            _media.VerticalAlignment = VerticalAlignment.Bottom;
            return;
        }

        // Video: the largest box of the video's shape that fits; never narrower than the controls need.
        Grid.SetRow(_media, 0);
        Grid.SetRowSpan(_media, 2);
        double w = Math.Min(width, height * _videoAspect);
        double h = w / _videoAspect;
        if (w < Math.Min(width, 260))
        {
            w = Math.Min(width, 260); // very tall video in a narrow pane: keep the controls usable
            h = Math.Min(height, w / _videoAspect);
        }
        _media.Width = w;
        _media.Height = Math.Max(h, Math.Min(height, ControlsHeight * 2));
        _media.HorizontalAlignment = HorizontalAlignment.Center;
        _media.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>True when the shell's picture for a music file is real cover art (not the type icon).</summary>
    private static async Task<bool> HasPictureAsync(StorageFile file)
    {
        try
        {
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.ResizeThumbnail);
            return thumbnail is { Type: ThumbnailType.Image };
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ShowCloudRenditionAsync(IThumbnailProvider provider, StorageItem item, CancellationToken ct)
    {
        int pixels = (int)Math.Clamp(DecodeWidth() * (XamlRoot?.RasterizationScale ?? 1), 320, 1600);
        await using var stream = await provider.OpenThumbnailAsync(item, pixels, ct);
        ct.ThrowIfCancellationRequested();
        if (stream is null)
        {
            ShowMessage("", "No preview for this file type in the cloud.\nDouble-click to download and open it.");
            return;
        }
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        ct.ThrowIfCancellationRequested();
        ImageView.Source = bitmap;
        ImageView.Visibility = Visibility.Visible;
        ShowNote("Preview from the cloud service");
    }

    private async Task ShowThumbnailAsync(string path, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        ct.ThrowIfCancellationRequested();
        var thumbnail = await ThumbnailAsync(file, ThumbnailMode.SingleItem, ct);
        ct.ThrowIfCancellationRequested();
        if (thumbnail is null)
        {
            ShowMessage("", "No preview for this file type");
            return;
        }
        ImageView.Source = thumbnail;
        ImageView.Visibility = Visibility.Visible;
    }

    private async Task<BitmapImage?> ThumbnailAsync(StorageFile file, ThumbnailMode mode, CancellationToken ct)
    {
        uint size = (uint)Math.Clamp(DecodeWidth() * (XamlRoot?.RasterizationScale ?? 1), 96, 1024);
        using var thumbnail = await file.GetThumbnailAsync(mode, size, ThumbnailOptions.ResizeThumbnail);
        ct.ThrowIfCancellationRequested();
        if (thumbnail is null || thumbnail.Size == 0) return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(thumbnail);
        return bitmap;
    }

    // ------------------------------------------------------------------ streams

    private static async Task<Stream> OpenStreamAsync(StorageItem item, IStorageProvider provider, bool isLocal, CancellationToken ct)
    {
        if (isLocal)
            return new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        if (provider is IFileContentProvider content)
            return await content.OpenReadAsync(item, ct);
        throw new NotSupportedException("This location can't be previewed.");
    }

    private static async Task<IRandomAccessStream> OpenRandomAccessAsync(StorageItem item, IStorageProvider provider, bool isLocal, long cloudLimit, CancellationToken ct)
    {
        var stream = await OpenStreamAsync(item, provider, isLocal, ct);
        if (isLocal) return stream.AsRandomAccessStream();

        await using (stream)
        {
            var memory = new MemoryStream(item.Size is > 0 and < int.MaxValue ? (int)item.Size.Value : 0);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + read > cloudLimit) throw new TooLargeException();
                memory.Write(buffer, 0, read);
            }
            memory.Position = 0;
            return memory.AsRandomAccessStream();
        }
    }

    private sealed class TooLargeException : Exception;

    // ------------------------------------------------------------------ details

    private void SetBasicDetails(StorageItem item, string typeName, bool isLocal)
    {
        _details.Clear();
        _details.Add(new("Name", item.Name));
        _details.Add(new("Type", typeName));
        if (isLocal && Path.GetDirectoryName(item.Path) is { Length: > 0 } folder) _details.Add(new("Location", folder));
        if (item.Created is { } created) _details.Add(new("Created", created.LocalDateTime.ToString("g")));
        if (item.Modified is { } modified)
        {
            _details.Add(new("Modified", modified.LocalDateTime.ToString("g")));
            _details.Add(new("Age", DetailFormatting.Age(modified, DateTimeOffset.Now)));
        }
        if (item.Kind == StorageItemKind.File && item.Size is { } size)
        {
            _details.Add(new("Size", DetailFormatting.Bytes(size)));
            _details.Add(new("Size (MB)", DetailFormatting.Decimal(size)));
            _details.Add(new("Size (MiB)", DetailFormatting.Binary(size)));
        }
        if (isLocal)
        {
            try
            {
                _details.Add(new("Attributes", DetailFormatting.Attributes(File.GetAttributes(item.Path))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        RefreshDetails();
    }

    private void AddDetails(IReadOnlyList<PreviewDetail> extra)
    {
        if (extra.Count == 0) return;
        _details.AddRange(extra);
        RefreshDetails();
    }

    private void RefreshDetails() => DetailsList.ItemsSource = _details.ToList();

    private async Task AddFolderDetailsAsync(string path, CancellationToken ct)
    {
        var (files, folders, capped) = await Task.Run(() =>
        {
            int f = 0, d = 0;
            try
            {
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    if (ct.IsCancellationRequested || f + d >= 100_000) return (f, d, true);
                    if (entry is DirectoryInfo) d++; else f++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
            return (f, d, false);
        }, ct);
        ct.ThrowIfCancellationRequested();
        string more = capped ? "+" : string.Empty;
        AddDetails([new("Contains", $"{files:N0}{more} files, {folders:N0}{more} folders")]);
    }

    private static async Task<IReadOnlyList<PreviewDetail>> LoadPropertiesAsync(string path, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var values = await file.Properties.RetrievePropertiesAsync(PropertyKeys);
            ct.ThrowIfCancellationRequested();
            return FormatProperties(values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // properties are a bonus: missing handlers or odd files just show the basics
        }
    }

    private static List<PreviewDetail> FormatProperties(IDictionary<string, object?> values)
    {
        var result = new List<PreviewDetail>();
        object? Get(string key) => values.TryGetValue(key, out var v) ? v : null;
        void Add(string label, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) result.Add(new(label, text));
        }

        if (Get("System.Media.Duration") is ulong ticks && ticks > 0) Add("Length", DetailFormatting.Duration(TimeSpan.FromTicks((long)ticks)));
        if (Get("System.Video.FrameWidth") is uint vw && Get("System.Video.FrameHeight") is uint vh && vw > 0) Add("Resolution", $"{vw} × {vh}");
        if (Get("System.Video.FrameRate") is uint fps && fps > 0) Add("Frame rate", $"{fps / 1000.0:0.##} fps");
        if (Get("System.Image.HorizontalSize") is uint iw && Get("System.Image.VerticalSize") is uint ih && iw > 0) Add("Dimensions", $"{iw} × {ih}");
        Add("Title", Text(Get("System.Title")));
        Add("Artists", Text(Get("System.Music.Artist")));
        Add("Album artist", Text(Get("System.Music.AlbumArtist")));
        Add("Album", Text(Get("System.Music.AlbumTitle")));
        if (Get("System.Music.TrackNumber") is uint track && track > 0) Add("Track", track.ToString());
        Add("Genre", Text(Get("System.Music.Genre")));
        if (Get("System.Media.Year") is uint year && year > 0) Add("Year", year.ToString());
        if (Get("System.Audio.EncodingBitrate") is uint bitrate && bitrate > 0) Add("Bit rate", DetailFormatting.BitRate(bitrate));
        if (Get("System.Audio.SampleRate") is uint rate && rate > 0) Add("Sample rate", $"{rate / 1000.0:0.#} kHz");
        if (Get("System.Audio.ChannelCount") is uint channels && channels > 0) Add("Channels", channels switch { 1 => "Mono", 2 => "Stereo", _ => channels.ToString() });
        Add("Camera maker", Text(Get("System.Photo.CameraManufacturer")));
        Add("Camera", Text(Get("System.Photo.CameraModel")));
        if (Get("System.Photo.DateTaken") is DateTimeOffset taken) Add("Date taken", taken.LocalDateTime.ToString("g"));
        if (Get("System.Document.PageCount") is int pages && pages > 0) Add("Pages", pages.ToString("N0"));
        if (Get("System.Document.WordCount") is int words && words > 0) Add("Words", words.ToString("N0"));
        Add("Authors", Text(Get("System.Author")));
        Add("Company", Text(Get("System.Company")));
        Add("Description", Text(Get("System.FileDescription")));
        Add("File version", Text(Get("System.FileVersion")));
        Add("Comment", Text(Get("System.Comment")));
        Add("Target", Text(Get("System.Link.TargetParsingPath")));
        return result;
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        string[] list => string.Join("; ", list.Where(x => !string.IsNullOrWhiteSpace(x))),
        IEnumerable<string> list => string.Join("; ", list.Where(x => !string.IsNullOrWhiteSpace(x))),
        _ => value.ToString(),
    };

    // ------------------------------------------------------------------ states

    private void ShowEmpty()
    {
        SetHeader(null, string.Empty);
        HeaderName.Text = "Preview";
        HeaderType.Text = "Select a file to see its preview and details";
        _details.Clear();
        RefreshDetails();
        ResetContent();
        ShowMessage("", "Nothing selected");
    }

    private void ShowMultiple(IReadOnlyList<FileItemViewModel> selected)
    {
        SetHeader(null, string.Empty);
        HeaderName.Text = $"{selected.Count:N0} items selected";
        int files = selected.Count(s => !s.IsContainer);
        long bytes = selected.Where(s => !s.IsContainer).Sum(s => s.Item.Size ?? 0);
        HeaderType.Text = $"{files:N0} files, {selected.Count - files:N0} folders";
        _details.Clear();
        if (files > 0)
        {
            _details.Add(new("Files size", DetailFormatting.Bytes(bytes)));
            _details.Add(new("Size (MB)", DetailFormatting.Decimal(bytes)));
            _details.Add(new("Size (MiB)", DetailFormatting.Binary(bytes)));
        }
        if (selected.Select(s => s.Item.Modified).Where(m => m is not null).ToList() is { Count: > 0 } dates)
            _details.Add(new("Modified", $"{dates.Min()!.Value.LocalDateTime:g} – {dates.Max()!.Value.LocalDateTime:g}"));
        RefreshDetails();
        ShowMessage("", "Select a single file to preview it");
    }

    private void SetHeader(FileItemViewModel? item, string type)
    {
        if (_headerItem is not null) _headerItem.PropertyChanged -= OnHeaderItemChanged;
        _headerItem = item;
        HeaderIcon.Source = null;
        HeaderIcon.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;

        item.PropertyChanged += OnHeaderItemChanged;
        item.EnsureIcon(IconSize.Large);
        HeaderIcon.Source = item.LargeIcon ?? item.Icon;
        HeaderName.Text = item.Name;
        HeaderType.Text = item.IsContainer ? type : string.IsNullOrEmpty(item.SizeText) ? type : $"{type} · {item.SizeText}";
    }

    private void OnHeaderItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileItemViewModel.LargeIcon) or nameof(FileItemViewModel.Icon) && sender is FileItemViewModel item)
            HeaderIcon.Source = item.LargeIcon ?? item.Icon;
    }

    private void ResetContent()
    {
        SetContentVisible(true);
        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        TextView.Text = string.Empty;
        TextScroller.Visibility = Visibility.Collapsed;
        PdfPages.Children.Clear();
        PdfScroller.Visibility = Visibility.Collapsed;
        MediaHost.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Collapsed;
        ContentNote.Visibility = Visibility.Collapsed;
        Loading.IsActive = false;
    }

    /// <summary>Folders have no content: the details take the whole height.</summary>
    private void SetContentVisible(bool visible)
    {
        ContentHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ContentRow.Height = visible ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        DetailsRow.Height = visible ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        DetailsScroller.MaxHeight = visible ? 320 : double.PositiveInfinity;
    }

    private void ShowMessage(string glyph, string text)
    {
        Loading.IsActive = false;
        MessageGlyph.Glyph = glyph;
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void ShowNote(string text)
    {
        ContentNote.Text = text;
        ContentNote.Visibility = Visibility.Visible;
    }

    private void StopMedia()
    {
        if (_media is null) return;
        _media.MediaPlayer?.Pause();
        _media.Source = null;
        _media.PosterSource = null;
    }

    private int DecodeWidth() => (int)Math.Max(ContentHost.ActualWidth > 0 ? ContentHost.ActualWidth : ActualWidth, 240);
}
