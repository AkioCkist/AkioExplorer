using AkioExplorer.Core.Transfers.Segmented;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AkioExplorer.App.Controls;

/// <summary>
/// IDM-style segment bar: the file as a horizontal track with the downloaded part of every segment filled in.
/// Parts a connection is working on right now use the accent colour with a marker at the write position; finished
/// or waiting parts a dimmer fill – you can watch the connections split the file.
/// </summary>
public sealed partial class SegmentBar : Grid
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentSnapshot>), typeof(SegmentBar),
        new PropertyMetadata(null, (d, _) => ((SegmentBar)d).Redraw()));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long), typeof(SegmentBar),
        new PropertyMetadata(0L, (d, _) => ((SegmentBar)d).Redraw()));

    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { RadiusX = 2, RadiusY = 2 };

    public SegmentBar()
    {
        Height = 6;
        CornerRadius = new CornerRadius(2);
        Children.Add(_track);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public IReadOnlyList<SegmentSnapshot>? Segments
    {
        get => (IReadOnlyList<SegmentSnapshot>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long Total
    {
        get => (long)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var segments = Segments;
        long total = Total;
        _track.Fill = Brush("ControlStrongFillColorDisabledBrush");
        if (segments is not { Count: > 0 } || total <= 0 || ActualWidth <= 0) return;

        var active = Brush("AccentFillColorDefaultBrush");
        var idle = Brush("AccentFillColorTertiaryBrush");
        var marker = Brush("TextFillColorPrimaryBrush");
        double width = ActualWidth;
        double height = Height;

        foreach (var s in segments)
        {
            long done = s.Position - s.Start;
            if (done <= 0 && !s.Active) continue;
            double x = s.Start * width / total;
            double w = Math.Max(1, done * width / total);
            var rect = new Rectangle { Width = w, Height = height, Fill = s.Active ? active : idle };
            Canvas.SetLeft(rect, x);
            _canvas.Children.Add(rect);

            if (s.Active)
            {
                var head = new Rectangle { Width = 2, Height = height, Fill = marker };
                Canvas.SetLeft(head, Math.Min(width - 2, x + w));
                _canvas.Children.Add(head);
            }
        }
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
