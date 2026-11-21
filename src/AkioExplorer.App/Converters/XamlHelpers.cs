using AkioExplorer.Core.Models;
using Microsoft.UI.Xaml;

namespace AkioExplorer.App.Converters;

/// <summary>Static functions for compiled x:Bind (faster than IValueConverter, no boxing through converters).</summary>
public static class XamlHelpers
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrEmpty(value);

    public static string SortGlyph(string column, SortField current, bool descending) =>
        column != current.ToString() ? string.Empty : descending ? "" : "";

    public static bool IsMode(FileViewMode mode, FileViewMode current) => mode == current;

    public static string FavoriteGlyph(bool isFavorite) => isFavorite ? "" : "";

    public static string FavoriteLabel(bool isFavorite) => isFavorite ? "Unpin from Favorites" : "Pin to Favorites";

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity(bool isError) =>
        isError ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    public static double ActiveIndicatorOpacity(bool show, bool active) => show && active ? 1.0 : 0.0;
}
