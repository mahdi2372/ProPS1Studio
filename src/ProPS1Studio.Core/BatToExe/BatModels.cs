using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Security;

namespace ProPS1Studio.Core.BatToExe;

/// <summary>UAC privilege level requested by the generated executable's manifest.</summary>
public enum BatExecutionLevel
{
    AsInvoker,
    RequireAdministrator,
    HighestAvailable,
}

/// <summary>Version information written into the generated executable's VERSION resource.</summary>
public sealed record BatVersionInfo(
    string Title,
    string Description,
    string Product,
    string Company,
    string Copyright,
    string FileVersion,
    string OriginalFilename);

/// <summary>A file bundled into the executable, decoded next to the extracted script when it runs.</summary>
public sealed record BatEmbeddedFile(string Name, string Mode, string Data);

/// <summary>Payload as stored inside the executable: a Base64 blob plus its encoding mode.
/// <see cref="AesSecret"/> carries the random AES key material for AES modes; it is never
/// embedded in the blob itself (only an IV and salt are), so the blob alone cannot be decrypted.</summary>
public sealed record BatPayload(string Mode, string Data, byte[]? AesSecret = null);

/// <summary>Everything the engine needs to convert one batch file.</summary>
public sealed class BatConversionRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    public bool OverwriteOutput { get; init; }
    public BatExecutionLevel ExecutionLevel { get; init; } = BatExecutionLevel.AsInvoker;

    public bool NoConsole { get; init; }
    public bool CaptureOutputLog { get; init; }
    public bool KeepConsoleOpen { get; init; }
    public bool ForwardArguments { get; init; } = true;
    public bool RunFromTempDirectory { get; init; }
    public bool KeepTempFile { get; init; }
    public string? ConsoleTitle { get; init; }

    /// <summary>Two hex digits: high nibble = background, low nibble = foreground (cmd "color" convention).</summary>
    public string? ConsoleColorHex { get; init; }

    public bool CompressPayload { get; init; } = true;
    public string? Passphrase { get; init; }

    /// <summary>AES-256-encrypts the embedded payload. On by default: without it the
    /// Base64+GZip payload is trivially reversed back to the batch source.</summary>
    public bool EncryptPayload { get; init; } = true;

    /// <summary>Wraps the finished executable in a per-build AES-256 encrypted stub with an
    /// anti-debug gate. Off by default in the engine so existing tests keep byte-stable output;
    /// the UI turns it on by default.</summary>
    public bool ProtectOutput { get; init; }

    public IReadOnlyList<string> ExtraFilePaths { get; init; } = Array.Empty<string>();

    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Copyright { get; init; }
    public string? FileVersion { get; init; }
    public string? IconPath { get; init; }

    public bool EnableSecretScan { get; init; } = true;
    public bool SecretFindingsAreFatal { get; init; }

    public bool SignAfterConversion { get; init; }
    public bool SigningFailureIsFatal { get; init; }
    public SignRequest? SignRequest { get; init; }

    public int TimeoutSeconds { get; init; } = 180;
}

/// <summary>Final outcome of a batch-to-executable conversion run.</summary>
public sealed record BatConversionResult
{
    public required bool Success { get; init; }
    public AppError? Error { get; init; }
    public string? OutputPath { get; init; }
    public long OutputSizeBytes { get; init; }
    public PeInfo? Pe { get; init; }
    public RequestedExecutionLevel ExecutionLevel { get; init; }
    public bool PayloadCompressed { get; init; }
    public bool PayloadObfuscated { get; init; }
    public bool PayloadEncrypted { get; init; }

    /// <summary>True when the output was wrapped in the encrypted anti-debug stub.</summary>
    public bool Protected { get; init; }
    public int ExtraFileCount { get; init; }
    public bool Signed { get; init; }
    public string? SignatureStatus { get; init; }
    public IReadOnlyList<SecretFinding> SecretFindings { get; init; } = Array.Empty<SecretFinding>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompilerOutput { get; init; } = Array.Empty<string>();
    public TimeSpan Duration { get; init; }
}
