using System.Runtime.InteropServices;
using AkioExplorer.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace AkioExplorer.App.Views;

/// <summary>
/// Draws the tray icon's menu with WinUI (same rounded, themed, icon menus as the rest of the app) instead of a
/// plain Win32 menu. A one-pixel borderless window sits under the cursor as the menu's anchor; the menu itself is
/// a windowed popup, so it can extend past that window anywhere on screen. Clicking elsewhere closes it.
/// </summary>
public sealed class TrayMenuWindow : Window
{
    private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly IntPtr _hwnd;
    private MenuFlyout? _menu;
    private bool _allowClose;

    public TrayMenuWindow()
    {
        Content = _root;
        Title = "AkioExplorer menu";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        // Windows keeps a minimum window size, so the "one pixel" anchor would show as a small black box: make it
        // fully transparent. The menu is its own popup window and stays visible.
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, GetWindowLongPtrW(_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);

        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Dismiss();
        };
        Activated += (_, e) =>
        {
            // Clicking the taskbar, the desktop or another app: put the menu away like a native one.
            if (e.WindowActivationState == WindowActivationState.Deactivated) Dismiss();
        };
    }

    /// <summary>Shows <paramref name="items"/> at a screen point in physical pixels (the cursor over the tray icon).</summary>
    public void Show(IReadOnlyList<TrayMenuItem> items, int x, int y, ElementTheme theme)
    {
        _menu?.Hide();
        _root.RequestedTheme = theme;
        AppWindow.MoveAndResize(new RectInt32(x, y, 1, 1));
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd); // the shell lets the tray owner take focus; needed so outside clicks close the menu

        var menu = _menu = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false,
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
            MenuFlyoutPresenterStyle = PresenterStyle(theme),
        };
        foreach (var element in Build(items)) menu.Items.Add(element);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) Dismiss();
        };

        void Open() => menu.ShowAt(_root, new FlyoutShowOptions { Position = new Point(0, 0), ShowMode = FlyoutShowMode.Standard });
        if (_root.IsLoaded) Open();
        else
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                _root.Loaded -= OnLoaded;
                Open();
            }
            _root.Loaded += OnLoaded;
        }
    }

    /// <summary>App exit: really close (otherwise the hidden window keeps the process alive).</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        _menu?.Hide();
        Close();
    }

    private void Dismiss()
    {
        var menu = _menu;
        _menu = null;
        menu?.Hide();
        AppWindow.Hide();
    }

    private static Style PresenterStyle(ElementTheme theme)
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, theme));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 240d));
        return style;
    }

    private IEnumerable<MenuFlyoutItemBase> Build(IReadOnlyList<TrayMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                yield return new MenuFlyoutSeparator();
                continue;
            }

            if (item.Children is { Count: > 0 } children)
            {
                var sub = new MenuFlyoutSubItem { Text = item.Text, IsEnabled = item.IsEnabled };
                if (Application.Current.Resources.TryGetValue("MenuFlyoutItemThemePaddingNarrow", out var subPadding) && subPadding is Thickness subNarrow)
                    sub.Padding = subNarrow;
                if (item.Glyph is { } subGlyph) sub.Icon = new FontIcon { Glyph = subGlyph };
                foreach (var child in Build(children)) sub.Items.Add(child);
                yield return sub;
                continue;
            }

            MenuFlyoutItem entry = item.Checked is { } isChecked
                ? new ToggleMenuFlyoutItem { IsChecked = isChecked }
                : new MenuFlyoutItem();
            entry.Text = item.Text;
            entry.IsEnabled = item.IsEnabled;
            // Same compact rows as the app's mouse menus, even when the tray click arrives as "unknown" input.
            if (Application.Current.Resources.TryGetValue("MenuFlyoutItemThemePaddingNarrow", out var padding) && padding is Thickness narrow)
                entry.Padding = narrow;
            if (item.IsDefault) entry.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (item.Glyph is { } glyph) entry.Icon = new FontIcon { Glyph = glyph };
            if (item.Keys is { } keys) entry.KeyboardAcceleratorTextOverride = keys;
            if (item.Invoke is { } invoke)
            {
                entry.Click += (_, _) =>
                {
                    Dismiss();
                    invoke();
                };
            }
            yield return entry;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
}
