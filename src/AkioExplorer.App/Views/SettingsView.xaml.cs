using AkioExplorer.App.ViewModels;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AkioExplorer.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        Cloud = App.Services.GetRequiredService<CloudAccountsViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await Cloud.LoadQuotasAsync();
        Categories.SelectedIndex = 0;
        ViewModel.Favorites.CollectionChanged += (_, _) => Bindings.Update();

    }

    public SettingsViewModel ViewModel { get; }

    public CloudAccountsViewModel Cloud { get; }

    private static MainWindow Shell => App.Services.GetRequiredService<MainWindow>();

    private async void OnChangeDownloadFolder(object sender, RoutedEventArgs e)
    {
        if (await Shell.PickFolderAsync() is { } folder) ViewModel.SetDownloadFolder(folder);
    }

    private void OnResetDownloadFolder(object sender, RoutedEventArgs e) => ViewModel.SetDownloadFolder(null);

    // ------------------------------------------------------------------ quick search hotkey box

    private void OnHotkeyFocus(object sender, RoutedEventArgs e) => ViewModel.BeginHotkeyEdit();

    private void OnHotkeyBlur(object sender, RoutedEventArgs e)
    {
        ViewModel.QuickSearchHotkey = HotkeyBox.Text; // the TwoWay binding may not have pushed it yet
        ViewModel.EndHotkeyEdit();
    }

    /// <summary>A key pressed with Ctrl / Alt / Win becomes the hotkey; plain typing edits the text.</summary>
    private void OnHotkeyKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        static bool Down(Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ViewModel.QuickSearchHotkey = HotkeyBox.Text;
            ViewModel.EndHotkeyEdit();
            ViewModel.BeginHotkeyEdit();
            HotkeyBox.SelectAll();
            e.Handled = true;
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Down(Windows.System.VirtualKey.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (Down(Windows.System.VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
        if (Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows)) modifiers |= HotkeyModifiers.Win;
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0) return;
        if (Down(Windows.System.VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;

        var gesture = new HotkeyGesture(modifiers, (int)e.Key);
        if (!gesture.IsValid) return; // only modifiers so far
        HotkeyBox.Text = gesture.ToString();
        HotkeyBox.Select(HotkeyBox.Text.Length, 0);
        ViewModel.QuickSearchHotkey = gesture.ToString();
        e.Handled = true;
    }

    private async void OnAddAccount(object sender, RoutedEventArgs e) => await Shell.AddCloudAccountFromSettingsAsync();

    private async void OnRenameAccount(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudAccountItem account })
            await AccountDialogs.RenameAsync(App.Services.GetRequiredService<Services.DialogService>(), Cloud, account);
    }

    private async void OnDisconnectAccount(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudAccountItem account }) await Shell.DisconnectFromSettingsAsync(account);
    }

    /// <summary>
    /// NavigationView measures its content with unbounded width, so the host passes the real width in.
    /// Keeps text wrapping and a comfortable reading width (max 1000 epx).
    /// </summary>
    public void SetAvailableWidth(double width)
    {
        if (width <= 0) return;
        Layout.Width = width;
        bool narrow = width < NarrowWidth;
        if (narrow != _narrow) ApplyLayout(narrow);
        Body.Width = narrow ? Math.Max(260, width - 24) : Math.Max(320, Math.Min(900, width - 236));
    }

    /// <summary>Below this width the categories move above the settings (a scrolling row) instead of a side column.</summary>
    private const double NarrowWidth = 720;

    private bool? _narrow;

    private void ApplyLayout(bool narrow)
    {
        _narrow = narrow;
        SidebarColumn.Width = narrow ? new GridLength(0) : new GridLength(220);
        Grid.SetRowSpan(Sidebar, narrow ? 1 : 2);
        Grid.SetColumnSpan(Sidebar, narrow ? 2 : 1);
        Sidebar.Padding = narrow ? new Thickness(16, 16, 16, 0) : new Thickness(16, 24, 0, 16);
        Grid.SetRow(Scroller, narrow ? 1 : 0);
        Grid.SetRowSpan(Scroller, narrow ? 1 : 2);
        Grid.SetColumn(Scroller, narrow ? 0 : 1);
        Grid.SetColumnSpan(Scroller, narrow ? 2 : 1);
        Body.Padding = narrow ? new Thickness(16, 8, 16, 32) : new Thickness(16, 24, 32, 32);

        // Categories: a vertical list beside the settings, or a horizontal strip above them.
        Categories.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(narrow
            ? "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal' /></ItemsPanelTemplate>"
            : "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Vertical' /></ItemsPanelTemplate>");
        ScrollViewer.SetHorizontalScrollMode(Categories, narrow ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(Categories, narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(Categories, narrow ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(Categories, narrow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }

    // ------------------------------------------------------------------ categories and search

    private IEnumerable<StackPanel> Sections => Body.Children.OfType<StackPanel>().Where(p => p.Tag is string);

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchBox.Text.Length > 0) SearchBox.Text = string.Empty; // picking a category ends the search
        else ShowCategory();
    }

    private void ShowCategory()
    {
        string? tag = (Categories.SelectedItem as FrameworkElement)?.Tag as string ?? "General";
        foreach (var section in Sections)
        {
            section.Visibility = (string)section.Tag == tag ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in section.Children.OfType<Controls.SettingRow>()) row.Visibility = Visibility.Visible;
        }
        NoMatches.Visibility = Visibility.Collapsed;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Recognition over recall: type any word and every matching setting shows, across categories.</summary>
    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        string query = sender.Text.Trim();
        if (query.Length == 0)
        {
            ShowCategory();
            return;
        }

        bool any = false;
        foreach (var section in Sections)
        {
            bool sectionHit = false;
            foreach (var row in section.Children.OfType<Controls.SettingRow>())
            {
                bool hit = row.Header.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                           || row.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                row.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                sectionHit |= hit;
            }
            section.Visibility = sectionHit ? Visibility.Visible : Visibility.Collapsed;
            any |= sectionHit;
        }
        NoMatches.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool HasFavorites => ViewModel.Favorites.Count > 0;

    private void OnRemoveFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FavoriteEntry entry }) ViewModel.RemoveFavoriteCommand.Execute(entry);
    }
}
