namespace AkioExplorer.Core.Abstractions;

/// <summary>One entry of the Windows shell context menu (shell extensions such as 7-Zip, Git, VLC, "Send to").</summary>
public sealed record ShellMenuEntry(
    int Id,
    string Text,
    string? Verb,
    IconBitmap? Icon,
    bool IsEnabled,
    bool IsSeparator,
    IReadOnlyList<ShellMenuEntry>? Children)
{
    public static ShellMenuEntry Separator { get; } = new(-1, string.Empty, null, null, true, true, null);
}

/// <summary>
/// A live shell context menu. Entries stay invokable until the menu is disposed (or replaced by the next one).
/// </summary>
public interface IShellMenu : IDisposable
{
    IReadOnlyList<ShellMenuEntry> Entries { get; }

    Task InvokeAsync(ShellMenuEntry entry);
}

/// <summary>An application that can open a file type (Windows "Open with" list).</summary>
public sealed record OpenWithApp(string Name, IconBitmap? Icon, string? IconImagePath, int Index, bool IsRecommended);

/// <summary>A code editor found on this PC (VS Code, Cursor, …) that can open folders as well as files.</summary>
public sealed record CodeEditor(string Id, string Name, string ExecutablePath, IconBitmap? Icon);

public interface IShellMenuService
{
    /// <summary>Shell extension items for local <paramref name="paths"/> (all in one folder). Items the app already offers are left out.</summary>
    Task<IShellMenu?> GetItemMenuAsync(IReadOnlyList<string> paths, bool extended);

    /// <summary>Shell extension items for the background of <paramref name="folder"/> ("New ▸", "Git Bash here", …).</summary>
    Task<IShellMenu?> GetBackgroundMenuAsync(string folder, bool extended);

    /// <summary>Apps registered for the type of <paramref name="path"/>; recommended ones first. Cached per extension.</summary>
    Task<IReadOnlyList<OpenWithApp>> GetOpenWithAppsAsync(string path);

    Task OpenWithAsync(OpenWithApp app, IReadOnlyList<string> paths);

    /// <summary>Code editors installed on this PC (detected once).</summary>
    Task<IReadOnlyList<CodeEditor>> GetCodeEditorsAsync();

    void OpenInEditor(CodeEditor editor, string path);
}
