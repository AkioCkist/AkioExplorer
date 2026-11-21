namespace AkioExplorer.Core.Logic;

/// <summary>Validates names against Windows rules before touching the file system (error prevention).</summary>
public static class FileNameValidator
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns null when valid, otherwise a short user-facing explanation.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "A name can't be empty.";

        if (name.Length > 255)
            return "That name is too long (maximum 255 characters).";

        foreach (char c in name)
        {
            if (c < 32)
                return "A name can't contain control characters.";
            if (Array.IndexOf(InvalidChars, c) >= 0)
                return "A name can't contain any of these characters: \\ / : * ? \" < > |";
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
            return "A name can't end with a space or a period.";

        if (name is "." or "..")
            return "That name is reserved by Windows.";

        string stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem))
            return $"\"{stem}\" is reserved by Windows. Choose another name.";

        return null;
    }

    /// <summary>"New folder", "New folder (2)", ... – first name not contained in <paramref name="existing"/>.</summary>
    public static string GetUniqueName(string baseName, ICollection<string> existing)
    {
        var set = existing as ISet<string> ?? new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(baseName)) return baseName;

        string stem = Path.GetFileNameWithoutExtension(baseName);
        string ext = Path.GetExtension(baseName);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (!set.Contains(candidate)) return candidate;
        }
    }
}
