namespace AkioExplorer.Core.Abstractions;

/// <summary>Archiving apps the app knows how to drive from the command line.</summary>
public enum ArchiverKind
{
    SevenZip,
    NanaZip,
    WinRar,
    Bandizip,
    PeaZip,
}

/// <summary>An archiving app installed on this PC (7-Zip, WinRAR…). <see cref="ExecutablePath"/> is the GUI program.</summary>
public sealed record ArchiverApp(ArchiverKind Kind, string Name, string ExecutablePath, IconBitmap? Icon);

/// <summary>One program start: the app shows its own progress, password and overwrite prompts.</summary>
public sealed record ArchiverInvocation(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

public interface IArchiverService
{
    /// <summary>Archivers installed on this PC (detected once, in a fixed order: 7-Zip, NanaZip, WinRAR, Bandizip, PeaZip).</summary>
    Task<IReadOnlyList<ArchiverApp>> GetArchiversAsync();

    /// <summary>Starts the archiver and returns once it has started (not when the job ends).</summary>
    void Start(ArchiverInvocation invocation);
}
