using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.CloudProviders.Http;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Links;

/// <summary>
/// GET requests made with a signed-in account's token for browsing someone else's shared link (OneDrive, Box):
/// a 401 refreshes the token once; failures become messages written for people.
/// </summary>
internal sealed class LinkRest(HttpClient http, IAccessTokenProvider tokens, string serviceName, IReadOnlyDictionary<string, string>? headers = null)
{
    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenAsync(string url, CancellationToken cancellationToken)
    {
        var response = await SendAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return new OwnedStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            string token = await tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: attempt > 0).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new StorageException(StorageErrorKind.Unavailable, $"Can't reach {serviceName}. Check your internet connection.", ex);
            }
            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            if (status == HttpStatusCode.Unauthorized && attempt == 0) continue;
            throw status switch
            {
                HttpStatusCode.Unauthorized => new StorageException(StorageErrorKind.AuthenticationRequired, $"The {serviceName} sign-in has expired. Reconnect the account to continue."),
                HttpStatusCode.NotFound => new StorageException(StorageErrorKind.NotFound, $"The {serviceName} link doesn't work: it was removed, or it isn't shared with this account."),
                HttpStatusCode.Forbidden => new StorageException(StorageErrorKind.AccessDenied, $"This {serviceName} account isn't allowed to open that link."),
                HttpStatusCode.TooManyRequests => new StorageException(StorageErrorKind.Throttled, $"{serviceName} is limiting requests right now. Wait a moment and try again."),
                >= HttpStatusCode.InternalServerError => new StorageException(StorageErrorKind.Unavailable, $"{serviceName} is having trouble right now. Try again in a moment."),
                _ => new StorageException(StorageErrorKind.Unknown, $"{serviceName} error {(int)status}: {Trim(detail)}"),
            };
        }
    }

    private static string Trim(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
