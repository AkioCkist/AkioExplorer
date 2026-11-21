using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace AkioExplorer.App.Services;

/// <summary>Applies Light / Dark / System theme to the window content and the caption buttons.</summary>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;
    private FrameworkElement? _root;
    private AppWindow? _appWindow;

    public ThemeService(ISettingsService settings)
    {
        _settings = settings;
    }

    public void Attach(FrameworkElement root, AppWindow appWindow)
    {
        _root = root;
        _appWindow = appWindow;
        root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        Apply(_settings.Current.Theme);
    }

    public void Apply(AppTheme theme)
    {
        if (_root is null) return;
        _root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        if (_root is null || _appWindow is null) return;
        var bar = _appWindow.TitleBar;
        bool dark = _root.ActualTheme == ElementTheme.Dark;

        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x08, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }
}
