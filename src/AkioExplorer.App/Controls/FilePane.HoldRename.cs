using AkioExplorer.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace AkioExplorer.App.Controls;

/// <summary>
/// Press and hold the left mouse button on an item for a second without moving: rename starts.
/// Moving (a drag), releasing early, a modifier key or another button cancels it. Mouse only: touch
/// press-and-hold stays the Windows gesture for the context menu.
/// </summary>
public sealed partial class FilePane
{
    private static readonly TimeSpan HoldToRenameDelay = TimeSpan.FromSeconds(0.27);
    private const double HoldSlop = 4; // px the pointer may wobble

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _holdTimer;
    private FileItemViewModel? _holdItem;
    private uint _holdPointerId;
    private Point _holdStart;
    private bool _holdTriggered;
    private DateTime _protectRenameUntil;
    private TextBox? _renamePopupBox;

    private void SetUpHoldToRename()
    {
        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = HoldToRenameDelay;
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += (_, _) => OnHoldElapsed();

        // handledEventsToo: the ListView marks pointer events handled for its own selection logic.
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnHoldPressed), handledEventsToo: true);
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnHoldMoved), handledEventsToo: true);
        Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnHoldReleased), handledEventsToo: true);
        // Lost capture / cancel end the gesture like a release (otherwise the rename box would stay "protected").
        Root.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnHoldReleased), handledEventsToo: true);
        Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnHoldReleased), handledEventsToo: true);
    }

    private void OnHoldPressed(object sender, PointerRoutedEventArgs e)
    {
        CancelHold();
        _holdTriggered = false;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed) return;
        if (e.KeyModifiers != VirtualKeyModifiers.None) return; // Ctrl/Shift+click is multi-select

        var item = ItemUnder(e.OriginalSource as DependencyObject);
        if (item is null || item.IsRenaming || item.IsDrive) return;

        _holdItem = item;
        _holdPointerId = e.Pointer.PointerId;
        _holdStart = point.Position;
        _holdTimer?.Start();
    }

    private void OnHoldMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_holdItem is null || e.Pointer.PointerId != _holdPointerId) return;
        var p = e.GetCurrentPoint(this).Position;
        if (Math.Abs(p.X - _holdStart.X) > HoldSlop || Math.Abs(p.Y - _holdStart.Y) > HoldSlop) CancelHold();
    }

    private void OnHoldReleased(object sender, PointerRoutedEventArgs e)
    {
        CancelHold();
        if (!_holdTriggered) return;
        _holdTriggered = false;
        // The list reacts to the release (selection, focus); put the keyboard back in the name box afterwards.
        _protectRenameUntil = DateTime.UtcNow.AddMilliseconds(500);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusActiveRenameEditor);
    }

    private void OnHoldElapsed()
    {
        var item = _holdItem;
        CancelHold();
        if (item is null || ViewModel is null || item.IsRenaming) return;
        if (!ViewModel.Items.Contains(item)) return; // folder changed meanwhile

        _holdTriggered = true;
        _protectRenameUntil = DateTime.UtcNow.AddSeconds(30); // until the button is released
        ViewModel.SelectItems([item]);
        ViewModel.BeginRename(item);
    }

    private void CancelHold()
    {
        _holdTimer?.Stop();
        _holdItem = null;
    }

    /// <summary>True while a hold-started rename must not end just because the list grabbed focus on mouse-up.</summary>
    private bool IsRenameProtected => DateTime.UtcNow < _protectRenameUntil;

    private void FocusActiveRenameEditor()
    {
        var renaming = ViewModel?.Items.FirstOrDefault(i => i.IsRenaming);
        if (renaming is null) return;
        TextBox? box = _renamePopupBox
                       ?? (ActiveList?.ContainerFromItem(renaming) is DependencyObject container ? FindDescendant<TextBox>(container) : null);
        if (box is null) return;

        box.Focus(FocusState.Keyboard);
        int dot = renaming.IsContainer ? -1 : box.Text.LastIndexOf('.');
        box.Select(0, dot > 0 ? dot : box.Text.Length);
    }

    private static FileItemViewModel? ItemUnder(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewBase) return null; // reached the list itself: empty space
            if (node is SelectorItem { Content: FileItemViewModel vm }) return vm;
            if (node is TextBox) return null;
        }
        return null;
    }
}
