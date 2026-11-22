using System.Net;
using System.Net.Http.Headers;
using AkioExplorer.Core.Transfers;
using AkioExplorer.Core.Transfers.Segmented;
using CG.Web.MegaApiClient;

namespace AkioExplorer.CloudProviders.Mega;

/// <summary>
/// Sends one MEGA upload over several connections. MegaApiClient encrypts the file and posts it part by part to
/// "uploadUrl/offset", waiting for each answer before it reads the next part, so a far-away MEGA server leaves the line
/// idle most of the time. MEGA accepts parts in any order and answers the request that completes the file with the
/// completion handle, so <see cref="MegaWebClient"/> hands each part to this pipeline, which sends it in the background
/// (a few at once, each retried on its own) and lets MegaApiClient go on encrypting the next one. Only the last part
/// waits for all of them and returns the handle.
/// <para>MegaWebClient finds the pipeline through <see cref="Current"/>, which <see cref="MegaProvider"/> sets around
/// the upload call (the value flows into MegaApiClient's worker task).</para>
/// </summary>
internal sealed class MegaUploadPipeline : IDisposable
{
    /// <summary>Bytes MegaApiClient packs into one request (MEGA's own chunks are 128 KB … 1 MB).</summary>
    public const int PackSize = 4 << 20;

    /// <summary>Parallel requests per file. MEGA's apps use about as many; more risks "too many connections".</summary>
    public const int DefaultConnections = 4;

    internal static readonly AsyncLocal<MegaUploadPipeline?> Current = new();

    private const int MaxAttempts = 5;
    private const int Slice = 64 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly MediaTypeHeaderValue OctetStream = new("application/octet-stream");

    private readonly string _name;
    private readonly long _length;
    private readonly IUploadProgressSink? _sink;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _slots;
    private readonly List<Part> _parts = new();
    private readonly List<Task> _tasks = new();
    private readonly object _gate = new();
    private readonly TimeSpan _retryDelay;
    private string? _baseUrl;
    private volatile string? _handle;
    private volatile Exception? _failure;
    private long _lastReport;
    private bool _disposed;

    public MegaUploadPipeline(string name, long length, IUploadProgressSink? sink, CancellationToken cancellationToken,
        int connections = DefaultConnections, TimeSpan? retryDelay = null)
    {
        _name = name;
        _length = length;
        _sink = sink;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _slots = new SemaphoreSlim(Math.Max(1, connections));
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _sink?.CountOnSend(); // reads run ahead of the network by a few parts: count what MEGA received instead
    }

    /// <summary>Cancelled by the caller or when a part fails for good (stops MegaApiClient from reading on).</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Why the upload stopped, when a part failed for good.</summary>
    public Exception? Failure => _failure;

    /// <summary>Called by MegaApiClient (through MegaWebClient) for each part, in file order, on its worker thread.</summary>
    public string Post(HttpClient http, Uri uri, Stream data)
    {
        string url = uri.OriginalString;
        int slash = url.LastIndexOf('/');
        if (slash < 0 || !long.TryParse(url.AsSpan(slash + 1), out long offset))
            throw new InvalidOperationException("Unexpected MEGA upload address.");
        lock (_gate)
        {
            _baseUrl ??= url[..slash];
            if (_baseUrl != url[..slash]) throw new IOException("MEGA restarted the upload.", _failure);
        }
        ThrowIfFailed();

        byte[] bytes = new byte[data.Length - data.Position];
        data.ReadExactly(bytes);

        _slots.Wait(_cts.Token); // at most N parts in memory and on the wire
        var part = new Part(offset, offset + bytes.Length);
        lock (_gate)
        {
            _parts.Add(part);
            _tasks.Add(Task.Run(() => SendAsync(http, uri, bytes, part)));
        }
        ReportParts(force: true);

        if (offset + bytes.Length < _length) return string.Empty; // MegaApiClient reads "" as "part accepted, go on"

        Task[] all;
        lock (_gate) all = _tasks.ToArray();
        Task.WaitAll(all, _cts.Token); // SendAsync never throws; a failure cancels the token
        ThrowIfFailed();
        return _handle ?? throw new IOException("MEGA received the whole file but didn't confirm the upload.");
    }

    private void ThrowIfFailed()
    {
        if (_failure is { } failure) throw new IOException("MEGA upload failed.", failure);
    }

    private async Task SendAsync(HttpClient http, Uri uri, byte[] bytes, Part part)
    {
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                Exception error;
                bool transient;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    stall.CancelAfter(StallTimeout);
                    try
                    {
                        part.Active = true;
                        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new PartContent(this, part, bytes, stall) };
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, stall.Token).ConfigureAwait(false);
                        string body = (await response.Content.ReadAsStringAsync(stall.Token).ConfigureAwait(false)).Trim();
                        bool code = long.TryParse(body, out long result) && result < 0;
                        if (response.IsSuccessStatusCode && !code)
                        {
                            if (body.Length > 0) _handle = body; // only the request that completes the file gets it
                            part.Done = true;
                            return;
                        }
                        (error, transient) = code
                            ? ((Exception)new MegaApiCodeException((ApiResultCode)result), result is -3 or -4 or -6 or -18 or -19)
                            : (new HttpRequestException($"MEGA's upload server answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode),
                               (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        (error, transient) = (new TimeoutException("MEGA's upload server stopped responding."), true);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        (error, transient) = (ex, true);
                    }
                    finally
                    {
                        part.Active = false;
                    }
                }

                if (!transient || attempt >= MaxAttempts) throw error;
                Rewind(part);
                ReportParts(force: true);
                await Task.Delay(_retryDelay * (1 << (attempt - 1)), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                _failure ??= ex;
                _cts.Cancel(); // the other parts and MegaApiClient's reader stop too
            }
        }
        finally
        {
            _slots.Release();
            ReportParts(force: true);
        }
    }

    private void Advance(Part part, int bytes)
    {
        Interlocked.Add(ref part.Position, bytes);
        _sink?.ReportSent(bytes);
        ReportParts(force: false);
    }

    private void Rewind(Part part)
    {
        long sent = Interlocked.Exchange(ref part.Position, part.Start) - part.Start;
        if (sent > 0) _sink?.ReportSent(-sent);
    }

    private void ReportParts(bool force)
    {
        if (_sink is null) return;
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastReport) < 150) return;
        Interlocked.Exchange(ref _lastReport, now);

        lock (_gate)
        {
            if (_disposed) return;
            var segments = new List<SegmentSnapshot>(_parts.Count);
            int active = 0;
            foreach (var part in _parts) // in file order: MegaApiClient posts them that way
            {
                if (part.Active) active++;
                long position = part.Done ? part.End : Interlocked.Read(ref part.Position);
                // Finished neighbours become one bar, so a big file doesn't turn into hundreds of slivers.
                if (part.Done && segments.Count > 0 && segments[^1] is { Active: false } last && last.Position == last.End && last.End == part.Start)
                    segments[^1] = last with { End = part.End, Position = part.End };
                else
                    segments.Add(new SegmentSnapshot(part.Start, part.End, position, part.Active));
            }
            _sink.ReportParts(new PartedFileProgress(_name, _length, active, segments));
        }
    }

    public void Dispose()
    {
        Task[] pending;
        lock (_gate) pending = _tasks.ToArray();
        if (pending.Any(t => !t.IsCompleted))
        {
            _cts.Cancel();
            Task.WaitAll(pending, TimeSpan.FromSeconds(10)); // cancelled requests end at once; never throws (see SendAsync)
        }
        lock (_gate) _disposed = true;
        _sink?.ReportParts(null);
        if (pending.All(t => t.IsCompleted)) _cts.Dispose();
    }

    private sealed class Part(long start, long end)
    {
        public readonly long Start = start;
        public readonly long End = end;
        public long Position = start;
        public volatile bool Active;
        public volatile bool Done;
    }

    /// <summary>Request body that reports progress as it goes out and keeps the stall timer from firing while it does.</summary>
    private sealed class PartContent : HttpContent
    {
        private readonly MegaUploadPipeline _owner;
        private readonly Part _part;
        private readonly byte[] _bytes;
        private readonly CancellationTokenSource _stall;

        public PartContent(MegaUploadPipeline owner, Part part, byte[] bytes, CancellationTokenSource stall)
        {
            _owner = owner;
            _part = part;
            _bytes = bytes;
            _stall = stall;
            Headers.ContentType = OctetStream;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _owner.Rewind(_part); // a resend starts this part over
            for (int offset = 0; offset < _bytes.Length; offset += Slice)
            {
                int count = Math.Min(Slice, _bytes.Length - offset);
                await stream.WriteAsync(_bytes.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                _stall.CancelAfter(StallTimeout);
                _owner.Advance(_part, count);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }
}

