using AkioExplorer.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AkioExplorer.App.Controls;

/// <summary>
/// <c>controls:ShortcutText.Command="NewTab"</c> on a menu item shows that command's current key ("Ctrl+T"), and
/// follows the user's changes in Settings › Keyboard shortcuts. The key itself is handled by the main window.
/// </summary>
public static class ShortcutText
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(string), typeof(ShortcutText), new PropertyMetadata(null, OnCommandChanged));

    private static readonly List<WeakReference<MenuFlyoutItem>> Items = [];
    private static ShortcutService? _service;

    public static string? GetCommand(DependencyObject element) => (string?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, string? value) => element.SetValue(CommandProperty, value);

    private static ShortcutService Service
    {
        get
        {
            if (_service is not null) return _service;
            _service = App.Services.GetRequiredService<ShortcutService>();
            var queue = DispatcherQueue.GetForCurrentThread();
            _service.Changed += (_, _) => queue.TryEnqueue(RefreshAll);
            return _service;
        }
    }

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MenuFlyoutItem item) return;
        Items.Add(new WeakReference<MenuFlyoutItem>(item));
        Refresh(item);
    }

    private static void Refresh(MenuFlyoutItem item)
    {
        if (GetCommand(item) is { Length: > 0 } id) item.KeyboardAcceleratorTextOverride = Service.Display(id);
    }

    private static void RefreshAll()
    {
        Items.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var reference in Items)
            if (reference.TryGetTarget(out var item)) Refresh(item);
    }
}
