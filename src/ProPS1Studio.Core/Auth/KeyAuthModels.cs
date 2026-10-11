namespace ProPS1Studio.Core.Auth;

/// <summary>
/// KeyAuth application credentials. Populated from keyauth.json (exe dir, working dir or
/// %LOCALAPPDATA%\ProPS1Studio) with PROPS_KEYAUTH_* environment variable overrides.
/// </summary>
public sealed class KeyAuthConfig
{
    public string Name { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string Version { get; set; } = "1.0";

    public string Endpoint { get; set; } = "https://keyauth.win/api/1.3/";

    /// <summary>True when credentials look complete enough to contact KeyAuth.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Name) && OwnerId.Length == 10;
}

/// <summary>Outcome of a single KeyAuth API call. Never contains secrets.</summary>
public sealed record KeyAuthResult(bool Success, string Message, string? Username = null, string? DownloadUrl = null)
{
    public static KeyAuthResult Ok(string? username = null) => new(true, string.Empty, username);

    public static KeyAuthResult Fail(string message, string? downloadUrl = null) =>
        new(false, message, null, downloadUrl);
}
