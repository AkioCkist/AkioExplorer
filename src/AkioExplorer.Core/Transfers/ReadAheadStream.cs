using System.Threading.Channels;

namespace AkioExplorer.Core.Transfers;

/// <summary>
/// Forward-only stream that keeps reading the source in the background while the consumer is busy. For
/// cloud → cloud copies this overlaps the download of the next blocks with the upload of the current one, instead
/// of the two taking turns (an upload chunk waits for the service's confirmation; meanwhile the download idles).
/// Memory is bounded: at most <c>blocks × blockSize</c> bytes are buffered.
/// </summary>
public sealed class ReadAheadStream : Stream
{
    private readonly Stream _source;
    private readonly Channel<(byte[] Buffer, int Count)> _blocks;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private readonly int _blockSize;
    private byte[]? _current;
    private int _currentCount;
    private int _currentOffset;
    private bool _completed;
    private long _position;
    private bool _disposed;

    public ReadAheadStream(Stream source, int blockSize = 4 << 20, int blocks = 4)
    {
        _source = source;
        _blockSize = blockSize;
        _blocks = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(blocks) { SingleReader = true, SingleWriter = true });
        _pump = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var buffer = new byte[_blockSize];
                int filled = 0;
                while (filled < buffer.Length)
                {
                    int read = await _source.ReadAsync(buffer.AsMemory(filled), _stop.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    filled += read;
                }
                if (filled > 0) await _blocks.Writer.WriteAsync((buffer, filled), _stop.Token).ConfigureAwait(false);
                if (filled < buffer.Length) break;
            }
            _blocks.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _blocks.Writer.TryComplete(ex); // surfaces on the consumer's next read
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0) return 0;
        while (_current is null || _currentOffset >= _currentCount)
        {
            if (_completed) return 0;
            if (!await _blocks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _completed = true;
                await _blocks.Reader.Completion.ConfigureAwait(false); // rethrows a source failure
                return 0;
            }
            if (_blocks.Reader.TryRead(out var block))
            {
                (_current, _currentCount, _currentOffset) = (block.Buffer, block.Count, 0);
            }
        }

        int n = Math.Min(destination.Length, _currentCount - _currentOffset);
        _current.AsMemory(_currentOffset, n).CopyTo(destination);
        _currentOffset += n;
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { await _pump.ConfigureAwait(false); } catch { }
        await _source.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _stop.Cancel();
            try { _pump.GetAwaiter().GetResult(); } catch { }
            _source.Dispose();
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }
}
