namespace AkioExplorer.Core.Models;

/// <summary>A command that can have keyboard shortcuts, with the keys it has out of the box.</summary>
public sealed record ShortcutCommand(string Id, string Title, string Category, IReadOnlyList<string> Defaults)
{
    public IReadOnlyList<HotkeyGesture> DefaultGestures { get; } = Defaults
        .Select(d => HotkeyGesture.TryParseShortcut(d, out var g) ? g : throw new ArgumentException($"Bad default shortcut {d} for {Id}"))
        .ToList();
}

/// <summary>
/// The app-wide shortcuts the user can change (Settings › Keyboard shortcuts). Defaults follow File Explorer.
/// Keys that belong to the file list itself (Enter, Del, F2, Backspace, Ctrl+A, type-to-find) are not listed.
/// <see cref="AppSettings.Shortcuts"/> stores only what the user changed; everything else uses these defaults.
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutCommand> All { get; } =
    [
        new("Back", "Back", "Navigation", ["Alt+Left"]),
        new("Forward", "Forward", "Navigation", ["Alt+Right"]),
        new("Up", "Up one level", "Navigation", ["Alt+Up"]),
        new("Refresh", "Refresh", "Navigation", ["F5", "Ctrl+R"]),
        new("Address", "Edit the address", "Navigation", ["Ctrl+L", "Alt+D", "F4"]),
        new("FocusSidebar", "Go to the sidebar", "Navigation", ["Ctrl+1"]),
        new("SwitchPane", "Switch pane", "Navigation", ["F6"]),

        new("Filter", "Filter or search this folder", "Search", ["Ctrl+F", "Ctrl+E", "F3"]),
        new("AdvancedSearch", "Advanced search", "Search", ["Ctrl+Shift+F"]),
        new("Palette", "Command palette", "Search", ["Ctrl+Shift+P"]),

        new("NewTab", "New tab", "Tabs", ["Ctrl+T"]),
        new("CloseTab", "Close tab", "Tabs", ["Ctrl+W"]),
        new("ReopenTab", "Reopen closed tab", "Tabs", ["Ctrl+Shift+T"]),
        new("NextTab", "Next tab", "Tabs", ["Ctrl+Tab"]),
        new("PreviousTab", "Previous tab", "Tabs", ["Ctrl+Shift+Tab"]),

        new("Copy", "Copy", "Edit", ["Ctrl+C"]),
        new("Cut", "Cut", "Edit", ["Ctrl+X"]),
        new("Paste", "Paste", "Edit", ["Ctrl+V"]),
        new("Undo", "Undo", "Edit", ["Ctrl+Z"]),
        new("CopyPath", "Copy path", "Edit", ["Ctrl+Shift+C"]),
        new("NewFolder", "New folder", "Edit", ["Ctrl+Shift+N"]),
        new("NewFile", "New text document", "Edit", ["Ctrl+Alt+N"]),
        new("BatchRename", "Rename many", "Edit", ["Ctrl+M"]),
        new("Properties", "Properties", "Edit", ["Alt+Enter"]),

        new("SelectNone", "Select none", "Selection", ["Ctrl+Shift+A"]),
        new("InvertSelection", "Invert selection", "Selection", ["Ctrl+I"]),

        new("DetailsView", "Details view", "View", ["Ctrl+Shift+6"]),
        new("GridView", "Grid view", "View", ["Ctrl+Shift+2"]),
        new("DualPane", "Dual pane", "View", ["Ctrl+Shift+D"]),
        new("Preview", "Preview pane", "View", ["Alt+P"]),
        new("HiddenItems", "Show hidden items", "View", ["Ctrl+H"]),
        new("FolderSizes", "Calculate folder sizes", "View", ["Ctrl+Alt+S"]),

        new("Terminal", "Open in Terminal", "Apps", ["Ctrl+Alt+T"]),
        new("Editor", "Open in code editor", "Apps", ["Ctrl+Shift+E"]),
    ];

    private static readonly Dictionary<string, ShortcutCommand> ById = All.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    public static ShortcutCommand? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>The keys of <paramref name="id"/> now: the user's choice if changed (possibly none), else the defaults.</summary>
    public static IReadOnlyList<HotkeyGesture> Gestures(string id, IReadOnlyDictionary<string, List<string>>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(id, out var custom))
            return custom.Select(t => HotkeyGesture.TryParseShortcut(t, out var g) ? g : (HotkeyGesture?)null)
                .OfType<HotkeyGesture>().Distinct().ToList();
        return Find(id)?.DefaultGestures ?? [];
    }

    /// <summary>The first key of <paramref name="id"/> as shown in menus ("Ctrl+T"), or empty when it has none.</summary>
    public static string Display(string id, IReadOnlyDictionary<string, List<string>>? overrides) =>
        Gestures(id, overrides) is [var first, ..] ? first.ToString() : string.Empty;

    public static bool IsCustomized(string id, IReadOnlyDictionary<string, List<string>>? overrides) =>
        overrides is not null && overrides.ContainsKey(id);

    /// <summary>The other command that already uses <paramref name="gesture"/>, if any.</summary>
    public static ShortcutCommand? UsedBy(HotkeyGesture gesture, string exceptId, IReadOnlyDictionary<string, List<string>>? overrides) =>
        All.FirstOrDefault(c => !string.Equals(c.Id, exceptId, StringComparison.OrdinalIgnoreCase) && Gestures(c.Id, overrides).Contains(gesture));

    /// <summary>
    /// Gives <paramref name="id"/> exactly <paramref name="gestures"/> and takes them away from any other command,
    /// so one key never runs two commands. Choosing the defaults again forgets the override.
    /// </summary>
    public static void Assign(Dictionary<string, List<string>> overrides, string id, IReadOnlyList<HotkeyGesture> gestures)
    {
        foreach (var other in All)
        {
            if (string.Equals(other.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            var current = Gestures(other.Id, overrides);
            if (!current.Any(gestures.Contains)) continue;
            SetOrForget(overrides, other, current.Where(g => !gestures.Contains(g)).ToList());
        }
        if (Find(id) is { } command) SetOrForget(overrides, command, gestures.Distinct().ToList());
    }

    public static void Reset(Dictionary<string, List<string>> overrides, string id) => overrides.Remove(id);

    private static void SetOrForget(Dictionary<string, List<string>> overrides, ShortcutCommand command, List<HotkeyGesture> gestures)
    {
        if (gestures.SequenceEqual(command.DefaultGestures)) overrides.Remove(command.Id);
        else overrides[command.Id] = gestures.Select(g => g.ToString()).ToList();
    }

    /// <summary>The command whose first default key is shown as <paramref name="defaultText"/> ("Ctrl+T" → New tab).</summary>
    public static ShortcutCommand? ByDefaultText(string? defaultText) =>
        string.IsNullOrEmpty(defaultText) || !HotkeyGesture.TryParseShortcut(defaultText, out var g)
            ? null
            : All.FirstOrDefault(c => c.DefaultGestures.Count > 0 && c.DefaultGestures[0] == g);
}
