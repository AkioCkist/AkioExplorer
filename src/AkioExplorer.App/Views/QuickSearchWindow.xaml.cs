using System.ComponentModel;
using System.Runtime.InteropServices;
using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace AkioExplorer.App.Views;

/// <summary>
/// The launcher popup: a borderless, always-on-top box in the upper third of the screen under the mouse.
/// Built once and hidden/shown, so the hotkey opens it instantly. Clicking elsewhere or Esc hides it.
/// </summary>
public sealed partial class QuickSearchWindow : Window
{
    private const double WidthDip = 720;
    private const double BoxDip = 64;
    private const double RowDip = 58;
    private const int VisibleRows = 8;

    private readonly ISettingsService _settings;
    private readonly IntPtr _hwnd;
    private bool _allowClose;
    private bool _visible;

    public QuickSearchWindow(QuickSearchViewModel viewModel, ISettingsService settings)
    {
        ViewModel = viewModel;
        _settings = settings;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false); // keeps Windows 11's rounded corners and shadow
        }
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();

        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true; // Alt+F4 just hides: the window is reused
            HidePopup();
        };
        Activated += OnActivated;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Results.CollectionChanged += (_, _) => UpdateSize();
        ViewModel.Opened += (_, _) => HidePopup();

        for (int i = 1; i <= 9; i++)
        {
            int index = i - 1;
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.Number0 + i, Modifiers = VirtualKeyModifiers.Menu };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                ViewModel.OpenAt(index);
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    public QuickSearchViewModel ViewModel { get; }

    public bool IsOpen => _visible;

    /// <summary>The hotkey: open, or hide when already open.</summary>
    public void Toggle()
    {
        if (_visible) HidePopup();
        else ShowPopup();
    }

    public void ShowPopup()
    {
        ViewModel.Prepare();
        Root.RequestedTheme = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        PlaceOnMouseMonitor();
        _visible = true;
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll(); // the last search stays, selected: type to replace, Enter to repeat
    }

    public void HidePopup()
    {
        if (!_visible) return;
        _visible = false;
        AppWindow.Hide();
    }

    /// <summary>App exit: really close.</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Clicking another window (or a launched app taking focus) puts the popup away, like a menu.
        if (args.WindowActivationState == WindowActivationState.Deactivated) HidePopup();
    }

    // ------------------------------------------------------------------ size and position

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private double HeightDip()
    {
        int rows = Math.Min(ViewModel.Results.Count, VisibleRows);
        return rows == 0 ? BoxDip : BoxDip + 1 + 12 + rows * RowDip;
    }

    private void PlaceOnMouseMonitor()
    {
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        // Move first so the DPI is the target monitor's, then size in that DPI.
        AppWindow.Move(new PointInt32(area.X + area.Width / 2, area.Y + area.Height / 5));
        double scale = Scale;
        int width = Math.Min((int)(WidthDip * scale), area.Width - 40);
        int height = (int)(HeightDip() * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + area.Height / 5, width, height));
    }

    private void UpdateSize()
    {
        Divider.Visibility = ViewModel.Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var size = AppWindow.Size;
        int height = (int)(HeightDip() * Scale);
        if (size.Height != height) AppWindow.Resize(new SizeInt32(size.Width, height));
    }

    // ------------------------------------------------------------------ keys

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Down:
                ViewModel.MoveSelection(1);
                break;
            case VirtualKey.Up:
                ViewModel.MoveSelection(-1);
                break;
            case VirtualKey.PageDown:
                ViewModel.MoveSelection(VisibleRows);
                break;
            case VirtualKey.PageUp:
                ViewModel.MoveSelection(-VisibleRows);
                break;
            case VirtualKey.Enter:
                // Enter opens; Ctrl+Enter shows it in AkioExplorer; Ctrl+Shift+Enter runs as administrator.
                ViewModel.Open(ViewModel.Selected, inFolder: ctrl && !shift, asAdmin: ctrl && shift);
                break;
            case VirtualKey.Tab:
                if (ViewModel.AcceptCompletion()) QueryBox.Select(QueryBox.Text.Length, 0);
                break;
            case VirtualKey.Escape:
                HidePopup();
                break;
            case VirtualKey.C when ctrl && shift:
                CopyText(ViewModel.Selected?.Path);
                break;
            default:
                return;
        }
        e.Handled = true;
        if (ViewModel.Selected is { } selected) ResultsList.ScrollIntoView(selected);
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ------------------------------------------------------------------ list

    private void OnResultClick(object sender, ItemClickEventArgs e) => ViewModel.Open(e.ClickedItem as QuickSearchResult);

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is QuickSearchResult result) ViewModel.EnsureIcon(result);
    }

    private void OnResultsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QuickSearchResult result || !result.IsOpenable) return;
        ViewModel.SelectedIndex = ViewModel.Results.IndexOf(result);

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, string? keys = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys is not null) item.KeyboardAcceleratorTextOverride = keys;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add(result.Kind switch
        {
            QuickResultKind.Web => "Search",
            QuickResultKind.Calculator => "Copy result",
            _ => "Open",
        },"", () => ViewModel.Open(result), "Enter");
        if (result.IsOnDisk)
        {
            Add("Show in AkioExplorer", "", () => ViewModel.Open(result, inFolder: true), "Ctrl+Enter");
            if (result.Kind is QuickResultKind.App or QuickResultKind.File)
                Add("Run as administrator", "", () => ViewModel.Open(result, asAdmin: true), "Ctrl+Shift+Enter");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Copy path", "", () => CopyText(result.Path), "Ctrl+Shift+C");
            Add("Copy name", "", () => CopyText(result.Title));
        }
        menu.ShowAt(ResultsList, e.GetPosition(ResultsList));
        e.Handled = true;
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    // ------------------------------------------------------------------ autocomplete ghost

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuickSearchViewModel.Completion) or nameof(QuickSearchViewModel.Query))
        {
            GhostTyped.Text = ViewModel.Query;
            GhostRest.Text = ViewModel.Completion;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
