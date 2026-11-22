using System.Net;
using System.Security.Cryptography;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Mega;

/// <summary>
/// Reads a MEGA file over several connections at once. MEGA serves byte ranges ("url/start-end") and encrypts with
/// AES-CTR, which can be decrypted from any offset, so the next few parts download in parallel while the reader
/// consumes the current one. The bytes come out in order and are checked against the file's MAC at the end
/// (MEGA's CBC-MAC per chunk), so a corrupted download fails instead of leaving a damaged file.
/// </summary>
internal sealed class MegaDownloadStream : Stream
{
    public const int PartSize = 4 << 20;
    public const int DefaultConnections = 4;
    private const int MaxAttempts = 5;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _refreshUrl;
    private readonly byte[] _key;
    private readonly byte[] _nonce;
    private readonly byte[] _expectedMac;
    private readonly MegaMac _mac;
    private readonly int _connections;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<long, Task<byte[]>> _parts = new();
    private readonly SemaphoreSlim _urlGate = new(1, 1);
    private string _url;
    private long _nextToStart;
    private byte[]? _current;
    private int _currentOffset;
    private long _position;
    private bool _verified;

    public MegaDownloadStream(HttpClient http, string url, long length, byte[] nodeKey, Func<CancellationToken, Task<string>> refreshUrl,
        CancellationToken cancellationToken, int connections = DefaultConnections)
    {
        _http = http;
        _url = url;
        Length = length;
        (_key, _nonce) = MegaCrypto.ContentKey(nodeKey);
        _expectedMac = nodeKey[24..32];
        _mac = new MegaMac(_key, _nonce);
        _refreshUrl = refreshUrl;
        _connections = Math.Max(1, connections);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public override long Length { get; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_position >= Length)
        {
            Verify();
            return 0;
        }

        if (_current is null || _currentOffset >= _current.Length)
        {
            long index = _position / PartSize;
            StartParts(index);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            _current = await _parts[index].WaitAsync(linked.Token).ConfigureAwait(false);
            _parts.Remove(index);
            _currentOffset = 0;
            _mac.Append(_current);
            StartParts(index + 1); // keep the line busy
        }

        int count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        _position += count;
        if (_position >= Length) Verify();
        return count;
    }

    private void StartParts(long from)
    {
        long last = (Length - 1) / PartSize;
        _nextToStart = Math.Max(_nextToStart, from);
        while (_nextToStart <= last && _nextToStart < from + _connections)
        {
            long index = _nextToStart++;
            _parts[index] = Task.Run(() => FetchAsync(index));
        }
    }

    private async Task<byte[]> FetchAsync(long index)
    {
        long start = index * PartSize;
        long end = Math.Min(Length, start + PartSize) - 1;
        for (int attempt = 1; ; attempt++)
        {
            string url = _url;
            try
            {
                using var response = await _http.GetAsync($"{url}/{start}-{end}", HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false);
                if (response.StatusCode == (HttpStatusCode)509)
                    throw new StorageException(StorageErrorKind.Throttled,
                        "MEGA's transfer quota is used up for now. MEGA lifts it after a few hours (or with a paid plan).");
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone && attempt < MaxAttempts)
                {
                    await RefreshAsync(url).ConfigureAwait(false); // download address expired
                    continue;
                }
                response.EnsureSuccessStatusCode();
                byte[] data = await response.Content.ReadAsByteArrayAsync(_cts.Token).ConfigureAwait(false);
                if (data.Length != end - start + 1) throw new IOException("MEGA sent an incomplete part.");
                MegaCrypto.DecryptContent(data, start, _key, _nonce);
                return data;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < MaxAttempts && !_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshAsync(string stale)
    {
        await _urlGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (_url == stale) _url = await _refreshUrl(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _urlGate.Release();
        }
    }

    private void Verify()
    {
        if (_verified || Length == 0) return;
        _verified = true;
        if (!_mac.Finish().AsSpan().SequenceEqual(_expectedMac))
            throw new StorageException(StorageErrorKind.Unknown, "The file downloaded from MEGA doesn't match its checksum (damaged in transit). Try again.");
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            foreach (var part in _parts.Values) part.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe
            _mac.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// MEGA's file MAC: the file is cut into chunks (128 KB, 256 KB … 1 MB, then 1 MB each); each chunk gets a CBC-MAC
/// starting from nonce‖nonce; the chunk MACs are chained with AES; the 16-byte result folds to 8 bytes.
/// Fed sequentially with any piece sizes.
/// </summary>
internal sealed class MegaMac : IDisposable
{
    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _start = new byte[16];
    private readonly byte[] _chunkMac = new byte[16];
    private readonly byte[] _fileMac = new byte[16];
    private readonly byte[] _pending = new byte[16];
    private int _pendingCount;
    private long _position;
    private long _chunkStart;
    private long _chunkEnd;
    private int _chunkIndex;

    public MegaMac(byte[] key, byte[] nonce)
    {
        _aes.Key = key;
        nonce.CopyTo(_start, 0);
        nonce.CopyTo(_start, 8);
        _start.CopyTo(_chunkMac, 0);
        _chunkEnd = NextChunkEnd(0, 0);
    }

    private static long NextChunkEnd(long start, int index) => start + (index < 8 ? (index + 1) * 131072L : 1048576L);

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int take = (int)Math.Min(data.Length, _chunkEnd - _position);
            Feed(data[..take]);
            _position += take;
            data = data[take..];
            if (_position == _chunkEnd) FinishChunk();
        }
    }

    private void Feed(ReadOnlySpan<byte> data)
    {
        if (_pendingCount > 0)
        {
            int fill = Math.Min(16 - _pendingCount, data.Length);
            data[..fill].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += fill;
            data = data[fill..];
            if (_pendingCount < 16) return;
            Cbc(_pending);
            _pendingCount = 0;
        }
        int whole = data.Length / 16 * 16;
        if (whole > 0) Cbc(data[..whole]);
        data[whole..].CopyTo(_pending);
        _pendingCount = data.Length - whole;
    }

    private void Cbc(ReadOnlySpan<byte> blocks)
    {
        byte[] output = new byte[blocks.Length];
        _aes.EncryptCbc(blocks, _chunkMac, output, PaddingMode.None);
        output.AsSpan(output.Length - 16).CopyTo(_chunkMac);
    }

    private void FinishChunk()
    {
        if (_pendingCount > 0)
        {
            _pending.AsSpan(_pendingCount).Clear();
            Cbc(_pending);
            _pendingCount = 0;
        }
        for (int i = 0; i < 16; i++) _fileMac[i] ^= _chunkMac[i];
        _aes.EncryptEcb(_fileMac, _fileMac, PaddingMode.None);
        _start.CopyTo(_chunkMac, 0);
        _chunkStart = _chunkEnd;
        _chunkEnd = NextChunkEnd(_chunkStart, ++_chunkIndex);
    }

    /// <summary>The 8-byte meta MAC (stored in the node key, bytes 24..32).</summary>
    public byte[] Finish()
    {
        if (_position > _chunkStart) FinishChunk();
        var meta = new byte[8];
        for (int i = 0; i < 4; i++)
        {
            meta[i] = (byte)(_fileMac[i] ^ _fileMac[i + 4]);
            meta[i + 4] = (byte)(_fileMac[i + 8] ^ _fileMac[i + 12]);
        }
        return meta;
    }

    public void Dispose() => _aes.Dispose();
}
