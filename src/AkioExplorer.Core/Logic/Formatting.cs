using System.Globalization;

namespace AkioExplorer.Core.Logic;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>App-wide: 1 KB = 1000 bytes instead of 1024 (Settings › Files and folders).</summary>
    public static bool DecimalUnits { get; set; }

    /// <summary>App-wide date style for list columns (Settings › Files and folders).</summary>
    public static Models.DateDisplay DateDisplay { get; set; }

    /// <summary>1023 → "1,023 B", 1536 → "1.5 KB", 10 MB → "10 MB" (3 significant digits).</summary>
    public static string FormatSize(long bytes, CultureInfo? culture = null) => FormatSize(bytes, DecimalUnits, culture);

    public static string FormatSize(long bytes, bool decimalUnits, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double step = decimalUnits ? 1000 : 1024;
        if (bytes < step) return bytes.ToString("N0", culture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= step && unit < Units.Length - 1)
        {
            value /= step;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, culture) + " " + Units[unit];
    }

    public static string FormatDate(DateTimeOffset value) => FormatDate(value.LocalDateTime, DateDisplay, DateTime.Now, CultureInfo.CurrentCulture);

    public static string FormatDate(DateTime local, Models.DateDisplay display, DateTime now, CultureInfo culture)
    {
        switch (display)
        {
            case Models.DateDisplay.DayMonthYear: return local.ToString("dd-MM-yyyy  HH:mm", culture);
            case Models.DateDisplay.YearMonthDay: return local.ToString("yyyy-MM-dd  HH:mm", culture);
            case Models.DateDisplay.MonthDayYear: return local.ToString("MM/dd/yyyy  h:mm tt", CultureInfo.InvariantCulture);
            case Models.DateDisplay.Relative:
                var age = now - local;
                if (age < TimeSpan.Zero) return local.ToString("g", culture); // clock skew / future dates
                if (age.TotalMinutes < 1) return "just now";
                if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
                if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
                if (age.TotalDays < 2) return "yesterday " + local.ToString("t", culture);
                if (age.TotalDays < 31) return $"{(int)age.TotalDays} days ago";
                return local.ToString("d", culture);
            default: return local.ToString("g", culture);
        }
    }
}
