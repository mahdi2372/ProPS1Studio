namespace ProPS1Studio.Core.Auth;

/// <summary>Async client for the KeyAuth 1.3 web API.</summary>
public interface IKeyAuthClient
{
    /// <summary>True once a session was established via <see cref="InitAsync"/>.</summary>
    bool IsInitialized { get; }

    /// <summary>Signed-in username (from KeyAuth), if any.</summary>
    string? Username { get; }

    Task<KeyAuthResult> InitAsync(CancellationToken ct = default);

    Task<KeyAuthResult> LoginAsync(string username, string password, CancellationToken ct = default);

    Task<KeyAuthResult> RegisterAsync(
        string username,
        string password,
        string licenseKey,
        string email,
        CancellationToken ct = default);

    Task<KeyAuthResult> LicenseAsync(string licenseKey, CancellationToken ct = default);

    /// <summary>Ends the session. Best effort - never throws for network problems.</summary>
    Task LogoutAsync(CancellationToken ct = default);
}
