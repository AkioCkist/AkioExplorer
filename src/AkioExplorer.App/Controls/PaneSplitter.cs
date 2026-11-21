using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Vertical drag handle that resizes the panel on its right: resize cursor, 8 px hit area around a 1 px line,
/// drag with the mouse, Left/Right arrows when focused (Shift = bigger steps), double-click resets.
/// </summary>
public sealed partial class PaneSplitter : Grid
{
    private readonly Rectangle _line;
    private double _startX;
    private double _startWidth;
    private bool _dragging;

    public PaneSplitter()
    {
        Width = 8;
        Background = new SolidColorBrush(Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        AutomationProperties.SetName(this, "Resize preview pane");
        ToolTipService.SetToolTip(this, "Drag to resize · double-click to reset");

        _line = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
        _line.Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        Children.Add(_line);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => { if (_dragging) { _dragging = false; Committed?.Invoke(this, EventArgs.Empty); } };
        PointerEntered += (_, _) => _line.Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        PointerExited += (_, _) => { if (!_dragging) _line.Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]; };
        DoubleTapped += (_, _) => { TargetWidth = DefaultWidth; Committed?.Invoke(this, EventArgs.Empty); };
        KeyDown += OnKeyDown;
    }

    public double DefaultWidth { get; set; } = 360;

    public double MinTargetWidth { get; set; } = 220;

    /// <summary>Largest width allowed right now (the file list keeps the rest).</summary>
    public Func<double> MaxTargetWidth { get; set; } = () => double.PositiveInfinity;

    /// <summary>Gets/sets the resized panel's width; set clamps to the limits.</summary>
    public Func<double> GetWidth { get; set; } = () => 0;

    public Action<double> SetWidth { get; set; } = _ => { };

    /// <summary>Raised when a drag or key resize ends (save the width).</summary>
    public event EventHandler? Committed;

    private double TargetWidth
    {
        get => GetWidth();
        set => SetWidth(Math.Clamp(value, MinTargetWidth, Math.Max(MinTargetWidth, MaxTargetWidth())));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = TargetWidth;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        // The panel is to the right: dragging left makes it wider.
        TargetWidth = _startWidth - (e.GetCurrentPoint(null).Position.X - _startX);
        e.Handled = true;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        _line.Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        double step = shift ? 64 : 16;
        switch (e.Key)
        {
            case VirtualKey.Left: TargetWidth += step; break;
            case VirtualKey.Right: TargetWidth -= step; break;
            default: return;
        }
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
