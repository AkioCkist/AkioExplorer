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
        InitializeComponent();
        ViewModel.Favorites.CollectionChanged += (_, _) => Bindings.Update();

    }

    public SettingsViewModel ViewModel { get; }

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
