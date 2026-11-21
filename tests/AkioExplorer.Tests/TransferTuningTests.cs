using AkioExplorer.Core.Transfers;

namespace AkioExplorer.Tests;

public sealed class TransferTuningTests
{
    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(100L * 1024 * 1024)]
    [InlineData(5L * 1024 * 1024 * 1024)]
    [InlineData(-1L)]
    public void Chunk_sizes_respect_service_granularity_and_limits(long length)
    {
        int google = UploadChunking.Google(length);
        int onedrive = UploadChunking.OneDrive(length);
        int dropbox = UploadChunking.Dropbox(length);

        Assert.Equal(0, google % UploadChunking.GoogleGranularity);
        Assert.Equal(0, onedrive % UploadChunking.OneDriveGranularity);
        Assert.Equal(0, dropbox % UploadChunking.DropboxGranularity);
        Assert.InRange(onedrive, 10 << 20, 60 << 20); // Graph: at most 60 MiB per request
        Assert.InRange(google, 8 << 20, 32 << 20);
        Assert.InRange(dropbox, 8 << 20, 32 << 20);
    }

    [Fact]
    public void Big_files_get_bigger_chunks()
    {
        Assert.Equal(8 << 20, UploadChunking.Google(20L << 20));
        Assert.Equal(32 << 20, UploadChunking.Google(2L << 30));
        Assert.True(UploadChunking.OneDrive(2L << 30) > UploadChunking.OneDrive(20L << 20));
    }

    [Fact]
    public async Task Read_ahead_returns_the_same_bytes()
    {
        var data = TestFileServer.RandomData(5 * 1024 * 1024 + 123, seed: 7);
        await using var stream = new ReadAheadStream(new MemoryStream(data), blockSize: 64 * 1024, blocks: 3);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, 10_000);
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public async Task Read_ahead_surfaces_source_errors()
    {
        await using var stream = new ReadAheadStream(new FailingStream(), blockSize: 1024, blocks: 2);
        var buffer = new byte[4096];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await stream.ReadAsync(buffer) > 0) { }
        });
    }

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ++_reads > 3 ? throw new IOException("connection reset") : Math.Min(count, 100);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
