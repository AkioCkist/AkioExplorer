using System.Text.Json;
using System.Text.Json.Serialization;
using AkioExplorer.Core.Abstractions;
using AkioExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AkioExplorer.Infrastructure.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Settings in %LocalAppData%\AkioExplorer\settings.json. Contains preferences only – never credentials
/// (those go to Windows Credential Manager in Phase 3). Writes are debounced and atomic (temp file + replace).
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private bool _dirty;

    public JsonSettingsService(ILogger<JsonSettingsService> logger)
        : this(Path.Combine(AppPaths.DataFolder, "settings.json"), logger)
    {
    }

    public JsonSettingsService(string path, ILogger<JsonSettingsService> logger)
    {
        _path = path;
        _logger = logger;
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();
    }

    public AppSettings Current { get; }

    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        Save();
    }

    public void Save()
    {
        lock (_gate)
        {
            _dirty = true;
            _debounce.Change(500, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save settings to {Path}", _path);
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings file is corrupt, using defaults");
            try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { /* best effort */ }
        }

        return new AppSettings();
    }

    public void Dispose()
    {
        _debounce.Dispose();
        Flush();
    }
}

public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AkioExplorer");

    public static string LogFolder { get; } = Path.Combine(DataFolder, "logs");
}
