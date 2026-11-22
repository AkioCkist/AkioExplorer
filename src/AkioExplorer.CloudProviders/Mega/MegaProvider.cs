using System.Runtime.CompilerServices;
using AkioExplorer.CloudProviders.Forms;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using CG.Web.MegaApiClient;

namespace AkioExplorer.CloudProviders.Mega;

/// <summary>
/// MEGA through MegaApiClient (end-to-end encrypted: names and contents are decrypted on this PC). MEGA hands out
/// the whole node tree in one request, so the tree is cached briefly and paths are looked up in it. After the
/// first sign-in only a session (id + master key) is kept, never the password, and 2FA isn't asked again.
/// </summary>
public sealed class MegaProvider : RemotePathProvider, IDisposable
{
    private static readonly TimeSpan TreeLifetime = TimeSpan.FromSeconds(30);

    private readonly string _email;
    private readonly string? _password;
    private readonly string? _mfa;
    private readonly MegaApiClient.LogonSessionToken? _session;
    private readonly MegaApiClient _client = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Tree? _tree;

    public MegaProvider(CloudAccount account, string email, string? password, string? mfa, MegaApiClient.LogonSessionToken? session)
        : base(account, CloudProviderType.Mega, "MEGA")
    {
        _email = email;
        _password = password;
        _mfa = mfa;
        _session = session;
    }

    /// <summary>The session after a password sign-in (what the connector stores).</summary>
    public MegaApiClient.LogonSessionToken? Session { get; private set; }

    public override StorageCapabilities Capabilities => base.Capabilities | StorageCapabilities.RecycleBin;

    private sealed class Tree(Dictionary<string, INode> byPath, Dictionary<string, List<INode>> children, INode root, DateTimeOffset loaded)
    {
        public Dictionary<string, INode> ByPath { get; } = byPath;
        public Dictionary<string, List<INode>> Children { get; } = children; // by parent node id
        public INode Root { get; } = root;
        public DateTimeOffset Loaded { get; } = loaded;
    }

    private async Task EnsureLoggedInAsync()
    {
        if (_client.IsLoggedIn) return;
        if (_session is not null)
        {
            await _client.LoginAsync(_session).ConfigureAwait(false);
        }
        else if (_password is not null)
        {
            Session = await _client.LoginAsync(_email, _password, string.IsNullOrWhiteSpace(_mfa) ? null : _mfa.Trim()).ConfigureAwait(false);
        }
        else
        {
            throw SignInFailed();
        }
    }

    private async Task<T> RunAsync<T>(string path, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoggedInAsync().ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageException)
        {
            throw Map(ex, path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Tree> TreeAsync(CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _tree is { } cached && DateTimeOffset.UtcNow - cached.Loaded < TreeLifetime) return cached;
        return await RunAsync(string.Empty, async () =>
        {
            var nodes = (await _client.GetNodesAsync().ConfigureAwait(false)).ToList();
            var root = nodes.First(n => n.Type == NodeType.Root);
            var children = nodes.Where(n => n.ParentId is not null).GroupBy(n => n.ParentId).ToDictionary(g => g.Key, g => g.ToList());
            var byPath = new Dictionary<string, INode>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };
            var pending = new Queue<(INode Node, string Path)>();
            pending.Enqueue((root, string.Empty));
            while (pending.Count > 0)
            {
                var (node, path) = pending.Dequeue();
                if (!children.TryGetValue(node.Id, out var kids)) continue;
                foreach (var child in kids)
                {
                    string childPath = path + "/" + child.Name;
                    byPath.TryAdd(childPath, child); // MEGA allows duplicate names; the first one wins for addressing
                    if (child.Type == NodeType.Directory) pending.Enqueue((child, childPath));
                }
            }
            return _tree = new Tree(byPath, children, root, DateTimeOffset.UtcNow);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Invalidate() => _tree = null;

    private async Task<INode> NodeAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        if (tree.ByPath.TryGetValue(path, out var node)) return node;
        tree = await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false); // changed elsewhere?
        return tree.ByPath.TryGetValue(path, out node) ? node : throw NotFound(path);
    }

    private static StorageItem ToItem(INode node, string path) => new()
    {
        Name = node.Name,
        Path = path,
        Kind = node.Type == NodeType.File ? StorageItemKind.File : StorageItemKind.Folder,
        Size = node.Type == NodeType.File ? node.Size : null,
        Modified = node.ModificationDate is { } m ? new DateTimeOffset(m).ToLocalTime() : node.CreationDate is { } c ? new DateTimeOffset(c).ToLocalTime() : null,
        Created = node.CreationDate is { } created ? new DateTimeOffset(created).ToLocalTime() : null,
    };

    // ------------------------------------------------------------------ listing

    public override async IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        path = Normalize(path);
        // Opening a folder re-reads the tree when it is older than a few seconds, so changes made elsewhere show up.
        var tree = await TreeAsync(cancellationToken, fresh: _tree is { } t && DateTimeOffset.UtcNow - t.Loaded > TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!tree.ByPath.TryGetValue(path, out var folder) || folder.Type == NodeType.File) throw NotFound(path);
        var kids = tree.Children.TryGetValue(folder.Id, out var list) ? list : [];
        yield return kids.Select(k => ToItem(k, CombinePath(path, k.Name))).ToList();
    }

    public override async Task<StorageItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (path.Length == 0) return null;
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        return tree.ByPath.TryGetValue(path, out var node) ? ToItem(node, path) : null;
    }

    // ------------------------------------------------------------------ mutations

    public override async Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        if (await GetItemAsync(path, cancellationToken).ConfigureAwait(false) is not null) throw Exists(path);
        var parent = await NodeAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var created = await RunAsync(path, () => _client.CreateFolderAsync(name, parent), cancellationToken).ConfigureAwait(false);
        Invalidate();
        return ToItem(created, path);
    }

    public override async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken)
    {
        foreach (var raw in paths)
        {
            string path = Normalize(raw);
            if (path.Length == 0) throw RootChange("deleted");
            var node = await NodeAsync(path, cancellationToken).ConfigureAwait(false);
            await RunAsync(path, async () => { await _client.DeleteAsync(node, moveToTrash: !permanent).ConfigureAwait(false); return 0; }, cancellationToken)
                .ConfigureAwait(false);
        }
        Invalidate();
    }

    public override async Task<StorageItem?> MoveWithinAsync(StorageItem source, string parentPath, string name, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string target = CombinePath(parentPath, name);
        if (await GetItemAsync(target, cancellationToken).ConfigureAwait(false) is not null) throw Exists(target);
        var node = await NodeAsync(source.Path, cancellationToken).ConfigureAwait(false);
        string currentParent = GetParentPath(Normalize(source.Path)) ?? string.Empty;
        if (!currentParent.Equals(Normalize(parentPath), StringComparison.OrdinalIgnoreCase))
        {
            var parent = await NodeAsync(parentPath, cancellationToken).ConfigureAwait(false);
            node = await RunAsync(target, () => _client.MoveAsync(node, parent), cancellationToken).ConfigureAwait(false);
        }
        if (node.Name != name) node = await RunAsync(target, () => _client.RenameAsync(node, name), cancellationToken).ConfigureAwait(false);
        Invalidate();
        return ToItem(node, target);
    }

    public override async Task<StorageQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        var info = await RunAsync(string.Empty, () => _client.GetAccountInformationAsync(), cancellationToken).ConfigureAwait(false);
        return new StorageQuota(info.UsedQuota, info.TotalQuota > 0 ? info.TotalQuota : null);
    }

    // ------------------------------------------------------------------ content

    public override async Task<Stream> OpenReadAsync(StorageItem file, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(file.Path, cancellationToken).ConfigureAwait(false);
        await EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _client.DownloadAsync(node, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Map(ex, file.Path);
        }
    }

    public override async Task<StorageItem> WriteFileAsync(string parentPath, string name, Stream content, long length, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureValidName(name);
        string path = CombinePath(parentPath, name);
        var existing = (await TreeAsync(cancellationToken, fresh: true).ConfigureAwait(false)).ByPath.GetValueOrDefault(Normalize(path));
        if (existing is not null && (!overwrite || existing.Type != NodeType.File)) throw Exists(path);
        var parent = await NodeAsync(parentPath, cancellationToken).ConfigureAwait(false);

        await EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
        INode uploaded;
        try
        {
            // MEGA keeps both files when names clash: upload first, then remove the old one.
            uploaded = await _client.UploadAsync(content, name, parent, null, DateTime.Now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Map(ex, path);
        }
        if (existing is not null)
            await RunAsync(path, async () => { await _client.DeleteAsync(existing, moveToTrash: true).ConfigureAwait(false); return 0; }, cancellationToken)
                .ConfigureAwait(false);
        Invalidate();
        return ToItem(uploaded, path);
    }

    private Task EnsureSignedInAsync(CancellationToken cancellationToken) => RunAsync(string.Empty, () => Task.FromResult(0), cancellationToken);

    // ------------------------------------------------------------------ errors

    private StorageException Map(Exception ex, string path) => ex switch
    {
        ApiException { ApiResultCode: ApiResultCode.TwoFactorAuthenticationError } => new StorageException(StorageErrorKind.AuthenticationRequired,
            "MEGA needs the 6-digit code from your authenticator app. Enter it and try again."),
        ApiException { ApiResultCode: ApiResultCode.BadSessionId or ApiResultCode.ResourceNotExists } when path.Length == 0 => SignInFailed(),
        ApiException { ApiResultCode: ApiResultCode.ResourceNotExists } => NotFound(path),
        ApiException { ApiResultCode: ApiResultCode.AccessDenied } => Denied(path),
        ApiException { ApiResultCode: ApiResultCode.ResourceAlreadyExists } => Exists(path),
        ApiException { ApiResultCode: ApiResultCode.QuotaExceeded } => new StorageException(StorageErrorKind.Throttled,
            "MEGA's transfer quota is used up for now. MEGA lifts it after a few hours (or with a paid plan)."),
        ApiException { ApiResultCode: ApiResultCode.TooManyRequests or ApiResultCode.ToManyRequestsForThisResource or ApiResultCode.TooManyConnectionsOnThisResource } => Busy(),
        ApiException { ApiResultCode: ApiResultCode.ResourceAdministrativelyBlocked } => new StorageException(StorageErrorKind.AccessDenied,
            $"MEGA has blocked {Describe(path)}."),
        ApiException api => new StorageException(StorageErrorKind.Unknown, $"MEGA error ({api.ApiResultCode})."),
        NotSupportedException { Message: var m } when m.Contains("hashcash", StringComparison.OrdinalIgnoreCase) => Busy(),
        HttpRequestException or System.Net.WebException or TimeoutException => Unreachable(ex),
        _ => new StorageException(StorageErrorKind.Unknown, $"MEGA error: {ex.Message}", ex),
    };

    public void Dispose()
    {
        // Don't log out: that would end the stored session.
        _gate.Dispose();
    }
}

public sealed class MegaConnector(ICredentialStore store) : FormConnectorBase(store)
{
    public override CloudProviderType Type => CloudProviderType.Mega;

    public override string Name => "MEGA";

    public override CloudCategory Category => CloudCategory.Popular;

    public override string Description => "Email and password (end-to-end encrypted)";

    public override string Keywords => "mega.nz mega.io";

    public override IReadOnlyList<CloudField> Fields { get; } =
    [
        new("user", "Email address"),
        new("password", "Password", CloudFieldKind.Secret) { Help = "Used once to sign in; AkioExplorer keeps only the MEGA session." },
        new("mfa", "Two-factor code", CloudFieldKind.Secret) { Optional = true, Placeholder = "123456", Help = "Only if two-factor authentication is on." },
    ];

    protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets)
    {
        MegaApiClient.LogonSessionToken? session = null;
        if (secrets.Get("session") is { } id && secrets.Get("masterKey") is { } key)
            session = new MegaApiClient.LogonSessionToken(id, Convert.FromBase64String(key));
        return new MegaProvider(account, account.Setting("user"), session is null ? secrets.Get("password") : null, secrets.Get("mfa"), session);
    }

    protected override CloudSecrets SecretsToStore(CloudSecrets typed, ICloudStorageProvider provider) =>
        provider is MegaProvider { Session: { } session }
            ? new CloudSecrets(new Dictionary<string, string> { ["session"] = session.SessionId, ["masterKey"] = Convert.ToBase64String(session.MasterKey) }, provider.DisplayName)
            : typed;

    protected override CloudIdentity Identity(CloudAccount account) => new(account.Setting("user"), null, account.Setting("user"));
}
