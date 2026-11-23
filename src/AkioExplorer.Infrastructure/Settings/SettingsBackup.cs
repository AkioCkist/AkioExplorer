using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Infrastructure.Settings;

/// <summary>
/// Settings › Backup and reset: export the preferences to a JSON file, import them on another PC, or go back to the
/// defaults. Machine and session state (cloud accounts, open tabs, window position, search and launch history) is
/// never exported and never touched; Windows integration lives in the registry and is not part of this either.
/// </summary>
public static class SettingsBackup
{
    public const string Format = "AkioExplorer settings";
    public const int Version = 1;

    /// <summary>Stays as it is on import and reset (and is left out of exports).</summary>
    private static readonly HashSet<string> MachineState =
    [
        nameof(AppSettings.CloudAccounts),
        nameof(AppSettings.LeftPane),
        nameof(AppSettings.RightPane),
        nameof(AppSettings.Window),
        nameof(AppSettings.RecentSearches),
        nameof(AppSettings.QuickSearchLaunches),
        nameof(AppSettings.QuickSearchHistory),
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Export(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (var name in MachineState) node.Remove(name);
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["exported"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
            ["settings"] = node,
        };
        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// Reads an exported file (or a plain copy of settings.json). Settings missing from the file get their defaults.
    /// </summary>
    /// <exception cref="FormatException">Not an AkioExplorer settings file.</exception>
    public static AppSettings Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The file isn't valid JSON: " + ex.Message, ex);
        }

        if (root is not JsonObject obj) throw new FormatException("The file doesn't contain AkioExplorer settings.");
        bool wrapped = obj["format"]?.GetValue<string>() == Format;
        if (!wrapped && obj["settings"] is not null) throw new FormatException("The file doesn't contain AkioExplorer settings.");
        var body = wrapped ? obj["settings"] as JsonObject : obj;
        if (body is null || !body.Any(p => typeof(AppSettings).GetProperty(p.Key) is not null))
            throw new FormatException("The file doesn't contain AkioExplorer settings.");

        try
        {
            return body.Deserialize(SettingsJsonContext.Default.AppSettings) ?? throw new FormatException("The settings in the file are empty.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("A setting in the file has a value AkioExplorer doesn't understand: " + ex.Message, ex);
        }
    }

    /// <summary>Copies every preference from <paramref name="source"/> into <paramref name="target"/> (kept in place, as the app holds it).</summary>
    public static void Apply(AppSettings source, AppSettings target, bool keepFavorites = false)
    {
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || MachineState.Contains(property.Name)) continue;
            if (keepFavorites && property.Name == nameof(AppSettings.Favorites)) continue;
            property.SetValue(target, property.GetValue(source));
        }
        target.Shortcuts = new Dictionary<string, List<string>>(target.Shortcuts ?? new(), StringComparer.OrdinalIgnoreCase);
        target.Favorites ??= new();
    }

    /// <summary>Back to how AkioExplorer was out of the box; favorites stay (they are the user's folders, not a preference).</summary>
    public static void ResetToDefaults(AppSettings target) => Apply(new AppSettings(), target, keepFavorites: true);
}
