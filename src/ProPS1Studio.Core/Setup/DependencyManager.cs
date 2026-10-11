using System.IO.Compression;
using System.Net.Http;
using ProPS1Studio.Core.Conversion;
using ProPS1Studio.Core.Errors;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Settings;
using ProPS1Studio.Core.Upx;

namespace ProPS1Studio.Core.Setup;

public sealed record ToolInstallResult
{
    public required bool Success { get; init; }
    public AppError? Error { get; init; }
    public string? InstalledPath { get; init; }
    public string Details { get; init; } = string.Empty;
}

public interface IPs2exeInstaller
{
    Task<ToolInstallResult> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public interface IUpxInstaller
{
    Task<ToolInstallResult> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class DependencyStatus
{
    public required string Name { get; init; }
    public required bool Installed { get; init; }
    public required bool Required { get; init; }
    public string? Version { get; init; }
    public string? Location { get; init; }
    public string? Details { get; init; }
    public bool MatchesPinnedHash { get; init; }
}

public interface IDependencyManager
{
    Task<IReadOnlyList<DependencyStatus>> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ToolInstallResult> InstallPs2exeAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<ToolInstallResult> InstallUpxAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Facade the Setup page uses: status of both tools plus consent-driven installs.</summary>
public sealed class DependencyManager : IDependencyManager
{
    private readonly IPs2exeCapabilityProbe _probe;
    private readonly IPs2exeInstaller _ps2exeInstaller;
    private readonly IUpxInstaller _upxInstaller;
    private readonly IUpxPacker _upx;

    public DependencyManager(
        IPs2exeCapabilityProbe probe,
        IPs2exeInstaller ps2exeInstaller,
        IUpxInstaller upxInstaller,
        IUpxPacker upx)
    {
        _probe = probe;
        _ps2exeInstaller = ps2exeInstaller;
        _upxInstaller = upxInstaller;
        _upx = upx;
    }

    public async Task<IReadOnlyList<DependencyStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var ps2exe = await _probe.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var upx = await _upx.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        return new[]
        {
            new DependencyStatus
            {
                Name = "ps2exe",
                Installed = ps2exe.IsAvailable,
                Required = true,
                Version = ps2exe.ModuleVersion,
                Location = ps2exe.ModulePath,
                Details = ps2exe.IsAvailable
                    ? $"Invoke-ps2exe available ({ps2exe.Parameters.Count} parameters detected)."
                    : ps2exe.Error ?? "Not installed.",
            },
            new DependencyStatus
            {
                Name = "UPX",
                Installed = upx.Available,
                Required = false,
                Version = upx.Version,
                Location = upx.Path,
                MatchesPinnedHash = upx.MatchesPinnedHash,
                Details = upx.Available
                    ? (upx.MatchesPinnedHash
                        ? $"Official 5.2.1 build verified at {upx.Path}."
                        : $"Found at {upx.Path} (hash not pinned).")
                    : upx.Error ?? "Not installed.",
            },
        };
    }

    public Task<ToolInstallResult> InstallPs2exeAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        _ps2exeInstaller.InstallAsync(progress, cancellationToken);

    public Task<ToolInstallResult> InstallUpxAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        _upxInstaller.InstallAsync(progress, cancellationToken);
}

/// <summary>
/// Installs the ps2exe module for the current user from the PowerShell Gallery
/// using a child PowerShell (process-scoped ExecutionPolicy Bypass, no machine changes).
/// </summary>
public sealed class Ps2exeInstaller : IPs2exeInstaller
{
    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly IPs2exeCapabilityProbe _probe;

    public Ps2exeInstaller(IProcessRunner runner, IAppLogger logger, IPs2exeCapabilityProbe probe)
    {
        _runner = runner;
        _logger = logger;
        _probe = probe;
    }

    public async Task<ToolInstallResult> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        const string source = nameof(Ps2exeInstaller);
        progress?.Report("Installing ps2exe from the PowerShell Gallery (CurrentUser scope)...");
        _logger.Write(LogLevel.Info, source, "Starting ps2exe installation.");

        var script = """
            $ErrorActionPreference = 'Stop'
            try {
                $opts = @{
                    Name = 'ps2exe'
                    Scope = 'CurrentUser'
                    Force = $true
                    AllowClobber = $true
                    Repository = 'PSGallery'
                    ErrorAction = 'Stop'
                }
                Install-Module @opts
                $mod = Get-Module -ListAvailable ps2exe | Sort-Object Version -Descending | Select-Object -First 1
                if (-not $mod) { throw 'ps2exe did not appear after installation.' }
                'INSTALLED_VERSION=' + $mod.Version
                'INSTALLED_PATH=' + $mod.Path
                exit 0
            }
            catch {
                'ERROR=' + $_.Exception.Message
                exit 1
            }
            """;

        var directory = Path.Combine(AppPaths.TempDirectory, $"install-ps2exe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "install.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken).ConfigureAwait(false);
            var result = await _runner.RunAsync(
                new ProcessSpec
                {
                    FileName = "powershell.exe",
                    Arguments = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
                    WorkingDirectory = directory,
                    TimeoutMilliseconds = 300_000,
                },
                line => { progress?.Report(line); _logger.Write(LogLevel.Info, source, line); },
                line => _logger.Write(LogLevel.Warn, source, line),
                cancellationToken).ConfigureAwait(false);

            var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var version = lines.FirstOrDefault(l => l.StartsWith("INSTALLED_VERSION=", StringComparison.Ordinal))?["INSTALLED_VERSION=".Length..];
            var path = lines.FirstOrDefault(l => l.StartsWith("INSTALLED_PATH=", StringComparison.Ordinal))?["INSTALLED_PATH=".Length..];
            var error = lines.FirstOrDefault(l => l.StartsWith("ERROR=", StringComparison.Ordinal))?["ERROR=".Length..];

            if (result.TimedOut)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.NetworkFailure("PowerShell Gallery", "the installation timed out"),
                };
            }

            if (!result.Success || error is not null)
            {
                var detail = error ?? result.StdErr.Trim();
                var appError = LooksLikeNetworkFailure(detail)
                    ? ErrorCatalog.NetworkFailure("PowerShell Gallery", detail)
                    : ErrorCatalog.Ps2exeInstallFailed(detail);
                _logger.Write(LogLevel.Error, source, $"ps2exe installation failed: {detail}");
                return new ToolInstallResult { Success = false, Error = appError, Details = detail };
            }

            _probe.Invalidate();
            var capability = await _probe.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!capability.IsAvailable)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.Ps2exeInstallFailed(
                        "the module was installed but Invoke-ps2exe could not be loaded afterwards"),
                    InstalledPath = path,
                };
            }

            progress?.Report($"ps2exe {capability.ModuleVersion} installed.");
            _logger.Write(LogLevel.Info, source, $"ps2exe {capability.ModuleVersion} installed to '{path}'.");
            return new ToolInstallResult
            {
                Success = true,
                InstalledPath = path,
                Details = $"ps2exe {version} installed for the current user.",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolInstallResult { Success = false, Error = ErrorCatalog.Ps2exeInstallFailed(ex.Message) };
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static bool LooksLikeNetworkFailure(string detail)
    {
        var text = detail.ToLowerInvariant();
        return text.Contains("no sources found") ||
               text.Contains("unable to load the service index") ||
               text.Contains("network") ||
               text.Contains("connection") ||
               text.Contains("proxy") ||
               text.Contains("timeout") ||
               text.Contains("could not resolve") ||
               text.Contains("503") ||
               text.Contains("403");
    }
}

/// <summary>
/// Downloads the official UPX 5.2.1 win64 release, verifies BOTH pinned SHA-256 hashes
/// (zip and upx.exe), extracts upx.exe to %LocalAppData%\ProPS1Studio\tools\upx\,
/// and verifies the result by running it.
/// </summary>
public sealed class UpxInstaller : IUpxInstaller
{
    private readonly IAppLogger _logger;
    private readonly IUpxPacker _upx;
    private static readonly HttpClient Http = CreateClient();

    public UpxInstaller(IAppLogger logger, IUpxPacker upx)
    {
        _logger = logger;
        _upx = upx;
    }

    public async Task<ToolInstallResult> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        const string source = nameof(UpxInstaller);
        var zipUrl = UpxManager.Upx521ZipUrl;
        progress?.Report($"Downloading UPX 5.2.1 from {zipUrl}...");
        _logger.Write(LogLevel.Info, source, $"Downloading UPX from {zipUrl}.");

        var tempDir = Path.Combine(AppPaths.TempDirectory, $"install-upx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "upx.zip");
        try
        {
            // 1. Download with progress.
            byte[] zipBytes;
            try
            {
                zipBytes = await DownloadAsync(zipUrl, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.NetworkFailure(zipUrl, ex.Message),
                };
            }

            await File.WriteAllBytesAsync(zipPath, zipBytes, cancellationToken).ConfigureAwait(false);

            // 2. Verify the archive hash before touching it.
            var zipHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes));
            if (!zipHash.Equals(UpxManager.Upx521Win64ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Write(LogLevel.Error, source, $"UPX zip hash mismatch: expected {UpxManager.Upx521Win64ZipSha256}, got {zipHash}.");
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.DownloadIntegrityFailure(zipUrl, UpxManager.Upx521Win64ZipSha256, zipHash),
                };
            }

            progress?.Report("Archive hash verified. Extracting...");

            // 3. Extract just upx.exe.
            string extractedPath;
            try
            {
                extractedPath = ExtractUpxExe(zipPath, tempDir);
            }
            catch (Exception ex)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.UpxFailed($"extraction failed: {ex.Message}", null),
                };
            }

            // 4. Verify the extracted binary hash.
            var exeHash = ToolLocator.ComputeSha256(extractedPath);
            if (!exeHash.Equals(UpxManager.Upx521Win64Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Write(LogLevel.Error, source, $"upx.exe hash mismatch: expected {UpxManager.Upx521Win64Sha256}, got {exeHash}.");
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.DownloadIntegrityFailure(zipUrl, UpxManager.Upx521Win64Sha256, exeHash),
                };
            }

            // 5. Install to the app tools directory.
            var targetDir = Path.Combine(AppPaths.ToolsDirectory, "upx");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, "upx.exe");
            try
            {
                File.Copy(extractedPath, targetPath, overwrite: true);
            }
            catch (Exception ex)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.UpxFailed($"could not write '{targetPath}': {ex.Message}", null),
                };
            }

            // 6. Run it to confirm it actually works on this machine.
            var availability = await _upx.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (!availability.Available)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    Error = ErrorCatalog.UpxFailed(availability.Error ?? "the installed UPX did not run", null),
                };
            }

            progress?.Report($"UPX {availability.Version} installed and verified.");
            _logger.Write(LogLevel.Info, source, $"UPX {availability.Version} installed to '{targetPath}' (hash verified).");
            return new ToolInstallResult
            {
                Success = true,
                InstalledPath = targetPath,
                Details = $"UPX {availability.Version}, SHA-256 verified against the pinned official 5.2.1 build.",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolInstallResult { Success = false, Error = ErrorCatalog.UpxFailed(ex.Message, null) };
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task<byte[]> DownloadAsync(string url, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long readTotal = 0;
        var lastReported = -10;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (total > 0)
            {
                var percent = (int)(readTotal * 100 / total);
                if (percent >= lastReported + 10)
                {
                    lastReported = percent;
                    progress?.Report($"Downloading UPX... {percent}% ({readTotal / 1024 / 1024} MB)");
                }
            }
        }

        return buffer.ToArray();
    }

    private static string ExtractUpxExe(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("upx.exe", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'));
        if (entry is null)
        {
            throw new InvalidDataException("The archive does not contain upx.exe.");
        }

        var target = Path.Combine(destination, Path.GetFileName(entry.FullName));
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProPS1Studio/1.0");
        return client;
    }
}
