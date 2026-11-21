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
        Body.Width = Math.Min(1000, width);
    }

    public bool HasFavorites => ViewModel.Favorites.Count > 0;

    private void OnRemoveFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FavoriteEntry entry }) ViewModel.RemoveFavoriteCommand.Execute(entry);
    }
}
