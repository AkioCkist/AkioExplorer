using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Auth;

/// <summary>Bearer tokens for API calls. <paramref name="forceRefresh"/> is used after a 401.</summary>
public interface IAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken, bool forceRefresh = false);
}

/// <summary>
/// Access tokens for one account. The refresh token lives only in the credential store; the short-lived access
/// token only in memory. Refreshes ahead of expiry, once at a time, and persists rotated refresh tokens.
/// </summary>
public sealed class OAuthTokenSource : IAccessTokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly OAuthClient _client;
    private readonly ICredentialStore _store;
    private readonly CloudAccount _account;
    private readonly string _serviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public OAuthTokenSource(OAuthClient client, ICredentialStore store, CloudAccount account, string serviceName)
    {
        _client = client;
        _store = store;
        _account = account;
        _serviceName = serviceName;
    }

    /// <summary>Stores the tokens of a fresh interactive sign-in.</summary>
    public void Accept(OAuthTokens tokens)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new StorageException(StorageErrorKind.AuthenticationRequired,
                $"{_serviceName} didn't grant offline access. Remove AkioExplorer from your account's connected apps and sign in again.");
        _store.Save(_account.CredentialKey, tokens.RefreshToken);
        _accessToken = tokens.AccessToken;
        _expiresAt = tokens.ExpiresAt;
    }

    public void Invalidate() => _accessToken = null;

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && _accessToken is { } cached && _expiresAt - RefreshMargin > DateTimeOffset.UtcNow) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _accessToken is { } again && _expiresAt - RefreshMargin > DateTimeOffset.UtcNow) return again;

            string refresh = _store.Load(_account.CredentialKey)
                ?? throw new StorageException(StorageErrorKind.AuthenticationRequired, ReconnectMessage());
            OAuthTokens tokens;
            try
            {
                tokens = await _client.RefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
            }
            catch (OAuthException ex) when (ex.IsInvalidGrant)
            {
                throw new StorageException(StorageErrorKind.AuthenticationRequired, ReconnectMessage(), ex);
            }
            catch (HttpRequestException ex)
            {
                throw new StorageException(StorageErrorKind.Unavailable, $"Can't reach {_serviceName}. Check your internet connection.", ex);
            }

            if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refresh)
                _store.Save(_account.CredentialKey, tokens.RefreshToken); // rotated (Microsoft)

            _accessToken = tokens.AccessToken;
            _expiresAt = tokens.ExpiresAt;
            return tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? StoredRefreshToken => _store.Load(_account.CredentialKey);

    private string ReconnectMessage() =>
        $"The sign-in for \"{_account.DisplayName}\" has expired or was revoked. Reconnect the account to continue.";
}
