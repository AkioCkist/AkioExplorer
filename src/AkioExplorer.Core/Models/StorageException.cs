namespace AkioExplorer.Core.Models;

public enum StorageErrorKind
{
    Unknown,
    NotFound,
    AccessDenied,
    AlreadyExists,
    InvalidName,
    InUse,
    Unavailable,
    NotSupported,
}

/// <summary>
/// Error raised by providers. <see cref="Exception.Message"/> is written for end users: plain language,
/// names the item, and suggests what to do (Nielsen heuristic 9).
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(StorageErrorKind kind, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
    }

    public StorageErrorKind Kind { get; }
}
