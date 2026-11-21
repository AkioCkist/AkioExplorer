using System.Text;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Core.Logic;

public enum PreviewKind
{
    /// <summary>Nothing to render: show the details card only.</summary>
    None,
    Image,
    Svg,
    Text,
    Pdf,
    Media,

    /// <summary>Windows shell thumbnail (Office documents, videos without a codec, etc.). Local files only.</summary>
    ShellThumbnail,
}

/// <summary>
/// Decides how the preview pane shows a file, and how many bytes it may fetch for it. Cloud files are downloaded
/// into memory, so their limits are lower than local files'. Keeps the pane fast: nothing big is read eagerly.
/// </summary>
public static class PreviewClassifier
{
    /// <summary>Text is shown up to this many bytes (enough for a screenful of any source file).</summary>
    public const int TextPrefixBytes = 64 * 1024;

    public const long LocalImageLimit = 64L * 1024 * 1024;
    public const long CloudImageLimit = 20L * 1024 * 1024;
    public const long LocalPdfLimit = 200L * 1024 * 1024;
    public const long CloudPdfLimit = 25L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic", ".avif",
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".3gp",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".config", ".env", ".properties", ".gitignore", ".gitattributes", ".editorconfig",
        ".cs", ".csproj", ".sln", ".props", ".targets", ".xaml", ".resx", ".vb", ".fs", ".c", ".h", ".cpp", ".hpp",
        ".cc", ".java", ".kt", ".kts", ".gradle", ".go", ".rs", ".py", ".rb", ".php", ".pl", ".lua", ".r", ".swift",
        ".dart", ".scala", ".sql", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".html", ".htm",
        ".css", ".scss", ".less", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".gd", ".tscn", ".tres",
        ".godot", ".shader", ".glsl", ".hlsl", ".srt", ".vtt", ".tex", ".bib", ".dockerfile", ".make", ".cmake",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "LICENSE", "README", "CHANGELOG", ".gitignore", ".editorconfig",
    };

    /// <summary>Extensions where the Windows shell thumbnail is a real page preview (Office, design files…).</summary>
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".psd", ".ai", ".blend",
        ".xps", ".oxps", ".3mf", ".stl", ".obj", ".fbx", ".glb", ".gltf", ".epub",
    };

    /// <param name="item">The selected item.</param>
    /// <param name="isLocal">True when the file is on this PC (shell features, random access, no download).</param>
    public static PreviewKind Classify(StorageItem item, bool isLocal)
    {
        if (item.Kind != StorageItemKind.File) return PreviewKind.None;
        string ext = item.Extension;
        long size = item.Size ?? 0;

        if (ImageExtensions.Contains(ext))
            return size <= (isLocal ? LocalImageLimit : CloudImageLimit) ? PreviewKind.Image : Fallback(isLocal);
        if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            return size <= CloudImageLimit ? PreviewKind.Svg : PreviewKind.None;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return size <= (isLocal ? LocalPdfLimit : CloudPdfLimit) ? PreviewKind.Pdf : Fallback(isLocal);
        if (MediaExtensions.Contains(ext))
            return isLocal ? PreviewKind.Media : PreviewKind.None; // streaming a cloud video would download all of it
        if (TextExtensions.Contains(ext) || TextFileNames.Contains(item.Name))
            return PreviewKind.Text;
        if (ThumbnailExtensions.Contains(ext))
            return Fallback(isLocal);

        // Unknown extension: local files get the shell thumbnail; the text sniffer decides for small ones.
        return isLocal ? PreviewKind.ShellThumbnail : PreviewKind.None;
    }

    /// <summary>
    /// Whether a local file's Windows thumbnail is a real picture (photo, video frame, album art, page) rather than
    /// its type icon – only those are requested for grid view, so folders of archives cost nothing.
    /// </summary>
    public static bool HasVisualThumbnail(StorageItem item)
    {
        if (item.Kind != StorageItemKind.File) return false;
        string ext = item.Extension;
        return ImageExtensions.Contains(ext) || MediaExtensions.Contains(ext) || ThumbnailExtensions.Contains(ext) ||
               ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extensions a cloud service can render a thumbnail for (photos and videos; documents vary by service).</summary>
    public static bool IsImageOrVideo(StorageItem item) =>
        item.Kind == StorageItemKind.File && (ImageExtensions.Contains(item.Extension) || MediaExtensions.Contains(item.Extension)) &&
        !IsAudio(item.Extension);

    private static bool IsAudio(string ext) => ext.ToLowerInvariant() is ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wma";

    /// <summary>Unknown local files this small are sniffed: shown as text when they decode cleanly.</summary>
    public static bool ShouldSniffText(StorageItem item) =>
        item.Kind == StorageItemKind.File && item.Size is > 0 and <= 4 * 1024 * 1024;

    private static PreviewKind Fallback(bool isLocal) => isLocal ? PreviewKind.ShellThumbnail : PreviewKind.None;

    /// <summary>Reads at most <paramref name="max"/> bytes from the start of <paramref name="stream"/>.</summary>
    public static async Task<byte[]> ReadPrefixAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        var buffer = new byte[max];
        int total = 0;
        while (total < max)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, max - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total == max ? buffer : buffer[..total];
    }

    /// <summary>
    /// Decodes bytes as text if they look like text: BOM-marked UTF-8/UTF-16, or valid UTF-8 without control
    /// characters other than whitespace. A multi-byte character cut by the prefix limit is tolerated.
    /// </summary>
    public static bool TryDecodeText(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Length == 0) return true;

        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Decode(new UTF8Encoding(false, false), bytes[3..], out text);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return Decode(Encoding.Unicode, bytes[2..], out text);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return Decode(Encoding.BigEndianUnicode, bytes[2..], out text);

        int controls = 0;
        foreach (byte b in bytes)
        {
            if (b == 0) return false;
            if (b < 0x20 && b is not (byte)'\n' and not (byte)'\r' and not (byte)'\t' and not 0x0C and not 0x1B) controls++;
        }
        if (controls > bytes.Length / 100 + 1) return false;

        // Drop a trailing partial UTF-8 sequence (the prefix limit can cut a character in half).
        int end = bytes.Length;
        int back = 0;
        while (back < 3 && end - back - 1 >= 0 && (bytes[end - back - 1] & 0xC0) == 0x80) back++;
        if (end - back - 1 >= 0 && bytes[end - back - 1] >= 0xC0)
        {
            int lead = bytes[end - back - 1];
            int needed = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : 1;
            if (back < needed) end -= back + 1;
        }

        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes[..end]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool Decode(Encoding encoding, ReadOnlySpan<byte> bytes, out string text)
    {
        text = encoding.GetString(bytes);
        return true;
    }
}
