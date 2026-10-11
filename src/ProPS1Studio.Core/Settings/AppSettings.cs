using System.Text.Json;
using System.Text.Json.Serialization;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.Core.Settings;

/// <summary>A completed conversion shown on the dashboard.</summary>
public sealed class RecentConversion
{
    public DateTime Timestamp { get; set; }
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorSummary { get; set; }
    public long OutputSizeBytes { get; set; }
    public bool RequireAdministrator { get; set; }
    public bool CompressedWithUpx { get; set; }
}

/// <summary>All persisted application settings. Never contains passwords or tokens.</summary>
public sealed class AppSettings
{
    public string? Ps2exeModulePath { get; set; }
    public string? UpxPath { get; set; }
    public string? DefaultOutputDirectory { get; set; }
    public bool CompressWithUpx { get; set; }
    public bool UpxForce { get; set; }
    public bool EnableSecretScan { get; set; } = true;
    public bool EncryptScript { get; set; } = true;
    public bool ProtectOutput { get; set; } = true;
    public List<string> ExtraSecretPatterns { get; set; } = new();
    public int ConversionTimeoutSeconds { get; set; } = 180;
    public string TimestampServer { get; set; } = "http://timestamp.digicert.com";
    public string? SignCertificateThumbprint { get; set; }
    public string? SignCertificateStoreLocation { get; set; } = "CurrentUser";
    public string? SignPfxPath { get; set; }
    public bool SignAfterConversion { get; set; }
    public string LogLevel { get; set; } = "Info";
    public bool OpenOutputFolderAfterConversion { get; set; } = true;
    public List<RecentConversion> RecentConversions { get; set; } = new();
    public string? LastInputDirectory { get; set; }
    public string? LastOutputDirectory { get; set; }

    /// <summary>True once the user has authenticated with KeyAuth at least once (drives register-first flow).</summary>
    public bool AuthCompleted { get; set; }

    /// <summary>Last remembered username. Never a password.</summary>
    public string? AuthLastUsername { get; set; }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(
        JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();

    public SettingsStore(string filePath, IAppLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public string FilePath => _filePath;

    public AppSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is null)
                {
                    throw new JsonException("Settings deserialized to null.");
                }

                Normalize(settings);
                return settings;
            }
            catch (Exception ex)
            {
                _logger.Write(LogLevel.Warn, nameof(SettingsStore),
                    $"Settings file was unreadable and has been reset: {ex.Message}");
                TryBackUpCorruptFile();
                var defaults = new AppSettings();
                try
                {
                    Save(defaults);
                }
                catch
                {
                    // Defaults remain in memory even if persistence fails.
                }

                return defaults;
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
    }

    private void TryBackUpCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.ConversionTimeoutSeconds < 15)
        {
            settings.ConversionTimeoutSeconds = 15;
        }

        if (settings.ConversionTimeoutSeconds > 3600)
        {
            settings.ConversionTimeoutSeconds = 3600;
        }

        if (settings.RecentConversions.Count > 50)
        {
            settings.RecentConversions = settings.RecentConversions.Take(50).ToList();
        }

        settings.ExtraSecretPatterns ??= new List<string>();
        settings.RecentConversions ??= new List<RecentConversion>();
    }
}

/// <summary>Well-known application folders under the user profile.</summary>
public static class AppPaths
{
    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProPS1Studio");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public static string ToolsDirectory => Path.Combine(RootDirectory, "tools");
    public static string TempDirectory => Path.Combine(RootDirectory, "temp");
    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");
    public static string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");
}
