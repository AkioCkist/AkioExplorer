using System.Text.Json;

namespace AkioExplorer.CloudProviders;

public sealed class CloudAppRegistration
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// OAuth app registrations (client ids) for each service. They are not user secrets: desktop apps are public clients.
/// Sources, later wins: built-in defaults (none in source control) → %LocalAppData%\AkioExplorer\cloud-apps.json →
/// environment variables. See docs/CLOUD-SETUP.md.
/// </summary>
public sealed class CloudAppsConfig
{
    public CloudAppRegistration GoogleDrive { get; set; } = new();

    public CloudAppRegistration Dropbox { get; set; } = new();

    public CloudAppRegistration OneDrive { get; set; } = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AkioExplorer", "cloud-apps.json");

    public static CloudAppsConfig Load()
    {
        var config = new CloudAppsConfig();
        try
        {
            if (File.Exists(FilePath))
            {
                var fromFile = JsonSerializer.Deserialize<CloudAppsConfig>(File.ReadAllText(FilePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (fromFile is not null) config = fromFile;
            }
        }
        catch (Exception)
        {
            // A malformed file must not stop the app; connectors simply report "not configured".
        }

        Override(config.GoogleDrive, "AKIO_GOOGLE_CLIENT_ID", "AKIO_GOOGLE_CLIENT_SECRET");
        Override(config.Dropbox, "AKIO_DROPBOX_APP_KEY", null);
        Override(config.OneDrive, "AKIO_ONEDRIVE_CLIENT_ID", null);
        return config;
    }

    /// <summary>Re-reads the file and environment in place, so edits apply without restarting the app.</summary>
    public void Reload()
    {
        var fresh = Load();
        GoogleDrive = fresh.GoogleDrive;
        Dropbox = fresh.Dropbox;
        OneDrive = fresh.OneDrive;
    }

    /// <summary>Writes an empty template so users have something to fill in.</summary>
    public static void EnsureTemplate()
    {
        try
        {
            if (File.Exists(FilePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, """
                {
                  // OAuth app registrations for cloud services. See docs/CLOUD-SETUP.md in the AkioExplorer repository.
                  // Save, then choose the service again in "Add cloud account".
                  "GoogleDrive": { "ClientId": "", "ClientSecret": "" },
                  "Dropbox":     { "ClientId": "" },
                  "OneDrive":    { "ClientId": "" }
                }
                """);
        }
        catch (Exception)
        {
        }
    }

    private static void Override(CloudAppRegistration target, string idVariable, string? secretVariable)
    {
        if (Environment.GetEnvironmentVariable(idVariable) is { Length: > 0 } id) target.ClientId = id;
        if (secretVariable is not null && Environment.GetEnvironmentVariable(secretVariable) is { Length: > 0 } secret) target.ClientSecret = secret;
    }
}
