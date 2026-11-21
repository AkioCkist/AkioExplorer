namespace AkioExplorer.CloudProviders.Http;

/// <summary>Read stream that also disposes the object owning the connection (HTTP response, SDK response).</summary>
internal sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Reads exactly <paramref name="buffer"/>.Length bytes unless the stream ends first.</summary>
internal static class StreamExtensions
{
    public static async Task<int> ReadFullAsync(this Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public static string GuessMimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };
}
