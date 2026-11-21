namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Read-only wrapper that reports bytes as they are consumed and pauses reading while the operation is paused.
/// Wrapping the source stream makes pause/progress work for every executor, including SDK uploads that pull the
/// stream themselves.
/// </summary>
public sealed class TransferStream : Stream
{
    private readonly Stream _inner;
    private readonly ITransferProgress _progress;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _leaveOpen;
    private long _position;

    public TransferStream(Stream inner, ITransferProgress progress, CancellationToken cancellationToken, bool leaveOpen = false)
    {
        _inner = inner;
        _progress = progress;
        _cancellationToken = cancellationToken;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.CanSeek ? _inner.Position : _position;
        set
        {
            // SDKs rewind to resend a failed chunk: give back the bytes that will be read again.
            long delta = value - Position;
            _inner.Position = value;
            _position = value;
            if (delta < 0) _progress.Report(delta);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _progress.WaitIfPaused(_cancellationToken);
        _cancellationToken.ThrowIfCancellationRequested();
        int read = _inner.Read(buffer, offset, count);
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _progress.WaitIfPausedAsync(_cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken);
        int read = await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        Advance(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        long before = Position;
        long after = _inner.Seek(offset, origin);
        _position = after;
        if (after < before) _progress.Report(after - before);
        return after;
    }

    private void Advance(int read)
    {
        if (read <= 0) return;
        _position += read;
        _progress.Report(read);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen) await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
