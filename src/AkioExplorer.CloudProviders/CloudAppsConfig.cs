using System.Reflection;
using System.Text.Json;
using AkioExplorer.Core.Cloud;

namespace AkioExplorer.CloudProviders;

public sealed class CloudAppRegistration
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// OAuth app registrations (client ids) for each service. They are not user secrets: desktop apps are public clients.
/// Sources, later wins:
/// 1. built into the build: <c>src/AkioExplorer.CloudProviders/cloud-apps.builtin.json</c> (git-ignored, embedded
///    at compile time) – with it, end users just click a service and sign in in their browser;
/// 2. <c>%LocalAppData%\AkioExplorer\cloud-apps.json</c> (written by the "Add cloud account" dialog);
/// 3. environment variables.
/// See docs/CLOUD-SETUP.md.
/// </summary>
public sealed class CloudAppsConfig
{
    internal const string BuiltInResourceName = "AkioExplorer.CloudApps.BuiltIn.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public CloudAppRegistration GoogleDrive { get; set; } = new();

    public CloudAppRegistration Dropbox { get; set; } = new();

    public CloudAppRegistration OneDrive { get; set; } = new();

    public CloudAppRegistration Box { get; set; } = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AkioExplorer", "cloud-apps.json");

    public static CloudAppsConfig Load()
    {
        var config = LoadBuiltIn() ?? new CloudAppsConfig();
        if (ReadFile() is { } fromFile)
        {
            Merge(config.GoogleDrive, fromFile.GoogleDrive);
            Merge(config.Dropbox, fromFile.Dropbox);
            Merge(config.OneDrive, fromFile.OneDrive);
            Merge(config.Box, fromFile.Box);
        }

        Override(config.GoogleDrive, "AKIO_GOOGLE_CLIENT_ID", "AKIO_GOOGLE_CLIENT_SECRET");
        Override(config.Dropbox, "AKIO_DROPBOX_APP_KEY", null);
        Override(config.OneDrive, "AKIO_ONEDRIVE_CLIENT_ID", null);
        Override(config.Box, "AKIO_BOX_CLIENT_ID", "AKIO_BOX_CLIENT_SECRET");
        return config;
    }

    /// <summary>Re-reads the file and environment in place, so edits apply without restarting the app.</summary>
    public void Reload()
    {
        var fresh = Load();
        GoogleDrive = fresh.GoogleDrive;
        Dropbox = fresh.Dropbox;
        OneDrive = fresh.OneDrive;
        Box = fresh.Box;
    }

    public CloudAppRegistration For(CloudProviderType type) => type switch
    {
        CloudProviderType.GoogleDrive => GoogleDrive,
        CloudProviderType.Dropbox => Dropbox,
        CloudProviderType.Box => Box,
        _ => OneDrive, // OneDrive, OneDrive for Business and SharePoint share one Microsoft registration
    };

    /// <summary>Stores one service's registration in the user's cloud-apps.json and applies it immediately.</summary>
    public void Save(CloudProviderType type, string clientId, string? clientSecret)
    {
        var file = ReadFile() ?? new CloudAppsConfig();
        var target = file.For(type);
        target.ClientId = clientId.Trim();
        target.ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim();

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, WriteOptions));
        Reload();
    }

    /// <summary>True when the user's cloud-apps.json holds an id for this service (the part <see cref="Clear"/> can remove).</summary>
    public static bool HasSaved(CloudProviderType type) => ReadFile()?.For(type).IsSet == true;

    /// <summary>
    /// Removes one service's saved registration from the user's cloud-apps.json and applies it immediately.
    /// An id built into the build or set by an environment variable still applies afterwards.
    /// </summary>
    public void Clear(CloudProviderType type)
    {
        if (ReadFile() is not { } file) return;
        var target = file.For(type);
        target.ClientId = null;
        target.ClientSecret = null;

        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, WriteOptions));
        Reload();
    }

    private static CloudAppsConfig? LoadBuiltIn()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BuiltInResourceName);
            return stream is null ? null : JsonSerializer.Deserialize<CloudAppsConfig>(stream, ReadOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CloudAppsConfig? ReadFile()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<CloudAppsConfig>(File.ReadAllText(FilePath), ReadOptions) : null;
        }
        catch (Exception)
        {
            // A malformed file must not stop the app; built-in ids (if any) still apply.
            return null;
        }
    }

    private static void Merge(CloudAppRegistration target, CloudAppRegistration? source)
    {
        if (source is null || !source.IsSet) return;
        target.ClientId = source.ClientId;
        target.ClientSecret = source.ClientSecret;
    }

    private static void Override(CloudAppRegistration target, string idVariable, string? secretVariable)
    {
        if (Environment.GetEnvironmentVariable(idVariable) is { Length: > 0 } id) target.ClientId = id;
        if (secretVariable is not null && Environment.GetEnvironmentVariable(secretVariable) is { Length: > 0 } secret) target.ClientSecret = secret;
    }
}
