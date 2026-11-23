using System.ComponentModel;
using AkioExplorer.App.Services;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Abstractions;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Browser-style zoom of the file list: Ctrl+wheel, touchpad / touch pinch, Ctrl +/-/0 (MainWindow), a bubble with
/// the percentage and Reset that hides itself, and the percentage in the status bar while it isn't 100 %.
/// The zoom lives in the pane's <see cref="ColumnLayout"/>; rows and tiles bind to its sizes.
/// </summary>
public sealed partial class FilePane
{
    private const int WheelNotch = 120;
    private static readonly TimeSpan ZoomBubbleTime = TimeSpan.FromSeconds(2);

    private DispatcherQueueTimer? _zoomBubbleTimer;
    private bool _pointerOverZoomBubble;
    private int _wheelZoomAccumulated;
    private long _wheelZoomAt;

    public void ZoomBy(int direction)
    {
        ViewModel?.Columns.Step(direction);
        ShowZoomBubble(); // also at the end of the range, so the limit is visible
    }

    public void ResetZoom()
    {
        if (ViewModel is null) return;
        ViewModel.Columns.Zoom = ColumnLayout.DefaultZoom;
        ShowZoomBubble();
    }

    private void AttachZoom(FilePaneViewModel? old, FilePaneViewModel? vm)
    {
        if (old is not null) old.Columns.PropertyChanged -= OnLayoutChanged;
        if (vm is not null) vm.Columns.PropertyChanged += OnLayoutChanged;
        UpdateZoomTexts();
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ColumnLayout.Zoom)) return;
        UpdateZoomTexts();
        ApplyTileSlotSize();
        RefreshRealizedVisuals();
    }

    private void UpdateZoomTexts()
    {
        var layout = ViewModel?.Columns;
        if (layout is null) return;
        ZoomBubbleText.Text = layout.ZoomText;
        ZoomResetButton.IsEnabled = !layout.IsDefaultZoom;
        ZoomStatusText.Text = layout.ZoomText;
        ZoomStatusButton.Visibility = layout.IsDefaultZoom ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ lists

    /// <summary>Both lists: catch Ctrl+wheel before the ScrollViewer scrolls, and let it report pinches.</summary>
    private void OnListLoaded(object sender, RoutedEventArgs e) => HookListZoom((ListViewBase)sender, attempts: 5);

    private PointerEventHandler? _listWheelHandler;

    private void HookListZoom(ListViewBase list, int attempts)
    {
        if (list == TilesGrid) ApplyTileSlotSize();
        list.ApplyTemplate();
        // A list created by x:Load can raise Loaded before its template is in the tree: try again shortly.
        if (FindDescendant<ScrollViewer>(list) is not { } scroller
            || FindDescendant<ScrollContentPresenter>(scroller) is not { } presenter)
        {
            if (attempts > 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => HookListZoom(list, attempts - 1));
            return;
        }

        // Pinch (precision touchpad, touch screen) arrives as ScrollViewer zoom. The scroller zooms the picture
        // while the fingers move; when they lift, the factor becomes the nearest zoom step and is reset to 1.
        scroller.ZoomMode = ZoomMode.Enabled;
        scroller.MinZoomFactor = 0.1f;
        scroller.MaxZoomFactor = 6f;
        scroller.ViewChanged -= OnScrollerViewChanged;
        scroller.ViewChanged += OnScrollerViewChanged;

        // The content presenter fills the viewport and sees the wheel before its ScrollViewer does.
        _listWheelHandler ??= OnListWheel;
        presenter.RemoveHandler(PointerWheelChangedEvent, _listWheelHandler);
        presenter.AddHandler(PointerWheelChangedEvent, _listWheelHandler, handledEventsToo: true);
    }

    private void OnListWheel(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0)
        {
            _wheelZoomAccumulated = 0;
            return;
        }
        e.Handled = true;
        _wheelZoomAt = Environment.TickCount64;
        // With zoom enabled for pinches the scroller also animates its own zoom on Ctrl+wheel (Handled doesn't stop
        // it): cancel that right after, so only our zoom steps happen.
        if (OwningScroller(sender as DependencyObject) is { } scroller)
            DispatcherQueue.TryEnqueue(() => scroller.ChangeView(null, null, 1f, disableAnimation: true));
        // Smooth wheels and touchpads send small deltas: one step per full notch.
        _wheelZoomAccumulated += e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        while (Math.Abs(_wheelZoomAccumulated) >= WheelNotch)
        {
            int direction = Math.Sign(_wheelZoomAccumulated);
            _wheelZoomAccumulated -= direction * WheelNotch;
            ZoomBy(direction);
        }
    }

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || sender is not ScrollViewer scroller) return;
        float factor = scroller.ZoomFactor;
        if (Math.Abs(factor - 1f) < 0.01f) return;
        scroller.ChangeView(null, null, 1f, disableAnimation: true);

        // Ctrl+wheel was already turned into steps; only a pinch (no Ctrl) changes the zoom here.
        if (ViewModel is null || IsControlDown() || Environment.TickCount64 - _wheelZoomAt < 1500) return;
        var layout = ViewModel.Columns;
        int before = layout.Zoom;
        layout.Zoom = (int)Math.Round(before * factor);
        if (layout.Zoom == before) layout.Step(factor > 1 ? 1 : -1); // a small pinch still moves one step
        ShowZoomBubble();
    }

    private static ScrollViewer? OwningScroller(DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller) return scroller;
        }
        return null;
    }

    private static bool IsControlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>GridView's wrap panel sizes every slot like its first item: tell it the new tile size.</summary>
    private void ApplyTileSlotSize()
    {
        if (TilesGrid?.ItemsPanelRoot is not ItemsWrapGrid wrap || ViewModel is null) return;
        var layout = ViewModel.Columns;
        wrap.ItemWidth = layout.TileWidth + 4; // + the container's 2 px margin each side
        wrap.ItemHeight = layout.TileHeight + 4;
    }

    /// <summary>Tiles already on screen ask for the icon / thumbnail size the new zoom needs.</summary>
    private void RefreshRealizedVisuals()
    {
        if (ActiveList?.ItemsPanelRoot is not Panel panel) return;
        bool grid = ActiveList is GridView;
        foreach (var child in panel.Children)
        {
            if (child is SelectorItem { Content: FileItemViewModel vm }) RequestVisuals(vm, grid);
        }
    }

    private void RequestVisuals(FileItemViewModel vm, bool grid)
    {
        if (!grid)
        {
            vm.EnsureIcon(IconSize.Small);
            return;
        }
        var layout = ViewModel?.Columns;
        vm.EnsureIcon(IconSize.Large);
        if (layout?.UsesJumboIcons == true) vm.EnsureIcon(IconSize.Jumbo);
        if (vm.IsMedia) vm.EnsureIcon(IconSize.Small); // the player's icon for the media badge
        _thumbnails.Request(vm, ViewModel?.Provider, ThumbnailService.PixelSizeFor(layout?.TilePictureHeight ?? 64));
    }

    // ------------------------------------------------------------------ bubble

    private void ShowZoomBubble()
    {
        UpdateZoomTexts();
        ZoomBubble.Opacity = 1;
        ZoomBubble.IsHitTestVisible = true;
        _zoomBubbleTimer ??= CreateZoomBubbleTimer();
        _zoomBubbleTimer.Stop();
        if (!_pointerOverZoomBubble) _zoomBubbleTimer.Start();
    }

    private DispatcherQueueTimer CreateZoomBubbleTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = ZoomBubbleTime;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideZoomBubble();
        return timer;
    }

    private void HideZoomBubble()
    {
        _zoomBubbleTimer?.Stop();
        ZoomBubble.Opacity = 0;
        ZoomBubble.IsHitTestVisible = false;
    }

    private void OnZoomBubblePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverZoomBubble = true;
        _zoomBubbleTimer?.Stop();
    }

    private void OnZoomBubblePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverZoomBubble = false;
        if (ZoomBubble.IsHitTestVisible) _zoomBubbleTimer?.Start();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomBy(1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomBy(-1);

    private void OnZoomResetClick(object sender, RoutedEventArgs e) => ResetZoom();

    private void OnZoomStatusClick(object sender, RoutedEventArgs e) => ShowZoomBubble();
}
