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
        Body.Width = Math.Max(320, Math.Min(900, width - 236));
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
