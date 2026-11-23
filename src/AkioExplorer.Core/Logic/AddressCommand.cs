namespace AkioExplorer.Core.Logic;

/// <summary>
/// Address-bar text that isn't a path is run like File Explorer does (and like Win+R): <c>cmd</c>, <c>code .</c>,
/// <c>powershell</c>, <c>wt</c>, a URL… with the open folder as working directory. This splits the text into
/// the program and its arguments; finding the program (PATH, App Paths) is left to the shell.
/// </summary>
public static class AddressCommand
{
    /// <summary>Splits <c>program args</c>; a program path with spaces must be quoted (<c>"C:\My Tools\x.exe" -v</c>).</summary>
    public static bool TryParse(string? text, out string program, out string arguments)
    {
        program = arguments = string.Empty;
        var line = text?.Trim() ?? string.Empty;
        if (line.Length == 0) return false;

        if (line[0] == '"')
        {
            int close = line.IndexOf('"', 1);
            if (close < 0) return false;
            program = line[1..close].Trim();
            arguments = line[(close + 1)..].Trim();
        }
        else
        {
            int space = line.IndexOfAny([' ', '\t']);
            program = space < 0 ? line : line[..space];
            arguments = space < 0 ? string.Empty : line[(space + 1)..].Trim();
        }
        return program.Length > 0;
    }

    /// <summary>
    /// Windows Terminal ignores the working directory and starts in its profile folder; without its own
    /// <c>-d</c> it should open where the user is, as every other shell does.
    /// </summary>
    public static string TerminalArguments(string program, string arguments, string folder)
    {
        string name = Path.GetFileNameWithoutExtension(program);
        if (!name.Equals("wt", StringComparison.OrdinalIgnoreCase)) return arguments;
        bool hasDirectory = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(a => a is "-d" or "--startingDirectory");
        if (hasDirectory) return arguments;
        string quoted = folder.EndsWith('\\') ? $"\"{folder}\\\"" : $"\"{folder}\""; // "C:\" would escape the quote
        return arguments.Length == 0 ? $"-d {quoted}" : $"-d {quoted} {arguments}";
    }
}
