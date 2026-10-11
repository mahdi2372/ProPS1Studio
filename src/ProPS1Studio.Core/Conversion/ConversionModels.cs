using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Security;

namespace ProPS1Studio.Core.Conversion;

/// <summary>Everything the engine needs to convert one script.</summary>
public sealed class ConversionRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    public bool OverwriteOutput { get; init; }
    public bool RequireAdministrator { get; init; }
    public bool NoConsole { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Copyright { get; init; }
    public string? FileVersion { get; init; }
    public string? IconPath { get; init; }

    /// <summary>Path to the ps2exe .psd1/.psm1; null means rely on PSModulePath discovery.</summary>
    public string? Ps2exeModulePath { get; init; }

    public bool CompressWithUpx { get; init; }
    public bool UpxForce { get; init; }
    public bool UpxFailureIsFatal { get; init; }

    public bool EnableSecretScan { get; init; } = true;
    public bool SecretFindingsAreFatal { get; init; }

    /// <summary>Bake the script into the executable as AES-256-CBC ciphertext instead of plain source.</summary>
    public bool EncryptScript { get; init; }

    /// <summary>Wraps the finished executable in a per-build AES-256 encrypted stub with an
    /// anti-debug gate. Off by default in the engine so existing tests keep byte-stable output;
    /// the UI turns it on by default.</summary>
    public bool ProtectOutput { get; init; }

    public bool SignAfterConversion { get; init; }
    public bool SigningFailureIsFatal { get; init; }
    public SignRequest? SignRequest { get; init; }

    public int TimeoutSeconds { get; init; } = 180;
}

/// <summary>Final outcome of a conversion pipeline run.</summary>
public sealed record ConversionResult
{
    public required bool Success { get; init; }
    public AppError? Error { get; init; }
    public string? OutputPath { get; init; }
    public long OutputSizeBytes { get; init; }
    public PeInfo? Pe { get; init; }
    public RequestedExecutionLevel ExecutionLevel { get; init; }
    public bool UpxCompressed { get; init; }
    public long? SizeBeforeUpx { get; init; }
    public bool Signed { get; init; }
    public string? SignatureStatus { get; init; }
    public bool Encrypted { get; init; }

    /// <summary>True when the output was wrapped in the encrypted anti-debug stub.</summary>
    public bool Protected { get; init; }
    public IReadOnlyList<SecretFinding> SecretFindings { get; init; } = Array.Empty<SecretFinding>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompilerOutput { get; init; } = Array.Empty<string>();
    public TimeSpan Duration { get; init; }
}

/// <summary>Which options the installed ps2exe module actually accepts (probed at runtime).</summary>
public sealed record Ps2exeCapability
{
    public bool IsAvailable { get; init; }
    public string? ModuleVersion { get; init; }
    public string? ModulePath { get; init; }
    public string? Error { get; init; }
    public IReadOnlySet<string> Parameters { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool Supports(string parameterName) => Parameters.Contains(parameterName);

    public static Ps2exeCapability Unavailable(string reason) => new()
    {
        IsAvailable = false,
        Error = reason,
    };
}
