using System.Text.Json;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.Auth;

/// <summary>
/// Loads KeyAuth application credentials from keyauth.json (exe directory, working directory
/// or %LOCALAPPDATA%\ProPS1Studio) with optional PROPS_KEYAUTH_* environment overrides.
/// </summary>
public static class KeyAuthConfigLoader
{
    public const string FileName = "keyauth.json";

    private static readonly JsonSerializerOptions JsonOptions = new(SettingsStore.JsonOptions)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static KeyAuthConfig Load(IAppLogger? logger = null)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Environment.CurrentDirectory, FileName),
            Path.Combine(AppPaths.RootDirectory, FileName),
        };

        return Load(candidates, Environment.GetEnvironmentVariable, logger);
    }

    public static KeyAuthConfig Load(
        IEnumerable<string> candidateFiles,
        Func<string, string?> getEnv,
        IAppLogger? logger = null)
    {
        var config = new KeyAuthConfig();

        foreach (var path in candidateFiles)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = Parse(json);
                if (parsed is null)
                {
                    logger?.Write(LogLevel.Warn, nameof(KeyAuthConfigLoader),
                        $"{FileName} at '{path}' was unreadable; using defaults.");
                }
                else
                {
                    config = parsed;
                    logger?.Write(LogLevel.Debug, nameof(KeyAuthConfigLoader),
                        $"Loaded KeyAuth config from '{path}'.");
                }
            }
            catch (Exception ex)
            {
                logger?.Write(LogLevel.Warn, nameof(KeyAuthConfigLoader),
                    $"{FileName} at '{path}' could not be read: {ex.Message}");
            }

            break; // First existing file wins.
        }

        ApplyEnvironment(config, getEnv);
        Normalize(config);
        return config;
    }

    /// <summary>Deserializes a keyauth.json document; returns null when malformed.</summary>
    public static KeyAuthConfig? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KeyAuthConfig>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void ApplyEnvironment(KeyAuthConfig config, Func<string, string?> getEnv)
    {
        var name = getEnv("PROPS_KEYAUTH_NAME");
        if (!string.IsNullOrWhiteSpace(name))
        {
            config.Name = name;
        }

        var ownerId = getEnv("PROPS_KEYAUTH_OWNERID");
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            config.OwnerId = ownerId;
        }

        var version = getEnv("PROPS_KEYAUTH_VERSION");
        if (!string.IsNullOrWhiteSpace(version))
        {
            config.Version = version;
        }

        var endpoint = getEnv("PROPS_KEYAUTH_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            config.Endpoint = endpoint;
        }
    }

    private static void Normalize(KeyAuthConfig config)
    {
        config.Name = config.Name?.Trim() ?? string.Empty;
        config.OwnerId = config.OwnerId?.Trim() ?? string.Empty;
        config.Version = string.IsNullOrWhiteSpace(config.Version) ? "1.0" : config.Version.Trim();
        config.Endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? "https://keyauth.win/api/1.3/"
            : config.Endpoint.Trim();
        if (!config.Endpoint.EndsWith('/'))
        {
            config.Endpoint += "/";
        }
    }
}
