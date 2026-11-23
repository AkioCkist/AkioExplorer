using System.Globalization;

namespace AkioExplorer.Core.Logic;

/// <summary>Value formats for the preview pane's details list (OneCommander-style: exact and rounded sizes, age…).</summary>
public static class DetailFormatting
{
    public static string Bytes(long bytes, CultureInfo? culture = null) =>
        bytes.ToString("N0", culture ?? CultureInfo.CurrentCulture) + (bytes == 1 ? " byte" : " bytes");

    /// <summary>Decimal units (1 MB = 1,000,000 bytes), the way drive makers and cloud services count.</summary>
    public static string Decimal(long bytes, CultureInfo? culture = null) => Scaled(bytes, 1000, ["B", "KB", "MB", "GB", "TB", "PB"], culture);

    /// <summary>Binary units (1 MiB = 1,048,576 bytes), the way Windows counts.</summary>
    public static string Binary(long bytes, CultureInfo? culture = null) => Scaled(bytes, 1024, ["B", "KiB", "MiB", "GiB", "TiB", "PiB"], culture);

    /// <summary>How long ago, compactly: "just now", "13 min", "5 h", "3 days", "2 months", "4 years".</summary>
    public static string Age(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.Zero) return "in the future";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h";
        if (span.TotalDays < 31) return Plural((int)span.TotalDays, "day");
        int months = (now.Year - when.Year) * 12 + now.Month - when.Month - (now.Day < when.Day ? 1 : 0);
        if (months < 12) return Plural(Math.Max(months, 1), "month");
        int years = months / 12;
        int rest = months % 12;
        return rest == 0 ? Plural(years, "year") : $"{Plural(years, "year")} {Plural(rest, "month")}";
    }

    /// <summary>"2:57", "1:02:03".</summary>
    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    /// <summary>Explorer-style attribute string, e.g. "-a----" or "rah---".</summary>
    public static string Attributes(FileAttributes attributes)
    {
        Span<char> chars =
        [
            attributes.HasFlag(FileAttributes.ReadOnly) ? 'r' : '-',
            attributes.HasFlag(FileAttributes.Archive) ? 'a' : '-',
            attributes.HasFlag(FileAttributes.Hidden) ? 'h' : '-',
            attributes.HasFlag(FileAttributes.System) ? 's' : '-',
            attributes.HasFlag(FileAttributes.Compressed) ? 'c' : attributes.HasFlag(FileAttributes.Encrypted) ? 'e' : '-',
            attributes.HasFlag(FileAttributes.ReparsePoint) ? 'l' : '-',
        ];
        return new string(chars);
    }

    /// <summary>
    /// One size line: the rounded size in the units the user picked, then the exact count, e.g. "32.5 MB (34,112,334 bytes)".
    /// Small sizes are exact already, so they stay a single number.
    /// </summary>
    public static string SizeSummary(long bytes, CultureInfo? culture = null) =>
        bytes < 1000 ? Bytes(bytes, culture) : $"{Formatting.FormatSize(bytes, culture)} ({Bytes(bytes, culture)})";

    /// <summary>A date with its age, e.g. "10/27/2026 2:25 AM · 27 days ago".</summary>
    public static string DateWithAge(DateTimeOffset when, DateTimeOffset now, CultureInfo? culture = null)
    {
        string date = when.LocalDateTime.ToString("g", culture ?? CultureInfo.CurrentCulture);
        string age = Age(when, now);
        return age is "just now" or "in the future" ? $"{date} · {age}" : $"{date} · {age} ago";
    }

    /// <summary>
    /// The attributes worth telling someone, in words ("Read-only, Hidden"); null when there are none.
    /// Archive and the like are left out: they are set on almost every file and say nothing.
    /// </summary>
    public static string? AttributeNames(FileAttributes attributes)
    {
        var names = new List<string>(5);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) names.Add("Read-only");
        if (attributes.HasFlag(FileAttributes.Hidden)) names.Add("Hidden");
        if (attributes.HasFlag(FileAttributes.System)) names.Add("System");
        if (attributes.HasFlag(FileAttributes.Compressed)) names.Add("Compressed");
        if (attributes.HasFlag(FileAttributes.Encrypted)) names.Add("Encrypted");
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) names.Add("Link");
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    public static string BitRate(uint bitsPerSecond) =>
        bitsPerSecond >= 1_000_000 ? $"{bitsPerSecond / 1_000_000.0:0.#} Mbps" : $"{Math.Round(bitsPerSecond / 1000.0)} kbps";

    private static string Scaled(long bytes, int step, string[] units, CultureInfo? culture)
    {
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        string format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, culture ?? CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
