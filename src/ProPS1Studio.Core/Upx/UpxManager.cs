using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Pe;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.Upx;

public sealed class UpxPackOptions
{
    public bool Force { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public string? UpxPath { get; init; }
}

public sealed record UpxPackResult
{
    public required bool Success { get; init; }
    public bool WasPacked { get; init; }
    public AppError? Error { get; init; }
    public long OriginalSize { get; init; }
    public long PackedSize { get; init; }
    public string? BackupPath { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? DetectedVersion { get; init; }
    public bool MatchesPinnedHash { get; init; }
}

public interface IUpxPacker
{
    Task<UpxPackResult> PackAsync(string exePath, UpxPackOptions options, CancellationToken cancellationToken = default);
    Task<UpxAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);
}

public sealed record UpxAvailability
{
    public required bool Available { get; init; }
    public string? Path { get; init; }
    public string? Version { get; init; }
    public bool MatchesPinnedHash { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// UPX compression with hard safety rules:
/// .NET assemblies are never packed (UPX refuses them), the original file is backed up,
/// the packed result is verified with `upx -t`, and the backup is restored on any failure.
/// </summary>
public sealed class UpxManager : IUpxPacker
{
    /// <summary>
    /// SHA-256 of upx.exe from the official GitHub release UPX 5.2.1 win64 zip.
    /// Downloads produced by the Setup page are checked against this value.
    /// </summary>
    public const string Upx521Win64Sha256 = "D20EBE0B7B22B6BE968C8C34BE61F94DDEA12CB11462E2CEC27F548EF9574DF8";

    /// <summary>SHA-256 of the official upx-5.2.1-win64.zip archive itself.</summary>
    public const string Upx521Win64ZipSha256 = "EABC6792A347D45E945BE7748423E7868FD01B0D2BCAA2F4B1031FD71FF69BDA";

    public const string Upx521ZipUrl =
        "https://github.com/upx/upx/releases/download/v5.2.1/upx-5.2.1-win64.zip";

    private static readonly string[] UnpackableMessages =
    {
        "are not yet supported",
        ".net files",
        "not supported",
        "cantdetectformat",
        "cantpack",
        "guard_cf enabled",
        "unsupported",
    };

    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;

    public UpxManager(IProcessRunner runner, IAppLogger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<UpxAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var resolved = ResolveUpxPath(null);
        if (resolved is null)
        {
            return new UpxAvailability { Available = false, Error = "UPX was not found." };
        }

        var probe = await RunAsync(resolved, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
        if (!probe.Success)
        {
            return new UpxAvailability
            {
                Available = false,
                Path = resolved,
                Error = $"UPX at '{resolved}' did not run: {probe.StdErr}".Trim(),
            };
        }

        var version = ParseVersion(probe.StdOut);
        var pinned = ToolLocator.MatchesAnySha256(resolved, new[] { Upx521Win64Sha256 });
        return new UpxAvailability
        {
            Available = true,
            Path = resolved,
            Version = version,
            MatchesPinnedHash = pinned,
        };
    }

    public async Task<UpxPackResult> PackAsync(string exePath, UpxPackOptions options, CancellationToken cancellationToken = default)
    {
        var source = nameof(UpxManager);

        if (!File.Exists(exePath))
        {
            return Fail(ErrorCatalog.UpxFailed($"File not found: {exePath}", null));
        }

        var pe = PeFile.Read(exePath);
        if (!pe.IsValid)
        {
            return Fail(ErrorCatalog.UpxFailed($"Not a valid executable: {exePath}", null));
        }

        if (pe.IsDotNet)
        {
            // UPX hard-refuses .NET files; never even try (Force does not override this).
            return Fail(ErrorCatalog.UpxIncompatible(
                "the file is a .NET assembly, which UPX does not support"));
        }

        var upxPath = ResolveUpxPath(options.UpxPath);
        if (upxPath is null)
        {
            return Fail(ErrorCatalog.UpxMissing());
        }

        var availability = await CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        long originalSize;
        try
        {
            originalSize = new FileInfo(exePath).Length;
        }
        catch (Exception ex)
        {
            return Fail(ErrorCatalog.UpxFailed(ex.Message, null));
        }

        var backupPath = exePath + ".upxbak";
        var packedTemp = exePath + ".upxnew";
        try
        {
            try
            {
                File.Copy(exePath, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                return Fail(ErrorCatalog.UpxFailed($"Could not create a backup: {ex.Message}", null));
            }

            if (File.Exists(packedTemp))
            {
                File.Delete(packedTemp);
            }

            var packArgs = options.Force
                ? new[] { "-9", "-q", "--force", "-o", packedTemp, exePath }
                : new[] { "-9", "-q", "-o", packedTemp, exePath };

            _logger.Write(LogLevel.Info, source, $"Running UPX on '{exePath}'...");
            var pack = await RunAsync(upxPath, packArgs, cancellationToken, options.TimeoutSeconds * 1000)
                .ConfigureAwait(false);

            if (pack.FailedToStart)
            {
                return Fail(ErrorCatalog.UpxFailed($"UPX could not be started: {pack.StartError}", backupPath));
            }

            if (pack.TimedOut)
            {
                return Fail(ErrorCatalog.UpxFailed("UPX timed out", backupPath));
            }

            if (pack.ExitCode != 0 || !File.Exists(packedTemp))
            {
                var detail = FirstUsefulLine(pack.StdErr, pack.StdOut) ?? $"exit code {pack.ExitCode}";
                if (IsIncompatible(detail))
                {
                    Cleanup(packedTemp);
                    return Fail(ErrorCatalog.UpxIncompatible(detail), originalSize, backupPath, availability);
                }

                Cleanup(packedTemp);
                return Fail(ErrorCatalog.UpxFailed(detail, backupPath), originalSize, backupPath, availability);
            }

            // Verify the freshly packed file before it replaces anything.
            var verify = await RunAsync(upxPath, new[] { "-t", packedTemp }, cancellationToken)
                .ConfigureAwait(false);
            if (!verify.Success)
            {
                Cleanup(packedTemp);
                return new UpxPackResult
                {
                    Success = false,
                    Error = ErrorCatalog.UpxVerificationFailed(packedTemp),
                    OriginalSize = originalSize,
                    BackupPath = backupPath,
                    Output = Combined(pack),
                    DetectedVersion = availability.Version,
                    MatchesPinnedHash = availability.MatchesPinnedHash,
                };
            }

            long packedSize;
            try
            {
                packedSize = new FileInfo(packedTemp).Length;
                if (packedSize <= 0 || packedSize >= originalSize)
                {
                    Cleanup(packedTemp);
                    return Fail(ErrorCatalog.UpxFailed(
                            $"compression did not shrink the file ({originalSize} → {packedSize} bytes)",
                            backupPath),
                        originalSize, backupPath, availability);
                }

                File.Copy(packedTemp, exePath, overwrite: true);
            }
            catch (Exception ex)
            {
                RestoreFromBackup(backupPath, exePath);
                Cleanup(packedTemp);
                return Fail(ErrorCatalog.UpxFailed($"Could not apply the compressed file: {ex.Message}", backupPath),
                    originalSize, backupPath, availability);
            }

            Cleanup(packedTemp);
            _logger.Write(LogLevel.Info, source,
                $"UPX packed '{exePath}' {ErrorCatalog.FormatBytes(originalSize)} → {ErrorCatalog.FormatBytes(packedSize)} (backup: {backupPath}).");

            return new UpxPackResult
            {
                Success = true,
                WasPacked = true,
                OriginalSize = originalSize,
                PackedSize = packedSize,
                BackupPath = backupPath,
                Output = Combined(pack),
                DetectedVersion = availability.Version,
                MatchesPinnedHash = availability.MatchesPinnedHash,
            };
        }
        catch (OperationCanceledException)
        {
            RestoreFromBackup(backupPath, exePath);
            Cleanup(packedTemp);
            throw;
        }
        catch (Exception ex)
        {
            RestoreFromBackup(backupPath, exePath);
            Cleanup(packedTemp);
            return Fail(ErrorCatalog.UpxFailed(ex.Message, backupPath), originalSize, backupPath, availability);
        }
    }

    private string? ResolveUpxPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var bundled = Path.Combine(AppPaths.ToolsDirectory, "upx", "upx.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return ToolLocator.FindOnPath("upx.exe");
    }

    private async Task<ProcessResult> RunAsync(
        string upxPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = 60_000)
    {
        var result = await _runner.RunAsync(
            new ProcessSpec
            {
                FileName = upxPath,
                Arguments = arguments,
                TimeoutMilliseconds = timeoutMilliseconds,
            },
            line => _logger.Write(LogLevel.Debug, nameof(UpxManager), line),
            line => _logger.Write(LogLevel.Debug, nameof(UpxManager), line),
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static string Combined(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdOut + result.StdErr;

    private static string? ParseVersion(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("upx ", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["upx ".Length..].Split(' ')[0].Trim();
            }
        }

        return null;
    }

    private static bool IsIncompatible(string detail)
    {
        var text = detail.ToLowerInvariant();
        return UnpackableMessages.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private static string? FirstUsefulLine(string stderr, string stdout)
    {
        foreach (var candidate in new[] { stderr, stdout })
        {
            foreach (var line in candidate.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("upx:", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
            }
        }

        return null;
    }

    private static void RestoreFromBackup(string backupPath, string targetPath)
    {
        try
        {
            if (File.Exists(backupPath) && File.Exists(targetPath))
            {
                File.Copy(backupPath, targetPath, overwrite: true);
            }
        }
        catch
        {
            // The backup file itself remains available for manual recovery.
        }
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private UpxPackResult Fail(AppError error, long originalSize = 0, string? backupPath = null, UpxAvailability? availability = null) => new()
    {
        Success = false,
        Error = error,
        OriginalSize = originalSize,
        BackupPath = backupPath,
        DetectedVersion = availability?.Version,
        MatchesPinnedHash = availability?.MatchesPinnedHash ?? false,
    };
}
