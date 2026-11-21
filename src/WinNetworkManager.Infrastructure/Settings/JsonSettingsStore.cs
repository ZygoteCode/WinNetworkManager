using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Infrastructure.Settings;

/// <summary>Source-generated JSON metadata for settings (no reflection, trimming-safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Result of loading settings.</summary>
/// <param name="Settings">Validated settings.</param>
/// <param name="Issues">Problems found (corrupted file, clamped values), for display to the user.</param>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Issues);

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON. Reads are validated and clamped; a corrupted file is preserved as
/// <c>settings.json.corrupt-*</c> and defaults are used. Writes are atomic (temporary file + replace) so a crash
/// never leaves a half-written document. Thread-safe.
/// </summary>
public sealed class JsonSettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private AppSettings _current = new();

    /// <summary>Creates a store for <paramref name="path"/>.</summary>
    public JsonSettingsStore(string path, ILogger<JsonSettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after settings change (on the thread that called <see cref="Save"/>).</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Current settings.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Loads settings from disk (defaults when the file does not exist).</summary>
    public SettingsLoadResult Load()
    {
        var issues = new List<string>();
        AppSettings? loaded = null;
        if (File.Exists(_path))
        {
            try
            {
                using var stream = File.OpenRead(_path);
                loaded = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                string backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try
                {
                    File.Copy(_path, backup, overwrite: true);
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(copyEx, "Could not preserve the unreadable settings file.");
                }

                issues.Add($"The settings file could not be read ({ErrorText.Describe(ex)}); defaults are used. The original was kept as {Path.GetFileName(backup)}.");
                _logger.LogWarning(ex, "Settings file unreadable; using defaults.");
            }
        }

        var validation = SettingsValidator.Validate(loaded ?? new AppSettings());
        issues.AddRange(validation.Issues);
        lock (_gate)
        {
            _current = validation.Settings;
        }

        foreach (string issue in validation.Issues)
        {
            _logger.LogWarning("Settings: {Issue}", issue);
        }

        return new SettingsLoadResult(validation.Settings, issues);
    }

    /// <summary>Validates, stores and persists <paramref name="settings"/>; returns the normalized value.</summary>
    public AppSettings Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = SettingsValidator.Validate(settings).Settings;
        lock (_gate)
        {
            WriteAtomically(normalized);
            _current = normalized;
        }

        Changed?.Invoke(this, normalized);
        return normalized;
    }

    /// <summary>Applies a change to the current settings and saves.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Save(change(Current));
    }

    private void WriteAtomically(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        }

        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Serializes settings (diagnostics, export of configuration).</summary>
    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
}
