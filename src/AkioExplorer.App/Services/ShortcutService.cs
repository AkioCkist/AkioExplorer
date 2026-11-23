using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.App.Services;

/// <summary>
/// The keyboard shortcuts in effect (<see cref="ShortcutCatalog"/> defaults plus the user's changes in settings).
/// <see cref="Changed"/> fires whenever they change, including after a settings import or reset, on the thread that
/// changed the settings (the UI thread in practice; listeners that touch XAML enqueue to be safe).
/// </summary>
public sealed class ShortcutService
{
    private readonly ISettingsService _settings;
    private string _signature;

    public ShortcutService(ISettingsService settings)
    {
        _settings = settings;
        _signature = Signature();
        // Import / reset replace the whole settings object's content: pick up the new shortcuts then.
        settings.Changed += (_, _) =>
        {
            string now = Signature();
            if (now == _signature) return;
            _signature = now;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    private Dictionary<string, List<string>> Overrides => _settings.Current.Shortcuts ??= new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HotkeyGesture> Gestures(string id) => ShortcutCatalog.Gestures(id, Overrides);

    /// <summary>"Ctrl+T", or empty when the command has no key.</summary>
    public string Display(string id) => ShortcutCatalog.Display(id, Overrides);

    public bool IsCustomized(string id) => ShortcutCatalog.IsCustomized(id, Overrides);

    public ShortcutCommand? UsedBy(HotkeyGesture gesture, string exceptId) => ShortcutCatalog.UsedBy(gesture, exceptId, Overrides);

    /// <summary>Shows today's key for a shortcut written with its default ("Ctrl+T" → whatever New tab uses now).</summary>
    public string? Current(string? defaultText) =>
        ShortcutCatalog.ByDefaultText(defaultText) is { } command ? NullIfEmpty(Display(command.Id)) : defaultText;

    public void Assign(string id, IReadOnlyList<HotkeyGesture> gestures) => Update(o => ShortcutCatalog.Assign(o, id, gestures));

    public void Reset(string id) => Update(o => ShortcutCatalog.Reset(o, id));

    public void ResetAll() => Update(o => o.Clear());

    private void Update(Action<Dictionary<string, List<string>>> change)
    {
        change(Overrides);
        _settings.NotifyChanged(); // saves, and raises Changed through the settings event
    }

    private string Signature() => string.Join(';', Overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
        .Select(p => p.Key + "=" + string.Join(',', p.Value)));

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
