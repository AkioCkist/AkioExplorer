using AkioExplorer.Core.Logic;

namespace AkioExplorer.Tests;

public sealed class SharedLinksTests
{
    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz?usp=sharing")]
    [InlineData("drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz")]
    [InlineData("https://mega.nz/folder/AbCdEfGh#0123456789abcdefABCDEF")]
    [InlineData("www.mediafire.com/folder/abc123def4567/stuff")]
    [InlineData("ftp://ftp.example.com/pub")]
    [InlineData("sftp://me@server.local:2222/home/me")]
    [InlineData("file:///C:/Users")]
    [InlineData("https://example.com/whatever")]
    [InlineData("  \"https://www.dropbox.com/scl/fo/abc/xyz?dl=0\"  ")]
    public void Recognises_links(string text) => Assert.True(SharedLinks.TryParseLink(text, out _));

    [Theory]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"\\server\share")]
    [InlineData("Documents")]
    [InlineData("notes.txt")]
    [InlineData("code .")]
    [InlineData("/home/me")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings://display")]
    [InlineData("example.com/x")]          // no scheme and not a known host: could be a folder name
    public void Leaves_paths_and_commands_alone(string text) => Assert.False(SharedLinks.TryParseLink(text, out _));

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz?usp=sharing", "1AbCdEfGhIjKlMnOpQrStUvWxYz", true)]
    [InlineData("https://drive.google.com/drive/u/1/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz", "1AbCdEfGhIjKlMnOpQrStUvWxYz", true)]
    [InlineData("https://drive.google.com/file/d/1ZyXwVuTsRqPoNmLkJiHgFeDcBa/view?usp=drive_link", "1ZyXwVuTsRqPoNmLkJiHgFeDcBa", false)]
    [InlineData("https://docs.google.com/spreadsheets/d/1ZyXwVuTsRqPoNmLkJiHgFeDcBa/edit#gid=0", "1ZyXwVuTsRqPoNmLkJiHgFeDcBa", false)]
    [InlineData("https://drive.google.com/open?id=1ZyXwVuTsRqPoNmLkJiHgFeDcBa", "1ZyXwVuTsRqPoNmLkJiHgFeDcBa", null)]
    public void Reads_google_drive_ids(string url, string id, bool? folder)
    {
        Assert.True(SharedLinks.TryGetGoogleDriveId(new Uri(url), out string found, out bool? isFolder));
        Assert.Equal(id, found);
        Assert.Equal(folder, isFolder);
    }

    [Fact]
    public void Ignores_other_google_pages() =>
        Assert.False(SharedLinks.TryGetGoogleDriveId(new Uri("https://drive.google.com/drive/my-drive"), out _, out _));

    [Theory]
    [InlineData("https://www.mediafire.com/folder/abc123def4567/My+Stuff", "abc123def4567", true)]
    [InlineData("https://www.mediafire.com/file/qwe123rty456uio/setup.exe/file", "qwe123rty456uio", false)]
    [InlineData("https://www.mediafire.com/view/qwe123rty456uio/photo.jpg", "qwe123rty456uio", false)]
    [InlineData("https://www.mediafire.com/?abc123def4567", "abc123def4567", true)]
    [InlineData("https://www.mediafire.com/?qwe123rty456uio", "qwe123rty456uio", false)]
    public void Reads_mediafire_keys(string url, string key, bool folder)
    {
        Assert.True(SharedLinks.TryParseMediaFire(new Uri(url), out string found, out bool isFolder));
        Assert.Equal(key, found);
        Assert.Equal(folder, isFolder);
    }

    [Fact]
    public void Ignores_mediafire_home() => Assert.False(SharedLinks.TryParseMediaFire(new Uri("https://www.mediafire.com/"), out _, out _));

    [Fact]
    public void Reads_ftp_address_with_credentials()
    {
        Assert.True(SharedLinks.TryParseLink("ftp://user%40corp:p%40ss@files.example.com:2121/pub/My%20Docs/", out var link));
        Assert.True(SharedLinks.TryParseServer(link, out var server));
        Assert.Equal("ftp", server.Scheme);
        Assert.Equal("files.example.com", server.Host);
        Assert.Equal(2121, server.Port);
        Assert.Equal("user@corp", server.User);
        Assert.Equal("p@ss", server.Password);
        Assert.Equal("/pub/My Docs", server.Path);
    }

    [Fact]
    public void Reads_anonymous_sftp_root()
    {
        Assert.True(SharedLinks.TryParseLink("sftp://nas.local", out var link));
        Assert.True(SharedLinks.TryParseServer(link, out var server));
        Assert.Null(server.Port);
        Assert.Null(server.User);
        Assert.Equal("/", server.Path);
    }

    [Theory]
    [InlineData("https://www.dropbox.com/scl/fo/abc123xyz/AAbbCC?rlkey=k3y&dl=0", true)]
    [InlineData("https://www.dropbox.com/sh/abc123/AAAxyz?dl=0", true)]
    [InlineData("https://www.dropbox.com/scl/fi/abc123/report.pdf?rlkey=k3y&dl=0", false)]
    [InlineData("https://www.dropbox.com/s/abc123/report.pdf?dl=0", false)]
    public void Reads_dropbox_links(string url, bool folder)
    {
        Assert.True(SharedLinks.TryParseDropbox(new Uri(url), out bool isFolder));
        Assert.Equal(folder, isFolder);
    }

    [Theory]
    [InlineData("https://www.dropbox.com/home")]
    [InlineData("https://www.dropbox.com/scl/fo/")]
    public void Ignores_other_dropbox_pages(string url) => Assert.False(SharedLinks.TryParseDropbox(new Uri(url), out _));

    [Theory]
    [InlineData("https://1drv.ms/f/s!AbCdEf123", null)]
    [InlineData("https://onedrive.live.com/?id=ABC%21123&cid=ABC", null)]
    [InlineData("https://onedrive.live.com/redir?resid=ABC!123&authkey=!xyz", null)]
    [InlineData("https://contoso.sharepoint.com/:f:/g/personal/me/EaBcD?e=xyz", true)]
    [InlineData("https://contoso-my.sharepoint.com/:w:/g/personal/me/EaBcD?e=xyz", false)]
    public void Reads_onedrive_links(string url, bool? folder)
    {
        Assert.True(SharedLinks.TryParseOneDrive(new Uri(url), out bool? isFolder));
        Assert.Equal(folder, isFolder);
    }

    [Theory]
    [InlineData("https://onedrive.live.com/")]
    [InlineData("https://contoso.sharepoint.com/sites/team/Shared%20Documents")]
    public void Ignores_other_onedrive_pages(string url) => Assert.False(SharedLinks.TryParseOneDrive(new Uri(url), out _));

    [Fact]
    public void Encodes_graph_share_ids() =>
        // Graph's rule: "u!" + base64url without padding.
        Assert.Equal("u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjMhNDU2",
            SharedLinks.GraphShareId(new Uri("https://onedrive.live.com/redir?resid=123!456")));

    [Theory]
    [InlineData("https://app.box.com/s/abc123def456", true)]
    [InlineData("https://contoso.app.box.com/s/abc123def456", true)]
    [InlineData("https://contoso.app.box.com/v/team-files", true)]
    [InlineData("https://app.box.com/folder/0", false)]
    [InlineData("https://example.com/s/abc", false)]
    public void Reads_box_links(string url, bool expected) => Assert.Equal(expected, SharedLinks.TryParseBox(new Uri(url)));

    [Fact]
    public void Web_links_are_not_servers()
    {
        Assert.True(SharedLinks.TryParseLink("https://example.com/a", out var link));
        Assert.False(SharedLinks.TryParseServer(link, out _));
    }
}
