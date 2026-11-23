using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Music and video preview with a small player bar of our own. The system transport controls have a fixed minimum
/// width and got clipped in a narrow preview pane. This one is a single row (play · elapsed · seek · length · volume)
/// that drops the volume, then the times, as the pane narrows. It sits under the picture, never on it.
/// </summary>
public sealed partial class PreviewPane
{
    private const double BarHeight = 40;
    private const double VolumeMinWidth = 200; // narrower: the volume button goes first
    private const double TimesMinWidth = 150;  // then the two times

    private MediaPlayerElement? _media;
    private MediaPlayer? _player;
    private Image? _art;
    private FontIcon? _artGlyph;
    private Button? _playButton;
    private FontIcon? _playGlyph;
    private Slider? _seek;
    private TextBlock? _elapsed;
    private TextBlock? _length;
    private Button? _volumeButton;
    private FontIcon? _volumeGlyph;
    private Slider? _volume;
    private DispatcherQueueTimer? _positionTimer;
    private bool _settingSeek;
    private bool _audio;
    private double _videoAspect = 16.0 / 9;

    /// <summary>
    /// Music: the cover on top (whole) and the bar below it.
    /// Video: the picture sized to the video's own shape above the bar, so there are no black bars.
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
            _media.Visibility = Visibility.Collapsed;
        }
        else
        {
            _art!.Visibility = Visibility.Collapsed;
            _artGlyph!.Visibility = Visibility.Collapsed;
            _media!.PosterSource = picture;
            _media.Visibility = Visibility.Visible;
            if (picture is { PixelWidth: > 0, PixelHeight: > 0 }) _videoAspect = (double)picture.PixelWidth / picture.PixelHeight;
        }

        ResetBar();
        _media.Source = MediaSource.CreateFromStorageFile(file);
        MediaHost.Visibility = Visibility.Visible;
        LayoutMedia();
    }

    private void EnsureMediaElements()
    {
        if (_media is not null) return;

        // Built in locals and published at the end: a failure half-way must not leave a broken player behind.
        var art = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Margin = new Thickness(8, 8, 8, 0) };
        var artGlyph = new FontIcon
        {
            Glyph = "",
            FontSize = 56,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };

        // MediaPlayerElement creates its MediaPlayer lazily (null until it has a source), so give it one up front.
        var player = new MediaPlayer { AutoPlay = false };
        player.MediaOpened += (sender, _) => DispatcherQueue.TryEnqueue(() =>
        {
            // The real frame size (the thumbnail can be letterboxed by the shell) and the length for the seek bar.
            uint w = sender.PlaybackSession.NaturalVideoWidth, h = sender.PlaybackSession.NaturalVideoHeight;
            if (w > 0 && h > 0) _videoAspect = (double)w / h;
            UpdateBar();
            LayoutMedia();
        });
        player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateBar);
        player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(UpdateBar);

        var media = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            AutoPlay = false,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        media.SetMediaPlayer(player);
        media.Tapped += (_, _) => TogglePlay(); // click the picture to play / pause

        var bar = BuildBar();

        MediaHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        MediaHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        MediaHost.Children.Add(art);
        MediaHost.Children.Add(artGlyph);
        MediaHost.Children.Add(media);
        Grid.SetRow(bar, 1);
        MediaHost.Children.Add(bar);
        MediaHost.SizeChanged += (_, _) => LayoutMedia();

        _art = art;
        _artGlyph = artGlyph;
        _player = player;
        _media = media;
    }

    private Grid BuildBar()
    {
        // One row: play · elapsed · seek · length · volume. Plain icon buttons, like the rest of the app.
        var bar = new Grid { Height = BarHeight, ColumnSpacing = 6, Padding = new Thickness(2, 0, 2, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconButton = (Style)Application.Current.Resources["ToolIconButtonStyle"];
        _playGlyph = new FontIcon { Glyph = "", FontSize = 14 };
        _playButton = new Button { Style = iconButton, Content = _playGlyph, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(_playButton, "Play");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_playButton, "Play");
        _playButton.Click += (_, _) => TogglePlay();

        _elapsed = TimeText(HorizontalAlignment.Left);
        Grid.SetColumn(_elapsed, 1);

        _seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.1,
            VerticalAlignment = VerticalAlignment.Center,
            IsThumbToolTipEnabled = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_seek, "Seek");
        _seek.ValueChanged += (_, e) =>
        {
            if (_settingSeek || _player is null) return;
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
            UpdateTime();
        };
        Grid.SetColumn(_seek, 2);

        _length = TimeText(HorizontalAlignment.Right);
        Grid.SetColumn(_length, 3);

        _volume = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 160, Margin = new Thickness(4, 0, 4, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_volume, "Volume");
        _volume.ValueChanged += (_, e) =>
        {
            if (_player is null) return;
            _player.Volume = e.NewValue / 100;
            _player.IsMuted = e.NewValue <= 0;
            _volumeGlyph!.Glyph = e.NewValue <= 0 ? "" : "";
        };
        _volumeGlyph = new FontIcon { Glyph = "", FontSize = 14 };
        _volumeButton = new Button
        {
            Style = iconButton,
            Content = _volumeGlyph,
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = new Flyout { Content = _volume, Placement = FlyoutPlacementMode.Top },
        };
        ToolTipService.SetToolTip(_volumeButton, "Volume");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_volumeButton, "Volume");
        Grid.SetColumn(_volumeButton, 4);

        bar.Children.Add(_playButton);
        bar.Children.Add(_elapsed);
        bar.Children.Add(_seek);
        bar.Children.Add(_length);
        bar.Children.Add(_volumeButton);
        bar.SizeChanged += (_, e) => ApplyBarWidth(e.NewSize.Width);
        return bar;
    }

    private static TextBlock TimeText(HorizontalAlignment alignment) => new()
    {
        Text = "0:00",
        FontSize = 11,
        TextAlignment = alignment == HorizontalAlignment.Left ? TextAlignment.Left : TextAlignment.Right,
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>Narrow pane: play and seek always stay; the volume button goes first, then the times.</summary>
    private void ApplyBarWidth(double width)
    {
        if (_volumeButton is null || _elapsed is null || _length is null) return;
        _volumeButton.Visibility = width >= VolumeMinWidth ? Visibility.Visible : Visibility.Collapsed;
        var times = width >= TimesMinWidth ? Visibility.Visible : Visibility.Collapsed;
        _elapsed.Visibility = _length.Visibility = times;
    }

    private void TogglePlay()
    {
        if (_player is null) return;
        var session = _player.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            // Finished: start again from the top, like any player.
            if (session.NaturalDuration > TimeSpan.Zero && session.Position >= session.NaturalDuration - TimeSpan.FromMilliseconds(250))
                session.Position = TimeSpan.Zero;
            _player.Play();
        }
    }

    private void ResetBar()
    {
        _positionTimer?.Stop();
        if (_seek is null) return;
        _settingSeek = true;
        _seek.Maximum = 1;
        _seek.Value = 0;
        _settingSeek = false;
        _playGlyph!.Glyph = "";
        _elapsed!.Text = _length!.Text = "0:00";
    }

    /// <summary>Play/pause icon, seek range and a position timer that only runs while playing.</summary>
    private void UpdateBar()
    {
        if (_player is null || _seek is null) return;
        var session = _player.PlaybackSession;
        bool playing = session.PlaybackState == MediaPlaybackState.Playing;
        _playGlyph!.Glyph = playing ? "" : "";
        string label = playing ? "Pause" : "Play";
        ToolTipService.SetToolTip(_playButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_playButton, label);

        double length = session.NaturalDuration.TotalSeconds;
        if (length > 0 && Math.Abs(_seek.Maximum - length) > 0.01)
        {
            _settingSeek = true;
            _seek.Maximum = length;
            _settingSeek = false;
        }
        UpdatePosition();

        _positionTimer ??= CreatePositionTimer();
        if (playing) _positionTimer.Start();
        else _positionTimer.Stop();
    }

    private DispatcherQueueTimer CreatePositionTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => UpdatePosition();
        return timer;
    }

    private void UpdatePosition()
    {
        if (_player is null || _seek is null) return;
        _settingSeek = true;
        _seek.Value = Math.Min(_player.PlaybackSession.Position.TotalSeconds, _seek.Maximum);
        _settingSeek = false;
        UpdateTime();
    }

    private void UpdateTime()
    {
        if (_player is null || _elapsed is null || _length is null) return;
        var session = _player.PlaybackSession;
        _elapsed.Text = FormatTime(session.Position);
        _length.Text = FormatTime(session.NaturalDuration);
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void LayoutMedia()
    {
        if (_media is null) return;
        double width = MediaHost.ActualWidth, height = MediaHost.ActualHeight - BarHeight;
        if (width <= 0 || height <= 0) return;
        if (_audio) return; // cover and bar size themselves in their rows

        // Video: the largest box of the video's shape that fits above the bar.
        double w = Math.Min(width, height * _videoAspect);
        _media.Width = w;
        _media.Height = w / _videoAspect;
    }

    private void StopMedia()
    {
        _positionTimer?.Stop();
        if (_media is null) return;
        _player?.Pause();
        _media.Source = null;
        _media.PosterSource = null;
        ResetBar();
    }
}
