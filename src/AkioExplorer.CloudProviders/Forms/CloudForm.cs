using System.Text.Json;
using AkioExplorer.Core.Cloud;
using AkioExplorer.Core.Models;

namespace AkioExplorer.CloudProviders.Forms;

/// <summary>Secrets of one account (password, secret key…), kept as one JSON object in Credential Manager.</summary>
public sealed class CloudSecrets
{
    private readonly Dictionary<string, string> _values;
    private readonly string _accountName;

    public CloudSecrets(IReadOnlyDictionary<string, string> values, string accountName)
    {
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        _accountName = accountName;
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? Get(string key) => _values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>The secret, or an "account needs reconnecting" error when Credential Manager lost it.</summary>
    public string Require(string key) => Get(key) ?? throw new StorageException(StorageErrorKind.AuthenticationRequired,
        $"The saved sign-in for \"{_accountName}\" is missing. Reconnect the account to continue.");

    public string Serialize() => JsonSerializer.Serialize(_values);

    public static CloudSecrets Load(ICredentialStore store, CloudAccount account)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (store.Load(account.CredentialKey) is { Length: > 0 } text)
        {
            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, string>>(text) is { } parsed)
                    foreach (var (key, value) in parsed) values[key] = value;
            }
            catch (JsonException)
            {
                // Not ours (or corrupt): treat as missing, the account asks to be reconnected.
            }
        }
        return new CloudSecrets(values, account.DisplayName);
    }
}

public static class CloudForm
{
    /// <summary>
    /// Form values as they will be used: typed values win, then stored settings (reconnect), then defaults.
    /// Text is trimmed; secrets are kept exactly as typed.
    /// </summary>
    public static Dictionary<string, string> Merge(IReadOnlyList<CloudField> fields, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? existing)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            string value = values.TryGetValue(field.Key, out var typed) ? typed : string.Empty;
            if (!field.IsSecret) value = value.Trim();
            if (value.Length == 0 && !field.IsSecret && existing is not null && existing.TryGetValue(field.Key, out var stored)) value = stored;
            if (value.Length == 0 && field.Default is { } fallback && !field.IsSecret) value = fallback;
            if (value.Length > 0) result[field.Key] = value;
        }
        return result;
    }

    /// <summary>The non-secret values (what goes into <see cref="CloudAccount.Settings"/>).</summary>
    public static Dictionary<string, string> NonSecret(IReadOnlyList<CloudField> fields, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? existing)
    {
        var secretKeys = fields.Where(f => f.IsSecret).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return Merge(fields, values, existing).Where(kv => !secretKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Throws a plain-language error naming the first required field left empty.</summary>
    public static void RequireFilled(IReadOnlyList<CloudField> fields, IReadOnlyDictionary<string, string> settings, CloudSecrets secrets)
    {
        foreach (var field in fields)
        {
            if (field.Optional) continue;
            bool filled = field.IsSecret ? secrets.Get(field.Key) is not null : settings.ContainsKey(field.Key);
            if (!filled) throw new StorageException(StorageErrorKind.InvalidName, $"Enter the {Lower(field.Label)}.");
        }
    }

    public static bool IsTrue(this CloudAccount account, string key) =>
        account.Settings.TryGetValue(key, out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static int Int(this CloudAccount account, string key, int fallback) =>
        int.TryParse(account.Setting(key), out int value) ? value : fallback;

    /// <summary>"https://" in front of bare host names, no trailing slash.</summary>
    public static string NormalizeUrl(string url, string defaultScheme = "https")
    {
        url = url.Trim();
        if (url.Length == 0) return url;
        if (!url.Contains("://", StringComparison.Ordinal)) url = defaultScheme + "://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new StorageException(StorageErrorKind.InvalidName, $"\"{url}\" isn't a valid address.");
        return url.TrimEnd('/');
    }

    /// <summary>"User name" → "user name", but acronyms stay ("SSH key", "AccessKey ID").</summary>
    private static string Lower(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;
}
