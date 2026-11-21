using System.Globalization;

namespace AkioExplorer.Core.Logic;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>1023 → "1,023 B", 1536 → "1.5 KB", 10 MB → "10 MB" (3 significant digits).</summary>
    public static string FormatSize(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 1024) return bytes.ToString("N0", culture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, culture) + " " + Units[unit];
    }
}
