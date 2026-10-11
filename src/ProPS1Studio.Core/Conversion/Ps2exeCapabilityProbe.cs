using System.Text;
using ProPS1Studio.Core.Logging;
using ProPS1Studio.Core.Processes;
using ProPS1Studio.Core.Settings;

namespace ProPS1Studio.Core.Conversion;

public interface IPs2exeCapabilityProbe
{
    /// <summary>Probes Invoke-ps2exe parameters; cached per module path until <see cref="Invalidate"/>.</summary>
    Task<Ps2exeCapability> ProbeAsync(string? modulePath = null, CancellationToken cancellationToken = default);

    void Invalidate();
}

/// <summary>
/// Discovers the real parameter set of the installed ps2exe module by running
/// Get-Command in a child PowerShell. Never trusts a hard-coded parameter list.
/// </summary>
public sealed class Ps2exeCapabilityProbe : IPs2exeCapabilityProbe, IDisposable
{
    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, Ps2exeCapability> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _workDirectory;

    public Ps2exeCapabilityProbe(IProcessRunner runner, IAppLogger logger)
    {
        _runner = runner;
        _logger = logger;
        _workDirectory = Path.Combine(AppPaths.TempDirectory, "capability");
    }

    public async Task<Ps2exeCapability> ProbeAsync(string? modulePath = null, CancellationToken cancellationToken = default)
    {
        var key = modulePath ?? string.Empty;
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var capability = await RunProbeAsync(modulePath, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _cache[key] = capability;
        }

        return capability;
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _cache.Clear();
        }
    }

    private async Task<Ps2exeCapability> RunProbeAsync(string? modulePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workDirectory);
        var scriptPath = Path.Combine(_workDirectory, $"probe-{Guid.NewGuid():N}.ps1");
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        script.AppendLine("$mod = " + Quote(modulePath ?? string.Empty));
        script.AppendLine("try {");
        script.AppendLine("    if ($mod) { Import-Module -Name $mod -Force -ErrorAction Stop }");
        script.AppendLine("    else { Import-Module ps2exe -Force -ErrorAction Stop }");
        script.AppendLine("    $cmd = Get-Command Invoke-ps2exe -ErrorAction Stop");
        script.AppendLine("    'PS2EXE_OK'");
        script.AppendLine("    'PS2EXE_VERSION=' + $cmd.Module.Version");
        script.AppendLine("    'PS2EXE_PATH=' + $cmd.Module.Path");
        script.AppendLine("    foreach ($k in $cmd.Parameters.Keys) { 'PARAM=' + $k }");
        script.AppendLine("}");
        script.AppendLine("catch {");
        script.AppendLine("    'PS2EXE_ERR=' + $_.Exception.Message");
        script.AppendLine("}");
        script.AppendLine("exit 0");

        try
        {
            File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Warn, nameof(Ps2exeCapabilityProbe), $"Could not write probe script: {ex.Message}");
            return Ps2exeCapability.Unavailable($"Could not write the capability probe script: {ex.Message}");
        }

        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec
                {
                    FileName = "powershell.exe",
                    Arguments = new[]
                    {
                        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", scriptPath,
                    },
                    WorkingDirectory = _workDirectory,
                    TimeoutMilliseconds = 60_000,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var lines = SplitLines(result.StdOut);
            var errorLine = lines.FirstOrDefault(l => l.StartsWith("PS2EXE_ERR=", StringComparison.Ordinal));
            if (errorLine is not null)
            {
                var reason = errorLine["PS2EXE_ERR=".Length..].Trim();
                _logger.Write(LogLevel.Warn, nameof(Ps2exeCapabilityProbe), $"ps2exe probe failed: {reason}");
                return Ps2exeCapability.Unavailable(reason);
            }

            if (!lines.Contains("PS2EXE_OK"))
            {
                var detail = result.TimedOut
                    ? "The capability probe timed out."
                    : $"The capability probe produced no result (exit {result.ExitCode}). {result.StdErr}".Trim();
                _logger.Write(LogLevel.Warn, nameof(Ps2exeCapabilityProbe), detail);
                return Ps2exeCapability.Unavailable(detail);
            }

            var version = lines
                .FirstOrDefault(l => l.StartsWith("PS2EXE_VERSION=", StringComparison.Ordinal))?["PS2EXE_VERSION=".Length..]
                .Trim();
            var resolvedPath = lines
                .FirstOrDefault(l => l.StartsWith("PS2EXE_PATH=", StringComparison.Ordinal))?["PS2EXE_PATH=".Length..]
                .Trim();
            var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                if (line.StartsWith("PARAM=", StringComparison.Ordinal))
                {
                    parameters.Add(line["PARAM=".Length..].Trim());
                }
            }

            _logger.Write(LogLevel.Info, nameof(Ps2exeCapabilityProbe),
                $"ps2exe {version ?? "?"} available with {parameters.Count} parameters.");
            return new Ps2exeCapability
            {
                IsAvailable = true,
                ModuleVersion = version,
                ModulePath = string.IsNullOrWhiteSpace(resolvedPath) ? null : resolvedPath,
                Parameters = parameters,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Warn, nameof(Ps2exeCapabilityProbe), $"Probe failed: {ex.Message}");
            return Ps2exeCapability.Unavailable(ex.Message);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void TryDelete(string path)
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
            // Temp cleanup is best-effort.
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDirectory))
            {
                Directory.Delete(_workDirectory, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }
}
