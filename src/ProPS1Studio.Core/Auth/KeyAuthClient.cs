using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.Auth;

/// <summary>
/// KeyAuth 1.3 web API client. Serialized through a gate, auto-initializes the session
/// for login/register/license calls, validates owner id on every response and maps
/// transport failures to friendly messages. Never logs secrets.
/// </summary>
public sealed class KeyAuthClient : IKeyAuthClient, IDisposable
{
    private const string AppNotFoundMessage =
        "KeyAuth application not found. Check the name/ownerid in keyauth.json.";

    private static readonly JsonSerializerOptions ResponseOptions = new(SettingsStore.JsonOptions)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly KeyAuthConfig _config;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private string? _sessionId;
    private string? _username;

    public KeyAuthClient(KeyAuthConfig config, IAppLogger? logger = null, HttpMessageHandler? handler = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool IsInitialized => _sessionId is not null;

    public string? Username => _username;

    public async Task<KeyAuthResult> InitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PostAsync(InitFields(), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<KeyAuthResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var fields = CommonFields("login");
        fields["username"] = username ?? string.Empty;
        fields["pass"] = password ?? string.Empty;
        fields["hwid"] = Hwid();
        fields["code"] = string.Empty;
        return CallAsync(fields, ct);
    }

    public Task<KeyAuthResult> RegisterAsync(
        string username,
        string password,
        string licenseKey,
        string email,
        CancellationToken ct = default)
    {
        var fields = CommonFields("register");
        fields["username"] = username ?? string.Empty;
        fields["pass"] = password ?? string.Empty;
        fields["key"] = licenseKey ?? string.Empty;
        fields["email"] = email ?? string.Empty;
        fields["hwid"] = Hwid();
        return CallAsync(fields, ct, registerUsername: username);
    }

    public Task<KeyAuthResult> LicenseAsync(string licenseKey, CancellationToken ct = default)
    {
        var fields = CommonFields("license");
        fields["key"] = licenseKey ?? string.Empty;
        fields["hwid"] = Hwid();
        fields["code"] = string.Empty;
        return CallAsync(fields, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_sessionId is null)
                {
                    return;
                }

                var fields = CommonFields("logout");
                fields["sessionid"] = _sessionId;
                await PostAsync(fields, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Best effort: the local session is cleared regardless.
        }
        finally
        {
            _sessionId = null;
            _username = null;
        }
    }

    /// <summary>MD5 of the running executable, lowercase hex - KeyAuth init "hash" field.</summary>
    public static string ComputeExeHash()
    {
        var path = Environment.ProcessPath
            ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? "unknown";
        try
        {
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes(path);
            return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        }
        catch
        {
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        }
    }

    /// <summary>Windows account SID; KeyAuth hardware id.</summary>
    public static string Hwid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.MachineName;
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }

    private Dictionary<string, string> InitFields() => new()
    {
        ["type"] = "init",
        ["ver"] = _config.Version,
        ["hash"] = ComputeExeHash(),
        ["name"] = _config.Name,
        ["ownerid"] = _config.OwnerId,
    };

    private Dictionary<string, string> CommonFields(string type) => new()
    {
        ["type"] = type,
        ["name"] = _config.Name,
        ["ownerid"] = _config.OwnerId,
    };

    /// <summary>Ensures a session exists, then performs the request. Serialized by the gate.</summary>
    private async Task<KeyAuthResult> CallAsync(
        Dictionary<string, string> fields,
        CancellationToken ct,
        string? registerUsername = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionId is null)
            {
                var init = await PostAsync(InitFields(), ct).ConfigureAwait(false);
                if (!init.Success)
                {
                    return init;
                }
            }

            fields["sessionid"] = _sessionId ?? string.Empty;
            var result = await PostAsync(fields, ct).ConfigureAwait(false);
            if (result.Success && registerUsername is not null && _username is null)
            {
                _username = registerUsername;
                return KeyAuthResult.Ok(registerUsername);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<KeyAuthResult> PostAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        if (!_config.IsConfigured)
        {
            return KeyAuthResult.Fail("KeyAuth is not configured (keyauth.json missing or incomplete).");
        }

        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await _http.PostAsync(_config.Endpoint, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Fail(fields["type"], "Too many requests. Please wait a moment and try again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(fields["type"], $"KeyAuth request failed (HTTP {(int)response.StatusCode}).");
            }

            var result = ParseResponse(body, fields["type"]);
            return result.Success ? result : Fail(fields["type"], result.Message, result.DownloadUrl);
        }
        catch (OperationCanceledException)
        {
            return Fail(fields["type"],
                ct.IsCancellationRequested ? "The request was cancelled." : "Could not reach KeyAuth (timeout). Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            return Fail(fields["type"], $"Could not reach KeyAuth: {ex.Message}");
        }
    }

    private KeyAuthResult ParseResponse(string body, string requestType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return KeyAuthResult.Fail("KeyAuth returned an empty response. Please try again.");
        }

        if (!body.TrimStart().StartsWith('{'))
        {
            return body.Contains("KeyAuth_Invalid", StringComparison.OrdinalIgnoreCase)
                ? KeyAuthResult.Fail(AppNotFoundMessage)
                : KeyAuthResult.Fail("KeyAuth returned an unexpected response. Please try again.");
        }

        AuthResponse? data;
        try
        {
            data = JsonSerializer.Deserialize<AuthResponse>(body, ResponseOptions);
        }
        catch (JsonException)
        {
            return KeyAuthResult.Fail("KeyAuth returned an unexpected response. Please try again.");
        }

        if (data is null)
        {
            return KeyAuthResult.Fail("KeyAuth returned an unexpected response. Please try again.");
        }

        if (!string.IsNullOrEmpty(data.Ownerid) && !string.Equals(data.Ownerid, _config.OwnerId, StringComparison.Ordinal))
        {
            return KeyAuthResult.Fail("KeyAuth rejected the response (owner id mismatch).");
        }

        if (string.Equals(data.Message, "invalidver", StringComparison.OrdinalIgnoreCase))
        {
            return KeyAuthResult.Fail(
                "This version is out of date. Please download the latest version.",
                data.Download);
        }

        if (!data.Success)
        {
            var message = string.IsNullOrWhiteSpace(data.Message)
                ? $"{requestType} failed."
                : data.Message!;
            return KeyAuthResult.Fail(message, data.Download);
        }

        if (!string.IsNullOrEmpty(data.Sessionid))
        {
            _sessionId = data.Sessionid;
        }

        if (!string.IsNullOrEmpty(data.Info?.Username))
        {
            _username = data.Info.Username;
        }

        _logger?.Write(LogLevel.Debug, nameof(KeyAuthClient), $"{requestType}: ok.");
        return KeyAuthResult.Ok(_username);
    }

    private KeyAuthResult Fail(string requestType, string message, string? downloadUrl = null)
    {
        _logger?.Write(LogLevel.Warn, nameof(KeyAuthClient), $"{requestType}: {message}");
        return KeyAuthResult.Fail(message, downloadUrl);
    }

    private sealed class AuthResponse
    {
        public bool Success { get; set; }

        public string? Message { get; set; }

        public string? Sessionid { get; set; }

        public string? Ownerid { get; set; }

        public string? Download { get; set; }

        public InfoData? Info { get; set; }

        public sealed class InfoData
        {
            public string? Username { get; set; }
        }
    }
}
