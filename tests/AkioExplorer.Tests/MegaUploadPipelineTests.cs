using System.Collections.Concurrent;
using System.Net;
using AkioExplorer.CloudProviders.Mega;
using AkioExplorer.Core.Transfers;

namespace AkioExplorer.Tests;

public class MegaUploadPipelineTests
{
    private const string Url = "https://gfs.example.mega.co.nz/ul/abc";
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 36 chars = 27 bytes, like MEGA's completion handle

    [Fact]
    public void Sends_parts_in_parallel_and_returns_the_handle_on_the_last_one()
    {
        var server = new FakeUploadServer(total: 300, delay: TimeSpan.FromMilliseconds(150));
        var sink = new Sink();
        using var pipeline = new MegaUploadPipeline("f.bin", 300, sink, CancellationToken.None, connections: 3);
        using var http = new HttpClient(server);

        Assert.Equal("", pipeline.Post(http, new Uri(Url + "/0"), new MemoryStream(new byte[100])));
        Assert.Equal("", pipeline.Post(http, new Uri(Url + "/100"), new MemoryStream(new byte[100])));
        Assert.Equal(Handle, pipeline.Post(http, new Uri(Url + "/200"), new MemoryStream(new byte[100])));

        Assert.True(server.MaxConcurrent >= 2, $"max concurrent {server.MaxConcurrent}");
        Assert.Equal(300, sink.Sent);
        Assert.True(sink.ReadCountingOff);
        Assert.NotNull(sink.LastParts);
        Assert.Equal(300, sink.LastParts!.Segments.Sum(s => s.Position - s.Start));
    }

    [Fact]
    public void Retries_a_part_the_server_drops()
    {
        var server = new FakeUploadServer(total: 200, delay: TimeSpan.Zero) { FailFirstAt = 100 };
        var sink = new Sink();
        using var pipeline = new MegaUploadPipeline("f.bin", 200, sink, CancellationToken.None, retryDelay: TimeSpan.FromMilliseconds(1));
        using var http = new HttpClient(server);

        pipeline.Post(http, new Uri(Url + "/0"), new MemoryStream(new byte[100]));
        Assert.Equal(Handle, pipeline.Post(http, new Uri(Url + "/100"), new MemoryStream(new byte[100])));
        Assert.Equal(200, sink.Sent); // the resent part was counted once
    }

    [Fact]
    public void Stops_on_a_permanent_error_and_keeps_the_reason()
    {
        var server = new FakeUploadServer(total: 200, delay: TimeSpan.Zero) { Answer = "-17" }; // over quota
        using var pipeline = new MegaUploadPipeline("f.bin", 200, null, CancellationToken.None);
        using var http = new HttpClient(server);

        pipeline.Post(http, new Uri(Url + "/0"), new MemoryStream(new byte[100]));
        Assert.ThrowsAny<Exception>(() => pipeline.Post(http, new Uri(Url + "/100"), new MemoryStream(new byte[100])));
        Assert.True(pipeline.Token.IsCancellationRequested);
        var failure = Assert.IsType<MegaApiCodeException>(pipeline.Failure);
        Assert.Equal(CG.Web.MegaApiClient.ApiResultCode.QuotaExceeded, failure.Code);
    }

    [Fact]
    public void Transfer_stream_stops_counting_reads_in_send_mode()
    {
        long total = 0;
        var progress = new Progress(b => total += b);
        using var stream = new TransferStream(new MemoryStream(new byte[100]), progress, CancellationToken.None);
        stream.CountOnSend();
        stream.ReadExactly(new byte[100]);
        stream.Position = 0;
        Assert.Equal(0, total);
        stream.ReportSent(40);
        Assert.Equal(40, total);
    }

    private sealed class Progress(Action<long> report) : ITransferProgress
    {
        public void Report(long bytes) => report(bytes);
        public ValueTask WaitIfPausedAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public void WaitIfPaused(CancellationToken ct) { }
    }

    private sealed class Sink : IUploadProgressSink
    {
        private long _sent;
        public long Sent => Interlocked.Read(ref _sent);
        public bool ReadCountingOff;
        public PartedFileProgress? LastParts;
        public void CountOnSend() => ReadCountingOff = true;
        public void ReportSent(long bytes) => Interlocked.Add(ref _sent, bytes);
        public void ReportParts(PartedFileProgress? parts)
        {
            if (parts is not null) LastParts = parts;
        }
    }

    /// <summary>MEGA-like upload endpoint: answers "" per part and the handle once every byte has arrived.</summary>
    private sealed class FakeUploadServer(long total, TimeSpan delay) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<long, bool> _received = new();
        private int _concurrent;
        private int _failed;
        public int MaxConcurrent;
        public long FailFirstAt { get; init; } = -1;
        public string? Answer { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                await Task.Delay(delay, cancellationToken);
                long offset = long.Parse(request.RequestUri!.AbsolutePath.Split('/')[^1]);
                if (offset == FailFirstAt && Interlocked.Exchange(ref _failed, 1) == 0)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (Answer is not null) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
                _received[offset] = true;
                long got = _received.Count * (long)body.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(got >= total ? Handle : "") };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
