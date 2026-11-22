using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Logic;

public enum RenameCase
{
    Unchanged,
    Lower,
    Upper,
    Title,
    Sentence,
}

/// <summary>
/// Multi-rename options (Total Commander / OneCommander style). Masks: [N] name without extension, [N2-5] characters
/// 2..5 of it, [E] extension, [C] counter, [P] parent folder, [YMD] modified date, [hms] modified time.
/// </summary>
public sealed record BatchRenameOptions
{
    public string NameMask { get; init; } = "[N]";
    public string ExtensionMask { get; init; } = "[E]";
    public string Find { get; init; } = string.Empty;
    public string Replace { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    public RenameCase Case { get; init; }
    public int CounterStart { get; init; } = 1;
    public int CounterStep { get; init; } = 1;
    public int CounterDigits { get; init; } = 1;
}

public sealed record RenamePlanEntry(StorageItem Item, string NewName, string? Error)
{
    public bool IsChange => Error is null && !string.Equals(Item.Name, NewName, StringComparison.Ordinal);
}

public static partial class BatchRenamer
{
    /// <summary>
    /// Computes new names for <paramref name="items"/> (in the given order, which drives [C]).
    /// <paramref name="otherNames"/> are names in the same folder that are not being renamed; clashes with them, with
    /// each other and invalid names are reported per entry instead of throwing.
    /// </summary>
    public static IReadOnlyList<RenamePlanEntry> Plan(
        IReadOnlyList<StorageItem> items,
        BatchRenameOptions options,
        IEnumerable<string> otherNames,
        string parentName = "")
    {
        Regex? regex = null;
        string? regexError = null;
        if (options.UseRegex && options.Find.Length > 0)
        {
            try
            {
                regex = new Regex(options.Find, options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                regexError = $"Invalid pattern: {ex.Message}";
            }
        }

        var names = new string[items.Count];
        var errors = new string?[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (regexError is not null)
            {
                names[i] = items[i].Name;
                errors[i] = regexError;
                continue;
            }

            try
            {
                long counter = options.CounterStart + (long)i * options.CounterStep;
                string name = Apply(items[i], options, counter, parentName, regex);
                names[i] = name;
                errors[i] = FileNameValidator.Validate(name);
            }
            catch (RegexMatchTimeoutException)
            {
                names[i] = items[i].Name;
                errors[i] = "The pattern took too long to run.";
            }
        }

        // Duplicates: against names that stay, and among the new names themselves.
        var taken = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) taken.Remove(item.Name);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) counts[n] = counts.GetValueOrDefault(n) + 1;

        var plan = new RenamePlanEntry[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            string? error = errors[i];
            if (error is null && taken.Contains(names[i])) error = "An item with this name already exists.";
            if (error is null && counts[names[i]] > 1) error = "Two items would get this name.";
            plan[i] = new RenamePlanEntry(items[i], names[i], error);
        }
        return plan;
    }

    public static string Apply(StorageItem item, BatchRenameOptions options, long counter, string parentName = "", Regex? findRegex = null)
    {
        bool isFile = item.Kind == StorageItemKind.File;
        string stem = isFile ? Path.GetFileNameWithoutExtension(item.Name) : item.Name;
        string ext = isFile ? Path.GetExtension(item.Name).TrimStart('.') : string.Empty;

        string counterText = Math.Abs(counter).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(options.CounterDigits, 1, 12), '0');
        if (counter < 0) counterText = "-" + counterText;

        string newStem = ExpandMask(options.NameMask, item, stem, ext, counterText, parentName);
        string newExt = isFile ? ExpandMask(options.ExtensionMask, item, stem, ext, counterText, parentName) : string.Empty;
        string name = newExt.Length > 0 ? $"{newStem}.{newExt}" : newStem;

        if (options.Find.Length > 0)
        {
            if (options.UseRegex)
            {
                var regex = findRegex ?? new Regex(options.Find, options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                name = regex.Replace(name, options.Replace);
            }
            else
            {
                name = name.Replace(options.Find, options.Replace, options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }
        }

        return ChangeCase(name, options.Case).Trim();
    }

    private static string ExpandMask(string mask, StorageItem item, string stem, string ext, string counter, string parentName)
    {
        if (string.IsNullOrEmpty(mask)) return string.Empty;
        var modified = item.Modified?.LocalDateTime;
        return MaskToken().Replace(mask, m =>
        {
            string token = m.Groups[1].Value;
            if (token == "N") return stem;
            if (token == "E") return ext;
            if (token == "C") return counter;
            if (token == "P") return parentName;
            if (token == "YMD") return modified?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            if (token == "hms") return modified?.ToString("HH-mm-ss", CultureInfo.InvariantCulture) ?? string.Empty;
            if (token.StartsWith('N') && SubRange().Match(token) is { Success: true } range)
            {
                int from = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                int to = range.Groups[2].Success ? int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture) : from;
                if (from < 1 || from > stem.Length) return string.Empty;
                to = Math.Min(Math.Max(to, from), stem.Length);
                return stem.Substring(from - 1, to - from + 1);
            }
            return m.Value; // unknown token: keep literally
        });
    }

    private static string ChangeCase(string name, RenameCase mode)
    {
        var culture = CultureInfo.CurrentCulture;
        switch (mode)
        {
            case RenameCase.Lower:
                return name.ToLower(culture);
            case RenameCase.Upper:
                return name.ToUpper(culture);
            case RenameCase.Title:
                return culture.TextInfo.ToTitleCase(name.ToLower(culture));
            case RenameCase.Sentence:
                if (name.Length == 0) return name;
                var lower = name.ToLower(culture);
                var sb = new StringBuilder(lower);
                sb[0] = char.ToUpper(sb[0], culture);
                return sb.ToString();
            default:
                return name;
        }
    }

    [GeneratedRegex(@"\[(N\d+(?:-\d+)?|N|E|C|P|YMD|hms)\]")]
    private static partial Regex MaskToken();

    [GeneratedRegex(@"^N(\d+)(?:-(\d+))?$")]
    private static partial Regex SubRange();
}
