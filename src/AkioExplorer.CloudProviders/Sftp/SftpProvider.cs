using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace AkioExplorer.CloudProviders.Sftp;

public sealed record SftpOptions(string Host, int Port, string User, string? Password, string? KeyFile, string? KeyPassphrase, string? HostKeyFingerprint);

/// <summary>
/// SFTP (SSH file transfer) through SSH.NET. One SSH session carries many parallel SFTP requests. Downloads and
/// uploads use SSH.NET's pipelined transfer (many reads/writes in flight) instead of one request per buffer, which
/// matters a lot on high-latency links. The server's host key is pinned on first connect (trust on first use).
/// </summary>
public sealed class SftpProvider : RemotePathProvider, IDisposable
{
    private readonly SftpOptions _options;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private SftpClient? _client;

    public SftpProvider(CloudAccount account, SftpOptions options)
        : base(account, CloudProviderType.Sftp, "SFTP")
    {
        _options = options;
    }

    /// <summary>Fingerprint (SHA-256) seen on the last connection; the connector stores it when the account is added.</summary>
    public string? SeenFingerprint { get; private set; }

    public override StorageCapabilities Capabilities => base.Capabilities;

    private static string Remote(string path) => Normalize(path) is { Length: > 0 } p ? p : "/";

    private async Task<SftpClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true } ready) return ready;
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true } again) return again;
            _client?.Dispose();

            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(_options.KeyFile))
            {
                if (!File.Exists(_options.KeyFile))
                    throw new StorageException(StorageErrorKind.AuthenticationRequired, $"The private key file \"{_options.KeyFile}\" is missing. Reconnect the account to continue.");
                var key = string.IsNullOrEmpty(_options.KeyPassphrase) ? new PrivateKeyFile(_options.KeyFile) : new PrivateKeyFile(_options.KeyFile, _options.KeyPassphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(_options.User, key));
            }
            if (!string.IsNullOrEmpty(_options.Password))
            {
                methods.Add(new PasswordAuthenticationMethod(_options.User, _options.Password));
                var interactive = new KeyboardInteractiveAuthenticationMethod(_options.User);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) prompt.Response = _options.Password;
                };
                methods.Add(interactive);
            }
            if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(_options.User));

            var info = new ConnectionInfo(_options.Host, _options.Port, _options.User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
            var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
            client.HostKeyReceived += (_, e) =>
            {
                SeenFingerprint = e.FingerPrintSHA256;
                // Pinned key must match; the first connection (no pin yet) is accepted and the connector stores it.
                e.CanTrust = string.IsNullOrEmpty(_options.HostKeyFingerprint) || e.FingerPrintSHA256 == _options.HostKeyFingerprint;
            };
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            _client = client;
            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Map(ex, string.Empty);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<T> RunAsync<T>(string path, Func<SftpClient, Task<T>> action, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageException)
        {
            throw Map(ex, path);
        }
    }

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var page = new List<StorageItem>(256);
        var entries = client.ListDirectoryAsync(Remote(path), cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                try
                {
                    if (!await entries.MoveNextAsync().ConfigureAwait(false)) break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw Map(ex, path);
                }
                var entry = entries.Current;
                if (entry.Name is "." or "..") continue;
                page.Add(await ToItemAsync(client, entry, CombinePath(path, entry.Name), cancellationToken).ConfigureAwait(false));
                if (page.Count >= 500)
                {
                    yield return page;
                    page = new List<StorageItem>(500);
                }
            }
        }
        finally
        {
            await entries.DisposeAsync().ConfigureAwait(false);
        }
        yield return page;
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        try
        {
            return await RunAsync(path, async c =>
            {
                var file = await c.GetAsync(path, cancellationToken).ConfigureAwait(false);
                return await ToItemAsync(c, file, path, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            return null;
        }
    }

    private static async Task<StorageItem> ToItemAsync(SftpClient client, ISftpFile entry, string path, CancellationToken cancellationToken)
    {
        bool folder = entry.IsDirectory;
        long size = entry.Length;
        if (entry.IsSymbolicLink)
        {
            try
            {
                var target = await client.GetAttributesAsync(entry.FullName, cancellationToken).ConfigureAwait(false); // follows the link
                folder = target.IsDirectory;
                size = target.Size;
            }
            catch (SftpPathNotFoundException)
            {
            }
        }
        return new StorageItem
        {
            Name = entry.Name,
            Path = path,
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = folder ? null : size,
            Modified = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime(),
            Attributes = (entry.Name.StartsWith('.') ? StorageItemAttributes.Hidden : 0) | (entry.IsSymbolicLink ? StorageItemAttributes.ReparsePoint : 0),
        };
    }

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        await RunAsync(path, async c =>
        {
            if (await c.ExistsAsync(path, cancellationToken).ConfigureAwait(false)) throw Exists(path);
            await c.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
        return new StorageItem { Name = name, Path = path, Kind = StorageItemKind.Folder, Modified = DateTimeOffset.Now };
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            string path = Normalize(raw);
            if (path.Length == 0) throw RootChange("deleted");
            await RunAsync(path, async c =>
            {
                await DeleteTreeAsync(c, path, cancellationToken).ConfigureAwait(false);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteTreeAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        var attributes = await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!attributes.IsDirectory || attributes.IsSymbolicLink)
        {
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }
        var children = new List<ISftpFile>();
        await foreach (var entry in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            if (entry.Name is not "." and not "..") children.Add(entry);
        foreach (var child in children)
        {
            if (child.IsDirectory && !child.IsSymbolicLink) await DeleteTreeAsync(client, child.FullName, cancellationToken).ConfigureAwait(false);
            else await client.DeleteFileAsync(child.FullName, cancellationToken).ConfigureAwait(false);
        }
        await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string from = Normalize(source.Path), to = CombinePath(parentPath, name);
        await RunAsync(to, async c =>
        {
            if (await c.ExistsAsync(to, cancellationToken).ConfigureAwait(false)) throw Exists(to);
            await c.RenameFileAsync(from, to, cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
        return source with { Name = name, Path = to };
    }

    // ------------------------------------------------------------------ content

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        if (!await client.ExistsAsync(path, cancellationToken).ConfigureAwait(false)) throw NotFound(path);

        // Pipelined download into a pipe; the caller reads the other end at its own pace (back-pressure via the pipe).
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 8 << 20, resumeWriterThreshold: 4 << 20));
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writerStream = pipe.Writer.AsStream();
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await client.DownloadFileAsync(path, writerStream, stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex is OperationCanceledException ? ex : Map(ex, path);
            }
            await pipe.Writer.CompleteAsync(failure).ConfigureAwait(false);
        }, CancellationToken.None);

        return new LeasedPipeStream(pipe.Reader.AsStream(), () =>
        {
            stop.Cancel();
            stop.Dispose();
        });
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        bool existed = await client.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        if (existed && !overwrite) throw Exists(path);
        try
        {
            await client.UploadFileAsync(content, path, true, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!existed)
            {
                try { await client.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            if (ex is OperationCanceledException or StorageException) throw;
            throw Map(ex, path);
        }
        return await GetItemAsync(path, cancellationToken).ConfigureAwait(false)
               ?? new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = length, Modified = DateTimeOffset.Now };
    }

    public override async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.GetStatusAsync("/", cancellationToken).ConfigureAwait(false); // statvfs@openssh.com
            ulong total = status.TotalBlocks * status.BlockSize, free = status.AvailableBlocks * status.BlockSize;
            return total == 0 ? null : new StorageQuota((long)(total - free), (long)total);
        }
        catch (Exception ex) when (ex is NotSupportedException or SshException or StorageException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ errors

    private StorageException Map(Exception ex, string path) => ex switch
    {
        StorageException storage => storage,
        SshAuthenticationException => SignInFailed(),
        SshConnectionException { Message: var m } when m.Contains("host key", StringComparison.OrdinalIgnoreCase) => HostKeyChanged(),
        SshConnectionException when SeenFingerprint is not null && _options.HostKeyFingerprint is not null && SeenFingerprint != _options.HostKeyFingerprint => HostKeyChanged(),
        SftpPathNotFoundException => NotFound(path),
        SftpPermissionDeniedException => Denied(path),
        SshOperationTimeoutException or SshConnectionException or System.Net.Sockets.SocketException or IOException => Unreachable(ex),
        SshException ssh => new StorageException(StorageErrorKind.Unknown, $"SFTP error: {ssh.Message}", ssh),
        _ => new StorageException(StorageErrorKind.Unknown, $"SFTP error: {ex.Message}", ex),
    };

    private StorageException HostKeyChanged() => new(StorageErrorKind.AccessDenied,
        $"The server's identity (host key) for \"{DisplayName}\" has changed since it was added. This can mean the server was reinstalled – or that " +
        "someone is intercepting the connection. If you expected the change, reconnect the account to trust the new key.");

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}

/// <summary>Reader end of a pipe that stops the producer when disposed.</summary>
internal sealed class LeasedPipeStream(Stream inner, Action onClose) : Stream
{
    private int _closed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            onClose();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
