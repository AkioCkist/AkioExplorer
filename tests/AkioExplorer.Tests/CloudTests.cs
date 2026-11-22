using System.Net;
using System.Security.Cryptography;
using System.Text;
using AkioExplorer.CloudProviders.Auth;
using AkioExplorer.CloudProviders.Dropbox;
using AkioExplorer.CloudProviders.GoogleDrive;
using AkioExplorer.CloudProviders.OneDrive;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;
using AkioExplorer.Infrastructure.Security;
using Dropbox.Api;
using Google.Apis.Http;

namespace AkioExplorer.Tests;

/// <summary>Programmable HTTP endpoint: each request is answered by the first matching route.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public FakeHttp On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    public FakeHttp OnJson(Func<HttpRequestMessage, bool> match, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(match, _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        foreach (var (match, respond) in _routes)
        {
            if (match(request)) return respond(request);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no fake route for {request.RequestUri}") };
    }
}

internal sealed class MemoryCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Items { get; } = new();
    public void Save(string key, string secret) => Items[key] = secret;
    public string? Load(string key) => Items.GetValueOrDefault(key);
    public void Delete(string key) => Items.Remove(key);
}

internal sealed class StaticTokens(string token = "test-token") : IAccessTokenProvider
{
    public int ForcedRefreshes { get; private set; }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (forceRefresh) ForcedRefreshes++;
        return ValueTask.FromResult(forceRefresh ? token + "-refreshed" : token);
    }
}

public class OAuthTests
{
    [Fact]
    public async Task Pkce_loopback_flow_exchanges_code_for_tokens()
    {
        string? challenge = null;
        var fake = new FakeHttp().On(r => r.RequestUri!.AbsoluteUri == "https://auth.test/token", r =>
        {
            var form = OAuthClient.ParseQuery(r.Content!.ReadAsStringAsync().Result);
            // Server-side PKCE check: SHA256(verifier) must equal the challenge sent to /authorize.
            string expected = OAuthClient.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
            Assert.Equal(challenge, expected);
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("the-code", form["code"]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"AT","refresh_token":"RT","expires_in":3600}"""),
            };
        });

        var client = new OAuthClient(
            new OAuthEndpoints("https://auth.test/authorize", "https://auth.test/token"),
            new OAuthApp("client-1", null, ["files"]),
            new HttpClient(fake),
            openBrowser: async uri =>
            {
                // Play the browser: the IdP redirects back to the loopback listener with code + state.
                var q = OAuthClient.ParseQuery(uri.Query);
                Assert.Equal("S256", q["code_challenge_method"]);
                Assert.Equal("client-1", q["client_id"]);
                challenge = q["code_challenge"];
                _ = Task.Run(async () =>
                {
                    using var browser = new HttpClient();
                    var page = await browser.GetStringAsync($"{q["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(q["state"])}");
                    Assert.Contains("signed in", page);
                });
                await Task.CompletedTask;
            });

        var tokens = await client.AuthorizeInteractiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Wrong_state_is_rejected()
    {
        var client = new OAuthClient(
            new OAuthEndpoints("https://auth.test/authorize", "https://auth.test/token"),
            new OAuthApp("c", null, []),
            new HttpClient(new FakeHttp()),
            openBrowser: uri =>
            {
                var q = OAuthClient.ParseQuery(uri.Query);
                _ = Task.Run(() => new HttpClient().GetAsync($"{q["redirect_uri"]}?code=x&state=forged"));
                return Task.CompletedTask;
            });

        var ex = await Assert.ThrowsAsync<OAuthException>(() => client.AuthorizeInteractiveAsync(CancellationToken.None));
        Assert.Equal("invalid_state", ex.Error);
    }

    [Fact]
    public async Task Token_source_refreshes_and_persists_rotated_refresh_token()
    {
        var fake = new FakeHttp().OnJson(_ => true, """{"access_token":"AT2","refresh_token":"RT2","expires_in":3600}""");
        var store = new MemoryCredentialStore();
        var account = new CloudAccount { DisplayName = "Test" };
        store.Save(account.CredentialKey, "RT1");
        var source = new OAuthTokenSource(
            new OAuthClient(new OAuthEndpoints("https://a/auth", "https://a/token"), new OAuthApp("c", null, []), new HttpClient(fake)),
            store, account, "Test");

        Assert.Equal("AT2", await source.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("AT2", await source.GetAccessTokenAsync(CancellationToken.None)); // cached, no second request
        Assert.Single(fake.Requests);
        Assert.Equal("RT2", store.Load(account.CredentialKey));
        Assert.Contains("refresh_token=RT1", fake.Requests[0].Body);
    }

    [Fact]
    public async Task Revoked_refresh_token_asks_user_to_reconnect()
    {
        var fake = new FakeHttp().OnJson(_ => true, """{"error":"invalid_grant","error_description":"Token has been revoked"}""", HttpStatusCode.BadRequest);
        var store = new MemoryCredentialStore();
        var account = new CloudAccount { DisplayName = "Work Drive" };
        store.Save(account.CredentialKey, "RT1");
        var source = new OAuthTokenSource(
            new OAuthClient(new OAuthEndpoints("https://a/auth", "https://a/token"), new OAuthApp("c", null, []), new HttpClient(fake)),
            store, account, "Test");

        var ex = await Assert.ThrowsAsync<StorageException>(async () => await source.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(StorageErrorKind.AuthenticationRequired, ex.Kind);
        Assert.Contains("Work Drive", ex.Message);
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void Windows_credential_manager_roundtrip()
    {
        var store = new WindowsCredentialStore();
        string key = "AkioExplorer/test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(store.Load(key));
            store.Save(key, "secret-refresh-token-ünïcode");
            Assert.Equal("secret-refresh-token-ünïcode", store.Load(key));
            store.Save(key, "rotated");
            Assert.Equal("rotated", store.Load(key));
        }
        finally
        {
            store.Delete(key);
        }
        Assert.Null(store.Load(key));
        store.Delete(key); // deleting a missing entry is fine
    }
}

public class OneDriveProviderTests
{
    private static readonly CloudAccount Account = new() { Type = CloudProviderType.OneDrive, DisplayName = "OneDrive" };

    [Fact]
    public async Task Lists_children_across_pages()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsoluteUri.Contains("/root:/My%20Docs:/children"), """
                {"value":[{"name":"Sub","folder":{"childCount":1},"lastModifiedDateTime":"2026-01-02T03:04:05Z"}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/next-page"}
                """)
            .OnJson(r => r.RequestUri!.AbsoluteUri.EndsWith("/next-page"), """
                {"value":[{"name":"a.txt","size":12,"file":{},"lastModifiedDateTime":"2026-01-02T03:04:05Z"}]}
                """);
        var provider = new OneDriveProvider(Account, new HttpClient(fake), new StaticTokens());

        var pages = new List<IReadOnlyList<StorageItem>>();
        await foreach (var page in provider.ListAsync("/My Docs", CancellationToken.None)) pages.Add(page);

        Assert.Equal(2, pages.Count);
        Assert.Equal(StorageItemKind.Folder, pages[0][0].Kind);
        Assert.Equal("/My Docs/Sub", pages[0][0].Path);
        Assert.Equal(12, pages[1][0].Size);
        Assert.Equal("Bearer test-token", fake.Requests[0].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Retries_once_with_fresh_token_after_401()
    {
        int calls = 0;
        var fake = new FakeHttp().On(_ => true, _ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"code":"InvalidAuthenticationToken"}}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") });
        var tokens = new StaticTokens();
        var provider = new OneDriveProvider(Account, new HttpClient(fake), tokens);

        await foreach (var _ in provider.ListAsync("", CancellationToken.None)) { }

        Assert.Equal(1, tokens.ForcedRefreshes);
        Assert.Equal("Bearer test-token-refreshed", fake.Requests[1].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Maps_errors_to_friendly_messages()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.Method == HttpMethod.Post, """{"error":{"code":"nameAlreadyExists","message":"exists"}}""", HttpStatusCode.Conflict)
            .OnJson(_ => true, """{"error":{"code":"itemNotFound","message":"nope"}}""", HttpStatusCode.NotFound);
        var provider = new OneDriveProvider(Account, new HttpClient(fake), new StaticTokens());

        Assert.Null(await provider.GetItemAsync("/missing.txt", CancellationToken.None));
        var ex = await Assert.ThrowsAsync<StorageException>(() => provider.CreateFolderAsync("", "Taken", CancellationToken.None));
        Assert.Equal(StorageErrorKind.AlreadyExists, ex.Kind);
        Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"fail\"", fake.Requests[^1].Body);
    }

    [Theory]
    [InlineData("/drive/root:", "")]
    [InlineData("/drive/root:/A%20B/C", "/A B/C")]
    [InlineData("/drives/abc/root:/X", "/X")]
    public void Strips_graph_root_prefix(string graph, string expected) => Assert.Equal(expected, OneDriveProvider.StripRootPrefix(graph));

    [Fact]
    public void Path_helpers()
    {
        var provider = new OneDriveProvider(Account, new HttpClient(new FakeHttp()), new StaticTokens());
        Assert.Equal(["", "/A", "/A/B"], provider.GetBreadcrumb("/A/B").Select(b => b.Path));
        Assert.Equal("/A", provider.GetParentPath("/A/B"));
        Assert.Equal("", provider.GetParentPath("/A"));
        Assert.Null(provider.GetParentPath(""));
        Assert.True(provider.IsSameOrDescendant("/a/b", "/A"));
        Assert.False(provider.IsSameOrDescendant("/ab", "/A"));
        Assert.Equal("/me/drive/root:/A%20B/%23c:", OneDriveProvider.ItemRef("/A B/#c"));
    }
}

public class DropboxProviderTests
{
    [Fact]
    public async Task Lists_folder_through_official_sdk()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsolutePath == "/2/files/list_folder", """
                {"entries":[
                  {".tag":"folder","name":"Photos","path_lower":"/photos","path_display":"/Photos","id":"id:1"},
                  {".tag":"file","name":"b.txt","path_lower":"/b.txt","path_display":"/b.txt","id":"id:2",
                   "client_modified":"2026-01-01T10:00:00Z","server_modified":"2026-01-01T10:00:00Z","rev":"0123456789abcdef","size":5,"is_downloadable":true}],
                 "cursor":"CUR","has_more":true}
                """)
            .OnJson(r => r.RequestUri!.AbsolutePath == "/2/files/list_folder/continue", """{"entries":[],"cursor":"CUR2","has_more":false}""");
        var account = new CloudAccount { Type = CloudProviderType.Dropbox, DisplayName = "Dropbox" };
        var provider = new DropboxProvider(account, () => new DropboxClient("token", new DropboxClientConfig("test") { HttpClient = new HttpClient(fake) }));

        var items = new List<StorageItem>();
        await foreach (var page in provider.ListAsync("", CancellationToken.None)) items.AddRange(page);

        Assert.Equal(2, items.Count);
        Assert.Equal(("/Photos", StorageItemKind.Folder), (items[0].Path, items[0].Kind));
        Assert.Equal(5, items[1].Size);
        Assert.Equal(2, fake.Requests.Count); // paged with the cursor
        Assert.Contains("\"cursor\":\"CUR\"", fake.Requests[1].Body);
    }

    [Fact]
    public async Task Missing_path_maps_to_not_found()
    {
        var fake = new FakeHttp().OnJson(_ => true,
            """{"error_summary":"path/not_found/..","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict);
        var account = new CloudAccount { Type = CloudProviderType.Dropbox, DisplayName = "Dropbox" };
        var provider = new DropboxProvider(account, () => new DropboxClient("token", new DropboxClientConfig("test") { HttpClient = new HttpClient(fake) }));

        Assert.Null(await provider.GetItemAsync("/nope", CancellationToken.None));
    }
}

public class GoogleDriveProviderTests
{
    private sealed class FakeFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    [Fact]
    public async Task Lists_and_builds_breadcrumbs_from_ids()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files/root"), """{"id":"ROOTID"}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files") && Uri.UnescapeDataString(r.RequestUri.Query).Contains("'root' in parents"), """
                {"files":[{"id":"F1","name":"Projects","mimeType":"application/vnd.google-apps.folder","parents":["ROOTID"]},
                          {"id":"D1","name":"Plan","mimeType":"application/vnd.google-apps.document","parents":["ROOTID"]}]}
                """)
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files") && Uri.UnescapeDataString(r.RequestUri.Query).Contains("'F1' in parents"), """
                {"files":[{"id":"F2","name":"2026","mimeType":"application/vnd.google-apps.folder","parents":["F1"]},
                          {"id":"X1","name":"notes.txt","mimeType":"text/plain","size":"42","parents":["F1"],"modifiedTime":"2026-03-01T00:00:00Z"}]}
                """);
        var account = new CloudAccount { Type = CloudProviderType.GoogleDrive, DisplayName = "My Drive" };
        var provider = new GoogleDriveProvider(account, new StaticTokens(), new HttpClient(fake), new FakeFactory(fake));

        var root = new List<StorageItem>();
        await foreach (var page in provider.ListAsync(GoogleDriveProvider.Root, CancellationToken.None)) root.AddRange(page);
        var projects = new List<StorageItem>();
        await foreach (var page in provider.ListAsync("F1", CancellationToken.None)) projects.AddRange(page);

        Assert.Equal("Google Docs", root.Single(i => i.Name == "Plan").TypeDescription);
        Assert.Equal(42, projects.Single(i => i.Name == "notes.txt").Size);
        Assert.Equal(["root", "F1", "F2"], provider.GetBreadcrumb("F2").Select(b => b.Path));
        Assert.Equal(["My Drive", "Projects", "2026"], provider.GetBreadcrumb("F2").Select(b => b.Name));
        Assert.Equal("F1", provider.GetParentPath("F2"));
        Assert.Equal("root", provider.GetParentPath("F1"));
        Assert.True(provider.IsSameOrDescendant("F2", "F1"));
        Assert.Equal("Bearer test-token", fake.Requests[0].Request.Headers.Authorization!.ToString());
    }

    private static GoogleDriveProvider Provider(FakeHttp fake) =>
        new(new CloudAccount { Type = CloudProviderType.GoogleDrive, DisplayName = "My Drive" }, new StaticTokens(), new HttpClient(fake), new FakeFactory(fake));

    private static async Task<List<StorageItem>> List(GoogleDriveProvider provider, string path)
    {
        var items = new List<StorageItem>();
        await foreach (var page in provider.ListAsync(path, CancellationToken.None)) items.AddRange(page);
        return items;
    }

    [Fact]
    public async Task Shared_drives_and_shared_with_me_are_folders_in_my_drive()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files/root"), """{"id":"ROOTID"}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/drives"), """{"drives":[{"id":"SD1","name":"Team"}]}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files") && Uri.UnescapeDataString(r.RequestUri.Query).Contains("sharedWithMe = true"), """
                {"files":[{"id":"S1","name":"From Lan.pdf","mimeType":"application/pdf","size":"5"}]}
                """)
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files") && Uri.UnescapeDataString(r.RequestUri.Query).Contains("'SD1' in parents"), """
                {"files":[{"id":"T1","name":"Budget","mimeType":"application/vnd.google-apps.folder","parents":["SD1"],"driveId":"SD1","folderColorRgb":"#4986e7"}]}
                """)
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files") && Uri.UnescapeDataString(r.RequestUri.Query).Contains("'root' in parents"), """{"files":[]}""");
        var provider = Provider(fake);

        var root = await List(provider, GoogleDriveProvider.Root);
        Assert.Equal([GoogleDriveProvider.SharedDrivesPath, GoogleDriveProvider.SharedWithMePath], root.Select(i => i.Path));

        var drives = await List(provider, GoogleDriveProvider.SharedDrivesPath);
        var team = await List(provider, "SD1");
        var shared = await List(provider, GoogleDriveProvider.SharedWithMePath);

        Assert.Equal("Team", drives.Single().Name);
        Assert.Equal(ItemLabel.Blue, team.Single().Label); // folder colour → label
        Assert.Equal("From Lan.pdf", shared.Single().Name);
        Assert.Equal(["My Drive", "Shared drives", "Team", "Budget"], provider.GetBreadcrumb("T1").Select(b => b.Name));
        Assert.Equal(GoogleDriveProvider.SharedWithMePath, provider.GetParentPath("S1"));
        // Listing inside a shared drive must name the drive.
        var teamRequest = fake.Requests.Single(r => Uri.UnescapeDataString(r.Request.RequestUri!.Query).Contains("'SD1' in parents")).Request.RequestUri!.Query;
        Assert.Contains("corpora=drive", teamRequest);
        Assert.Contains("driveId=SD1", teamRequest);
        await Assert.ThrowsAsync<StorageException>(() =>
            provider.WriteFileAsync(GoogleDriveProvider.SharedWithMePath, "x.txt", new MemoryStream([1]), 1, false, CancellationToken.None));
    }

    [Fact]
    public async Task Google_docs_open_in_the_browser_and_offer_export_formats()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files/root"), """{"id":"ROOTID"}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/drives"), """{"drives":[]}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files"), """
                {"files":[{"id":"D1","name":"Plan","mimeType":"application/vnd.google-apps.document","parents":["ROOTID"]},
                          {"id":"X1","name":"report.docx","mimeType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document","size":"9","parents":["ROOTID"]}]}
                """);
        var provider = Provider(fake);
        var items = await List(provider, GoogleDriveProvider.Root);
        var doc = items.Single(i => i.Name == "Plan");
        var docx = items.Single(i => i.Name == "report.docx");

        Assert.True(provider.OpensInBrowser(doc));
        Assert.False(provider.OpensInBrowser(docx));
        Assert.Contains(provider.ExportOptions(doc), o => o.Extension == ".pdf");
        Assert.Empty(provider.ExportOptions(docx));
        Assert.True(provider.CanConvert(docx));
        Assert.False(provider.CanConvert(doc));
    }

    [Fact]
    public async Task Largest_files_search_sorts_on_the_server_and_stops_below_the_minimum()
    {
        var fake = new FakeHttp()
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files/root"), """{"id":"ROOTID"}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files") && r.RequestUri.Query.Contains("pageToken=P2"), """{"files":[{"id":"C","name":"c.bin","mimeType":"x","size":"10"}]}""")
            .OnJson(r => r.RequestUri!.AbsolutePath.EndsWith("/files"), """
                {"nextPageToken":"P2","files":[{"id":"A","name":"a.bin","mimeType":"x","size":"900000000"},{"id":"B","name":"b.bin","mimeType":"x","size":"5"}]}
                """);
        var provider = Provider(fake);
        var query = SearchQuery.Empty with { Size = SizeFilter.Custom, MinSize = 10L * 1024 * 1024, Scope = SearchScope.Everywhere };

        var found = new List<StorageItem>();
        await foreach (var item in provider.SearchAsync(GoogleDriveProvider.Root, query, CancellationToken.None)) found.Add(item);

        Assert.Equal(["a.bin"], found.Select(f => f.Name));
        Assert.Contains(fake.Requests, r => r.Request.RequestUri!.Query.Contains("orderBy=quotaBytesUsed"));
        Assert.DoesNotContain(fake.Requests, r => r.Request.RequestUri!.Query.Contains("pageToken=P2")); // stopped early
    }
}
