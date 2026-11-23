using AkioExplorer.Core.Cloud;

namespace AkioExplorer.Tests;

public sealed class CloudAccountShortNameTests
{
    [Fact]
    public void Generated_name_shortens_to_the_mailbox()
    {
        var account = new CloudAccount { DisplayName = "Google Drive name@example.com", Email = "name@example.com" };
        Assert.Equal("name", account.ShortName);
    }

    [Fact]
    public void User_name_without_at_is_kept_whole()
    {
        var account = new CloudAccount { DisplayName = "SFTP deploy", UserName = "deploy" };
        Assert.Equal("deploy", account.ShortName);
    }

    [Fact]
    public void Renamed_account_keeps_its_name()
    {
        var account = new CloudAccount { DisplayName = "Work drive", Email = "name@example.com" };
        Assert.Equal("Work drive", account.ShortName);
    }

    [Fact]
    public void Account_without_identity_keeps_the_service_name()
    {
        var account = new CloudAccount { DisplayName = "WebDAV" };
        Assert.Equal("WebDAV", account.ShortName);
    }
}
