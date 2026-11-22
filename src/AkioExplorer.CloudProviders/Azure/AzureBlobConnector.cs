using AkioExplorer.CloudProviders.Forms;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Azure;

public sealed class AzureBlobConnector(ICredentialStore store) : FormConnectorBase(store)
{
    private const string Global = "Azure (global)";
    private const string Government = "Azure Government";

    public override CloudProviderType Type => CloudProviderType.AzureBlob;

    public override string Name => "Azure Blob Storage";

    public override CloudCategory Category => CloudCategory.ObjectStorage;

    public override string Description => "Storage account name and access key";

    public override string Keywords => "microsoft azure blob container storage account";

    public override IReadOnlyList<CloudField> Fields { get; } =
    [
        new("user", "Storage account name") { Placeholder = "mystorageaccount" },
        new("secret", "Access key", CloudFieldKind.Secret) { Help = "Azure portal → storage account → Security + networking → Access keys." },
        new("container", "Container") { Optional = true, Help = "Leave empty to see all containers." },
        new("cloud", "Cloud", CloudFieldKind.Choice) { Choices = [Global, Government], Default = Global },
    ];

    protected override Dictionary<string, string> Normalize(Dictionary<string, string> settings)
    {
        string name = settings.GetValueOrDefault("user", string.Empty).Trim().ToLowerInvariant();
        if (name.Length is < 3 or > 24 || !name.All(char.IsAsciiLetterOrDigit))
            throw new StorageException(StorageErrorKind.InvalidName, "Storage account names have 3–24 lowercase letters and numbers.");
        settings["user"] = name;
        settings["endpoint"] = settings.GetValueOrDefault("cloud") switch
        {
            Government => "core.usgovcloudapi.net",
            _ => "core.windows.net",
        };
        return settings;
    }

    protected override ICloudStorageProvider Create(CloudAccount account, CloudSecrets secrets) =>
        new AzureBlobProvider(account, account.Setting("user"), secrets.Get("secret") ?? string.Empty, account.Setting("endpoint", "core.windows.net"),
            account.Setting("container") is { Length: > 0 } container ? container : null);

    protected override CloudIdentity Identity(CloudAccount account)
    {
        string container = account.Setting("container");
        string who = container.Length > 0 ? $"{account.Setting("user")}/{container}" : account.Setting("user");
        return new CloudIdentity(null, who, who);
    }
}
