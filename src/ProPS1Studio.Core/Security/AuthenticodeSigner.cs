using System.Text;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;

namespace ProPS1Studio.Core.Security;

/// <summary>Request to Authenticode-sign one file. <see cref="PfxPassword"/> lives in memory only.</summary>
public sealed record SignRequest
{
    public required string TargetPath { get; init; }
    public string? CertificateThumbprint { get; init; }
    public string StoreLocation { get; init; } = "CurrentUser";
    public string? PfxPath { get; init; }
    public string? PfxPassword { get; init; }
    public string? TimestampServer { get; init; } = "http://timestamp.digicert.com";
    public string? Description { get; init; }
}

public sealed record SignResult
{
    public required bool Success { get; init; }
    public AppError? Error { get; init; }
    public string? SignatureStatus { get; init; }
    public string? StatusMessage { get; init; }
    public string? Warning { get; init; }
}

public interface IAuthenticodeSigner
{
    Task<SignResult> SignAsync(SignRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Signs executables with Set-AuthenticodeSignature in a child PowerShell.
/// PFX passwords travel via a child-process environment variable — never argv, never logs.
/// </summary>
public sealed class AuthenticodeSigner : IAuthenticodeSigner
{
    public const string PasswordEnvironmentVariable = "PROPS_SIGN_PFX_PASSWORD";

    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;

    public AuthenticodeSigner(IProcessRunner runner, IAppLogger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<SignResult> SignAsync(SignRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TargetPath))
        {
            return Failed(ErrorCatalog.SignFailed($"File not found: {request.TargetPath}"));
        }

        if (string.IsNullOrWhiteSpace(request.CertificateThumbprint) &&
            string.IsNullOrWhiteSpace(request.PfxPath))
        {
            return Failed(new AppError(ErrorCode.CertificateNotFound, "No certificate was configured for signing.",
                "Select a certificate thumbprint in Settings, or provide a PFX file."));
        }

        if (request.PfxPath is not null && !File.Exists(request.PfxPath))
        {
            return Failed(ErrorCatalog.SignFailed($"PFX file not found: {request.PfxPath}"));
        }

        var script = BuildScript(request);
        var directory = Path.Combine(Settings.AppPaths.TempDirectory, $"sign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "sign.ps1");

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken)
                .ConfigureAwait(false);

            var environment = new Dictionary<string, string>();
            if (request.PfxPassword is not null)
            {
                environment[PasswordEnvironmentVariable] = request.PfxPassword;
            }

            var result = await _runner.RunAsync(
                new ProcessSpec
                {
                    FileName = "powershell.exe",
                    Arguments = new[]
                    {
                        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", scriptPath,
                    },
                    WorkingDirectory = directory,
                    EnvironmentVariables = environment,
                    TimeoutMilliseconds = 60_000,
                },
                line => _logger.Write(LogLevel.Debug, nameof(AuthenticodeSigner), line),
                line => _logger.Write(LogLevel.Warn, nameof(AuthenticodeSigner), line),
                cancellationToken).ConfigureAwait(false);

            var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var status = ValueOf(lines, "STATUS=");
            var statusMessage = ValueOf(lines, "MESSAGE=");
            var timestampFallback = ValueOf(lines, "TIMESTAMP_FALLBACK=");
            var error = ValueOf(lines, "ERROR=");

            if (result.TimedOut)
            {
                return Failed(ErrorCatalog.SignFailed("Signing timed out."));
            }

            if (result.FailedToStart)
            {
                return Failed(ErrorCatalog.SignFailed($"Could not start PowerShell: {result.StartError}"));
            }

            if (error is not null || result.ExitCode != 0)
            {
                return Failed(MapError(error ?? result.StdErr.Trim()));
            }

            if (status is null || status.Equals("NotSigned", StringComparison.OrdinalIgnoreCase))
            {
                return Failed(ErrorCatalog.SignFailed(
                    statusMessage ?? "The file was left unsigned."));
            }

            string? warning = null;
            if (timestampFallback is not null)
            {
                warning = $"Timestamping failed ({timestampFallback}); the file was signed without a timestamp.";
            }
            else if (!status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
            {
                warning = $"Signature applied with status '{status}' ({statusMessage}). " +
                          "The signature is present but its trust chain is not validated on this machine.";
            }

            return new SignResult
            {
                Success = true,
                SignatureStatus = status,
                StatusMessage = statusMessage,
                Warning = warning,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(ErrorCatalog.SignFailed(ex.Message));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the password is only in the deleted script as $env reference.
            }
        }
    }

    private static string BuildScript(SignRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$path = " + Q(request.TargetPath));
        sb.AppendLine("try {");
        sb.AppendLine("    $cert = $null");
        if (!string.IsNullOrWhiteSpace(request.PfxPath))
        {
            sb.AppendLine("    $pfxBytes = [System.IO.File]::ReadAllBytes(" + Q(request.PfxPath) + ")");
            sb.AppendLine("    $pwdText = $env:" + PasswordEnvironmentVariable);
            sb.AppendLine("    if (-not $pwdText) { $pwdText = '' }");
            sb.AppendLine("    $securePwd = ConvertTo-SecureString -String $pwdText -AsPlainText -Force");
            sb.AppendLine("    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable");
            sb.AppendLine("    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxBytes, $securePwd, $flags)");
        }
        else
        {
            var location = request.StoreLocation is "LocalMachine" or "CurrentUser"
                ? request.StoreLocation
                : "CurrentUser";
            sb.AppendLine("    $storePath = 'Cert:\\" + location + "\\My'");
            sb.AppendLine("    $cert = Get-ChildItem -Path $storePath | Where-Object { $_.Thumbprint -eq " +
                          Q(request.CertificateThumbprint!.ToUpperInvariant()) + " } | Select-Object -First 1");
            sb.AppendLine("    if (-not $cert) { throw 'CERT_NOT_FOUND' }");
            sb.AppendLine("    if (-not $cert.HasPrivateKey) { throw 'NO_PRIVATE_KEY' }");
        }

        sb.AppendLine("    $ts = " + Q(request.TimestampServer ?? string.Empty));
        sb.AppendLine("    $sig = $null");
        sb.AppendLine("    $tsFallback = $null");
        sb.AppendLine("    if ($ts) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $ts");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            $tsFallback = $_.Exception.Message");
        sb.AppendLine("            $sig = $null");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    if (-not $sig) {");
        sb.AppendLine("        if ($tsFallback) { 'TIMESTAMP_FALLBACK=' + $tsFallback }");
        sb.AppendLine("        $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256");
        sb.AppendLine("    }");
        sb.AppendLine("    $check = Get-AuthenticodeSignature -FilePath $path");
        sb.AppendLine("    'STATUS=' + $check.Status");
        sb.AppendLine("    'MESSAGE=' + $check.StatusMessage");
        sb.AppendLine("    if ($check.Status -eq 'NotSigned' -or $check.Status -eq 'HashMismatch') { exit 1 }");
        sb.AppendLine("    exit 0");
        sb.AppendLine("}");
        sb.AppendLine("catch {");
        sb.AppendLine("    'ERROR=' + $_.Exception.Message");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static AppError MapError(string error)
    {
        if (error.Contains("CERT_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCatalog.CertificateNotFound("(configured thumbprint)");
        }

        if (error.Contains("NO_PRIVATE_KEY", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCatalog.CertificateNoPrivateKey("(configured thumbprint)");
        }

        return ErrorCatalog.SignFailed(error.Trim());
    }

    private static string? ValueOf(IEnumerable<string> lines, string prefix) =>
        lines.Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            .Trim();

    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";

    private static SignResult Failed(AppError error) => new() { Success = false, Error = error };
}
