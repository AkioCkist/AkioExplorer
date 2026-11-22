using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using FluentFTP;
using FluentFTP.Exceptions;

namespace AkioExplorer.CloudProviders.Ftp;

public enum FtpSecurity
{
    /// <summary>Plain FTP (password sent unencrypted).</summary>
    None,

    /// <summary>FTPS with AUTH TLS on the normal port (21).</summary>
    Explicit,

    /// <summary>FTPS with TLS from the first byte (port 990).</summary>
    Implicit,
}

public sealed record FtpOptions(string Host, int Port, string User, string Password, FtpSecurity Security, bool TrustAnyCertificate, bool Active);

/// <summary>
/// FTP / FTPS through FluentFTP. An FTP control connection does one thing at a time, so a small pool of
/// connections lets listing, thumbnails and several transfers run side by side.
/// </summary>
public sealed class FtpProvider : RemotePathProvider, IDisposable
{
    private const int MaxConnections = 4;

    private readonly FtpOptions _options;
    private readonly ConcurrentBag<AsyncFtpClient> _idle = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private volatile bool _disposed;

    public FtpProvider(CloudAccount account, FtpOptions options)
        : base(account, CloudProviderType.Ftp, options.Security == FtpSecurity.None ? "FTP" : "FTPS")
    {
        _options = options;
    }

    public override StorageCapabilities Capabilities => base.Capabilities;

    private static string Remote(string path) => Normalize(path) is { Length: > 0 } p ? p : "/";

    // ------------------------------------------------------------------ connection pool

    private sealed class Lease(FtpProvider owner, AsyncFtpClient client) : IAsyncDisposable
    {
        public AsyncFtpClient Client { get; } = client;

        public bool Broken { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Broken || owner._disposed || !Client.IsConnected)
            {
                try { await Client.Disconnect().ConfigureAwait(false); } catch { }
                Client.Dispose();
            }
            else
            {
                owner._idle.Add(Client);
            }
            owner._slots.Release();
        }
    }

    private async Task<Lease> RentAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_idle.TryTake(out var idle))
            {
                if (idle.IsConnected) return new Lease(this, idle);
                idle.Dispose();
            }

            var config = new FtpConfig
            {
                EncryptionMode = _options.Security switch
                {
                    FtpSecurity.Explicit => FtpEncryptionMode.Explicit,
                    FtpSecurity.Implicit => FtpEncryptionMode.Implicit,
                    _ => FtpEncryptionMode.None,
                },
                ValidateAnyCertificate = _options.TrustAnyCertificate,
                DataConnectionType = _options.Active ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive,
                ConnectTimeout = 15000,
                ReadTimeout = 30000,
                DataConnectionConnectTimeout = 15000,
                DataConnectionReadTimeout = 30000,
                SocketKeepAlive = true,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RetryAttempts = 1,
            };
            var client = new AsyncFtpClient(_options.Host, _options.User.Length > 0 ? _options.User : "anonymous", _options.Password, _options.Port, config);
            try
            {
                await client.Connect(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            return new Lease(this, client);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _slots.Release();
            throw Map(ex, string.Empty);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private async Task<T> RunAsync<T>(string path, Func<AsyncFtpClient, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(lease.Client).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageException)
        {
            lease.Broken = ex is not FtpCommandException;
            throw Map(ex, path);
        }
        catch
        {
            lease.Broken = true; // cancelled mid-command: the control connection state is unknown
            throw;
        }
    }

    private Task RunAsync(string path, Func<AsyncFtpClient, Task> action, CancellationToken cancellationToken) =>
        RunAsync(path, async c => { await action(c).ConfigureAwait(false); return 0; }, cancellationToken);

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var listing = await RunAsync(path, async c =>
        {
            if (path.Length > 0 && !await c.DirectoryExists(Remote(path), cancellationToken).ConfigureAwait(false)) throw NotFound(path);
            return await c.GetListing(Remote(path), FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        yield return listing.Where(i => i.Name is not "." and not "..").Select(i => ToItem(i, CombinePath(path, i.Name))).ToList();
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        return await RunAsync(path, async c =>
        {
            if (await c.FileExists(path, cancellationToken).ConfigureAwait(false))
            {
                long size = await c.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
                DateTime modified = await c.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
                return new StorageItem
                {
                    Name = ItemName(path), Path = path, Kind = StorageItemKind.File, Size = size >= 0 ? size : null,
                    Modified = modified == DateTime.MinValue ? null : new DateTimeOffset(modified).ToLocalTime(),
                };
            }
            return await c.DirectoryExists(path, cancellationToken).ConfigureAwait(false)
                ? new StorageItem { Name = ItemName(path), Path = path, Kind = StorageItemKind.Folder }
                : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static StorageItem ToItem(FtpListItem item, string path)
    {
        bool folder = item.Type == FtpObjectType.Directory || (item.Type == FtpObjectType.Link && item.LinkObject?.Type == FtpObjectType.Directory);
        return new StorageItem
        {
            Name = item.Name,
            Path = path,
            Kind = folder ? StorageItemKind.Folder : StorageItemKind.File,
            Size = folder ? null : item.Size >= 0 ? item.Size : null,
            Modified = item.Modified == DateTime.MinValue ? null : new DateTimeOffset(item.Modified).ToLocalTime(),
            Created = item.Created == DateTime.MinValue ? null : new DateTimeOffset(item.Created).ToLocalTime(),
            Attributes = item.Name.StartsWith('.') ? StorageItemAttributes.Hidden : StorageItemAttributes.None,
        };
    }

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        await RunAsync(path, async c =>
        {
            if (await c.DirectoryExists(path, cancellationToken).ConfigureAwait(false) || await c.FileExists(path, cancellationToken).ConfigureAwait(false))
                throw Exists(path);
            if (!await c.CreateDirectory(path, false, cancellationToken).ConfigureAwait(false)) throw Denied(path);
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
                if (await c.DirectoryExists(path, cancellationToken).ConfigureAwait(false))
                    await c.DeleteDirectory(path, FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
                else
                    await c.DeleteFile(path, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string from = Normalize(source.Path), to = CombinePath(parentPath, name);
        await RunAsync(to, async c =>
        {
            if (await c.FileExists(to, cancellationToken).ConfigureAwait(false) || await c.DirectoryExists(to, cancellationToken).ConfigureAwait(false))
                throw Exists(to);
            await c.Rename(from, to, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return source with { Name = name, Path = to };
    }

    // ------------------------------------------------------------------ content

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        string path = Normalize(file.Path);
        var lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await lease.Client.OpenRead(path, FtpDataType.Binary, 0, true, cancellationToken).ConfigureAwait(false);
            return new LeasedStream(stream, async () =>
            {
                try { await lease.Client.GetReply(CancellationToken.None).ConfigureAwait(false); } catch { lease.Broken = true; }
                await lease.DisposeAsync().ConfigureAwait(false);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lease.Broken = true;
            await lease.DisposeAsync().ConfigureAwait(false);
            throw Map(ex, path);
        }
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        bool existed = false;
        try
        {
            await RunAsync(path, async c =>
            {
                existed = await c.FileExists(path, cancellationToken).ConfigureAwait(false);
                if (existed && !overwrite) throw Exists(path);
                var status = await c.UploadStream(content, path, FtpRemoteExists.Overwrite, false, null, cancellationToken).ConfigureAwait(false);
                if (status == FtpStatus.Failed) throw new StorageException(StorageErrorKind.Unknown, $"The server didn't accept {Describe(path)}.");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch when (!existed)
        {
            try { await RunAsync(path, c => c.DeleteFile(path, CancellationToken.None), CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
        return await GetItemAsync(path, cancellationToken).ConfigureAwait(false)
               ?? new StorageItem { Name = name, Path = path, Kind = StorageItemKind.File, Size = length, Modified = DateTimeOffset.Now };
    }

    // ------------------------------------------------------------------ errors

    private StorageException Map(Exception ex, string path) => ex switch
    {
        StorageException storage => storage,
        FtpAuthenticationException => SignInFailed(),
        FtpCommandException { CompletionCode: "550" } => NotFound(path),
        FtpCommandException { CompletionCode: "553" } => new StorageException(StorageErrorKind.InvalidName, $"The server doesn't allow the name {Describe(path)}."),
        FtpCommandException { CompletionCode: "552" or "452" } => Full(),
        FtpCommandException { CompletionCode: "530" } => SignInFailed(),
        FtpCommandException command => new StorageException(StorageErrorKind.Unknown, $"The FTP server refused: {command.Message}"),
        AuthenticationException or FtpInvalidCertificateException => new StorageException(StorageErrorKind.AccessDenied,
            "The server's security certificate isn't trusted. If it's your own server with a self-signed certificate, reconnect and allow it.", ex),
        TimeoutException or System.Net.Sockets.SocketException or IOException => Unreachable(ex),
        _ => new StorageException(StorageErrorKind.Unknown, $"FTP error: {ex.Message}", ex),
    };

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryTake(out var client)) client.Dispose();
    }
}

/// <summary>A read stream that runs <paramref name="onClose"/> (finish the FTP reply, return the connection) when disposed.</summary>
internal sealed class LeasedStream(Stream inner, Func<Task> onClose) : Stream
{
    private int _closed;

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

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        await inner.DisposeAsync().ConfigureAwait(false);
        await onClose().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            inner.Dispose();
            onClose().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}
