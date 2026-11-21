using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AkioExplorer.CloudProviders.Auth;

public sealed record OAuthEndpoints(string AuthorizeUrl, string TokenUrl, string? RevokeUrl = null);

/// <summary>
/// App registration for one service. Desktop apps are public clients: PKCE protects the code exchange; a
/// "client secret" (Google desktop clients) is not confidential and is only sent because the service requires it.
/// </summary>
public sealed record OAuthApp(
    string ClientId,
    string? ClientSecret,
    IReadOnlyList<string> Scopes,
    int? FixedRedirectPort = null,
    IReadOnlyDictionary<string, string>? ExtraAuthorizeParameters = null,
    string RedirectHost = "localhost");

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? IdToken);

public sealed class OAuthException(string error, string? description) : Exception(description ?? error)
{
    public string Error { get; } = error;

    public bool IsInvalidGrant => Error is "invalid_grant" or "invalid_token";
}

/// <summary>
/// OAuth 2.0 authorization-code flow with PKCE (RFC 7636) and a loopback redirect (RFC 8252 §7.3):
/// the user signs in in their own browser (password managers, 2FA and SSO keep working), the app never sees the
/// password, and the one-time code is delivered to a listener on localhost.
/// </summary>
public sealed class OAuthClient
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly OAuthEndpoints _endpoints;
    private readonly OAuthApp _app;
    private readonly HttpClient _http;
    private readonly Func<Uri, Task> _openBrowser;

    public OAuthClient(OAuthEndpoints endpoints, OAuthApp app, HttpClient http, Func<Uri, Task>? openBrowser = null)
    {
        _endpoints = endpoints;
        _app = app;
        _http = http;
        _openBrowser = openBrowser ?? OpenSystemBrowser;
    }

    public async Task<OAuthTokens> AuthorizeInteractiveAsync(CancellationToken cancellationToken)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        int port = _app.FixedRedirectPort ?? GetFreePort();
        string redirectUri = $"http://{_app.RedirectHost}:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new OAuthException("listener_failed", $"Couldn't start the sign-in listener on port {port}: {ex.Message}");
        }

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _app.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (_app.Scopes.Count > 0) query["scope"] = string.Join(' ', _app.Scopes);
        if (_app.ExtraAuthorizeParameters is not null)
        {
            foreach (var (key, value) in _app.ExtraAuthorizeParameters) query[key] = value;
        }

        var authorizeUri = new Uri(_endpoints.AuthorizeUrl + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
        await _openBrowser(authorizeUri).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        using var stop = timeout.Token.Register(() => { try { listener.Stop(); } catch { } });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OAuthException("timeout", "Sign-in took too long. Try again.");
            }

            var parameters = ParseQuery(context.Request.Url?.Query);
            if (!parameters.ContainsKey("code") && !parameters.ContainsKey("error"))
            {
                // Browsers also ask for /favicon.ico etc.
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            bool ok = parameters.TryGetValue("code", out var code) && parameters.GetValueOrDefault("state") == state;
            await RespondAsync(context, ok).ConfigureAwait(false);

            if (parameters.TryGetValue("error", out var error))
                throw new OAuthException(error, error == "access_denied" ? "Sign-in was cancelled in the browser." : parameters.GetValueOrDefault("error_description"));
            if (parameters.GetValueOrDefault("state") != state)
                throw new OAuthException("invalid_state", "The sign-in response didn't match this request. Try again.");

            return await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _app.ClientId,
                ["code_verifier"] = verifier,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _app.ClientId,
        }, cancellationToken);

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (_endpoints.RevokeUrl is null) return;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        using var response = await _http.PostAsync(_endpoints.RevokeUrl, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OAuthTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_app.ClientSecret)) form["client_secret"] = _app.ClientSecret;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoints.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new OAuthException("invalid_response", $"The sign-in server returned an unexpected response ({(int)response.StatusCode}).");
        }

        using (json)
        {
            var root = json.RootElement;
            if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
            {
                string error = root.TryGetProperty("error", out var e) ? e.ToString() : $"http_{(int)response.StatusCode}";
                string? description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                throw new OAuthException(error, description);
            }

            string access = root.GetProperty("access_token").GetString()!;
            string? refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            string? idToken = root.TryGetProperty("id_token", out var i) ? i.GetString() : null;
            int expiresIn = root.TryGetProperty("expires_in", out var x) && x.TryGetInt32(out int seconds) ? seconds : 3600;
            return new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), idToken);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, bool success)
    {
        string message = success
            ? "You're signed in. You can close this tab and return to AkioExplorer."
            : "Sign-in didn't complete. Return to AkioExplorer to try again.";
        string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>AkioExplorer</title>" +
                      "<style>body{font-family:'Segoe UI',sans-serif;display:grid;place-items:center;height:90vh;color:#1b1b1b}" +
                      "@media(prefers-color-scheme:dark){body{background:#202020;color:#fff}}</style></head>" +
                      $"<body><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static Task OpenSystemBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }

    internal static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
